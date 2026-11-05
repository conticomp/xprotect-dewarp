using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace FisheyeDewarp
{
    /// <summary>
    /// Writes every unhandled exception in Smart Client to dewarp.log, noting whether this plugin is on the stack, so a
    /// crash report from the field can be tied to the plugin (or ruled out). It only logs: Smart Client still decides
    /// whether to show its error dialog or exit.
    /// </summary>
    internal static class CrashLog
    {
        private static Application _app;

        public static void Start()
        {
            AppDomain.CurrentDomain.UnhandledException += OnUnhandled;
            TaskScheduler.UnobservedTaskException += OnUnobservedTask;
            _app = Application.Current;
            if (_app != null) _app.DispatcherUnhandledException += OnDispatcherUnhandled;
            else Log.Info("No WPF application yet; UI thread exceptions are logged only if they end the process");
        }

        public static void Stop()
        {
            AppDomain.CurrentDomain.UnhandledException -= OnUnhandled;
            TaskScheduler.UnobservedTaskException -= OnUnobservedTask;
            if (_app != null) _app.DispatcherUnhandledException -= OnDispatcherUnhandled;
            _app = null;
        }

        private static void OnDispatcherUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e) =>
            Write("UI thread", e.Exception);

        private static void OnUnhandled(object sender, UnhandledExceptionEventArgs e) =>
            Write(e.IsTerminating ? "Background thread (Smart Client is exiting)" : "Background thread", e.ExceptionObject as Exception);

        private static void OnUnobservedTask(object sender, UnobservedTaskExceptionEventArgs e) =>
            Write("Unobserved task", e.Exception);

        private static void Write(string where, Exception ex)
        {
            string text = ex?.ToString() ?? "(no exception object)";
            string ours = text.Contains(nameof(FisheyeDewarp) + ".") ? "plugin IS on the stack" : "plugin not on the stack";
            Log.Error($"Unhandled exception in Smart Client, {where}, {ours}", ex);
        }
    }
}
