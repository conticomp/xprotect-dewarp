using System;
using System.Collections.Generic;
using VideoOS.Platform;
using VideoOS.Platform.Log;
using VideoOS.Platform.Util;

namespace FisheyeDewarp
{
    /// <summary>
    /// Export permission and XProtect audit log entries for everything that takes video out of Smart Client: the
    /// MP4 export and the snapshot. Both need the camera's Export permission, like Smart Client's own export.
    /// </summary>
    internal static class Audit
    {
        public const string Exported = "DewarpedExport";
        public const string ExportDenied = "DewarpedExportDenied";
        public const string Snapshot = "DewarpedSnapshot";
        public const string SnapshotDenied = "DewarpedSnapshotDenied";

        private const string ExportAction = "EXPORT";
        private const string App = "FisheyeDewarp";
        private const string Component = "Export";
        private static readonly object Gate = new object();
        private static bool _registered;

        /// <summary>
        /// Whether the current user may export this camera. A denial is written to the audit log under
        /// <paramref name="deniedMessageId"/>, since it is an attempt to take video out.
        /// </summary>
        public static bool CheckExportPermission(Item camera, string deniedMessageId)
        {
            try
            {
                SecurityAccess.CheckPermission(camera, ExportAction);
                return true;
            }
            catch (Exception ex)
            {
                Log.Info($"{deniedMessageId} for camera='{camera?.Name}': {ex.Message}");
                Entry(deniedMessageId, camera, PermissionState.Denied, new Dictionary<string, string> { ["camera"] = camera?.Name ?? "" });
                return false;
            }
        }

        public static void Entry(string messageId, Item camera, string permission, Dictionary<string, string> values)
        {
            try
            {
                if (!LogClient.Instance.Initialized)
                {
                    Log.Info("Audit: LogClient not initialized, no server audit entry written");
                    return;
                }
                lock (Gate)
                {
                    if (!_registered)
                    {
                        var messages = new Dictionary<string, LogMessage>
                        {
                            [Exported] = Message(Exported, "{user} exported dewarped video of {camera} from {start} to {end} ({frames} frames) on {workstation} to {file}"),
                            [ExportDenied] = Message(ExportDenied, "{user} was denied dewarped video export of {camera} on {workstation}: no export permission"),
                            [Snapshot] = Message(Snapshot, "{user} took a dewarped snapshot of {camera} at {time} ({view}, {size}) on {workstation} to {destination}"),
                            [SnapshotDenied] = Message(SnapshotDenied, "{user} was denied a dewarped snapshot of {camera} on {workstation}: no export permission"),
                        };
#pragma warning disable CS0618 // the 25.3+ overload with applicationName is not in 25.2
                        LogClient.Instance.RegisterDictionary(new LogMessageDictionary("en-US", "1.2", App, Component, messages, "Camera"));
#pragma warning restore CS0618
                        _registered = true;
                    }
                }
                values["user"] = CurrentUser();
                values["workstation"] = Environment.MachineName;
                // AuditEntry never throws: Smart Client queues the write and reports Log Server failures only in
                // its own logs (C:\ProgramData\Milestone\XProtect Smart Client\LegacyLogClientLog.txt, MIPLog.txt).
                LogClient.Instance.AuditEntry(App, Component, messageId, camera, values, permission);
                Log.Info($"Audit entry handed to Smart Client: {messageId} (Log Server delivery is not confirmed here; see LegacyLogClientLog.txt)");
            }
            catch (Exception ex)
            {
                Log.Error("Audit entry could not be handed to Smart Client", ex);
            }
        }

        /// <summary>
        /// The Smart Client user, written into the audit text itself so the entry names who exported even where
        /// the log viewer's User column is not shown. "Display Name (DOMAIN\account)" when both are known.
        /// </summary>
        public static string CurrentUser()
        {
            string windowsUser = Environment.UserDomainName + "\\" + Environment.UserName;
            try
            {
                var login = VideoOS.Platform.Login.LoginSettingsCache.GetLoginSettings(EnvironmentManager.Instance.MasterSite);
                string account = login?.FullyQualifiedUserName;
                string display = login?.UserName;
                if (string.IsNullOrWhiteSpace(account)) account = windowsUser;
                return string.IsNullOrWhiteSpace(display) || display == account ? account : $"{display} ({account})";
            }
            catch (Exception ex)
            {
                Log.Error("Could not read the logged-in user", ex);
                return windowsUser;
            }
        }

        /// <summary>Local time with its UTC offset, so the entry reads like the timeline and is still unambiguous.</summary>
        public static string Time(DateTime utc) => utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz");

        private static LogMessage Message(string id, string text) => new LogMessage
        {
            Id = id,
            Group = Group.Audit,
            Category = "Export",
            Severity = Severity.Info,
            Status = Status.StatusQuo,
            RelatedObjectKind = Kind.Camera,
            Message = text,
        };
    }
}
