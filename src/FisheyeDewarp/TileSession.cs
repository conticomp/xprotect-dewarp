using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BitmapSource = System.Windows.Media.Imaging.BitmapSource;
using System.Windows.Threading;
using VideoOS.Platform.Client;
using VideoOS.Platform.Log;
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

        // A failed grab makes Smart Client log the error and show its exception dialog, so after a failure no grab
        // is tried again before _grabRetryAt, and the wait doubles while the failures continue. This happens when
        // the connection to the VMS drops.
        private static readonly TimeSpan GrabBackoffMin = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan GrabBackoffMax = TimeSpan.FromSeconds(30);
        private TimeSpan _grabBackoff = GrabBackoffMin;
        private DateTime _grabRetryAt;
        private int _grabFailures;

        // In Live, frames come from the tile's own decoded stream instead of Smart Client's slow grab.
        private LiveDecoder _live;
        private bool _liveRefused;
        private bool _stillInLive;   // the mode _frame came from
        private DateTime _lastLiveFrameUtc;
        // After this long without a decoded frame, the tile goes back to grabbing Smart Client's frames.
        private static readonly TimeSpan LiveStall = TimeSpan.FromSeconds(2);
        private Rect _crop = WholeImage;
        private int _cropLogs;

        // Mouse moves arrive far faster than the eye needs; sharp renders are capped at about 30 per second.
        private static readonly TimeSpan MinStillInterval = TimeSpan.FromMilliseconds(33);
        private DateTime _lastStillStart;
        private DispatcherTimer _stillTimer;
        private RenderMap _renderMap;   // the still's pixel map, reused while the view stays put
        private DateTime _lastWheel;

        // Latest frame the tile showed, for the export's default times and source size.
        private DateTime _lastImageTime;
        private System.Drawing.Size _originalSize;
        private (DateTime Start, DateTime End)? _loggedSelection;
        private string _loggedSelectionError;

        public TileSession(ImageViewerAddOn addOn)
        {
            _addOn = addOn;
            _addOn.ImageDisplayedEvent += OnImageDisplayed;
            _addOn.UserControlSizeOrLocationChangedEvent += OnSizeOrLocationChanged;
        }

        /// <summary>Raised on the UI thread when any tile's dewarp is turned on or off.</summary>
        public static event Action<TileSession> EnabledChanged;

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
            EnabledChanged?.Invoke(this);
            _imageEventsLogged = 0;
            _stillLogs = 0;
            _frame = null;
            _frameStale = _lastImageTime != default;   // grab the frame on screen, if the tile has shown one
            _stillInLive = _addOn.InLiveMode;
            UpdateLiveDecoder();
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
            UpdateLiveDecoder();
            EnabledChanged?.Invoke(this);
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
        /// Copies the current dewarped view to the clipboard, or saves it as a PNG file, rendered from the
        /// full-resolution frame rather than the on-screen tile, so it is sharper than what the operator sees.
        /// Like an export, it needs the camera's Export permission and is written to the audit log.
        /// </summary>
        public void Snapshot(bool saveToFile)
        {
            if (!Enabled)
            {
                Log.Info($"Snapshot ignored, dewarp is off: {Describe()}");
                return;
            }
            if (_snapshotBusy) return;
            try
            {
                VideoOS.Platform.Item camera = Camera;
                if (camera == null)
                {
                    ShowToast("No camera in this tile");
                    return;
                }
                if (!Audit.CheckExportPermission(camera, Audit.SnapshotDenied))
                {
                    ShowToast("You do not have permission to export from this camera");
                    return;
                }

                BitmapSource frame = _addOn.GetCurrentDisplayedImageAsImageSource(false) as BitmapSource;
                if (frame == null)
                {
                    ShowToast("No image to copy yet");
                    return;
                }
                // Taken before the save dialog, so the snapshot is the frame on screen when the button was clicked.
                DateTime? frameTime = DisplayedTimeUtc;
                RenderJob job = PrepareRender(SnapshotRenderer.Capture(frame), fitToScreen: false);
                string view = _view.ToString();

                string path = null;
                if (saveToFile)
                {
                    path = AskSnapshotPath(camera.Name, frameTime);
                    if (path == null) return;
                }

                _snapshotBusy = true;
                ShowToast(path == null ? "Copying..." : "Saving...", keep: true);
                Task.Run(() =>
                    {
                        BitmapSource image = job.Render();
                        if (path != null) SavePng(image, path);
                        return image;
                    })
                    .ContinueWith(t =>
                    {
                        _snapshotBusy = false;
                        if (t.IsFaulted)
                        {
                            Log.Error($"Snapshot failed for {Describe()}", t.Exception);
                            ShowToast(path == null ? "Snapshot failed" : "Could not save the snapshot");
                            return;
                        }
                        if (path == null) Clipboard.SetImage(t.Result);
                        Log.Info($"Snapshot {job} camera='{camera.Name}' {view} -> {path ?? "clipboard"}");
                        Audit.Entry(Audit.Snapshot, camera, PermissionState.Granted, new Dictionary<string, string>
                        {
                            ["camera"] = camera.Name,
                            ["time"] = frameTime.HasValue ? Audit.Time(frameTime.Value) : "an unknown time",
                            ["view"] = view,
                            ["size"] = $"{job.Width}x{job.Height}",
                            ["destination"] = path ?? "the clipboard",
                        });
                        ShowToast(path == null ? "Dewarped snapshot copied to clipboard" : "Saved " + Path.GetFileName(path));
                    }, TaskScheduler.FromCurrentSynchronizationContext());
            }
            catch (Exception ex)
            {
                _snapshotBusy = false;
                Log.Error($"Snapshot failed for {Describe()}", ex);
                ShowToast("Snapshot failed");
            }
        }

        private static string _lastSnapshotFolder;

        /// <summary>Save dialog for a snapshot, named after the camera and frame time. Null when cancelled.</summary>
        private string AskSnapshotPath(string camera, DateTime? frameTimeUtc)
        {
            string folder = _lastSnapshotFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Dewarp snapshots");
            Directory.CreateDirectory(folder);
            DateTime time = frameTimeUtc?.ToLocalTime() ?? DateTime.Now;
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save dewarped snapshot",
                Filter = "PNG image (*.png)|*.png",
                DefaultExt = ".png",
                AddExtension = true,
                OverwritePrompt = true,
                InitialDirectory = folder,
                FileName = $"{ExportWindow.SafeName(camera)} {time:yyyy-MM-dd HHmmss} dewarped.png",
            };
            if (dialog.ShowDialog(OwnerWindow) != true) return null;
            _lastSnapshotFolder = Path.GetDirectoryName(dialog.FileName);
            return dialog.FileName;
        }

        private static void SavePng(BitmapSource image, string path)
        {
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
            using (var file = File.Create(path))
                encoder.Save(file);
        }

        /// <summary>The camera shown in this tile, or null if it is not known yet.</summary>
        public VideoOS.Platform.Item Camera => _addOn.CameraFQID == null ? null : VideoOS.Platform.Configuration.Instance.GetItem(_addOn.CameraFQID);

        /// <summary>Recording time of the frame on screen (UTC), or null before the first frame.</summary>
        public DateTime? DisplayedTimeUtc =>
            _lastImageTime == default ? (DateTime?)null : DewarpExporter.ToUtc(_lastImageTime);

        /// <summary>
        /// The span selected on the timeline of this tile's window (the Smart Client's "time selection", also
        /// set by its own export mode), in UTC. False when nothing is selected.
        /// </summary>
        public bool TryGetTimelineSelection(out DateTime startUtc, out DateTime endUtc)
        {
            startUtc = endUtc = default;
            // The export window polls every second, so a result is logged only when it differs from the last one.
            // (default, default) stands for "nothing selected".
            VideoOS.Platform.FQID controller = null;
            try
            {
                // Null addresses the main window's timeline; a floating window has its own playback controller.
                controller = _addOn.WindowInformation?.PlaybackControllerFQID;
                var replies = VideoOS.Platform.EnvironmentManager.Instance.SendMessage(
                    new Message(MessageId.SmartClient.GetTimelineSelectedIntervalRequest), controller);
                _loggedSelectionError = null;
                foreach (object reply in replies)
                {
                    if (!(reply is VideoOS.Platform.Util.TimeInterval interval) || interval.Duration <= TimeSpan.Zero) continue;
                    startUtc = DewarpExporter.ToUtc(interval.StartTime);
                    endUtc = DewarpExporter.ToUtc(interval.EndTime);
                    if (_loggedSelection != (startUtc, endUtc))
                    {
                        _loggedSelection = (startUtc, endUtc);
                        Log.Info($"Timeline selection {interval.StartTime:o} ({interval.StartTime.Kind}) to {interval.EndTime:o} ({interval.EndTime.Kind}) {SelectionSource(controller)}");
                    }
                    return true;
                }
                if (_loggedSelection != (default(DateTime), default(DateTime)))
                {
                    _loggedSelection = (default, default);
                    string got = replies.Count == 0 ? "no reply" : string.Join(", ", replies.Select(r => r is VideoOS.Platform.Util.TimeInterval ti ? $"TimeInterval {ti.Duration}" : r?.GetType().Name ?? "null"));
                    Log.Info($"Timeline selection: none (replies: {got}) {SelectionSource(controller)}");
                }
            }
            catch (Exception ex)
            {
                if (ex.Message != _loggedSelectionError)
                {
                    _loggedSelectionError = ex.Message;
                    Log.Error($"Timeline selection request failed {SelectionSource(controller)}", ex);
                }
            }
            return false;
        }

        private string SelectionSource(VideoOS.Platform.FQID controller) =>
            $"controller={(controller == null ? "main" : controller.ObjectId.ToString())} {Describe()}";

        /// <summary>The camera's full frame size, from the last frame shown.</summary>
        public System.Drawing.Size SourceSize => _originalSize;

        public double LensHalfFovRadians => LensHalfFov;

        /// <summary>Width / height of the tile's picture, so an export frames exactly what the operator sees.</summary>
        public double TileAspect
        {
            get
            {
                Size paint = _addOn.PaintSizeWpf;
                return paint.Width > 0 && paint.Height > 0 ? paint.Width / paint.Height : 16.0 / 9;
            }
        }

        /// <summary>The Smart Client window holding this tile, to own dialogs.</summary>
        public Window OwnerWindow => (_input != null ? Window.GetWindow(_input) : null) ?? Application.Current?.MainWindow;

        /// <summary>The current virtual PTZ view, for an export. False when dewarp is off.</summary>
        public bool TryGetView(out double[,] rotation, out double horizontalFov)
        {
            rotation = Enabled ? _view.Rotation() : null;
            horizontalFov = _view.Fov;
            if (Enabled) Log.Info($"Export view {_view} {Describe()}");
            return Enabled;
        }

        /// <summary>
        /// Everything a CPU render needs, captured on the UI thread. Snapshots match the camera's pixel density;
        /// the on-screen still matches the tile's device pixels.
        /// </summary>
        private RenderJob PrepareRender(SourceFrame source, bool fitToScreen)
        {
            ViewLens(out double[,] m, out double tanX, out double tanY);
            double aspect = tanX / tanY;

            int width;
            if (fitToScreen)
            {
                double scale = _input != null ? VisualTreeHelper.GetDpi(_input).DpiScaleX : 1;
                width = (int)Math.Max(64, Math.Round(_addOn.PaintSizeWpf.Width * scale));
            }
            else
            {
                // Match the fisheye's own pixel density at the centre of the view (stereographic lens), so the
                // snapshot neither throws detail away nor pretends to more than the camera captured.
                double theta = Math.Acos(Math.Max(-1, Math.Min(1, m[2, 2])));
                double halfCos = Math.Cos(theta / 2);
                double pixelsPerRadian = source.FullWidth / 2.0 / (2 * Math.Tan(LensHalfFov / 2) * halfCos * halfCos);
                width = (int)Math.Max(1280, Math.Min(3840, pixelsPerRadian * 2 * tanX));
            }
            int height = Math.Max(1, (int)Math.Round(width / aspect));
            return new RenderJob(source, m, tanX, tanY, LensHalfFov, width, height, fitToScreen ? _renderMap : null);
        }

        /// <summary>The virtual camera for the tile's current view and shape, as the CPU render and the crop use it.</summary>
        private void ViewLens(out double[,] m, out double tanX, out double tanY)
        {
            m = _view.Rotation();
            tanX = Math.Tan(_view.Fov / 2);
            tanY = tanX / TileAspect;
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
            TimeSpan sinceLast = DateTime.UtcNow - _lastStillStart;
            if (sinceLast < MinStillInterval)
            {
                if (_stillTimer == null)
                {
                    _stillTimer = new DispatcherTimer();
                    _stillTimer.Tick += (sender, e) =>
                    {
                        _stillTimer.Stop();
                        RenderStill();
                    };
                }
                if (!_stillTimer.IsEnabled)
                {
                    _stillTimer.Interval = MinStillInterval - sinceLast;
                    _stillTimer.Start();
                }
                return;
            }
            _stillPending = false;
            _lastStillStart = DateTime.UtcNow;
            try
            {
                UpdateCrop();
                RequestFrame();
                if (_frame == null) return;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                RenderJob job = PrepareRender(_frame, fitToScreen: true);
                Size paint = _lastPaint;
                _stillBusy = true;
                Task.Run(job.Render).ContinueWith(t =>
                {
                    _stillBusy = false;
                    _renderMap = t.IsFaulted ? null : job.Map;   // a failed build may have half-written the arrays it borrowed
                    if (t.IsFaulted)
                    {
                        Log.Error($"Sharp render failed for {Describe()}", t.Exception);
                        return;
                    }
                    if (_stillLogs++ < 8)
                        Log.Info($"Sharp render {job}, total {sw.ElapsedMilliseconds} ms, live={_addOn.InLiveMode}");
                    if (_still == null) return;
                    if (paint == _lastPaint && _frame != null)   // null: the mode changed while rendering
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
        /// and never while the operator is steering. Only a frame Smart Client has newly displayed is
        /// fetched; with no new frame there is nothing new to fetch, and a grab then is likely to fail.
        /// </summary>
        private void RequestFrame()
        {
            if (_grabbing || !Enabled || !_frameStale) return;
            if (LiveFeeding) return;
            if (_frame != null && Steering) return;
            if (DateTime.UtcNow < _grabRetryAt) return;
            _grabbing = true;
            _frameStale = false;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool onUi = _grabOnUiThread;
            bool grabInLive = _stillInLive;

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
                    // Wait for a new frame and the backoff before trying again.
                    _grabFailures++;
                    _grabRetryAt = DateTime.UtcNow + _grabBackoff;
                    if (_noFrameLogs++ < 3 || _grabFailures == 1)
                        Log.Info($"No full-resolution frame from Smart Client (failure {_grabFailures}, next try in {_grabBackoff.TotalSeconds:F0} s or later) for {Describe()}");
                    _grabBackoff = TimeSpan.FromTicks(Math.Min(GrabBackoffMax.Ticks, _grabBackoff.Ticks * 2));
                    return;
                }
                if (_grabFailures > 0)
                    Log.Info($"Frame grab recovered after {_grabFailures} failure(s) for {Describe()}");
                _grabFailures = 0;
                _grabBackoff = GrabBackoffMin;
                if (LiveFeeding) return;   // a decoded frame newer than this grab is already showing
                if (grabInLive != _stillInLive) return;   // grabbed before a Live/Playback switch: the other mode's picture
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

        /// <summary>
        /// Keeps the live decode cropped to the part of the fisheye the view samples, plus a margin so panning and
        /// zooming out stay sharp until the next frame. Re-crops when the view leaves the crop or shrinks well inside it.
        /// </summary>
        private void UpdateCrop()
        {
            if (_live == null || _originalSize.IsEmpty) return;
            Rect need = ViewBounds();
            if (need.IsEmpty) return;
            Rect fresh = need;
            fresh.Inflate(Math.Max(0.04, need.Width * 0.3), Math.Max(0.04, need.Height * 0.3));
            fresh.Intersect(WholeImage);
            // Keep the current crop while it covers the view and is not much larger than a fresh one would be.
            if (_crop.Contains(need) && _crop.Width * _crop.Height <= 2 * fresh.Width * fresh.Height) return;
            _crop = fresh;
            _live.SetCrop(_crop);
            if (_cropLogs++ < 10)
                Log.Info($"Live decode crop {_crop.X:F3},{_crop.Y:F3} {_crop.Width:F3}x{_crop.Height:F3} ({_crop.Width * _crop.Height * 100:F0}% of the image) for {_view}");
        }

        /// <summary>The part of the fisheye image (fractions 0..1) the current view samples, from a grid of view rays.</summary>
        private Rect ViewBounds()
        {
            ViewLens(out double[,] m, out double tanX, out double tanY);
            double tanHalfLens = Math.Tan(LensHalfFov / 2);
            const int n = 16;
            Rect bounds = Rect.Empty;
            for (int j = 0; j <= n; j++)
                for (int i = 0; i <= n; i++)
                    if (SnapshotRenderer.MapToSource(m, tanX, tanY, LensHalfFov, tanHalfLens, i, j, n + 1, n + 1, out double ix, out double iy))
                        bounds.Union(new Point(ix, iy));
            if (bounds.IsEmpty) return bounds;
            // The grid samples pixel centres; widen by one grid step so the edges of the view are covered too.
            bounds.Inflate(bounds.Width / n, bounds.Height / n);
            bounds.Intersect(WholeImage);
            return bounds;
        }

        /// <summary>Runs the tile's own live decode while dewarp is on in Live, and stops it otherwise.</summary>
        private void UpdateLiveDecoder()
        {
            bool want = Enabled && _addOn.InLiveMode;
            if (Enabled && _addOn.InLiveMode != _stillInLive)
            {
                // The still shows the other mode's picture (e.g. the last playback frame over live video); hide it
                // until a frame from this mode arrives.
                _stillInLive = _addOn.InLiveMode;
                _frame = null;
                _frameStale = true;
                if (_still != null) _still.Visibility = Visibility.Collapsed;
            }
            if (_live != null && _live.GaveUp)
            {
                _live.Dispose();
                _live = null;
                _lastLiveFrameUtc = default;
                _liveRefused = true;
                _frameStale = true;
            }
            if (want && _live == null && !_liveRefused)
            {
                VideoOS.Platform.Item camera = Camera;
                if (camera == null || _originalSize.IsEmpty) return;   // the size comes from the first displayed frame
                if (!LiveDecoder.Allowed)
                {
                    _liveRefused = true;
                    Log.Info($"Live decode turned off by the no-live-decode file; grabbing frames from Smart Client for {Describe()}");
                    return;
                }
                Dispatcher dispatcher = _input?.Dispatcher;
                if (dispatcher == null) return;
                LiveDecoder decoder = null;
                decoder = LiveDecoder.Start(camera, _addOn.StreamId, _originalSize, frame =>
                    dispatcher.BeginInvoke(new Action(() => OnLiveFrame(decoder, frame))));
                _live = decoder;
                _lastLiveFrameUtc = default;
                _liveRefused = decoder == null;
                _crop = WholeImage;
                _cropLogs = 0;
            }
            else if (!want)
            {
                _liveRefused = false;
                _live?.Dispose();
                _live = null;
                _lastLiveFrameUtc = default;
            }
        }

        private void OnLiveFrame(LiveDecoder from, SourceFrame frame)
        {
            from.Delivered();
            UpdateLiveDecoder();   // a switch to Playback shows up here first: the decode keeps delivering until stopped
            if (_live != from) return;
            _lastLiveFrameUtc = DateTime.UtcNow;
            _frame = frame;
            _frameStale = false;
            RenderStill();
        }

        /// <summary>The live decode is delivering frames, so Smart Client's frames are neither grabbed nor rendered.</summary>
        private bool LiveFeeding => _live != null && DateTime.UtcNow - _lastLiveFrameUtc < LiveStall;

        private static readonly Rect WholeImage = new Rect(0, 0, 1, 1);

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
            UpdateLiveDecoder();   // follows the tile between Live and Playback
            bool resized = area != _lastArea || _addOn.PaintSizeWpf != _lastPaint;
            if (resized) UpdateGeometry();
            if (LiveFeeding)
            {
                // The decode's own frames trigger the renders; this one would redo the same frame.
                if (resized) RenderStill();
                return;
            }
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
