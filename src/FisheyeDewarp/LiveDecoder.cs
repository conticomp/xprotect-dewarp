using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using VideoOS.Platform;
using VideoOS.Platform.Live;

namespace FisheyeDewarp
{
    /// <summary>
    /// The tile's own full-resolution live stream, decoded by MIP in the background (on the GPU where it can), so
    /// the sharp view in Live keeps up with the camera instead of waiting 150-450 ms for each frame from Smart
    /// Client. Only the part of the image the view needs is decoded. It costs a second stream from the recording
    /// server per dewarped Live tile. Turned off by creating %LOCALAPPDATA%\FisheyeDewarp\no-live-decode (read
    /// each time Dewarp is turned on); the sharp view then grabs frames from Smart Client as before.
    /// </summary>
    internal sealed class LiveDecoder : IDisposable
    {
        private static readonly string DisableFlag = Path.Combine(Log.Folder, "no-live-decode");
        private static readonly TimeSpan StatsInterval = TimeSpan.FromMinutes(1);
        private static int _running;

        private readonly BitmapLiveSource _source;
        private readonly Action<SourceFrame> _onFrame;
        private readonly string _camera;
        private System.Drawing.Size _fullSize;

        // Each frame gets its own planes, never written again once handed over, since the tile may still be
        // rendering an earlier frame. Only one frame waits for the UI thread at a time (1 while one is on its way);
        // frames that arrive meanwhile are skipped before they are copied.
        private int _delivering;
        private bool _formatLogged;

        // Frames coarser than the camera's pixel density would make the sharp view softer than grabbing does.
        private const int CoarseLimit = 10;
        private int _coarseRun;

        private readonly object _statsGate = new object();
        private int _frames;
        private long _copyTicks;
        private string _decodeStatus;
        private int _lastWidth, _lastHeight;
        private double _lastCropArea;
        private Timer _statsTimer;
        private bool _disposed;
        private bool _counted;

        public static bool Allowed => !File.Exists(DisableFlag);

        /// <summary>The tile has taken the frame passed to onFrame; the next one may be delivered.</summary>
        public void Delivered() => Interlocked.Exchange(ref _delivering, 0);

        /// <summary>
        /// True once the decode kept sending frames coarser than the camera, or in a format it cannot use: it delivers no more frames, and the
        /// tile should stop it and grab frames from Smart Client instead.
        /// </summary>
        public bool GaveUp { get; private set; }

        private LiveDecoder(Item camera, Guid streamId, Action<SourceFrame> onFrame)
        {
            _camera = camera.Name;
            _onFrame = onFrame;
            // YCbCr 4:2:0, so only the pixels the view samples get converted: about a quarter less CPU than MIP
            // converting every frame to BGR32.
            _source = new BitmapLiveSource(camera, BitmapFormat.YCbCr420_Planar);
            if (streamId != Guid.Empty) _source.StreamId = streamId;
            _source.LiveContentEvent += OnContent;
            _source.LiveStatusEvent += OnStatus;
        }

        /// <summary>Starts decoding; <paramref name="onFrame"/> is called on a background thread. Null on failure.</summary>
        public static LiveDecoder Start(Item camera, Guid streamId, System.Drawing.Size size, Action<SourceFrame> onFrame)
        {
            LiveDecoder decoder = null;
            try
            {
                decoder = new LiveDecoder(camera, streamId, onFrame);
                decoder._fullSize = size;
                decoder._source.Init();
                // The size must be set explicitly: left at 0, frames arrive scaled to 640x480.
                decoder._source.Width = size.Width;
                decoder._source.Height = size.Height;
                decoder._source.SetKeepAspectRatio(true, false);
                decoder._source.SetWidthHeight();
                decoder._source.LiveModeStart = true;
                decoder._statsTimer = new Timer(_ => decoder.LogStats(), null, StatsInterval, StatsInterval);
                Interlocked.Increment(ref _running);
                decoder._counted = true;
                Log.Info($"Live decode START camera='{camera.Name}' stream={streamId} size={size.Width}x{size.Height} (running: {_running})");
                return decoder;
            }
            catch (Exception ex)
            {
                Log.Error($"Live decode could not start for camera='{camera?.Name}'", ex);
                decoder?.Dispose();
                return null;
            }
        }

