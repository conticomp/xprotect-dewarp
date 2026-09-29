using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using PixelFormat = System.Drawing.Imaging.PixelFormat;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using VideoOS.Platform;
using VideoOS.Platform.Data;
using VideoOS.Platform.Log;
using VideoOS.Platform.Util;

namespace FisheyeDewarp
{
    /// <summary>Everything an export needs, captured on the UI thread.</summary>
    internal sealed class ExportRequest
    {
        public Item Camera { get; set; }
        public DateTime StartUtc { get; set; }
        public DateTime EndUtc { get; set; }
        public int SourceWidth { get; set; }
        public int SourceHeight { get; set; }
        public double[,] Rotation { get; set; }
        public double TanX { get; set; }
        public double TanY { get; set; }
        public double LensHalfFov { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public string Path { get; set; }
        public bool BurnIn { get; set; } = true;
    }

    /// <summary>
    /// Recorded fisheye video -> fixed dewarped view -> H.264 MP4. Frames come from Milestone's decoder
    /// (BitmapVideoSource) at full resolution, go through a precomputed DewarpMap, get an optional camera
    /// name and timestamp burned in, and are encoded by Media Foundation with their real recording times.
    /// </summary>
    internal static class DewarpExporter
    {
        private const string AuditApp = "FisheyeDewarp";
        private const string AuditComponent = "Export";
        private const string AuditMessageId = "DewarpedExport";
        private static bool _auditRegistered;

        /// <summary>Runs the export on a dedicated thread; BitmapVideoSource must be used from a single thread.</summary>
        public static void Start(ExportRequest request, Action<string> progress, Action<string> done)
        {
            var thread = new Thread(() =>
            {
                string result;
                try
                {
                    result = Run(request, progress);
                }
                catch (Exception ex)
                {
                    Log.Error($"Export failed: {Describe(request)}", ex);
                    result = ex is MediaFoundationUnavailableException ? ex.Message : "Export failed: " + ex.Message;
                }
                done(result);
            })
            {
                IsBackground = true,
                Name = "Dewarp export",
                Priority = ThreadPriority.BelowNormal,
            };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }

