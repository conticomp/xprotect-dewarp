using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using VideoOS.Platform;
using VideoOS.Platform.Data;

namespace FisheyeDewarp
{
    /// <summary>
    /// The tile's own reader of the recording, so the sharp view in Playback keeps up with the video instead of
    /// waiting 150-900 ms for each frame from Smart Client. It fetches the frame Smart Client is showing, as
    /// YCbCr 4:2:0 cropped to the part of the image the view needs, on a background thread; requests collapse to
    /// the latest time. MIP decodes these on the CPU. Turned off by creating
    /// %LOCALAPPDATA%\FisheyeDewarp\no-playback-decode (read each time Dewarp is turned on in Playback); the
    /// sharp view then grabs frames from Smart Client as before.
    /// </summary>
    internal sealed class PlaybackDecoder : IDisposable
    {
        private static readonly string DisableFlag = Path.Combine(Log.Folder, "no-playback-decode");
        private static readonly TimeSpan StatsInterval = TimeSpan.FromMinutes(1);
        // Ahead by up to this much, step frame by frame: a seek decodes from the previous key frame, about 1 s.
        private static readonly TimeSpan MaxStep = TimeSpan.FromSeconds(3);
        // A fetch running longer than this is stuck (seeks take up to about 3 s).
        private static readonly TimeSpan StallLimit = TimeSpan.FromSeconds(10);
        // After a crop change with no new request, wait this long for one before fetching the same frame again
        // at the new crop: while playing, the next request comes within a frame and a re-fetch would be a seek.
        private const int RefetchDelayMs = 250;

        private readonly Item _camera;
        private readonly string _cameraName;
        private readonly System.Drawing.Size _fullSize;
        private readonly Action<SourceFrame, DateTime> _onFrame;
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private readonly object _gate = new object();
        private DateTime _requested;
        private bool _hasRequest;
        private System.Windows.Rect _crop = new System.Windows.Rect(0, 0, 1, 1);
        private bool _cropChanged = true;
        private volatile bool _disposed;
        private volatile bool _failed;
        private long _fetchStarted;   // Stopwatch timestamp while a fetch runs, else 0

        // Worker-thread state.
        private BitmapVideoSource _source;
        private System.Windows.Rect _appliedCrop;
        private DateTime _lastTime;       // the frame the source is positioned on
        private bool _hasLast;
        private DateTime _deliveredTime;  // the last frame handed to the tile, at _deliveredCrop
        private System.Windows.Rect _deliveredCrop;
        private bool _hasDelivered;

        private readonly object _statsGate = new object();
        private int _requests, _frames, _sequential, _seeks;
        private long _fetchTicks, _maxFetchTicks;
        private string _decodeStatus;
        private readonly Timer _statsTimer;

        public static bool Allowed => !File.Exists(DisableFlag);

        /// <summary>
        /// True once the reader could not start or fetch, or a fetch has hung: it delivers no more frames, and the
        /// tile should stop it and grab frames from Smart Client instead.
        /// </summary>
        public bool GaveUp
        {
            get
            {
                if (_failed) return true;
                long started = Interlocked.Read(ref _fetchStarted);
                return started != 0 && Stopwatch.GetTimestamp() - started > StallLimit.TotalSeconds * Stopwatch.Frequency;
            }
        }

        /// <param name="onFrame">Called on the reader's thread with each frame and its recording time (UTC).</param>
        public PlaybackDecoder(Item camera, System.Drawing.Size fullSize, Action<SourceFrame, DateTime> onFrame)
        {
            _camera = camera;
            _cameraName = camera.Name;
            _fullSize = fullSize;
            _onFrame = onFrame;
            new Thread(Run) { IsBackground = true, Name = "Dewarp playback " + _cameraName }.Start();
            _statsTimer = new Timer(_ => LogStats(), null, StatsInterval, StatsInterval);
            Log.Info($"Playback decode START camera='{_cameraName}' size={fullSize.Width}x{fullSize.Height}");
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

        /// <summary>
        /// Decode only this part of the image (fractions 0..1), still at the camera's pixel density. If no new
        /// frame is requested soon (paused), the last frame is fetched again at the new crop.
        /// </summary>
        public void SetCrop(System.Windows.Rect crop)
        {
            lock (_gate)
            {
                _crop = crop;
                _cropChanged = true;
            }
            _wake.Set();
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
                    bool cropOnly;
                    lock (_gate) cropOnly = !_hasRequest && _cropChanged;
                    if (cropOnly && _wake.WaitOne(RefetchDelayMs)) _wake.Set();   // something else arrived; go round again
                    if (_disposed) break;

                    DateTime target;
                    System.Windows.Rect crop;
                    bool cropChanged;
                    lock (_gate)
                    {
                        if (_hasRequest) target = _requested;
                        else if (_cropChanged && _hasDelivered) target = _deliveredTime;
                        else continue;
                        _hasRequest = false;
                        crop = _crop;
                        cropChanged = _cropChanged;
                        _cropChanged = false;
                    }
                    if (cropChanged) ApplyCrop(crop);
                    // Paused, Smart Client repeats the time on screen: the tile already has that frame.
                    if (_hasDelivered && target == _deliveredTime && _appliedCrop == _deliveredCrop) continue;
                    Interlocked.Exchange(ref _fetchStarted, Stopwatch.GetTimestamp());
                    try { Fetch(target); }
                    finally { Interlocked.Exchange(ref _fetchStarted, 0); }
                }
            }
            catch (Exception ex)
            {
                _failed = true;
                Log.Error($"Playback decode failed camera='{_cameraName}'", ex);
            }
            finally
            {
                try { _source?.Close(); } catch (Exception ex) { Log.Error($"Playback decode close failed camera='{_cameraName}'", ex); }
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
                    if (_disposed) return;
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
                _hasLast = true;
                lock (_statsGate)
                {
                    _frames++;
                    if (sequential) _sequential++; else _seeks++;
                    _fetchTicks += ticks;
                    _maxFetchTicks = Math.Max(_maxFetchTicks, ticks);
                    _decodeStatus = $"{frame.HardwareDecodingStatus}";
                }
                SourceFrame copy = Copy(frame);
                // A frame decoded before a crop change has the old size; its crop is unknown, so skip it.
                if (copy == null || Math.Abs(copy.FullWidth - _fullSize.Width) > 4 || _disposed) return;
                _deliveredTime = target;
                _deliveredCrop = _appliedCrop;
                _hasDelivered = true;
                _onFrame(copy, _lastTime);
            }
            finally
            {
                frame.Dispose();
            }
        }

        private SourceFrame Copy(BitmapData frame)
        {
            if (frame.Format != BitmapFormat.YCbCr420_Planar)
                throw new NotSupportedException($"Playback frames arrive as {frame.Format}, which the sharp view cannot use");
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
            Log.Info($"Playback decode camera='{_cameraName}': {frames / StatsInterval.TotalSeconds:F1} fps ({seq} stepped, {seeks} seeks), fetch avg {avg:F0} ms max {max:F0} ms, decode={status}");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _statsTimer.Dispose();
            _wake.Set();
            Log.Info($"Playback decode STOP camera='{_cameraName}'");
        }
    }
}
