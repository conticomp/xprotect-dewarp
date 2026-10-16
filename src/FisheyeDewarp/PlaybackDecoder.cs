using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using VideoOS.Platform;
using VideoOS.Platform.Data;

namespace FisheyeDewarp
{
    /// <summary>
    /// SPIKE: the tile's own playback reader. Smart Client hands over a displayed playback frame in 0.2-0.5 s, so
    /// the sharp view in Playback lags; this fetches the frame Smart Client is showing from the recording server
    /// itself, as YCbCr 4:2:0 cropped to the view, on a background thread. Requests collapse to the latest time.
    /// </summary>
    internal sealed class PlaybackDecoder : IDisposable
    {
        private static readonly TimeSpan StatsInterval = TimeSpan.FromSeconds(10);
        // Ahead by up to this much, step frame by frame: a seek decodes from the previous key frame, about 1 s.
        private static readonly TimeSpan MaxStep = TimeSpan.FromSeconds(3);
        private static bool _hardwareRequested;

        private readonly Item _camera;
        private readonly string _cameraName;
        private readonly System.Drawing.Size _fullSize;
        private readonly Action<SourceFrame, DateTime> _onFrame;
        private readonly Thread _thread;
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private readonly object _gate = new object();
        private DateTime _requested;
        private bool _hasRequest;
        private System.Windows.Rect _crop = new System.Windows.Rect(0, 0, 1, 1);
        private bool _cropChanged = true;
        private volatile bool _disposed;

        // Worker-thread state.
        private BitmapVideoSource _source;
        private System.Windows.Rect _appliedCrop;
        private DateTime _lastTime, _nextTime;
        private bool _hasLast;

        private readonly object _statsGate = new object();
        private int _requests, _frames, _sequential, _seeks;
        private long _fetchTicks, _maxFetchTicks;
        private string _decodeStatus;
        private Timer _statsTimer;

        /// <summary>True once the reader could not start or fetch; the tile grabs frames from Smart Client instead.</summary>
        public bool Failed { get; private set; }

        public PlaybackDecoder(Item camera, System.Drawing.Size fullSize, Action<SourceFrame, DateTime> onFrame)
        {
            _camera = camera;
            _cameraName = camera.Name;
            _fullSize = fullSize;
            _onFrame = onFrame;
            _thread = new Thread(Run) { IsBackground = true, Name = "Dewarp playback " + _cameraName };
            _thread.Start();
            _statsTimer = new Timer(_ => LogStats(), null, StatsInterval, StatsInterval);
            string hw = EnvironmentManager.Instance.EnvironmentOptions.TryGetValue(EnvironmentOptions.HardwareDecodingMode, out string mode) ? mode : "(not set)";
            if (!_hardwareRequested && string.IsNullOrEmpty(mode))
            {
                // Unset in Smart Client, and MIP's playback sources then decode on the CPU (about 120 ms a frame).
                _hardwareRequested = true;
                EnvironmentManager.Instance.EnvironmentOptions[EnvironmentOptions.HardwareDecodingMode] = EnvironmentOptions.OptionHardwareDecodingModeAuto;
                EnvironmentManager.Instance.FireEnvironmentOptionsChangedEvent();
                hw += " -> " + EnvironmentOptions.OptionHardwareDecodingModeAuto;
            }
            Log.Info($"Playback decode START camera='{_cameraName}' size={fullSize.Width}x{fullSize.Height} HardwareDecodingMode={hw}");
        }

        /// <summary>Fetch the frame recorded at <paramref name="utc"/> (the time Smart Client is showing).</summary>
        public void Request(DateTime utc)
        {
            lock (_gate)
            {
                _requested = utc;
                _hasRequest = true;
                _requests++;
            }
            _wake.Set();
        }

        /// <summary>Decode only this part of the image (fractions 0..1) from the next fetch on.</summary>
        public void SetCrop(System.Windows.Rect crop)
        {
            lock (_gate)
            {
                _crop = crop;
                _cropChanged = true;
            }
        }

        private void Run()
        {
            try
            {
                _source = new BitmapVideoSource(_camera);
                _source.Init(_fullSize.Width, _fullSize.Height, BitmapFormat.YCbCr420_Planar);
                while (!_disposed)
                {
                    _wake.WaitOne();
                    if (_disposed) break;
                    DateTime target;
                    System.Windows.Rect crop;
                    bool cropChanged;
                    lock (_gate)
                    {
                        if (!_hasRequest) continue;
                        target = _requested;
                        _hasRequest = false;
                        crop = _crop;
                        cropChanged = _cropChanged;
                        _cropChanged = false;
                    }
                    if (cropChanged) ApplyCrop(crop);
                    Fetch(target);
                }
            }
            catch (Exception ex)
            {
                Failed = true;
                Log.Error($"Playback decode failed camera='{_cameraName}'", ex);
            }
            finally
            {
                try { _source?.Close(); } catch (Exception ex) { Log.Error("Playback decode close failed", ex); }
            }
        }

