using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using VideoOS.Platform;
using VideoOS.Platform.Data;
using VideoOS.Platform.Log;
using VideoOS.Platform.Util;
using PixelFormat = System.Drawing.Imaging.PixelFormat;

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

    internal sealed class ExportResult
    {
        public bool Succeeded { get; set; }
        public bool Cancelled { get; set; }
        public string Message { get; set; }
    }

    /// <summary>
    /// Recorded fisheye video -> fixed dewarped view -> H.264 MP4. Frames come from Milestone's decoder
    /// (BitmapVideoSource) at full resolution, go through a precomputed DewarpMap, get an optional camera
    /// name and timestamp burned in, and are encoded by Media Foundation with their real recording times.
    /// </summary>
    internal static class DewarpExporter
    {
        public static readonly TimeSpan MaxDuration = TimeSpan.FromHours(4);

        // Fetching recorded frames is bound by round trips to the recording server, not by CPU (about 2% CPU on
        // 22 cores, near-linear scaling with parallel sources), so the range is split into chunks that are
        // decoded, dewarped and encoded in parallel into temporary MP4s, then joined without re-encoding.
        // Each source costs about 2.5 s to open and seek, hence the minimum chunk length.
        private const int MaxParallel = 6;
        private static readonly TimeSpan MinChunk = TimeSpan.FromSeconds(10);

        private const string ExportAction = "EXPORT";
        private const string AuditApp = "FisheyeDewarp";
        private const string AuditComponent = "Export";
        private const string AuditExported = "DewarpedExport";
        private const string AuditDenied = "DewarpedExportDenied";
        private static readonly object AuditGate = new object();
        private static bool _auditRegistered;

        /// <summary>
        /// Whether the current user may export this camera. A denial is written to the audit log, since it is
        /// an attempt to export.
        /// </summary>
        public static bool CheckPermission(Item camera)
        {
            try
            {
                SecurityAccess.CheckPermission(camera, ExportAction);
                return true;
            }
            catch (Exception ex)
            {
                Log.Info($"Export denied for camera='{camera?.Name}': {ex.Message}");
                Audit(AuditDenied, camera, PermissionState.Denied, new Dictionary<string, string> { ["camera"] = camera?.Name ?? "" });
                return false;
            }
        }

        /// <summary>
        /// Runs the export on background threads and reports progress (0..1) and the result from them.
        /// BitmapVideoSource must be used from a single thread, so each chunk gets its own.
        /// </summary>
        public static void Start(ExportRequest request, Action<double> progress, Action<ExportResult> done, CancellationToken cancel)
        {
            var thread = new Thread(() =>
            {
                ExportResult result;
                try
                {
                    result = Run(request, progress, cancel);
                }
                catch (OperationCanceledException)
                {
                    Log.Info($"Export cancelled: {Describe(request)}");
                    result = new ExportResult { Cancelled = true, Message = "Export cancelled." };
                }
                catch (MediaFoundationUnavailableException ex)
                {
                    Log.Error($"Export failed: {Describe(request)}", ex);
                    result = new ExportResult { Message = ex.Message };
                }
                catch (Exception ex)
                {
                    Log.Error($"Export failed: {Describe(request)}", ex);
                    result = new ExportResult { Message = "Export failed: " + (ex.InnerException ?? ex).Message };
                }
                done(result);
            })
            {
                IsBackground = true,
                Name = "Dewarp export",
            };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }

        private static ExportResult Run(ExportRequest r, Action<double> progress, CancellationToken cancel)
        {
            Log.Info($"Export start {Describe(r)}");
            if (!CheckPermission(r.Camera))
                return new ExportResult { Message = "You do not have permission to export video from this camera." };
            var total = Stopwatch.StartNew();

            TimeSpan duration = r.EndUtc - r.StartUtc;
            int count = (int)Math.Max(1, Math.Min(MaxParallel, duration.Ticks / MinChunk.Ticks));
            string tempDir = Path.Combine(Path.GetTempPath(), "FisheyeDewarp", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            Directory.CreateDirectory(Path.GetDirectoryName(r.Path));

            var segments = new Segment[count];
            for (int k = 0; k < count; k++)
            {
                segments[k] = new Segment
                {
                    Index = k,
                    Last = k == count - 1,
                    Start = r.StartUtc + TimeSpan.FromTicks(duration.Ticks / count * k),
                    End = k == count - 1 ? r.EndUtc : r.StartUtc + TimeSpan.FromTicks(duration.Ticks / count * (k + 1)),
                    Path = Path.Combine(tempDir, $"part{k}.mp4"),
                };
            }

            try
            {
                // Chunks are joined into one H.264 stream, so they must all come from the same encoder (mixing a
                // GPU and the software encoder gives a corrupt file). Try the GPU for all of them; if it fails
                // anywhere, for example a consumer NVIDIA card refusing another session, redo them all in software.
                string encoder = "gpu";
                if (!RunChunks(r, segments, hardware: true, progress, cancel, out Exception gpuError))
                {
                    Log.Info($"Export: GPU encoder failed ({gpuError.Message}); redoing all chunks with the software encoder");
                    encoder = "software";
                    foreach (Segment seg in segments) seg.Reset();
                    if (!RunChunks(r, segments, hardware: false, progress, cancel, out Exception error))
                        throw new InvalidOperationException(error.Message, error);
                }
                TimeSpan chunkTime = total.Elapsed;

                int frames = 0;
                var parts = new List<(string, long)>();
                foreach (Segment seg in segments)
                {
                    frames += seg.Frames;
                    if (seg.Frames > 0) parts.Add((seg.Path, Math.Max(0, (seg.FirstFrame - r.StartUtc).Ticks)));
                }
                if (frames == 0)
                {
                    Log.Info("Export found no recorded frames in the range");
                    return new ExportResult { Message = "There is no recorded video in that time range." };
                }

                var join = Stopwatch.StartNew();
                try
                {
                    Mp4Joiner.Join(parts, r.Path);
                }
                catch
                {
                    TryDelete(r.Path);   // a half-written MP4 is unplayable
                    throw;
                }
                join.Stop();

                double sec = total.Elapsed.TotalSeconds;
                foreach (Segment seg in segments)
                    Log.Info($"Export chunk {seg.Index} {seg.Start:HH:mm:ss.fff}..{seg.End:HH:mm:ss.fff} {seg.Frames} frames, first frame after {seg.FirstFrameMs} ms " +
                             $"| per frame: decode {Per(seg.Decode, seg.Frames)} dewarp {Per(seg.Dewarp, seg.Frames)} burn-in {Per(seg.Overlay, seg.Frames)} encode {Per(seg.Encode, seg.Frames)} ms " +
                             $"| decode={seg.DecodeStatus} encoder={encoder}");
                Log.Info($"Export done {frames} frames in {count} chunks, {sec:F1} s ({frames / Math.Max(sec, 0.001):F1} fps; chunks {chunkTime.TotalSeconds:F1} s, join {join.ElapsedMilliseconds} ms) " +
                         $"| {r.Path} ({new FileInfo(r.Path).Length / 1024} KB)");

                Audit(AuditExported, r.Camera, PermissionState.Granted, new Dictionary<string, string>
                {
                    ["camera"] = r.Camera.Name,
                    ["start"] = r.StartUtc.ToString("o"),
                    ["end"] = r.EndUtc.ToString("o"),
                    ["file"] = r.Path,
                });
                return new ExportResult { Succeeded = true, Message = $"Exported {frames} frames to {r.Path}" };
            }
            finally
            {
                try
                {
                    Directory.Delete(tempDir, true);
                }
                catch (Exception ex)
                {
                    Log.Error("Could not delete export temp folder " + tempDir, ex);
                }
            }
        }

        /// <summary>
        /// Runs every chunk on its own thread. A failed chunk cancels the others rather than letting them run to
        /// the end for nothing. False (with the first error) if a chunk failed; throws if the user cancelled.
        /// </summary>
        private static bool RunChunks(ExportRequest r, Segment[] segments, bool hardware, Action<double> progress, CancellationToken cancel, out Exception error)
        {
            using (var chunks = CancellationTokenSource.CreateLinkedTokenSource(cancel))
            {
                var threads = new List<Thread>();
                foreach (Segment segment in segments)
                {
                    Segment seg = segment;
                    var thread = new Thread(() =>
                    {
                        try
                        {
                            ExportSegment(r, seg, hardware, () => progress(Progress(r, segments)), chunks.Token);
                        }
                        catch (Exception ex)
                        {
                            seg.Error = ex;
                            chunks.Cancel();
                        }
                    })
                    {
                        IsBackground = true,
                        Name = "Dewarp export " + seg.Index,
                        Priority = ThreadPriority.BelowNormal,
                    };
                    thread.SetApartmentState(ApartmentState.MTA);
                    threads.Add(thread);
                }
                threads.ForEach(t => t.Start());
                threads.ForEach(t => t.Join());
            }
            cancel.ThrowIfCancellationRequested();
            error = null;
            foreach (Segment seg in segments)
                if (seg.Error != null && !(seg.Error is OperationCanceledException))
                {
                    error = seg.Error;
                    return false;
                }
            return true;
        }

        /// <summary>One chunk: its own BitmapVideoSource and encoder, frames timed from the chunk's first frame.</summary>
        private static void ExportSegment(ExportRequest r, Segment seg, bool hardware, Action reportProgress, CancellationToken cancel)
        {
            var sinceStart = Stopwatch.StartNew();
            var source = new BitmapVideoSource(r.Camera);
            Mp4H264Writer writer = null;
            try
            {
                source.Init(r.SourceWidth, r.SourceHeight, BitmapFormat.BGR32);
                using (var burnIn = r.BurnIn ? new BurnIn(r.Camera.Name, r.Width, r.Height) : null)
                {
                    var output = new byte[r.Width * r.Height * 4];
                    byte[] pending = null;   // hold one frame back so its duration is known
                    long pendingTime = 0, lastDuration = TimeSpan.FromSeconds(1 / 30.0).Ticks;
                    DewarpMap map = null;
                    int mapWidth = 0;

                    seg.Decode.Start();
                    object next = source.Get(seg.Start);
                    seg.Decode.Stop();
                    while (next is BitmapData frame)
                    {
                        try
                        {
                            cancel.ThrowIfCancellationRequested();
                            DateTime t = ToUtc(frame.DateTime);
                            if (seg.Last ? t > seg.End : t >= seg.End)
                            {
                                next = null;   // disposed by the finally below
                                break;
                            }

                            // Get() can land before the chunk start. The first chunk keeps the frame that was on
                            // screen at the start time; later chunks leave it to the chunk before them.
                            bool beforeStart = t < seg.Start;
                            bool keep = !beforeStart || (seg.Index == 0 && seg.Frames == 0 && !(frame.IsNextAvailable && ToUtc(frame.NextDateTime) <= seg.Start));
                            if (keep)
                            {
                                if (seg.Frames == 0)
                                {
                                    seg.FirstFrame = beforeStart ? seg.Start : t;
                                    seg.FirstFrameMs = sinceStart.ElapsedMilliseconds;
                                    seg.DecodeStatus = frame.HardwareDecodingStatus;
                                }
                                int planeWidth = frame.GetPlaneWidth(0);
                                if (map == null || planeWidth != mapWidth)
                                {
                                    mapWidth = planeWidth;
                                    map = DewarpMap.Build(r.Rotation, r.TanX, r.TanY, r.LensHalfFov, r.Width, r.Height,
                                        planeWidth, frame.GetPlaneHeight(0), frame.GetPlaneStride(0));
                                }

                                seg.Dewarp.Start();
                                map.Apply(frame.GetPlanePointer(0), output);
                                seg.Dewarp.Stop();

                                seg.Overlay.Start();
                                burnIn?.Draw(output, t.ToLocalTime());
                                seg.Overlay.Stop();

                                long time = Math.Max(0, (t - seg.FirstFrame).Ticks);
                                seg.Encode.Start();
                                if (writer == null) writer = new Mp4H264Writer(seg.Path, r.Width, r.Height, 30, BitrateFor(r.Width, r.Height), hardware);
                                if (pending != null)
                                {
                                    lastDuration = Math.Max(1, time - pendingTime);
                                    writer.WriteFrame(pending, pendingTime, lastDuration);
                                }
                                seg.Encode.Stop();
                                pending = pending ?? new byte[output.Length];
                                Buffer.BlockCopy(output, 0, pending, 0, output.Length);
                                pendingTime = time;
                                seg.Frames++;
                                seg.Done = t;
                                if (seg.Frames % 10 == 0) reportProgress();
                            }
                        }
                        finally
                        {
                            frame.Dispose();
                        }

                        seg.Decode.Start();
                        next = source.GetNext();
                        seg.Decode.Stop();
                    }
                    (next as BitmapData)?.Dispose();

                    if (pending != null)
                    {
                        seg.Encode.Start();
                        writer.WriteFrame(pending, pendingTime, lastDuration);
                        writer.Finish();
                        seg.Encode.Stop();
                    }
                }
            }
            finally
            {
                writer?.Dispose();
                source.Close();
            }
        }

        private static double Progress(ExportRequest r, Segment[] all)
        {
            long done = 0;
            foreach (Segment seg in all)
                if (seg.Frames > 0) done += Math.Max(0, (seg.Done - seg.Start).Ticks);
            return Math.Min(0.99, (double)done / Math.Max(1, (r.EndUtc - r.StartUtc).Ticks));
        }

        private static void Audit(string messageId, Item camera, string permission, Dictionary<string, string> values)
        {
            try
            {
                if (!LogClient.Instance.Initialized)
                {
                    Log.Info("Audit: LogClient not initialized, no server audit entry written");
                    return;
                }
                lock (AuditGate)
                {
                    if (!_auditRegistered)
                    {
                        var messages = new Dictionary<string, LogMessage>
                        {
                            [AuditExported] = AuditMessage(AuditExported, "Dewarped MP4 export of {camera} from {start} to {end} to {file}"),
                            [AuditDenied] = AuditMessage(AuditDenied, "Dewarped MP4 export of {camera} denied: no export permission"),
                        };
#pragma warning disable CS0618 // the 25.3+ overload with applicationName is not in 25.2
                        LogClient.Instance.RegisterDictionary(new LogMessageDictionary("en-US", "1.0", AuditApp, AuditComponent, messages, "Camera"));
#pragma warning restore CS0618
                        _auditRegistered = true;
                    }
                }
                LogClient.Instance.AuditEntry(AuditApp, AuditComponent, messageId, camera, values, permission);
                Log.Info($"Audit entry written: {messageId}");
            }
            catch (Exception ex)
            {
                Log.Error("Audit entry failed", ex);
            }
        }

        private static LogMessage AuditMessage(string id, string text) => new LogMessage
        {
            Id = id,
            Group = Group.Audit,
            Category = "Export",
            Severity = Severity.Info,
            Status = Status.StatusQuo,
            RelatedObjectKind = Kind.Camera,
            Message = text,
        };

        private static int BitrateFor(int width, int height) => (int)Math.Min(20_000_000, Math.Max(2_000_000, width * (long)height * 4));

        private static DateTime ToUtc(DateTime t) => t.Kind == DateTimeKind.Local ? t.ToUniversalTime() : DateTime.SpecifyKind(t, DateTimeKind.Utc);

        private static string Per(Stopwatch sw, int frames) => frames == 0 ? "-" : (sw.Elapsed.TotalMilliseconds / frames).ToString("F1");

        private static string Describe(ExportRequest r) =>
            $"camera='{r.Camera?.Name}' {r.StartUtc:o}..{r.EndUtc:o} src={r.SourceWidth}x{r.SourceHeight} out={r.Width}x{r.Height} burnIn={r.BurnIn} -> {r.Path}";

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                Log.Error("Could not delete incomplete export " + path, ex);
            }
        }

        private sealed class Segment
        {
            public int Index;
            public bool Last;
            public DateTime Start, End, FirstFrame, Done;
            public string Path;
            public int Frames;
            public long FirstFrameMs;
            public string DecodeStatus;
            public Exception Error;
            public readonly Stopwatch Decode = new Stopwatch(), Dewarp = new Stopwatch(), Overlay = new Stopwatch(), Encode = new Stopwatch();

            /// <summary>Forget a failed attempt before redoing the chunk.</summary>
            public void Reset()
            {
                Frames = 0;
                FirstFrameMs = 0;
                FirstFrame = Done = default;
                DecodeStatus = null;
                Error = null;
                Decode.Reset();
                Dewarp.Reset();
                Overlay.Reset();
                Encode.Reset();
            }
        }

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
