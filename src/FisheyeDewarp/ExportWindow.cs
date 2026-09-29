using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using VideoOS.Platform;
using VideoOS.Platform.UI.Controls;

namespace FisheyeDewarp
{
    /// <summary>
    /// "Export dewarped video" for one tile. Modeless, so the operator can keep scrubbing the timeline and
    /// take the start and end from what the tile shows. The view is taken from the tile when Export is pressed.
    /// </summary>
    internal sealed class ExportWindow : VideoOSWindow
    {
        private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";
        private static readonly Dictionary<TileSession, ExportWindow> OpenWindows = new Dictionary<TileSession, ExportWindow>();
        private static string _lastFolder;

        private readonly TileSession _session;
        private readonly Item _camera;
        private readonly TextBox _start, _end, _folder;
        private readonly RadioButton _size1080, _size720;
        private readonly CheckBox _burnIn;
        private readonly ProgressBar _progress;
        private readonly TextBlock _status;
        private readonly Button _export, _close, _openFolder;
        private readonly List<UIElement> _inputs = new List<UIElement>();
        private CancellationTokenSource _cancel;
        private string _lastFile;

        public static void ShowFor(TileSession session)
        {
            if (OpenWindows.TryGetValue(session, out ExportWindow existing))
            {
                existing.Activate();
                return;
            }
            Window owner = session.OwnerWindow;
            Item camera = session.Camera;
            if (camera == null)
            {
                VideoOSMessageBox.Show(owner, "Export dewarped video", "No camera", "This tile has no camera to export from.", VideoOSMessageBox.Buttons.OK);
                return;
            }
            if (!DewarpExporter.CheckPermission(camera))
            {
                VideoOSMessageBox.Show(owner, "Export dewarped video", "Export not allowed",
                    $"You do not have permission to export video from {camera.Name}.", VideoOSMessageBox.Buttons.OK);
                return;
            }

            var window = new ExportWindow(session, camera);
            if (owner != null) window.Owner = owner;
            OpenWindows[session] = window;
            window.Closed += (s, e) => OpenWindows.Remove(session);
            window.Show();
        }

        private ExportWindow(TileSession session, Item camera)
        {
            _session = session;
            _camera = camera;
            Title = "Export dewarped video";
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            try
            {
                VideoOSTheming.SetTheme(this, ClientControl.Instance.Theme.ThemeType);
            }
            catch (Exception ex)
            {
                Log.Error("Could not apply the Smart Client theme to the export window", ex);
            }

            DateTime shown = (session.DisplayedTimeUtc ?? DateTime.UtcNow).ToLocalTime();
            DateTime now = DateTime.Now;
            _start = new VideoOSTextBoxMedium { Text = shown.AddSeconds(-30).ToString(TimeFormat), Width = 170 };
            _end = new VideoOSTextBoxMedium { Text = (shown.AddSeconds(30) > now ? now : shown.AddSeconds(30)).ToString(TimeFormat), Width = 170 };
            _folder = new VideoOSTextBoxMedium
            {
                Text = _lastFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Dewarp exports"),
                Width = 300,
            };
            _size1080 = new VideoOSRadioButtonMedium { Content = "1920 wide", GroupName = "size", IsChecked = true, Margin = new Thickness(0, 0, 16, 0) };
            _size720 = new VideoOSRadioButtonMedium { Content = "1280 wide", GroupName = "size" };
            _burnIn = new VideoOSCheckBoxMedium { Content = "Burn in camera name and time", IsChecked = true };
            _progress = new VideoOSProgressBar { Minimum = 0, Maximum = 1, Height = 6, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 12, 0, 0) };
            _status = new VideoOSTextBlockBodySmall { TextWrapping = TextWrapping.Wrap, MaxWidth = 460, Margin = new Thickness(0, 8, 0, 0) };
            _export = new VideoOSButtonPrimaryMedium { Content = "Export", MinWidth = 90, IsDefault = true };
            _close = new VideoOSButtonSecondaryMedium { Content = "Close", MinWidth = 90, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
            _openFolder = new VideoOSButtonTertiaryMedium { Content = "Show file", Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 8, 0) };
            _export.Click += (s, e) => StartExport();
            _close.Click += (s, e) => CloseOrCancel();
            _openFolder.Click += (s, e) => ShowFile();