        private static string Run(ExportRequest r, Action<string> progress)
        {
            Log.Info($"Export start {Describe(r)}");
            ProbePermissions(r.Camera);

            var total = Stopwatch.StartNew();
            var decode = new Stopwatch();
            var dewarp = new Stopwatch();
            var overlay = new Stopwatch();
            var encode = new Stopwatch();
            int frames = 0;
            string hwStatus = null;
            DateTime first = DateTime.MinValue, last = DateTime.MinValue;

            Directory.CreateDirectory(Path.GetDirectoryName(r.Path));
            var source = new BitmapVideoSource(r.Camera);
            try
            {
                source.Init(r.SourceWidth, r.SourceHeight, BitmapFormat.BGR32);
                Log.Info($"Export source init {r.SourceWidth}x{r.SourceHeight} -> reports {source.Width}x{source.Height}");

                using (var writer = new Mp4H264Writer(r.Path, r.Width, r.Height, 30, BitrateFor(r.Width, r.Height)))
                using (var burnIn = r.BurnIn ? new BurnIn(r.Camera.Name, r.Width, r.Height) : null)
                {
                    var output = new byte[r.Width * r.Height * 4];
                    DewarpMap map = null;
                    long lastDuration = TimeSpan.FromSeconds(1 / 30.0).Ticks;
                    byte[] pending = null;   // hold one frame back so its duration is known
                    long pendingTime = 0;

                    decode.Start();
                    object next = source.Get(r.StartUtc);
                    decode.Stop();
                    while (next is BitmapData frame)
                    {
                        try
                        {
                            DateTime t = ToUtc(frame.DateTime);
                            if (t > r.EndUtc) break;
                            if (t < r.StartUtc && frames == 0 && frame.IsNextAvailable && ToUtc(frame.NextDateTime) <= r.StartUtc)
                            {
                                // Get() can land slightly before the start; skip ahead.
                            }
                            else
                            {
                                if (frames == 0)
                                {
                                    first = t;
                                    hwStatus = frame.HardwareDecodingStatus;
                                    Log.Info($"Export first frame {t:o} (kind {frame.DateTime.Kind}) plane {frame.GetPlaneWidth(0)}x{frame.GetPlaneHeight(0)} stride {frame.GetPlaneStride(0)} decode={hwStatus}");
                                }
                                if (map == null || frame.GetPlaneWidth(0) != r.SourceWidth)
                                {
                                    r.SourceWidth = frame.GetPlaneWidth(0);
                                    r.SourceHeight = frame.GetPlaneHeight(0);
                                    var build = Stopwatch.StartNew();
                                    map = DewarpMap.Build(r.Rotation, r.TanX, r.TanY, r.LensHalfFov, r.Width, r.Height,
                                        r.SourceWidth, r.SourceHeight, frame.GetPlaneStride(0));
                                    Log.Info($"Export map built in {build.ElapsedMilliseconds} ms");
                                }

                                dewarp.Start();
                                map.Apply(frame.GetPlanePointer(0), output);
                                dewarp.Stop();

                                overlay.Start();
                                burnIn?.Draw(output, t.ToLocalTime());
                                overlay.Stop();

                                long time = (t - r.StartUtc).Ticks;
                                if (time < 0) time = 0;
                                encode.Start();
                                if (pending != null)
                                {
                                    lastDuration = Math.Max(1, time - pendingTime);
                                    writer.WriteFrame(pending, pendingTime, lastDuration);
                                }
                                encode.Stop();
                                pending = pending ?? new byte[output.Length];
                                Buffer.BlockCopy(output, 0, pending, 0, output.Length);
                                pendingTime = time;
                                last = t;
                                frames++;
                                if (frames % 30 == 0)
                                    progress($"Exporting... {(t - r.StartUtc).TotalSeconds:F0} / {(r.EndUtc - r.StartUtc).TotalSeconds:F0} s");
                            }
                        }
                        finally
                        {
                            frame.Dispose();
                        }

                        decode.Start();
                        next = source.GetNext();
                        decode.Stop();
                    }

                    encode.Start();
                    if (pending != null) writer.WriteFrame(pending, pendingTime, lastDuration);
                    writer.Finish();
                    encode.Stop();
                }
            }
            finally
            {
                source.Close();
            }

            double sec = total.Elapsed.TotalSeconds;
            Log.Info($"Export done {frames} frames {first:o}..{last:o} in {sec:F1} s ({frames / Math.Max(sec, 0.001):F1} fps) " +
                     $"| per frame: decode {Per(decode, frames)} dewarp {Per(dewarp, frames)} burn-in {Per(overlay, frames)} encode {Per(encode, frames)} ms " +
                     $"| decode={hwStatus} | {r.Path} ({new FileInfo(r.Path).Length / 1024} KB)");
            Audit(r, frames);
            return frames == 0 ? "No recorded video in that time range" : $"Exported {frames} frames to {Path.GetFileName(r.Path)}";
        }

        /// <summary>Spike: log which camera permission IDs the SDK accepts, to confirm the real export action ID.</summary>
        private static void ProbePermissions(Item camera)
        {
            var results = new List<string>();
            foreach (string action in new[] { "EXPORT", "GENERIC_READ", "PLAYBACK", "VIEW_LIVE", "EXPORT_SEQUENCE" })
            {
                try
                {
                    SecurityAccess.CheckPermission(camera, action);
                    results.Add(action + "=granted");
                }
                catch (Exception ex)
                {
                    results.Add($"{action}=denied({ex.GetType().Name}: {ex.Message})");
                }
            }
            Log.Info("Export permission probe: " + string.Join("; ", results));
        }