        private void ApplyCrop(System.Windows.Rect crop)
        {
            _source.SuspendSettingsUpdate();
            _source.SetCropRectangle(crop.X, crop.Y, crop.Width, crop.Height);
            _source.Width = Math.Max(16, (int)Math.Ceiling(crop.Width * _fullSize.Width));
            _source.Height = Math.Max(16, (int)Math.Ceiling(crop.Height * _fullSize.Height));
            _source.SetWidthHeight();
            _source.ResumeSettingsUpdate();
            _appliedCrop = crop;
        }

        private void Fetch(DateTime target)
        {
            long start = Stopwatch.GetTimestamp();
            object got;
            bool sequential = _hasLast && target > _lastTime && target - _lastTime <= MaxStep;
            if (sequential)
            {
                // Playing forward: step to the frame Smart Client shows, decoding any skipped frames on the way.
                got = _source.GetNext();
                while (got is BitmapData step && DewarpExporter.ToUtc(step.DateTime) < target && step.IsNextAvailable)
                {
                    step.Dispose();
                    got = _source.GetNext();
                }
            }
            else
            {
                got = _source.GetAtOrBefore(target);
            }
            long ticks = Stopwatch.GetTimestamp() - start;

            var frame = got as BitmapData;
            if (frame == null)
            {
                _hasLast = false;
                return;
            }
            try
            {
                _lastTime = DewarpExporter.ToUtc(frame.DateTime);
                _nextTime = frame.IsNextAvailable ? DewarpExporter.ToUtc(frame.NextDateTime) : _lastTime;
                _hasLast = true;
                lock (_statsGate)
                {
                    _frames++;
                    if (sequential) _sequential++; else _seeks++;
                    _fetchTicks += ticks;
                    _maxFetchTicks = Math.Max(_maxFetchTicks, ticks);
                    _decodeStatus = $"{frame.HardwareDecodingStatus} {frame.Format} {frame.ColorSpace}";
                }
                SourceFrame copy = Copy(frame);
                // A frame decoded before a crop change has the old size; its crop is unknown, so skip it.
                if (copy != null && Math.Abs(copy.FullWidth - _fullSize.Width) > 4) copy = null;
                if (copy != null && !_disposed) _onFrame(copy, _lastTime);
            }
            finally
            {
                frame.Dispose();
            }
        }

        private SourceFrame Copy(BitmapData frame)
        {
            if (frame.Format != BitmapFormat.YCbCr420_Planar) return null;
            var planes = new byte[3][];
            for (int p = 0; p < 3; p++)
            {
                int width = frame.GetPlaneWidth(p), height = frame.GetPlaneHeight(p), stride = frame.GetPlaneStride(p);
                planes[p] = new byte[width * height];
                IntPtr scan0 = frame.GetPlanePointer(p);
                if (stride == width)
                    Marshal.Copy(scan0, planes[p], 0, width * height);
                else
                    for (int y = 0; y < height; y++)
                        Marshal.Copy(scan0 + y * stride, planes[p], y * width, width);
            }
            return new SourceFrame(planes[0], planes[1], planes[2], frame.GetPlaneWidth(0), frame.GetPlaneHeight(0),
                frame.GetPlaneWidth(1), frame.GetPlaneHeight(1), _appliedCrop);
        }

        private void LogStats()
        {
            int requests, frames, seq, seeks;
            double avg, max;
            string status;
            lock (_gate) { requests = _requests; _requests = 0; }
            lock (_statsGate)
            {
                frames = _frames; seq = _sequential; seeks = _seeks; status = _decodeStatus;
                avg = frames == 0 ? 0 : _fetchTicks * 1000.0 / Stopwatch.Frequency / frames;
                max = _maxFetchTicks * 1000.0 / Stopwatch.Frequency;
                _frames = _sequential = _seeks = 0;
                _fetchTicks = _maxFetchTicks = 0;
            }
            if (requests == 0 && frames == 0) return;
            Log.Info($"Playback decode camera='{_cameraName}': {requests / StatsInterval.TotalSeconds:F1} requests/s, {frames / StatsInterval.TotalSeconds:F1} frames/s ({seq} next, {seeks} seek), fetch avg {avg:F0} ms max {max:F0} ms, crop {_appliedCrop.Width * _appliedCrop.Height * 100:F0}%, decode={status}");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _statsTimer?.Dispose();
            _wake.Set();
            Log.Info($"Playback decode STOP camera='{_cameraName}'");
        }
    }
}