            var grid = new Grid { Margin = new Thickness(20) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            int row = 0;
            AddRow(grid, ref row, "Camera", new VideoOSTextBlockBodyMedium { Text = camera.Name, VerticalAlignment = VerticalAlignment.Center });
            AddRow(grid, ref row, "Start", Inline(_start, UseDisplayedButton(_start)));
            AddRow(grid, ref row, "End", Inline(_end, UseDisplayedButton(_end)));
            AddRow(grid, ref row, "Size", Inline(_size1080, _size720));
            AddRow(grid, ref row, "", _burnIn);
            var browse = new VideoOSButtonSecondaryMedium { Content = "Browse...", Margin = new Thickness(8, 0, 0, 0) };
            browse.Click += (s, e) => Browse();
            AddRow(grid, ref row, "Save to", Inline(_folder, browse));
            _inputs.AddRange(new UIElement[] { _start, _end, _folder, _size1080, _size720, _burnIn, browse });

            var hint = new VideoOSTextBlockBodySmall
            {
                Text = "The export frames exactly what the tile shows (same view and shape) at the moment you press Export. Keep scrubbing the timeline and use \"Displayed time\" to pick the start and end.",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 460,
                Margin = new Thickness(0, 12, 0, 0),
            };

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            buttons.Children.Add(_openFolder);
            buttons.Children.Add(_export);
            buttons.Children.Add(_close);

            var root = new StackPanel { Margin = new Thickness(0) };
            root.Children.Add(grid);
            var footer = new StackPanel { Margin = new Thickness(20, 0, 20, 20) };
            footer.Children.Add(hint);
            footer.Children.Add(_progress);
            footer.Children.Add(_status);
            footer.Children.Add(buttons);
            root.Children.Add(footer);
            Content = root;

            Closing += (s, e) => _cancel?.Cancel();   // the export stops at its next frame and removes its files
        }

        private Button UseDisplayedButton(TextBox target)
        {
            var button = new VideoOSButtonSecondaryMedium { Content = "Displayed time", Margin = new Thickness(8, 0, 0, 0), ToolTip = "Use the time of the frame the tile shows now" };
            button.Click += (s, e) =>
            {
                DateTime? shown = _session.DisplayedTimeUtc;
                if (shown.HasValue) target.Text = shown.Value.ToLocalTime().ToString(TimeFormat);
            };
            _inputs.Add(button);
            return button;
        }

