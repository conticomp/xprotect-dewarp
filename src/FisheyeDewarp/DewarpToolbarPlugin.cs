using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using VideoOS.Platform;
using VideoOS.Platform.Client;
using VideoOS.Platform.UI.Controls;
using System.Windows.Media.Imaging;

namespace FisheyeDewarp
{
    internal enum ToolbarKind
    {
        Dewarp,
        Snapshot,
        Export,
    }

    /// <summary>Camera tile toolbar buttons, in Live and Playback: the Dewarp toggle and the dewarped Snapshot action.</summary>
    internal sealed class DewarpToolbarPlugin : ViewItemToolbarPlugin
    {
        private readonly ToolbarKind _kind;

        public DewarpToolbarPlugin(ToolbarKind kind) => _kind = kind;

        public override Guid Id =>
            _kind == ToolbarKind.Dewarp ? new Guid("2F5D8C1A-7E43-4C8B-9B6F-0A1D3E5C7B92")
            : _kind == ToolbarKind.Snapshot ? new Guid("A4D7E2B9-6C31-4F58-9E0A-2B8C5D1F7E63")
            : new Guid("5C1E8B3D-2A47-4F96-8D0B-7E3A9C6F1D24");

        public override string Name => _kind.ToString();

        public override ToolbarPluginType ToolbarPluginType =>
            _kind == ToolbarKind.Dewarp ? ToolbarPluginType.Toggle : ToolbarPluginType.Action;

        public override ToolbarPluginOverflowMode ToolbarPluginOverflowMode => ToolbarPluginOverflowMode.NeverInOverflow;

        public override void Init()
        {
            ViewItemToolbarPlaceDefinition.ViewItemIds = new List<Guid> { ViewAndLayoutItem.CameraBuiltinId };
            ViewItemToolbarPlaceDefinition.WorkSpaceIds = new List<Guid> { ClientControl.LiveBuildInWorkSpaceId, ClientControl.PlaybackBuildInWorkSpaceId };
            ViewItemToolbarPlaceDefinition.WorkSpaceStates = new List<WorkSpaceState> { WorkSpaceState.Normal };
        }

        public override void Close()
        {
        }

        public override ViewItemToolbarPluginInstance GenerateViewItemToolbarPluginInstance() => new DewarpToolbarPluginInstance(_kind);
    }

    internal sealed class DewarpToolbarPluginInstance : ViewItemToolbarPluginInstance
    {
        private static VideoOSIconSourceBase _dewarpIcon;
        private static VideoOSIconSourceBase _snapshotIcon;
        private static VideoOSIconSourceBase _exportIcon;
        private readonly ToolbarKind _kind;
        private Guid _windowId;
        private int _index = -1;

        public DewarpToolbarPluginInstance(ToolbarKind kind) => _kind = kind;

        public override void Init(Item viewItemInstance, Item window)
        {
            _windowId = window?.FQID?.ObjectId ?? Guid.Empty;
            if (viewItemInstance?.Properties != null && viewItemInstance.Properties.TryGetValue("Index", out string raw))
                int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out _index);
            if (_kind == ToolbarKind.Dewarp)
            {
                Title = "Dewarp";
                Tooltip = "Dewarp this fisheye camera. Drag to look around, scroll to zoom, double-click to reset.";
                IconSource = _dewarpIcon ?? (_dewarpIcon = CreateIcon(DewarpIcon()));
            }
            else if (_kind == ToolbarKind.Export)
            {
                Title = "Export dewarped (spike)";
                Tooltip = "Spike: export the last 30 seconds of the dewarped view to MP4 in Videos\\Dewarp exports.";
                IconSource = _exportIcon ?? (_exportIcon = CreateIcon(ExportIcon()));
            }
            else
            {
                Title = "Dewarped snapshot";
                Tooltip = "Copy the dewarped view to the clipboard, at full camera resolution.";
                IconSource = _snapshotIcon ?? (_snapshotIcon = CreateIcon(SnapshotIcon()));
            }
        }

        public override void OnIsCheckedChanged()
        {
            TileSession session = FindSession();
            if (session != null && !session.SetEnabled(IsChecked) && IsChecked)
                IsChecked = false;
        }

        public override void Activate()
        {
            if (_kind == ToolbarKind.Export) FindSession()?.ExportSpike();
            else FindSession()?.Snapshot();
        }

        private TileSession FindSession()
        {
            TileSession session = DewarpBackgroundPlugin.Find(_windowId, _index);
            if (session == null)
                Log.Error($"No tile found for window={_windowId} index={_index}. Known tiles: {DewarpBackgroundPlugin.DescribeAll()}");
            return session;
        }

        public override void Close()
        {
        }

        /// <summary>A lens circle with a highlighted view window.</summary>
        private static Drawing DewarpIcon()
        {
            var white = new SolidColorBrush(Colors.White);
            var group = new DrawingGroup();
            group.Children.Add(new GeometryDrawing(null, new Pen(white, 1.5), new EllipseGeometry(new Point(8, 8), 6.5, 6.5)));
            group.Children.Add(new GeometryDrawing(white, null, Geometry.Parse("M 8,8 L 12.6,3.4 A 6.5,6.5 0 0 1 14.5,8 Z")));
            return group;
        }

        /// <summary>A camera body with a lens.</summary>
        private static Drawing SnapshotIcon()
        {
            var white = new SolidColorBrush(Colors.White);
            var group = new DrawingGroup();
            group.Children.Add(new GeometryDrawing(null, new Pen(white, 1.5), Geometry.Parse("M 1.5,5 L 5,5 L 6.5,3 L 9.5,3 L 11,5 L 14.5,5 L 14.5,13 L 1.5,13 Z")));
            group.Children.Add(new GeometryDrawing(null, new Pen(white, 1.5), new EllipseGeometry(new Point(8, 8.8), 2.6, 2.6)));
            return group;
        }

        /// <summary>A film strip with a down arrow.</summary>
        private static Drawing ExportIcon()
        {
            var white = new SolidColorBrush(Colors.White);
            var group = new DrawingGroup();
            group.Children.Add(new GeometryDrawing(null, new Pen(white, 1.5), Geometry.Parse("M 1.5,2.5 L 14.5,2.5 L 14.5,9.5 L 1.5,9.5 Z")));
            group.Children.Add(new GeometryDrawing(white, null, Geometry.Parse("M 5,11 L 11,11 L 8,15 Z")));
            return group;
        }

        /// <summary>Renders a 16-unit drawing to a 32px bitmap, so no image resources are needed.</summary>
        private static VideoOSIconSourceBase CreateIcon(Drawing drawing)
        {
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.PushTransform(new ScaleTransform(2, 2));
                dc.DrawDrawing(drawing);
            }
            var bitmap = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return new VideoOSIconBitmapSource { BitmapSource = bitmap };
        }
    }
}
