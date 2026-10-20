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

        public override Guid Id => new Guid("6B0C3E52-1B8F-4E0B-9E0D-4E7C2B9A7D11");

        public override string Name => "Fisheye Dewarp tile tracker";

        public override List<EnvironmentType> TargetEnvironments => new List<EnvironmentType> { EnvironmentType.SmartClient };

        public override void Init()
        {
            ClientControl.Instance.NewImageViewerControlEvent += OnNewImageViewer;
            Log.Info("Background plugin initialized");
        }

        public override void Close()
        {
            ClientControl.Instance.NewImageViewerControlEvent -= OnNewImageViewer;
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