        private void StartExport()
        {
            if (!TryParse(_start.Text, out DateTime start)) { Fail("Enter the start time as " + TimeFormat + "."); return; }
            if (!TryParse(_end.Text, out DateTime end)) { Fail("Enter the end time as " + TimeFormat + "."); return; }
            if (end <= start) { Fail("The end time must be after the start time."); return; }
            if (end - start > DewarpExporter.MaxDuration) { Fail($"Export at most {DewarpExporter.MaxDuration.TotalHours:F0} hours at a time."); return; }
            if (start > DateTime.UtcNow) { Fail("The start time is in the future."); return; }
            if (string.IsNullOrWhiteSpace(_folder.Text)) { Fail("Choose a folder to save to."); return; }
            if (!_session.TryGetView(out double[,] rotation, out double fov))
            {
                Fail("Turn on Dewarp in the tile and aim the view, then press Export.");
                return;
            }
            System.Drawing.Size source = _session.SourceSize;
            if (source.Width == 0)
            {
                Fail("The tile has not shown any video yet.");
                return;
            }

            string folder = _folder.Text.Trim();
            string path;
            try
            {
                Directory.CreateDirectory(folder);
                path = UniquePath(folder, $"{SafeName(_camera.Name)} {start.ToLocalTime():yyyy-MM-dd HHmmss} dewarped");
            }
            catch (Exception ex)
            {
                Fail($"Cannot save to that folder: {ex.Message}");
                return;
            }
            _lastFolder = folder;

            // Same shape as the tile, so the export frames exactly what the operator sees.
            int width = _size1080.IsChecked == true ? 1920 : 1280;
            int height = Math.Max(2, (int)Math.Round(width / _session.TileAspect / 2) * 2);
            double tanX = Math.Tan(fov / 2);
            var request = new ExportRequest
            {
                Camera = _camera,
                StartUtc = start,
                EndUtc = end,
                SourceWidth = source.Width,
                SourceHeight = source.Height,
                Rotation = rotation,
                TanX = tanX,
                TanY = tanX * height / width,
                LensHalfFov = _session.LensHalfFovRadians,
                Width = width,
                Height = height,
                Path = path,
                BurnIn = _burnIn.IsChecked == true,
            };

            SetRunning(true);
            _status.Text = "Exporting...";
            _cancel = new CancellationTokenSource();
            DewarpExporter.Start(request,
                p => Dispatcher.BeginInvoke(new Action(() => _progress.Value = p)),
                result => Dispatcher.BeginInvoke(new Action(() => Finished(result, path))),
                _cancel.Token);
        }

        private void Finished(ExportResult result, string path)
        {
            _cancel?.Dispose();
            _cancel = null;
            if (!IsLoaded) return;   // closed while exporting
            _progress.Value = result.Succeeded ? 1 : 0;
            SetRunning(false);
            _status.Text = result.Message;
            _lastFile = result.Succeeded ? path : null;
            _openFolder.Visibility = result.Succeeded ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SetRunning(bool running)
        {
            foreach (UIElement input in _inputs) input.IsEnabled = !running;
            _export.IsEnabled = !running;
            _close.Content = running ? "Cancel" : "Close";
            _progress.Visibility = running || _progress.Value > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (running)
            {
                _progress.Value = 0;
                _openFolder.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>While exporting, Cancel (or Esc) stops the export and keeps the window open.</summary>
        private void CloseOrCancel()
        {
            if (_cancel == null)
            {
                Close();
                return;
            }
            _cancel.Cancel();
            _status.Text = "Cancelling...";
        }

        private void Fail(string message)
        {
            _status.Text = message;
        }

        private void Browse()
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog { SelectedPath = _folder.Text, Description = "Save dewarped exports to" })
            {
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) _folder.Text = dialog.SelectedPath;
            }
        }

        private void ShowFile()
        {
            if (_lastFile == null || !File.Exists(_lastFile)) return;
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{_lastFile}\"");
        }

        private static bool TryParse(string text, out DateTime utc)
        {
            bool ok = DateTime.TryParseExact(text?.Trim(), TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTime local)
                      || DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out local);
            utc = ok ? local.ToUniversalTime() : default;
            return ok;
        }

        private static string SafeName(string name) => string.Concat(name.Split(Path.GetInvalidFileNameChars())).Trim();

        private static string UniquePath(string folder, string baseName)
        {
            string path = Path.Combine(folder, baseName + ".mp4");
            for (int n = 2; File.Exists(path); n++)
                path = Path.Combine(folder, $"{baseName} ({n}).mp4");
            return path;
        }

        private static StackPanel Inline(params UIElement[] children)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (UIElement child in children) panel.Children.Add(child);
            return panel;
        }

        private static void AddRow(Grid grid, ref int row, string label, UIElement content)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var text = new VideoOSTextBlockLabel { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
            Grid.SetRow(text, row);
            Grid.SetRow(content, row);
            Grid.SetColumn(content, 1);
            if (content is FrameworkElement element) element.Margin = new Thickness(element.Margin.Left, 4, element.Margin.Right, 4);
            grid.Children.Add(text);
            grid.Children.Add(content);
            row++;
        }
    }
}
