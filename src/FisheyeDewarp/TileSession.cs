using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BitmapSource = System.Windows.Media.Imaging.BitmapSource;
using System.Windows.Threading;
using VideoOS.Platform.Client;
using VideoOS.Platform.Messaging;

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
        private TextBlock _toast;
        private DispatcherTimer _toastTimer;
        private bool _snapshotBusy;
        private Size _lastPaint;

        // Sharp-when-still: after the view stops moving, the tile shows a full-resolution render of the
        // current frame on top of the (softer) live shader view, refreshed as new frames arrive.
        private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(250);
        private Image _still;
        private DispatcherTimer _settleTimer;
        private bool _settled;
        private bool _stillBusy;
        private bool _stillPending;
        private int _viewVersion;
        private int _stillLogs;

        public TileSession(ImageViewerAddOn addOn)
        {
            _addOn = addOn;
            _addOn.ImageDisplayedEvent += OnImageDisplayed;
            _addOn.UserControlSizeOrLocationChangedEvent += OnSizeOrLocationChanged;
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
            _addOn.DigitalZoomEnabled = false;   // Smart Client's own zoom would fight the drag/scroll steering

            _toast = new TextBlock
            {
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromArgb(0xC0, 0x20, 0x20, 0x20)),
                Padding = new Thickness(12, 6, 12, 6),
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false,
            };
            _input = new Border { Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), Cursor = Cursors.SizeAll, Child = _toast };
            _input.MouseLeftButtonDown += OnMouseDown;
            _input.MouseMove += OnMouseMove;
            _input.MouseLeftButtonUp += OnMouseUp;
            _input.MouseWheel += OnMouseWheel;
            _still = new Image { Stretch = Stretch.Fill, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
            var layers = new Grid();
            layers.Children.Add(_still);
            layers.Children.Add(_input);
            _settleTimer = new DispatcherTimer { Interval = SettleDelay };
            _settleTimer.Tick += OnSettled;

            UpdateGeometry();
            _addOn.VideoEffect = _effect;
            _overlayId = _addOn.ActiveElementsOverlayAdd(new List<FrameworkElement> { layers },
                new ActiveElementsOverlayRenderParameters { Placement = OverlayPlacement.FollowImageViewport, ShowAlways = true, ZOrder = 10 });

            Enabled = true;
            _imageEventsLogged = 0;
            _stillLogs = 0;
            ViewChanged();
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
            _toast = null;
            _toastTimer?.Stop();
            _settleTimer?.Stop();
            _settleTimer = null;
            _still = null;
            _settled = false;
            _addOn.DigitalZoomEnabled = _savedDigitalZoom;
            Enabled = false;
            Log.Info($"Dewarp OFF {Describe()}");
            return true;
        }

        public void Close()
        {
            _addOn.ImageDisplayedEvent -= OnImageDisplayed;
            _addOn.UserControlSizeOrLocationChangedEvent -= OnSizeOrLocationChanged;
            if (Enabled) Disable();
        }

        /// <summary>
        /// Copies the current dewarped view to the clipboard, rendered from the full-resolution frame
        /// rather than the on-screen tile, so it is sharper than what the operator sees.
        /// </summary>
        public void Snapshot()
        {
            if (!Enabled)
            {
                Log.Info($"Snapshot ignored, dewarp is off: {Describe()}");
                return;
            }
            if (_snapshotBusy) return;
            try
            {
                BitmapSource frame = _addOn.GetCurrentDisplayedImageAsImageSource(false) as BitmapSource;
                if (frame == null)
                {
                    ShowToast("No image to copy yet");
                    return;
                }

                RenderJob job = PrepareRender(frame, fitToScreen: false);
                _snapshotBusy = true;
                ShowToast("Copying...", keep: true);
                string camera = _addOn.CameraName;
                string view = _view.ToString();
                Task.Run(job.Render)
                    .ContinueWith(t =>
                    {
                        _snapshotBusy = false;
                        if (t.IsFaulted)
                        {
                            Log.Error($"Snapshot failed for {Describe()}", t.Exception);
                            ShowToast("Snapshot failed");
                            return;
                        }
                        Clipboard.SetImage(t.Result);
                        Log.Info($"Snapshot {job} camera='{camera}' {view}");
                        ShowToast("Dewarped snapshot copied to clipboard");
                    }, TaskScheduler.FromCurrentSynchronizationContext());
            }
            catch (Exception ex)
            {
                _snapshotBusy = false;
                Log.Error($"Snapshot failed for {Describe()}", ex);
                ShowToast("Snapshot failed");
            }
        }

        /// <summary>
        /// Everything a CPU render needs, captured on the UI thread. Snapshots match the camera's pixel density;
        /// the on-screen still matches the tile's device pixels.
        /// </summary>
        private RenderJob PrepareRender(BitmapSource frame, bool fitToScreen)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Size paint = _addOn.PaintSizeWpf;
            double aspect = paint.Width > 0 && paint.Height > 0 ? paint.Width / paint.Height : 16.0 / 9;
            double tanX = Math.Tan(_view.Fov / 2), tanY = tanX / aspect;
            double[,] m = _view.Rotation();
            SourceFrame source = SnapshotRenderer.Capture(frame);

            int width;
            if (fitToScreen)
            {
                double scale = _input != null ? VisualTreeHelper.GetDpi(_input).DpiScaleX : 1;
                width = (int)Math.Max(64, Math.Round(paint.Width * scale));
            }
            else
            {
                // Match the fisheye's own pixel density at the centre of the view (stereographic lens), so the
                // snapshot neither throws detail away nor pretends to more than the camera captured.
                double theta = Math.Acos(Math.Max(-1, Math.Min(1, m[2, 2])));
                double halfCos = Math.Cos(theta / 2);
                double pixelsPerRadian = source.Width / 2.0 / (2 * Math.Tan(LensHalfFov / 2) * halfCos * halfCos);
                width = (int)Math.Max(1280, Math.Min(3840, pixelsPerRadian * 2 * tanX));
            }
            int height = Math.Max(1, (int)Math.Round(width / aspect));
            return new RenderJob(source, m, tanX, tanY, LensHalfFov, width, height, sw.ElapsedMilliseconds);
        }

        /// <summary>The view moved: show the live shader view, and plan a sharp render once it settles.</summary>
        private void ViewChanged()
        {
            _viewVersion++;
            _settled = false;
            if (_still != null) _still.Visibility = Visibility.Collapsed;
            if (_settleTimer == null) return;
            _settleTimer.Stop();
            _settleTimer.Start();
        }

        private void OnSettled(object sender, EventArgs e)
        {
            _settleTimer?.Stop();
            _settled = true;
            RenderStill();
        }

        /// <summary>Render the current frame at full resolution for the tile. At most one render runs at a time.</summary>
        private void RenderStill()
        {
            if (!Enabled || !_settled || _still == null) return;
            if (_stillBusy)
            {
                _stillPending = true;
                return;
            }
            try
            {
                BitmapSource frame = _addOn.GetCurrentDisplayedImageAsImageSource(false) as BitmapSource;
                if (frame == null) return;
                RenderJob job = PrepareRender(frame, fitToScreen: true);
                int version = _viewVersion;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                _stillBusy = true;
                _stillPending = false;
                Task.Run(job.Render).ContinueWith(t =>
                {
                    _stillBusy = false;
                    if (t.IsFaulted)
                    {
                        Log.Error($"Sharp render failed for {Describe()}", t.Exception);
                        return;
                    }
                    if (_stillLogs++ < 5)
                        Log.Info($"Sharp render {job}, total {sw.ElapsedMilliseconds} ms");
                    if (_still == null || !_settled || version != _viewVersion) return;
                    _still.Source = t.Result;
                    _still.Visibility = Visibility.Visible;
                    if (_stillPending) RenderStill();
                }, TaskScheduler.FromCurrentSynchronizationContext());
            }
            catch (Exception ex)
            {
                _stillBusy = false;
                Log.Error($"Sharp render failed for {Describe()}", ex);
            }
        }

        private void ShowToast(string text, bool keep = false)
        {
            if (_toast == null) return;
            _toast.Text = text;
            _toast.Visibility = Visibility.Visible;
            _toastTimer?.Stop();
            if (keep) return;
            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _toastTimer.Tick += (s, e) =>
            {
                _toastTimer?.Stop();
                if (_toast != null) _toast.Visibility = Visibility.Collapsed;
            };
            _toastTimer.Start();
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
            if (paint != _lastPaint)
            {
                // The tile changed size (e.g. toolbar or timeline shown); a still rendered for the old size is wrong.
                _lastPaint = paint;
                ViewChanged();
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
            Dispatcher dispatcher = _input?.Dispatcher;
            if (dispatcher == null) return;
            if (dispatcher.CheckAccess()) OnFrame(e.VideoEffectArea);
            else dispatcher.BeginInvoke(new Action(() => OnFrame(e.VideoEffectArea)));
        }

        private void OnFrame(Rect area)
        {
            if (!Enabled) return;
            if (area != _lastArea || _addOn.PaintSizeWpf != _lastPaint) UpdateGeometry();
            if (_settled) RenderStill();
        }

        private void OnSizeOrLocationChanged(object sender, EventArgs e)
        {
            if (!Enabled || _input == null) return;
            if (_input.Dispatcher.CheckAccess()) UpdateGeometry();
            else _input.Dispatcher.BeginInvoke(new Action(UpdateGeometry));
        }

        private void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                _view.Reset();
                UpdateGeometry();
                ViewChanged();
                Log.Info($"Reset {_view}");
            }
            else
            {
                _dragLast = e.GetPosition(_input);
                _input.CaptureMouse();
                ViewChanged();
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
            ViewChanged();
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
            ViewChanged();
            e.Handled = true;
        }
    }
}
