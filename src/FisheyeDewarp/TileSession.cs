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

        // Sharp view: a CPU render from the full-resolution frame, shown over the (softer) shader view and
        // redone whenever the view moves or a new frame arrives. The shader view only shows until the first
        // render lands. The captured frame is reused while the operator drags over a still image.
        private Image _still;
        private SourceFrame _frame;
        private bool _stillBusy;
        private bool _stillPending;
        private int _stillLogs;
        private int _noFrameLogs;
        private bool _frameStale = true;
        private bool _grabbing;
        private bool _grabOnUiThread;
        private int _grabLogs;
        private DateTime _lastWheel;

        // Latest frame the tile showed, for the export spike's time range and source size.
        private DateTime _lastImageTime;
        private System.Drawing.Size _originalSize;
        private bool _exportBusy;

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

            UpdateGeometry();
            _addOn.VideoEffect = _effect;
            _overlayId = _addOn.ActiveElementsOverlayAdd(new List<FrameworkElement> { layers },
                new ActiveElementsOverlayRenderParameters { Placement = OverlayPlacement.FollowImageViewport, ShowAlways = true, ZOrder = 10 });

            Enabled = true;
            _imageEventsLogged = 0;
            _stillLogs = 0;
            _frame = null;
            _frameStale = true;
            RenderStill();
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
            _still = null;
            _frame = null;
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

                RenderJob job = PrepareRender(SnapshotRenderer.Capture(frame), fitToScreen: false);
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
        /// Spike: export the 30 seconds up to the frame on screen, dewarped with the current view, to
        /// Videos\Dewarp exports. Timings go to the log.
        /// </summary>
        public void ExportSpike()
        {
            if (!Enabled)
            {
                Log.Info($"Export ignored, dewarp is off: {Describe()}");
                return;
            }
            if (_exportBusy) return;
            try
            {
                VideoOS.Platform.Item camera = VideoOS.Platform.Configuration.Instance.GetItem(_addOn.CameraFQID);
                if (camera == null || _lastImageTime == default || _originalSize.Width == 0)
                {
                    ShowToast("No video to export yet");
                    return;
                }

                DateTime end = _lastImageTime.Kind == DateTimeKind.Local ? _lastImageTime.ToUniversalTime() : DateTime.SpecifyKind(_lastImageTime, DateTimeKind.Utc);
                Log.Info($"Export spike: tile image time {_lastImageTime:o} kind={_lastImageTime.Kind} original={_originalSize}");
                Size paint = _addOn.PaintSizeWpf;
                double aspect = paint.Width > 0 && paint.Height > 0 ? paint.Width / paint.Height : 16.0 / 9;
                int width = 1920, height = Math.Max(2, (int)Math.Round(width / aspect / 2) * 2);
                double tanX = Math.Tan(_view.Fov / 2);
                string folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Dewarp exports");
                string name = string.Concat(camera.Name.Split(System.IO.Path.GetInvalidFileNameChars()));
                var request = new ExportRequest
                {
                    Camera = camera,
                    StartUtc = end - TimeSpan.FromSeconds(30),
                    EndUtc = end,
                    SourceWidth = _originalSize.Width,
                    SourceHeight = _originalSize.Height,
                    Rotation = _view.Rotation(),
                    TanX = tanX,
                    TanY = tanX / aspect,
                    LensHalfFov = LensHalfFov,
                    Width = width,
                    Height = height,
                    Path = System.IO.Path.Combine(folder, $"{name} {end.ToLocalTime():yyyy-MM-dd HHmmss} dewarped.mp4"),
                };
                Log.Info($"Export spike view {_view}");

                bool benchmark = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
                _exportBusy = true;
                ShowToast(benchmark ? "Benchmarking decode..." : "Exporting...", keep: true);
                Dispatcher dispatcher = _input.Dispatcher;
                DewarpExporter.Start(request,
                    text => dispatcher.BeginInvoke(new Action(() => ShowToast(text, keep: true))),
                    text => dispatcher.BeginInvoke(new Action(() =>
                    {
                        _exportBusy = false;
                        ShowToast(text);
                    })), benchmark);
            }
            catch (Exception ex)
            {
                _exportBusy = false;
                Log.Error($"Export failed for {Describe()}", ex);
                ShowToast("Export failed");
            }
        }

        /// <summary>
        /// Everything a CPU render needs, captured on the UI thread. Snapshots match the camera's pixel density;
        /// the on-screen still matches the tile's device pixels.
        /// </summary>
        private RenderJob PrepareRender(SourceFrame source, bool fitToScreen)
        {
            Size paint = _addOn.PaintSizeWpf;
            double aspect = paint.Width > 0 && paint.Height > 0 ? paint.Width / paint.Height : 16.0 / 9;
            double tanX = Math.Tan(_view.Fov / 2), tanY = tanX / aspect;
            double[,] m = _view.Rotation();

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
            return new RenderJob(source, m, tanX, tanY, LensHalfFov, width, height);
        }

        /// <summary>
        /// Render the current view at full resolution for the tile. One render runs at a time; requests that
        /// arrive meanwhile collapse into a single follow-up render with the latest view and frame.
        /// </summary>
        private void RenderStill()
        {
            if (!Enabled || _still == null) return;
            if (_stillBusy)
            {
                _stillPending = true;
                return;
            }
            _stillPending = false;
            try
            {
                RequestFrame();
                if (_frame == null) return;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                RenderJob job = PrepareRender(_frame, fitToScreen: true);
                Size paint = _lastPaint;
                _stillBusy = true;
                Task.Run(job.Render).ContinueWith(t =>
                {
                    _stillBusy = false;
                    if (t.IsFaulted)
                    {
                        Log.Error($"Sharp render failed for {Describe()}", t.Exception);
                        return;
                    }
                    if (_stillLogs++ < 8)
                        Log.Info($"Sharp render {job}, total {sw.ElapsedMilliseconds} ms, live={_addOn.InLiveMode}");
                    if (_still == null) return;
                    if (paint == _lastPaint)
                    {
                        _still.Source = t.Result;
                        _still.Visibility = Visibility.Visible;
                    }
                    if (_stillPending) RenderStill();
                }, TaskScheduler.FromCurrentSynchronizationContext());
            }
            catch (Exception ex)
            {
                _stillBusy = false;
                Log.Error($"Sharp render failed for {Describe()}", ex);
            }
        }

        /// <summary>While the operator steers, keep dewarping the frame already in hand; grabbing a new one is the slow part.</summary>
        private bool Steering => (_input != null && _input.IsMouseCaptured) || DateTime.UtcNow - _lastWheel < TimeSpan.FromMilliseconds(300);

        /// <summary>
        /// Fetch the latest full-resolution frame in the background. Smart Client's frame grab can take
        /// hundreds of milliseconds in Live, so it runs off the UI thread when Smart Client allows it,
        /// and never while the operator is steering.
        /// </summary>
        private void RequestFrame()
        {
            if (_grabbing || !Enabled || (!_frameStale && _frame != null)) return;
            if (_frame != null && Steering) return;
            _grabbing = true;
            _frameStale = false;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool onUi = _grabOnUiThread;

            Func<SourceFrame> grab = () =>
            {
                System.Drawing.Bitmap bitmap = null;
                try
                {
                    // The deprecated GDI variant skips the WPF conversion that GetCurrentDisplayedImageAsImageSource does internally.
#pragma warning disable CS0618
                    bitmap = _addOn.GetCurrentDisplayedImageAsBitmap(false);
#pragma warning restore CS0618
                }
                catch (NullReferenceException)
                {
                    // Smart Client occasionally has no frame to hand out in Live; the next frame retries.
                }
                if (bitmap == null) return null;
                using (bitmap)
                {
                    long grabMs = sw.ElapsedMilliseconds;
                    SourceFrame frame = SnapshotRenderer.Capture(bitmap);
                    if (_grabLogs++ < 8)
                        Log.Info($"Frame grab {frame.Width}x{frame.Height} on {(onUi ? "UI" : "worker")} thread: Smart Client {grabMs} ms, copy {sw.ElapsedMilliseconds - grabMs} ms, live={_addOn.InLiveMode}");
                    return frame;
                }
            };

            Action<SourceFrame> done = frame =>
            {
                _grabbing = false;
                if (!Enabled) return;
                if (frame == null)
                {
                    _frameStale = true;
                    if (_noFrameLogs++ < 3) Log.Info($"No full-resolution frame available yet for {Describe()}");
                    return;
                }
                _frame = frame;
                RenderStill();
            };

            if (onUi)
            {
                SourceFrame frame = null;
                try { frame = grab(); }
                catch (Exception ex) { Log.Error($"Frame grab failed for {Describe()}", ex); }
                done(frame);
                return;
            }

            Task.Run(grab).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    // Smart Client may insist on its own thread; fall back to grabbing there from now on.
                    Log.Info($"Frame grab off the UI thread failed, using the UI thread from now on: {t.Exception?.InnerException?.GetType().Name}: {t.Exception?.InnerException?.Message}");
                    _grabOnUiThread = true;
                    _grabbing = false;
                    _frameStale = true;
                    RequestFrame();
                    return;
                }
                done(t.Result);
            }, TaskScheduler.FromCurrentSynchronizationContext());
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
                // The tile changed size (e.g. toolbar or timeline shown); a render for the old shape would be distorted.
                _lastPaint = paint;
                if (_still != null) _still.Visibility = Visibility.Collapsed;
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
            _lastImageTime = e.ImageTime;
            _originalSize = new System.Drawing.Size((int)e.OriginalImageSize.Width, (int)e.OriginalImageSize.Height);
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

        private void ViewChanged()
        {
            UpdateGeometry();
            RenderStill();
        }

        private void OnFrame(Rect area)
        {
            if (!Enabled) return;
            if (area != _lastArea || _addOn.PaintSizeWpf != _lastPaint) UpdateGeometry();
            _frameStale = true;
            RenderStill();
        }

        private void OnSizeOrLocationChanged(object sender, EventArgs e)
        {
            if (!Enabled || _input == null) return;
            if (_input.Dispatcher.CheckAccess()) ViewChanged();
            else _input.Dispatcher.BeginInvoke(new Action(ViewChanged));
        }

        private void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                _view.Reset();
                ViewChanged();
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
            ViewChanged();
            e.Handled = true;
        }

        private void OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_input != null && _input.IsMouseCaptured)
            {
                _input.ReleaseMouseCapture();
                Log.Info($"Drag end {_view}");
                RenderStill();   // pick up the frames that arrived while dragging
            }
            e.Handled = true;
        }

        private void OnMouseWheel(object sender, MouseWheelEventArgs e)
        {
            _lastWheel = DateTime.UtcNow;
            _view.Zoom(e.Delta);
            ViewChanged();
            e.Handled = true;
        }
    }
}
