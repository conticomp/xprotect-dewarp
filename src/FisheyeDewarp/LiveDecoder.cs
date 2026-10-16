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
    /// SPIKE: the tile's own full-resolution live stream, decoded by MIP in the background, so the sharp view in
    /// Live keeps up with the camera instead of waiting 150-450 ms for each frame from Smart Client. It costs a
    /// second stream from the recording server and a second decode per dewarped Live tile.
    /// Turned off by creating %LOCALAPPDATA%\FisheyeDewarp\no-live-decode (read each time Dewarp is turned on).
    /// </summary>
    internal sealed class LiveDecoder : IDisposable
    {
        private static readonly string DisableFlag = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FisheyeDewarp", "no-live-decode");

        // Frames come as YCbCr 4:2:0 and only the pixels the view samples are converted, which costs about a
        // third less CPU than MIP converting every frame to BGR32. SPIKE: the bgr-decode file asks for BGR32.
        private static readonly string BgrFlag = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FisheyeDewarp", "bgr-decode");

        private static int _running;

        private readonly BitmapLiveSource _source;
        private readonly Action<SourceFrame> _onFrame;
        private readonly string _camera;

        // Frames are copied into a few reused buffers: a 2992x2992 frame is 36 MB, and allocating one per frame
        // would keep the garbage collector busy. One is being filled, one is the latest frame, one may be rendering.
        private readonly int[][] _pool = new int[4][];
        private readonly byte[][][] _yuvPool = new byte[4][][];
        private int _next;
        private readonly bool _yuv;
        private int _formatLogs;
        private int _unknownFormatLogs;

        private readonly object _statsGate = new object();
        private int _frames;
        private long _copyTicks;
        private string _decodeStatus;
        private string _lastSize;
        private Timer _statsTimer;
        private bool _disposed;
        private bool _counted;

        public static bool Allowed => !File.Exists(DisableFlag);

        public static int Running => _running;

        private LiveDecoder(Item camera, Guid streamId, Action<SourceFrame> onFrame)
        {
            _camera = camera.Name;
            _onFrame = onFrame;
            _yuv = !File.Exists(BgrFlag);
            _source = new BitmapLiveSource(camera, _yuv ? BitmapFormat.YCbCr420_Planar : BitmapFormat.BGR32);
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
                decoder._source.Init();
                // The size must be set explicitly: left at 0, frames arrive scaled to 640x480.
                decoder._source.Width = size.Width;
                decoder._source.Height = size.Height;
                decoder._source.SetKeepAspectRatio(true, false);
                decoder._source.SetWidthHeight();
                decoder._source.LiveModeStart = true;
                decoder._statsTimer = new Timer(_ => decoder.LogStats(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
                Interlocked.Increment(ref _running);
                decoder._counted = true;
                Log.Info($"Live decode START camera='{camera.Name}' stream={streamId} size={size.Width}x{size.Height} format={(decoder._yuv ? "YCbCr420" : "BGR32")} (running: {_running})");
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
                if (_disposed) return;
                long start = Stopwatch.GetTimestamp();
                SourceFrame frame = Copy(content);
                long ticks = Stopwatch.GetTimestamp() - start;
                lock (_statsGate)
                {
                    if (frame != null) _lastSize = $"{frame.Width}x{frame.Height} ({frame.Crop.Width * frame.Crop.Height * 100:F0}% of the image)";
                    _frames++;
                    _copyTicks += ticks;
                    _decodeStatus = content.HardwareDecodingStatus;
                }
                if (frame != null) _onFrame(frame);
            }
            catch (Exception ex)
            {
                Log.Error($"Live decode frame failed camera='{_camera}'", ex);
            }
            finally
            {
                content.Dispose();
            }
        }

        private SourceFrame Copy(LiveSourceBitmapContent content)
        {
            if (_formatLogs++ < 1)
                Log.Info($"Live decode format camera='{_camera}': {content.Format}, planes " +
                    $"{content.GetPlaneWidth(0)}x{content.GetPlaneHeight(0)}/{content.GetPlaneStride(0)}" +
                    (content.Format == BitmapFormat.YCbCr420_Planar
                        ? $", {content.GetPlaneWidth(1)}x{content.GetPlaneHeight(1)}/{content.GetPlaneStride(1)}, {content.GetPlaneWidth(2)}x{content.GetPlaneHeight(2)}/{content.GetPlaneStride(2)}"
                        : ""));
            var crop = content.CroppingDefined
                ? new System.Windows.Rect(content.CropX, content.CropY, content.CropWidth, content.CropHeight)
                : new System.Windows.Rect(0, 0, 1, 1);
            if (content.Format == BitmapFormat.YCbCr420_Planar) return CopyYuv(content, crop);
            if (content.Format != BitmapFormat.BGR32 && content.Format != BitmapFormat.BGRA)
            {
                if (_unknownFormatLogs++ < 1) Log.Info($"Live decode camera='{_camera}' sent {content.Format}, which the sharp view cannot use; grabbing frames instead");
                return null;
            }
            int width = content.GetPlaneWidth(0), height = content.GetPlaneHeight(0), stride = content.GetPlaneStride(0);
            int[] pixels = _pool[_next];
            if (pixels == null || pixels.Length != width * height) _pool[_next] = pixels = new int[width * height];
            _next = (_next + 1) % _pool.Length;
            IntPtr scan0 = content.GetPlanePointer(0);
            if (stride == width * 4)
                Marshal.Copy(scan0, pixels, 0, pixels.Length);
            else
                for (int y = 0; y < height; y++)
                    Marshal.Copy(scan0 + y * stride, pixels, y * width, width);
            return new SourceFrame(pixels, width, height, crop);
        }

        private SourceFrame CopyYuv(LiveSourceBitmapContent content, System.Windows.Rect crop)
        {
            byte[][] planes = _yuvPool[_next] ?? (_yuvPool[_next] = new byte[3][]);
            _next = (_next + 1) % _yuvPool.Length;
            for (int p = 0; p < 3; p++)
            {
                int width = content.GetPlaneWidth(p), height = content.GetPlaneHeight(p), stride = content.GetPlaneStride(p);
                if (planes[p] == null || planes[p].Length != width * height) planes[p] = new byte[width * height];
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
        public void SetCrop(System.Windows.Rect crop, System.Drawing.Size full)
        {
            try
            {
                _source.SuspendSettingsUpdate();
                _source.SetCropRectangle(crop.X, crop.Y, crop.Width, crop.Height);
                _source.Width = Math.Max(16, (int)Math.Ceiling(crop.Width * full.Width));
                _source.Height = Math.Max(16, (int)Math.Ceiling(crop.Height * full.Height));
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
                size = _lastSize;
                frames = _frames;
                copyMs = frames == 0 ? 0 : _copyTicks * 1000.0 / Stopwatch.Frequency / frames;
                status = _decodeStatus;
                _frames = 0;
                _copyTicks = 0;
            }
            Log.Info($"Live decode camera='{_camera}': {frames / 10.0:F1} fps at {size}, copy {copyMs:F1} ms/frame, decode={status}");
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
