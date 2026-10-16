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

        private static int _running;

        private readonly BitmapLiveSource _source;
        private readonly Action<SourceFrame> _onFrame;
        private readonly string _camera;

        // Frames are copied into a few reused buffers: a 2992x2992 frame is 36 MB, and allocating one per frame
        // would keep the garbage collector busy. One is being filled, one is the latest frame, one may be rendering.
        private readonly int[][] _pool = new int[4][];
        private int _next;

        private readonly object _statsGate = new object();
        private int _frames;
        private long _copyTicks;
        private DateTime _lastFrameUtc;
        private string _decodeStatus;
        private Timer _statsTimer;
        private bool _disposed;
        private bool _counted;

        public static bool Allowed => !File.Exists(DisableFlag);

        public static int Running => _running;

        private LiveDecoder(Item camera, Guid streamId, Action<SourceFrame> onFrame)
        {
            _camera = camera.Name;
            _onFrame = onFrame;
            _source = new BitmapLiveSource(camera, BitmapFormat.BGR32);
            if (streamId != Guid.Empty) _source.StreamId = streamId;
            _source.LiveContentEvent += OnContent;
            _source.LiveStatusEvent += OnStatus;
        }

        /// <summary>Starts decoding; <paramref name="onFrame"/> is called on a background thread. Null on failure.</summary>
        public static LiveDecoder Start(Item camera, Guid streamId, Action<SourceFrame> onFrame)
        {
            LiveDecoder decoder = null;
            try
            {
                decoder = new LiveDecoder(camera, streamId, onFrame);
                decoder._source.Init();
                decoder._source.LiveModeStart = true;   // Width/Height stay 0: frames arrive at the camera's own size
                decoder._statsTimer = new Timer(_ => decoder.LogStats(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
                Interlocked.Increment(ref _running);
                decoder._counted = true;
                Log.Info($"Live decode START camera='{camera.Name}' stream={streamId} (running: {_running})");
                return decoder;
            }
            catch (Exception ex)
            {
                Log.Error($"Live decode could not start for camera='{camera?.Name}'", ex);
                decoder?.Dispose();
                return null;
            }
        }

        /// <summary>True while frames are arriving; until then (or if they stop) the tile falls back to grabbing.</summary>
        public bool Receiving
        {
            get
            {
                lock (_statsGate) return DateTime.UtcNow - _lastFrameUtc < TimeSpan.FromSeconds(2);
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
                    _frames++;
                    _copyTicks += ticks;
                    _lastFrameUtc = DateTime.UtcNow;
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
            if (content.Format != BitmapFormat.BGR32 && content.Format != BitmapFormat.BGRA) return null;
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
            return new SourceFrame(pixels, width, height);
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
            string status;
            lock (_statsGate)
            {
                frames = _frames;
                copyMs = frames == 0 ? 0 : _copyTicks * 1000.0 / Stopwatch.Frequency / frames;
                status = _decodeStatus;
                _frames = 0;
                _copyTicks = 0;
            }
            Log.Info($"Live decode camera='{_camera}': {frames / 10.0:F1} fps, copy {copyMs:F1} ms/frame, decode={status}");
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
