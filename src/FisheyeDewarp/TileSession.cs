using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VideoOS.Platform.Client;

namespace FisheyeDewarp
{
    /// <summary>
    /// Dewarp state for one native camera tile: the shader on the tile, a transparent input layer for
    /// drag/scroll steering, and the virtual PTZ position.
    /// </summary>
    internal sealed class TileSession
    {
        private const double LensHalfFov = 91 * Math.PI / 180;   // Axis M4328-P: 182 degree lens

        private readonly ImageViewerAddOn _addOn;
        private readonly PtzView _view = new PtzView();
        private DewarpEffect _effect;
        private Border _input;
        private Guid _overlayId;
        private bool _savedDigitalZoom;
        private Point _dragLast;
        private Rect _lastArea;
        private int _imageEventsLogged;

        public TileSession(ImageViewerAddOn addOn)
        {
            _addOn = addOn;
            _addOn.ImageDisplayedEvent += OnImageDisplayed;
        }

        public bool Enabled { get; private set; }

        public ImageViewerAddOn AddOn => _addOn;

        public Guid WindowId => _addOn.WindowInformation?.WindowId ?? Guid.Empty;

        public int ViewItemIndex => _addOn.ViewItemIndex;

        public string Describe() =>
            $"camera='{_addOn.CameraName}' window={WindowId} index={ViewItemIndex} type={_addOn.ImageViewerType} live={_addOn.InLiveMode}";

        public bool SetEnabled(bool enabled)
        {
            try
            {
                return enabled ? Enable() : Disable();
            }
            catch (Exception ex)
            {
                Log.Error($"SetEnabled({enabled}) failed for {Describe()}", ex);
                return false;
            }
        }

        private bool Enable()
        {
            if (Enabled) return true;
            if (!DewarpEffect.TryCreate(out _effect, out string error))
            {
                Log.Error("Cannot enable dewarp, shader unavailable: " + error);
                return false;
            }

            _savedDigitalZoom = _addOn.DigitalZoomEnabled;
            _addOn.DigitalZoomEnabled = false;

            _input = new Border { Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), Cursor = Cursors.SizeAll };
            _input.MouseLeftButtonDown += OnMouseDown;
            _input.MouseMove += OnMouseMove;
            _input.MouseLeftButtonUp += OnMouseUp;
            _input.MouseWheel += OnMouseWheel;

            UpdateGeometry();
            _addOn.VideoEffect = _effect;
            _overlayId = _addOn.ActiveElementsOverlayAdd(new List<FrameworkElement> { _input },
                new ActiveElementsOverlayRenderParameters { Placement = OverlayPlacement.FollowImageViewport, ShowAlways = true, ZOrder = 10 });

            Enabled = true;
            _imageEventsLogged = 0;
            Log.Info($"Dewarp ON  {Describe()} {_view} | {GeometryReport()}");
            return true;
        }

        private bool Disable()
        {
            if (!Enabled) return true;
            _addOn.VideoEffect = null;
            if (_overlayId != Guid.Empty) _addOn.ActiveElementsOverlayRemove(_overlayId);
            _overlayId = Guid.Empty;
            _input = null;
            _addOn.DigitalZoomEnabled = _savedDigitalZoom;
            Enabled = false;
            Log.Info($"Dewarp OFF {Describe()}");
            return true;
        }

        public void Close()
        {
            _addOn.ImageDisplayedEvent -= OnImageDisplayed;
            if (Enabled) Disable();
        }

        /// <summary>Where the video sits inside the tile, and how large the output is, feed the shader constants.</summary>
        private void UpdateGeometry()
        {
            if (_effect == null) return;

            Rect area = NormalizedArea();
            _lastArea = _addOn.VideoEffectArea;
            _effect.SetArea(area);
            _effect.SetOutputRect(area);
            _effect.SetCircle(0.5, 0.5, 0.5, 0.5);

            Size paint = _addOn.PaintSizeWpf;
            double aspect = paint.Height > 0 ? paint.Width / paint.Height : 1;
            double tanX = Math.Tan(_view.Fov / 2);
            _effect.SetLens(tanX, tanX / aspect, LensHalfFov, LensProjection.Stereographic);
            _effect.SetRotation(_view.Rotation());

            if (_input != null && paint.Width > 0)
            {
                _input.Width = paint.Width;
                _input.Height = paint.Height;
            }
        }

        /// <summary>
        /// VideoEffectArea's units are not documented. Treat values within 0..1 as already normalized,
        /// otherwise normalize against the control size. The log records the raw values so this can be confirmed.
        /// </summary>
        private Rect NormalizedArea()
        {
            Rect a = _addOn.VideoEffectArea;
            if (a.IsEmpty || a.Width <= 0 || a.Height <= 0) return new Rect(0, 0, 1, 1);
            if (a.Right <= 1.0001 && a.Bottom <= 1.0001) return a;
            Size size = _addOn.SizeWpf;
            if (size.Width <= 0 || size.Height <= 0) return new Rect(0, 0, 1, 1);
            return new Rect(a.X / size.Width, a.Y / size.Height, a.Width / size.Width, a.Height / size.Height);
        }

        private string GeometryReport() =>
            $"VideoEffectArea={_addOn.VideoEffectArea} SizeWpf={_addOn.SizeWpf} PaintSizeWpf={_addOn.PaintSizeWpf} ImageSizeWpf={_addOn.ImageSizeWpf} normalizedArea={NormalizedArea()}";

        private void OnImageDisplayed(object sender, ImageDisplayedEventArgs e)
        {
            if (!Enabled) return;
            if (_imageEventsLogged < 3)
            {
                _imageEventsLogged++;
                Log.Info($"ImageDisplayed {Describe()} original={e.OriginalImageSize} window={e.WindowSize} area={e.VideoEffectArea} flipH={e.IsFlippedHorizontally} flipV={e.IsFlippedVertically}");
            }
            if (e.VideoEffectArea != _lastArea)
            {
                _lastArea = e.VideoEffectArea;
                _input?.Dispatcher.BeginInvoke(new Action(UpdateGeometry));
            }
        }

        private void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                _view.Reset();
                UpdateGeometry();
                Log.Info($"Reset {_view}");
            }
            else
            {
                _dragLast = e.GetPosition(_input);
                _input.CaptureMouse();
            }
            e.Handled = true;
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (_input == null || !_input.IsMouseCaptured) return;
            Point p = e.GetPosition(_input);
            double w = Math.Max(_input.ActualWidth, 1), h = Math.Max(_input.ActualHeight, 1);
            _view.Drag((p.X - _dragLast.X) / w, (p.Y - _dragLast.Y) / h, w / h);
            _dragLast = p;
            UpdateGeometry();
            e.Handled = true;
        }

        private void OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_input != null && _input.IsMouseCaptured)
            {
                _input.ReleaseMouseCapture();
                Log.Info($"Drag end {_view}");
            }
            e.Handled = true;
        }

        private void OnMouseWheel(object sender, MouseWheelEventArgs e)
        {
            _view.Zoom(e.Delta);
            UpdateGeometry();
            e.Handled = true;
        }
    }
}
