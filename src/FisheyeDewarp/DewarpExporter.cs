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
        public static void Start(ExportRequest request, Action<string> progress, Action<string> done, bool benchmark = false)
        {
            var thread = new Thread(() =>
            {
                string result;
                try
                {
                    result = benchmark ? Benchmark(request, progress) : Run(request, progress);
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

        private const int MaxParallel = 6;
        private static readonly TimeSpan MinChunk = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Fetching recorded frames is bound by round trips to the recording server, not by CPU, so the range is
        /// split into chunks that are decoded, dewarped and encoded in parallel into temporary MP4s, then joined
        /// without re-encoding.
        /// </summary>
        private static string Run(ExportRequest r, Action<string> progress)
        {
            Log.Info($"Export start {Describe(r)}");
            ProbePermissions(r.Camera);
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
                    Start = r.StartUtc + TimeSpan.FromTicks(duration.Ticks / count * k),
                    End = k == count - 1 ? r.EndUtc : r.StartUtc + TimeSpan.FromTicks(duration.Ticks / count * (k + 1)),
                    Path = Path.Combine(tempDir, $"part{k}.mp4"),
                };
            }

            try
            {
                var threads = new List<Thread>();
                foreach (Segment segment in segments)
                {
                    Segment seg = segment;
                    var thread = new Thread(() =>
                    {
                        try
                        {
                            ExportSegment(r, seg, segments, progress);
                        }
                        catch (Exception ex)
                        {
                            seg.Error = ex;
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
                foreach (Segment seg in segments)
                    if (seg.Error != null) throw new InvalidOperationException($"Chunk {seg.Index} failed: {seg.Error.Message}", seg.Error);
                TimeSpan parallelTime = total.Elapsed;

                int frames = 0;
                var parts = new List<(string, long)>();
                foreach (Segment seg in segments)
                {
                    frames += seg.Frames;
                    if (seg.Frames > 0) parts.Add((seg.Path, (seg.FirstFrame - r.StartUtc).Ticks < 0 ? 0 : (seg.FirstFrame - r.StartUtc).Ticks));
                }
                if (frames == 0)
                {
                    Log.Info("Export found no recorded frames in the range");
                    return "No recorded video in that time range";
                }

                // Chunks from different encoders have different SPS/PPS; an MP4 track carries only the first set.
                var encoders = new HashSet<string>();
                foreach (Segment seg in segments)
                    if (seg.Frames > 0) encoders.Add(seg.Encoder);
                if (encoders.Count > 1) Log.Error("Export chunks used different encoders; the joined file may not play correctly: " + string.Join(", ", encoders));

                progress("Finishing...");
                var join = Stopwatch.StartNew();
                Mp4Joiner.Join(parts, r.Path);
                join.Stop();

                double sec = total.Elapsed.TotalSeconds;
                foreach (Segment seg in segments)
                    Log.Info($"Export chunk {seg.Index} {seg.Start:HH:mm:ss.fff}..{seg.End:HH:mm:ss.fff} {seg.Frames} frames, first frame after {seg.FirstFrameMs} ms " +
                             $"| per frame: decode {Per(seg.Decode, seg.Frames)} dewarp {Per(seg.Dewarp, seg.Frames)} burn-in {Per(seg.Overlay, seg.Frames)} encode {Per(seg.Encode, seg.Frames)} ms " +
                             $"| decode={seg.DecodeStatus} encoder={seg.Encoder}");
                Log.Info($"Export done {frames} frames in {count} chunks, {sec:F1} s ({frames / Math.Max(sec, 0.001):F1} fps; chunks {parallelTime.TotalSeconds:F1} s, join {join.ElapsedMilliseconds} ms) " +
                         $"| {r.Path} ({new FileInfo(r.Path).Length / 1024} KB)");
                Audit(r, frames);
                return $"Exported {frames} frames to {Path.GetFileName(r.Path)}";
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

        /// <summary>One chunk: its own BitmapVideoSource and encoder, frames timed from the chunk's first frame.</summary>
        private static void ExportSegment(ExportRequest r, Segment seg, Segment[] all, Action<string> progress)
        {
            var sinceStart = Stopwatch.StartNew();
            var source = new BitmapVideoSource(r.Camera);
            try
            {
                source.Init(r.SourceWidth, r.SourceHeight, BitmapFormat.BGR32);
                Mp4H264Writer writer = null;
                try
                {
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
                                DateTime t = ToUtc(frame.DateTime);
                                bool last = seg.Index == all.Length - 1;
                                if (last ? t > seg.End : t >= seg.End) break;

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
                                    if (writer == null) writer = OpenWriter(r, seg);
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
                                    if (seg.Frames % 15 == 0) progress(Progress(r, all));
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
                }
            }
            finally
            {
                source.Close();
            }
        }

        /// <summary>Prefer a GPU encoder; consumer NVIDIA cards cap concurrent sessions, so fall back to software.</summary>
        private static Mp4H264Writer OpenWriter(ExportRequest r, Segment seg)
        {
            int bitrate = BitrateFor(r.Width, r.Height);
            try
            {
                var writer = new Mp4H264Writer(seg.Path, r.Width, r.Height, 30, bitrate, hardware: true);
                seg.Encoder = "auto";
                return writer;
            }
            catch (System.Runtime.InteropServices.COMException ex)
            {
                Log.Info($"Export chunk {seg.Index}: hardware encoder unavailable ({ex.Message}, 0x{ex.ErrorCode:X8}), using software");
                seg.Encoder = "software";
                return new Mp4H264Writer(seg.Path, r.Width, r.Height, 30, bitrate, hardware: false);
            }
        }

        private static string Progress(ExportRequest r, Segment[] all)
        {
            long done = 0;
            foreach (Segment seg in all)
                if (seg.Frames > 0) done += (seg.Done - seg.Start).Ticks;
            double pct = 100.0 * done / Math.Max(1, (r.EndUtc - r.StartUtc).Ticks);
            return $"Exporting... {Math.Min(99, pct):F0}%";
        }

        private sealed class Segment
        {
            public int Index;
            public DateTime Start, End, FirstFrame, Done;
            public string Path;
            public int Frames;
            public long FirstFrameMs;
            public string DecodeStatus, Encoder;
            public Exception Error;
            public readonly Stopwatch Decode = new Stopwatch(), Dewarp = new Stopwatch(), Overlay = new Stopwatch(), Encode = new Stopwatch();
        }

        /// <summary>
        /// Spike: decode-only throughput for different pixel formats and numbers of parallel sources, to see
        /// whether decoding is CPU, colour-conversion or round-trip bound.
        /// </summary>
        private static string Benchmark(ExportRequest r, Action<string> progress)
        {
            const int framesPerSource = 40;
            Log.Info($"Benchmark start {Describe(r)} cpus={Environment.ProcessorCount}");
            var summary = new List<string>();
            var configs = new (string Name, BitmapFormat Format, int Sources)[]
            {
                ("BGR32 x1", BitmapFormat.BGR32, 1),
                ("YUV420 x1", BitmapFormat.YCbCr420_Planar, 1),
                ("BGR32 x2", BitmapFormat.BGR32, 2),
                ("BGR32 x4", BitmapFormat.BGR32, 4),
            };
            foreach (var c in configs)
            {
                progress($"Benchmark {c.Name}...");
                Process process = Process.GetCurrentProcess();
                TimeSpan cpu0 = process.TotalProcessorTime;
                var wall = Stopwatch.StartNew();
                var counts = new int[c.Sources];
                var threads = new List<Thread>();
                string decodeStatus = null;
                for (int k = 0; k < c.Sources; k++)
                {
                    int index = k;
                    DateTime from = r.StartUtc + TimeSpan.FromTicks((r.EndUtc - r.StartUtc).Ticks / c.Sources * index);
                    var t = new Thread(() =>
                    {
                        var source = new BitmapVideoSource(r.Camera);
                        try
                        {
                            source.Init(r.SourceWidth, r.SourceHeight, c.Format);
                            object next = source.Get(from);
                            while (next is BitmapData frame && counts[index] < framesPerSource)
                            {
                                decodeStatus = decodeStatus ?? frame.HardwareDecodingStatus;
                                frame.Dispose();
                                counts[index]++;
                                next = source.GetNext();
                            }
                            (next as BitmapData)?.Dispose();
                        }
                        catch (Exception ex)
                        {
                            Log.Error($"Benchmark {c.Name} source {index} failed", ex);
                        }
                        finally
                        {
                            source.Close();
                        }
                    }) { IsBackground = true };
                    t.SetApartmentState(ApartmentState.MTA);
                    threads.Add(t);
                }
                // Wall time includes each source's Init and first seek, as a real chunked export would.
                threads.ForEach(t => t.Start());
                threads.ForEach(t => t.Join());
                wall.Stop();
                process.Refresh();
                double cpuPct = (process.TotalProcessorTime - cpu0).TotalMilliseconds / wall.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100;
                int total = 0;
                foreach (int n in counts) total += n;
                string line = $"{c.Name}: {total} frames in {wall.Elapsed.TotalSeconds:F1} s = {total / wall.Elapsed.TotalSeconds:F1} fps, CPU {cpuPct:F0}% of machine, decode={decodeStatus}";
                Log.Info("Benchmark " + line);
                summary.Add(line);
            }
            return "Benchmark done, see log";
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
