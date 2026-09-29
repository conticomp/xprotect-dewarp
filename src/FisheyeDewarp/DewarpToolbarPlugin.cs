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
    /// <summary>"Dewarp" toggle on every camera tile's toolbar, in Live and Playback.</summary>
    internal enum ToolbarKind
    {
        Dewarp,
        Sharp,
    }

    internal sealed class DewarpToolbarPlugin : ViewItemToolbarPlugin
    {
        private readonly ToolbarKind _kind;

        public DewarpToolbarPlugin(ToolbarKind kind) => _kind = kind;

        public override Guid Id => _kind == ToolbarKind.Dewarp
            ? new Guid("2F5D8C1A-7E43-4C8B-9B6F-0A1D3E5C7B92")
            : new Guid("7C1E9A3B-5D2F-4E8A-A6B4-3F0C9D8E2A17");

        public override string Name => _kind.ToString();

        public override ToolbarPluginType ToolbarPluginType => ToolbarPluginType.Toggle;

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
        private static VideoOSIconSourceBase _icon;
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
                IconSource = _icon ?? (_icon = CreateIcon());
            }
            else
            {
                Title = "Sharp";
                Tooltip = "Prototype: use Smart Client's digital zoom so the dewarped view samples more detail.";
            }
        }

        public override void OnIsCheckedChanged()
        {
            TileSession session = DewarpBackgroundPlugin.Find(_windowId, _index);
            if (session == null)
            {
                Log.Error($"No tile found for window={_windowId} index={_index}. Known tiles: {DewarpBackgroundPlugin.DescribeAll()}");
                return;
            }
            if (_kind == ToolbarKind.Sharp)
                session.SetSharp(IsChecked);
            else if (!session.SetEnabled(IsChecked) && IsChecked)
                IsChecked = false;
        }

        public override void Close()
        {
        }

        /// <summary>A lens circle with a highlighted view window, drawn in code so no image resources are needed.</summary>
        private static VideoOSIconSourceBase CreateIcon()
        {
            var white = new SolidColorBrush(Colors.White);
            var group = new DrawingGroup();
            group.Children.Add(new GeometryDrawing(null, new Pen(white, 1.5), new EllipseGeometry(new Point(8, 8), 6.5, 6.5)));
            group.Children.Add(new GeometryDrawing(white, null, Geometry.Parse("M 8,8 L 12.6,3.4 A 6.5,6.5 0 0 1 14.5,8 Z")));
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.PushTransform(new ScaleTransform(2, 2));
                dc.DrawDrawing(group);
            }
            var bitmap = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return new VideoOSIconBitmapSource { BitmapSource = bitmap };
        }
    }
}
