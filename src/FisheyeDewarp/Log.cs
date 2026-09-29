using System;
using System.IO;

namespace FisheyeDewarp
{
    /// <summary>File log at %LOCALAPPDATA%\FisheyeDewarp\dewarp.log so behaviour can be inspected without a debugger.</summary>
    internal static class Log
    {
        private static readonly object Gate = new object();
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FisheyeDewarp", "dewarp.log");

        public static void Info(string message) => Write("INFO", message);

        public static void Error(string message, Exception ex = null) =>
            Write("ERROR", ex == null ? message : message + " :: " + ex);

        private static void Write(string level, string message)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                    File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
                }
            }
            catch
            {
                // Logging must never take down Smart Client.
            }
        }
    }
}