        private void OnContent(object sender, EventArgs e)
        {
            var args = e as LiveContentEventArgs;
            if (args == null) return;
            if (args.Exception != null)
            {
                Log.Info($"Live decode error camera='{_camera}': {args.Exception.Message}");
                return;
            }
            var content = args.LiveContent as LiveSourceBitmapContent;
            if (content == null)
            {
                args.LiveContent?.Dispose();
                return;
            }
            try
            {
                if (_disposed || GaveUp) return;
                if (Interlocked.CompareExchange(ref _delivering, 1, 0) != 0) return;   // the UI thread has not taken the last one
                long start = Stopwatch.GetTimestamp();
                SourceFrame frame = Copy(content);
                long ticks = Stopwatch.GetTimestamp() - start;
                if (frame == null)
                {
                    Delivered();
                    return;
                }
                lock (_statsGate)
                {
                    _lastWidth = frame.Width;
                    _lastHeight = frame.Height;
                    _lastCropArea = frame.Crop.Width * frame.Crop.Height;
                    _frames++;
                    _copyTicks += ticks;
                    _decodeStatus = content.HardwareDecodingStatus;
                }
                if (frame.FullWidth < _fullSize.Width * 0.98 || frame.FullHeight < _fullSize.Height * 0.98)
                {
                    Delivered();
                    if (++_coarseRun < CoarseLimit) return;
                    GaveUp = true;   // the tile stops the decode on its own thread
                    Log.Info($"Live decode camera='{_camera}' keeps sending {frame.Width}x{frame.Height} for crop {frame.Crop}, coarser than the camera's {_fullSize.Width}x{_fullSize.Height}; giving up");
                    return;
                }
                _coarseRun = 0;
                _onFrame(frame);
            }
            catch (Exception ex)
            {
                Delivered();
                Log.Error($"Live decode frame failed camera='{_camera}'", ex);
            }
            finally
            {
                content.Dispose();
            }
        }

        private SourceFrame Copy(LiveSourceBitmapContent content)
        {
            if (content.Format != BitmapFormat.YCbCr420_Planar)
            {
                GaveUp = true;   // the tile stops the decode and grabs frames from Smart Client instead
                Log.Info($"Live decode camera='{_camera}' sent {content.Format}, which the sharp view cannot use; giving up");
                return null;
            }
            if (!_formatLogged)
            {
                _formatLogged = true;
                Log.Info($"Live decode camera='{_camera}': {content.Format}, colour space {content.ColorSpace}");
            }
            var crop = content.CroppingDefined
                ? new System.Windows.Rect(content.CropX, content.CropY, content.CropWidth, content.CropHeight)
                : new System.Windows.Rect(0, 0, 1, 1);
            var planes = new byte[3][];
            for (int p = 0; p < 3; p++)
            {
                int width = content.GetPlaneWidth(p), height = content.GetPlaneHeight(p), stride = content.GetPlaneStride(p);
                planes[p] = new byte[width * height];
                IntPtr scan0 = content.GetPlanePointer(p);
                if (stride == width)
                    Marshal.Copy(scan0, planes[p], 0, width * height);
                else
                    for (int y = 0; y < height; y++)
                        Marshal.Copy(scan0 + y * stride, planes[p], y * width, width);
            }
            return new SourceFrame(planes[0], planes[1], planes[2], content.GetPlaneWidth(0), content.GetPlaneHeight(0),
                content.GetPlaneWidth(1), content.GetPlaneHeight(1), crop);
        }

        /// <summary>
        /// Decode only this part of the image (fractions 0..1), still at the camera's pixel density. Converting and
        /// copying a full 2992x2992 frame is most of the sharp view's CPU cost, and the view only needs part of it.
        /// </summary>
        public void SetCrop(System.Windows.Rect crop)
        {
            try
            {
                _source.SuspendSettingsUpdate();
                _source.SetCropRectangle(crop.X, crop.Y, crop.Width, crop.Height);
                _source.Width = Math.Max(16, (int)Math.Ceiling(crop.Width * _fullSize.Width));
                _source.Height = Math.Max(16, (int)Math.Ceiling(crop.Height * _fullSize.Height));
                _source.SetWidthHeight();
                _source.ResumeSettingsUpdate();
            }
            catch (Exception ex)
            {
                Log.Error($"Live decode crop failed camera='{_camera}'", ex);
            }
        }

        private void OnStatus(object sender, EventArgs e)
        {
            if (e is LiveStatusEventArgs status)
                Log.Info($"Live decode status camera='{_camera}': flags={status.ChangedStatusFlags} now={status.CurrentStatusFlags}");
        }

        private void LogStats()
        {
            int frames;
            double copyMs;
            string status, size;
            lock (_statsGate)
            {
                size = $"{_lastWidth}x{_lastHeight} ({_lastCropArea * 100:F0}% of the image)";
                frames = _frames;
                copyMs = frames == 0 ? 0 : _copyTicks * 1000.0 / Stopwatch.Frequency / frames;
                status = _decodeStatus;
                _frames = 0;
                _copyTicks = 0;
            }
            Log.Info($"Live decode camera='{_camera}': {frames / StatsInterval.TotalSeconds:F1} fps at {size}, copy {copyMs:F1} ms/frame, decode={status}");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _statsTimer?.Dispose();
            try
            {
                _source.LiveModeStart = false;
                _source.LiveContentEvent -= OnContent;
                _source.LiveStatusEvent -= OnStatus;
                _source.Close();
            }
            catch (Exception ex)
            {
                Log.Error($"Live decode close failed camera='{_camera}'", ex);
            }
            if (_counted) Interlocked.Decrement(ref _running);
            Log.Info($"Live decode STOP camera='{_camera}' (running: {_running})");
        }
    }
}
