using System;
using System.Collections.Generic;
using System.Linq;
using VideoOS.Platform;
using VideoOS.Platform.Background;
using VideoOS.Platform.Client;

namespace FisheyeDewarp
{
    /// <summary>Tracks every native camera tile Smart Client creates so the toolbar button can find its tile.</summary>
    internal sealed class DewarpBackgroundPlugin : BackgroundPlugin
    {
        private static readonly List<TileSession> Sessions = new List<TileSession>();

        // SPIKE: Smart Client's CPU use every 10 s while any tile is dewarped, to compare live decode with grabbing.
        private System.Threading.Timer _cpuTimer;
        private TimeSpan _lastCpu;
        private DateTime _lastCpuAt;

        public override Guid Id => new Guid("6B0C3E52-1B8F-4E0B-9E0D-4E7C2B9A7D11");

        public override string Name => "Fisheye Dewarp tile tracker";

        public override List<EnvironmentType> TargetEnvironments => new List<EnvironmentType> { EnvironmentType.SmartClient };

        public override void Init()
        {
            ClientControl.Instance.NewImageViewerControlEvent += OnNewImageViewer;
            _cpuTimer = new System.Threading.Timer(_ => LogCpu(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
            Log.Info("Background plugin initialized");
        }

        public override void Close()
        {
            ClientControl.Instance.NewImageViewerControlEvent -= OnNewImageViewer;
            _cpuTimer?.Dispose();
            lock (Sessions)
            {
                foreach (var s in Sessions) s.Close();
                Sessions.Clear();
            }
        }

        public static TileSession Find(Guid windowId, int viewItemIndex)
        {
            lock (Sessions)
            {
                return Sessions.FirstOrDefault(s => s.WindowId == windowId && s.ViewItemIndex == viewItemIndex);
            }
        }

        public static string DescribeAll()
        {
            lock (Sessions)
            {
                return string.Join("; ", Sessions.Select(s => s.Describe()));
            }
        }

        private void LogCpu()
        {
            try
            {
                TimeSpan cpu = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;
                DateTime now = DateTime.UtcNow;
                if (_lastCpuAt != default)
                {
                    int dewarped;
                    lock (Sessions) dewarped = Sessions.Count(s => s.Enabled);
                    double cores = (cpu - _lastCpu).TotalSeconds / (now - _lastCpuAt).TotalSeconds;
                    int sharp = System.Threading.Interlocked.Exchange(ref TileSession.SharpFrames, 0);
                    if (dewarped > 0)
                        Log.Info($"CPU Smart Client {cores * 100 / Environment.ProcessorCount:F1}% of all {Environment.ProcessorCount} cores ({cores:F2} cores) | dewarped tiles {dewarped}, live decoders {LiveDecoder.Running}, sharp frames {sharp / (now - _lastCpuAt).TotalSeconds:F1}/s (all tiles)");
                }
                _lastCpu = cpu;
                _lastCpuAt = now;
            }
            catch (Exception ex)
            {
                Log.Error("CPU sample failed", ex);
            }
        }

        private void OnNewImageViewer(ImageViewerAddOn addOn)
        {
            if (addOn.ImageViewerType != ImageViewerType.CameraViewItem) return;
            var session = new TileSession(addOn);
            addOn.CloseEvent += (sender, e) =>
            {
                session.Close();
                lock (Sessions) Sessions.Remove(session);
            };
            lock (Sessions) Sessions.Add(session);
        }
    }
}