        private static void Audit(ExportRequest r, int frames)
        {
            try
            {
                if (!LogClient.Instance.Initialized)
                {
                    Log.Info("Audit: LogClient not initialized, no server audit entry written");
                    return;
                }
                if (!_auditRegistered)
                {
#pragma warning disable CS0618 // the 25.3+ overload with applicationName is not in 25.2
                    var messages = new Dictionary<string, LogMessage>
                    {
                        [AuditMessageId] = new LogMessage
                        {
                            Id = AuditMessageId,
                            Group = Group.Audit,
                            Category = "Export",
                            Severity = Severity.Info,
                            Status = Status.StatusQuo,
                            RelatedObjectKind = Kind.Camera,
                            Message = "Dewarped MP4 export of {camera} from {start} to {end} ({frames} frames) to {file}",
                        },
                    };
                    LogClient.Instance.RegisterDictionary(new LogMessageDictionary("en-US", "1.0", AuditApp, AuditComponent, messages, "Camera"));
#pragma warning restore CS0618
                    _auditRegistered = true;
                }
                LogClient.Instance.AuditEntry(AuditApp, AuditComponent, AuditMessageId, r.Camera, new Dictionary<string, string>
                {
                    ["camera"] = r.Camera.Name,
                    ["start"] = r.StartUtc.ToString("o"),
                    ["end"] = r.EndUtc.ToString("o"),
                    ["frames"] = frames.ToString(),
                    ["file"] = r.Path,
                }, PermissionState.Granted);
                Log.Info("Audit entry written");
            }
            catch (Exception ex)
            {
                Log.Error("Audit entry failed", ex);
            }
        }

        private static int BitrateFor(int width, int height) => (int)Math.Min(20_000_000, Math.Max(2_000_000, width * (long)height * 4));

        private static DateTime ToUtc(DateTime t) => t.Kind == DateTimeKind.Local ? t.ToUniversalTime() : DateTime.SpecifyKind(t, DateTimeKind.Utc);

        private static string Per(Stopwatch sw, int frames) => frames == 0 ? "-" : (sw.Elapsed.TotalMilliseconds / frames).ToString("F1");

        private static string Describe(ExportRequest r) =>
            $"camera='{r.Camera?.Name}' {r.StartUtc:o}..{r.EndUtc:o} src={r.SourceWidth}x{r.SourceHeight} out={r.Width}x{r.Height} -> {r.Path}";

        /// <summary>Camera name and timestamp drawn into the top-left corner of each frame.</summary>
        private sealed class BurnIn : IDisposable
        {
            private readonly string _camera;
            private readonly int _width, _height;
            private readonly Font _font;
            private readonly SolidBrush _text = new SolidBrush(Color.White);
            private readonly SolidBrush _back = new SolidBrush(Color.FromArgb(160, 0, 0, 0));

            public BurnIn(string camera, int width, int height)
            {
                _camera = camera;
                _width = width;
                _height = height;
                _font = new Font("Segoe UI", Math.Max(10, height / 45f), FontStyle.Bold, GraphicsUnit.Pixel);
            }

            public void Draw(byte[] bgra, DateTime local)
            {
                GCHandle pin = GCHandle.Alloc(bgra, GCHandleType.Pinned);
                try
                {
                    using (var bitmap = new Bitmap(_width, _height, _width * 4, PixelFormat.Format32bppRgb, pin.AddrOfPinnedObject()))
                    using (Graphics g = Graphics.FromImage(bitmap))
                    {
                        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                        g.SmoothingMode = SmoothingMode.None;
                        string text = $"{_camera}   {local:yyyy-MM-dd HH:mm:ss.fff}";
                        SizeF size = g.MeasureString(text, _font);
                        float pad = _font.Size / 3;
                        g.FillRectangle(_back, 0, 0, size.Width + 2 * pad, size.Height + 2 * pad);
                        g.DrawString(text, _font, _text, pad, pad);
                    }
                }
                finally
                {
                    pin.Free();
                }
            }

            public void Dispose()
            {
                _font.Dispose();
                _text.Dispose();
                _back.Dispose();
            }
        }
    }
}
