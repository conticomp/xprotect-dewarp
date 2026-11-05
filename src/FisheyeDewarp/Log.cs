using System;
using System.IO;

namespace FisheyeDewarp
{
    /// <summary>File log at %LOCALAPPDATA%\FisheyeDewarp\dewarp.log so behaviour can be inspected without a debugger.</summary>
    internal static class Log
    {
        private static readonly object Gate = new object();
        /// <summary>%LOCALAPPDATA%\FisheyeDewarp: the log, and flag files that change the plugin's behaviour.</summary>
        public static readonly string Folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FisheyeDewarp");

        private static readonly string FilePath = Path.Combine(Folder, "dewarp.log");

        public static void Info(string message) => Write("INFO", message);

        public static void Error(string message, Exception ex = null) =>
            Write("ERROR", ex == null ? message : message + " :: " + ex);

        /// <summary>
        /// Runs code that Smart Client calls into (toolbar and tile events). An exception escaping from there reaches
        /// Smart Client's UI thread and takes it down, so it is logged with context and swallowed instead.
        /// </summary>
        public static void Guard(string context, Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Error($"{context} failed", ex);
            }
        }

        // When dewarp.log passes this size it becomes dewarp.1.log (replacing the previous one) and a new log starts.
        private const long MaxBytes = 10 * 1024 * 1024;

        // Each Smart Client run has its own process id, so lines from separate runs (and crash restarts) can be told apart.
        private static readonly int ProcessId = System.Diagnostics.Process.GetCurrentProcess().Id;

        // Line prefix matches Smart Client's MIPLog.txt (local time with UTC offset, thread id) so the two logs line up:
        //   2026-11-05 08:10:32.123-08:00 [pid 4120 tid  1] ERROR message :: exception with stack trace
        private static void Write(string level, string message)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                    var file = new FileInfo(FilePath);
                    if (file.Exists && file.Length > MaxBytes)
                    {
                        string previous = Path.Combine(Folder, "dewarp.1.log");
                        File.Delete(previous);
                        file.MoveTo(previous);
                    }
                    File.AppendAllText(FilePath,
                        $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fffzzz} [pid {ProcessId} tid {Environment.CurrentManagedThreadId,2}] {level} {message}{Environment.NewLine}");
                }
            }
            catch
            {
                // Logging must never take down Smart Client.
            }
        }
    }
}
