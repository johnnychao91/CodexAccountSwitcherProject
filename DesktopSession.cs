using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Win32.SafeHandles;

namespace CodexAccountSwitcher
{
    public sealed class DesktopSessionState
    {
        public int ProtocolVersion { get; set; }
        public string SessionId { get; set; }
        public int Generation { get; set; }
        public DesktopProcessIdentity Host { get; set; }
        public DesktopProcessIdentity App { get; set; }
        public List<DesktopProcessIdentity> OwnedActors { get; set; }
        public string Home { get; set; }
        public string PipeName { get; set; }
        public bool Known { get; set; }
        public bool Idle { get; set; }
        public bool ControlEnabled { get; set; }
        public bool TransportReady { get; set; }
        public string State { get; set; }
        public int ActiveTurns { get; set; }
        public int PendingRequests { get; set; }
        public int BackgroundTerminals { get; set; }
        public bool AllBackendsExited { get; set; }
        public bool AppExitConfirmed { get; set; }
        public bool RecoveryAccepted { get; set; }
        public bool OwnerExitCleanup { get; set; }
        public string Reason { get; set; }
        public int? BackendExitCode { get; set; }
        public DesktopSessionState() { ProtocolVersion = 1; OwnedActors = new List<DesktopProcessIdentity>(); }
        public string Describe()
        {
            string detail = BackendExitCode.HasValue ? "受管後端已退出（代碼 " + BackendExitCode.Value + "），請正常 Quit 後重新啟用" :
                Reason == "desktopStatusTimeout" || Reason == "desktopStatusRejected" || Reason == "desktopStatusUnavailable" ? UnknownDetail() :
                !ControlEnabled ? (Reason == "localWorkPolicyChanged" ? "Local Work 選項已變更，請正常 Quit 後重新開啟受管桌面" : "自動控制已停用，請從 Codex 選單正常 Quit") : !Known ? UnknownDetail() :
                State == "quiesced" && AllBackendsExited && AppExitConfirmed ? "已正常暫停，等待恢復" :
                Idle ? "閒置" : "忙碌，執行中回合 " + ActiveTurns + "、待處理要求 " + PendingRequests + "、背景工作 " + BackgroundTerminals;
            return "桌面控制：" + detail + "。";
        }
        private string UnknownDetail()
        {
            string prefix = TransportReady ? "後端已連線，" : "";
            if (Reason == "initializing") return prefix + (TransportReady ? "正在取得工作狀態，已阻止切換" : "連線初始化中，已阻止切換");
            if (Reason == "runtimeQueryUnavailable") return prefix + "工作狀態查詢未完成，已阻止切換";
            if (Reason == "runtimeChangedDuringQuery") return prefix + "查詢期間工作狀態變動，尚未確認可安全切換";
            if (Reason == "desktopStatusTimeout") return "控制狀態查詢逾時，正在等待重新確認；尚未自動關閉";
            if (Reason == "desktopStatusRejected") return "控制狀態尚未確認，可能正在重新連線；已阻止切換";
            if (Reason == "desktopStatusUnavailable") return "暫時無法查詢控制狀態，已阻止切換";
            return prefix + "工作狀態未知，已阻止切換";
        }
    }

    internal sealed class DesktopIntegrationConfiguration
    {
        public int ProtocolVersion { get; set; }
        public bool Enabled { get; set; }
        public bool? ControlRequested { get; set; }
        public bool BlockLocalWork { get; set; }
        public string AuthorizationGeneration { get; set; }
        public string Home { get; set; }
        internal bool ResolveControlRequested()
        { return ControlRequested ?? (Enabled || String.IsNullOrEmpty(AuthorizationGeneration)); }
    }

    internal sealed class DesktopHostSpecification
    {
        public int ProtocolVersion { get; set; }
        public string SessionId { get; set; }
        public string Directory { get; set; }
        public string ConfigurationPath { get; set; }
        public string AuthorizationGeneration { get; set; }
        public string Home { get; set; }
        public string AppExecutable { get; set; }
        public string ControllerExecutable { get; set; }
        public string HostExecutable { get; set; }
        public string PackageFullName { get; set; }
        public string PackageFamilyName { get; set; }
        public string AppId { get; set; }
        public bool FirstEnable { get; set; }
        public bool BlockLocalWork { get; set; }
        public string UiProfile { get; set; }
        public string SqliteHome { get; set; }
        public DesktopProcessIdentity BootstrapHost { get; set; }
        // Only explicitly constructed private fixtures set this. It never bypasses MSIX validation.
        public string FixtureProfileRoot { get; set; }
        public string FixtureProxyUri { get; set; }
    }

    internal static class DesktopSessionFiles
    {
        private const int MaximumMetadataBytes = 131072;
        [StructLayout(LayoutKind.Sequential)]
        private struct MetadataFileInformation
        {
            internal uint Attributes, CreatedLow, CreatedHigh, AccessLow, AccessHigh, WrittenLow, WrittenHigh,
                VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern SafeFileHandle OpenMetadataFile(string path, uint access, uint share, IntPtr security,
            uint creation, uint attributes, IntPtr template);
        [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", ExactSpelling = true, SetLastError = true)]
        private static extern bool ReadMetadataInformation(SafeFileHandle file, out MetadataFileInformation information);
        internal static string ReadPrivateText(string path)
        {
            string fullPath = Path.GetFullPath(path);
            ManagedBridge.EnsureNoReparsePoint(Path.GetDirectoryName(fullPath));
            SafeFileHandle opened = null;
            for (int attempt = 0; ; attempt++)
            {
                // Open the leaf itself, including reparse points, and permit atomic
                // replacement. Security, bounds and data are checked on this handle.
                opened = OpenMetadataFile(fullPath, 0x80000000U, 7, IntPtr.Zero, 3, 0x00200000U, IntPtr.Zero);
                if (opened != null && !opened.IsInvalid) break;
                int error = Marshal.GetLastWin32Error(); if (opened != null) opened.Dispose();
                bool transient = error == 2 || error == 3 || error == 5 || error == 32 || error == 33 || error == 303;
                if (!transient || attempt >= 31)
                    throw new IOException("Desktop metadata could not be opened.", new System.ComponentModel.Win32Exception(error));
                Thread.Sleep(10);
            }
            using (opened)
            using (var stream = new FileStream(opened, FileAccess.Read, 4096, false))
            {
                MetadataFileInformation information;
                if (!ReadMetadataInformation(opened, out information) ||
                    (information.Attributes & (uint)(FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0 ||
                    information.SizeHigh != 0 || information.SizeLow > MaximumMetadataBytes)
                    throw new IOException("Invalid desktop integration metadata.");
                VerifySecurity(stream.GetAccessControl());
                // The additional byte also rejects a file that grows after the first
                // size check. A concurrent replacement cannot change the opened file.
                var bytes = new byte[MaximumMetadataBytes + 1]; int length = 0, count;
                while ((count = stream.Read(bytes, length, Math.Min(4096, bytes.Length - length))) != 0)
                {
                    length += count;
                    if (length > MaximumMetadataBytes) throw new IOException("Invalid desktop integration metadata.");
                }
                int offset = length >= 3 && bytes[0] == 239 && bytes[1] == 187 && bytes[2] == 191 ? 3 : 0;
                return new UTF8Encoding(false, true).GetString(bytes, offset, length - offset);
            }
        }
        internal static string Canonical(string value) { return Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        internal static string CapturePath(string name)
        {
            string value = Environment.GetEnvironmentVariable(name); if (String.IsNullOrWhiteSpace(value)) return null;
            if (!Path.IsPathRooted(value)) throw new UserError("桌面自訂資料位置必須使用絕對路徑，整合不會變更現有位置。");
            return Path.GetFullPath(value);
        }
        internal static bool EphemeralEnvironment(string key)
        {
            return key.StartsWith("VSCODE_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("CAS_DESKTOP_", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("CODEX_THREAD_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("CODEX_TURN_", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("CODEX_SESSION_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("CODEX_TOOL_", StringComparison.OrdinalIgnoreCase) ||
                new[] { "CODEX_APP_SERVER_WS_URL", "CODEX_APP_SERVER_FORCE_CLI", "CODEX_CLI_PATH", "CODEX_INTERNAL_ORIGINATOR_OVERRIDE", "ELECTRON_RUN_AS_NODE" }.Contains(key, StringComparer.OrdinalIgnoreCase);
        }
        internal static void ValidateExternalStartup(DesktopPackageTarget target)
        {
            if (!String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CODEX_APP_SERVER_WS_URL")) || Environment.GetEnvironmentVariable("CODEX_APP_SERVER_FORCE_CLI") == "1")
                throw new UserError("目前環境已有自訂桌面後端設定。請從一般 Windows 環境啟動切換器；整合不會覆寫外部後端。");
            string cli = Environment.GetEnvironmentVariable("CODEX_CLI_PATH");
            if (!String.IsNullOrWhiteSpace(cli) && !String.Equals(Path.GetFullPath(cli), target.CliExecutable, StringComparison.OrdinalIgnoreCase))
                throw new UserError("目前環境指定了外部 Codex CLI，桌面整合不會覆寫此設定。");
        }
        internal static void ValidateLocalWorkCliOverride(string requested, string verifiedCliExecutable, bool blockLocalWork)
        {
            if (blockLocalWork || String.IsNullOrWhiteSpace(requested)) return;
            bool same = false;
            try { same = Path.IsPathRooted(requested) && String.Equals(Path.GetFullPath(requested), Path.GetFullPath(verifiedCliExecutable), StringComparison.OrdinalIgnoreCase); }
            catch { }
            if (!same) throw new UserError("目前環境指定了外部 Local Work CLI，無法確認其工作狀態；請使用一般 Codex，或移除此外部設定後再啟用桌面控制。");
        }
        internal static bool ValidBootstrap(DesktopProcessIdentity bootstrap, string hostExecutable, int session)
        {
            if (bootstrap == null) return true; DateTimeOffset start;
            return bootstrap.Pid > 0 && bootstrap.SessionId == session && DateTimeOffset.TryParse(bootstrap.StartedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out start) &&
                String.Equals(Path.GetFullPath(bootstrap.ExecutablePath), Path.GetFullPath(hostExecutable), StringComparison.OrdinalIgnoreCase);
        }
        internal static DesktopHostSpecification ReadSpecification(string path)
        {
            string text = ReadPrivateText(path); var raw = ManagedBridge.Parse(text); object first;
            if (raw == null || Count(raw, "ProtocolVersion") != 1 || !raw.TryGetValue("FirstEnable", out first) || !(first is bool)) throw new IOException("Desktop launch fields missing.");
            RequireOptionalBool(raw, "BlockLocalWork");
            return new JavaScriptSerializer { MaxJsonLength = 131072 }.Deserialize<DesktopHostSpecification>(text);
        }
        internal static DesktopIntegrationConfiguration ReadConfiguration(string path)
        {
            string text = ReadPrivateText(path); var raw = ManagedBridge.Parse(text);
            if (raw == null || Count(raw, "ProtocolVersion") != 1 || String.IsNullOrWhiteSpace(Text(raw, "Home")) || !Path.IsPathRooted(Text(raw, "Home")))
                throw new IOException("Desktop configuration fields missing.");
            RequireBool(raw, "Enabled"); RequireOptionalBool(raw, "BlockLocalWork"); RequireOptionalBool(raw, "ControlRequested");
            object authorization;
            if (raw.TryGetValue("AuthorizationGeneration", out authorization) && authorization != null && !(authorization is string) ||
                Flag(raw, "Enabled") && String.IsNullOrWhiteSpace(Text(raw, "AuthorizationGeneration")))
                throw new IOException("Desktop authorization fields missing.");
            return new JavaScriptSerializer { MaxJsonLength = 131072 }.Deserialize<DesktopIntegrationConfiguration>(text);
        }
        internal static void VerifyPrivate(string path, bool file)
        {
            ManagedBridge.EnsureNoReparsePoint(file ? Path.GetDirectoryName(path) : path);
            if (file)
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > 131072 || (info.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Invalid desktop integration metadata.");
            }
            FileSystemSecurity security = file ? (FileSystemSecurity)File.GetAccessControl(path) : Directory.GetAccessControl(path);
            VerifySecurity(security);
        }
        private static void VerifySecurity(FileSystemSecurity security)
        {
            using (var identity = WindowsIdentity.GetCurrent())
            {
                var current = identity.User;
                if (!security.GetOwner(typeof(SecurityIdentifier)).Equals(current)) throw new IOException("Desktop metadata owner changed.");
                foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                    if (rule.AccessControlType == AccessControlType.Allow && !rule.IdentityReference.Equals(current) &&
                        !rule.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)))
                        throw new IOException("Desktop metadata is not private to this Windows user.");
            }
        }
        internal static void CreatePrivate(string directory)
        { ManagedBridge.PrivateDirectory(directory); VerifyPrivate(directory, false); }
        internal static T Read<T>(string path)
        { return new JavaScriptSerializer { MaxJsonLength = 131072 }.Deserialize<T>(ReadPrivateText(path)); }
        internal static void Write(string path, object value)
        {
            VerifyPrivate(Path.GetDirectoryName(path), false);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var serializer = new JavaScriptSerializer { MaxJsonLength = 131072 };
            string encoded = serializer.Serialize(value);
            var configuration = value as DesktopIntegrationConfiguration;
            if (configuration != null && !configuration.ControlRequested.HasValue)
            {
                // Existing typed fixtures and legacy writers retain an omitted preference.
                // An explicit JSON null remains invalid when reading untrusted metadata.
                var raw = ManagedBridge.Parse(encoded); raw.Remove("ControlRequested"); encoded = serializer.Serialize(raw);
            }
            if (File.Exists(path)) VerifyPrivate(path, true);
            try
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(encoded);
                using (var stream = ManagedBridge.CreatePrivateFile(temporary))
                { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                VerifyPrivate(temporary, true);
                if (File.Exists(path)) { VerifyPrivate(path, true); File.Replace(temporary, path, null); } else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        internal static bool IdentityEquals(DesktopProcessIdentity a, DesktopProcessIdentity b)
        { return a != null && b != null && a.Pid == b.Pid && a.StartedUtc == b.StartedUtc && String.Equals(a.ExecutablePath, b.ExecutablePath, StringComparison.OrdinalIgnoreCase) && a.SessionId == b.SessionId; }
        internal static bool Flag(Dictionary<string, object> value, string key)
        { object item; return value != null && value.TryGetValue(key, out item) && Object.Equals(item, true); }
        internal static int Count(Dictionary<string, object> value, string key)
        { object item; return value != null && value.TryGetValue(key, out item) && item is int && (int)item >= 0 ? (int)item : -1; }
        internal static string Text(Dictionary<string, object> value, string key)
        { object item; return value != null && value.TryGetValue(key, out item) ? item as string : null; }
        internal static bool Quiet(Dictionary<string, object> value)
        { return Flag(value, "known") && Flag(value, "idle") && Count(value, "activeTurns") == 0 && Count(value, "pendingRequests") == 0 && Count(value, "backgroundTerminals") == 0; }
        private static void RequireOptionalBool(Dictionary<string, object> raw, string key)
        { object item; if (raw.TryGetValue(key, out item) && !(item is bool)) throw new IOException("Invalid desktop boolean field."); }
        private static void RequireBool(Dictionary<string, object> raw, string key)
        { object value; if (!raw.TryGetValue(key, out value) || !(value is bool)) throw new IOException("Desktop snapshot boolean missing."); }
        private static void ValidateIdentity(object raw)
        {
            var value = raw as Dictionary<string, object>; DateTimeOffset started;
            if (value == null || Count(value, "Pid") <= 0 || Count(value, "ParentPid") < 0 || Count(value, "SessionId") < 0 ||
                !DateTimeOffset.TryParse(Text(value, "StartedUtc"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out started) ||
                String.IsNullOrWhiteSpace(Text(value, "ExecutablePath")) || !Path.IsPathRooted(Text(value, "ExecutablePath"))) throw new IOException("Desktop snapshot actor identity missing.");
        }
        internal static void ValidateSnapshot(Dictionary<string, object> raw)
        { ValidateSnapshot(raw, true); }
        private static void ValidateSnapshot(Dictionary<string, object> raw, bool requireQuiescence)
        {
            Guid session;
            if (raw == null || Count(raw, "ProtocolVersion") != 1 || Count(raw, "Generation") < 0 ||
                !Guid.TryParseExact(Text(raw, "SessionId"), "N", out session) ||
                !new[] { "starting", "running", "orphaned", "preparing", "prepared", "committing", "closingApp", "quiesced", "recoveryRequired", "ownerExitCleanup", "exited", "unknown" }.Contains(Text(raw, "State")) ||
                String.IsNullOrWhiteSpace(Text(raw, "Home")) || !Path.IsPathRooted(Text(raw, "Home")) ||
                Text(raw, "PipeName") != "CodexAccountSwitcher.desktop." + Text(raw, "SessionId") ||
                Count(raw, "ActiveTurns") < 0 || Count(raw, "PendingRequests") < 0 || Count(raw, "BackgroundTerminals") < 0) throw new IOException("Desktop snapshot fields missing.");
            foreach (string key in new[] { "Known", "Idle", "ControlEnabled", "AllBackendsExited", "AppExitConfirmed", "RecoveryAccepted", "OwnerExitCleanup" }) RequireBool(raw, key);
            if (raw.ContainsKey("TransportReady")) RequireBool(raw, "TransportReady");
            object exitCode;
            if (raw.TryGetValue("BackendExitCode", out exitCode) && exitCode != null &&
                (!(exitCode is int) || Flag(raw, "Known") || Flag(raw, "Idle") || requireQuiescence && Text(raw, "State") == "quiesced"))
                throw new IOException("Contradictory desktop backend exit diagnostic.");
            object host, app, actors;
            if (!raw.TryGetValue("Host", out host) || !raw.TryGetValue("App", out app) || !raw.TryGetValue("OwnedActors", out actors)) throw new IOException("Desktop snapshot actors missing.");
            ValidateIdentity(host); if (app != null) ValidateIdentity(app);
            var list = actors as object[]; if (list == null || list.Length == 0 || list.Length > 256) throw new IOException("Desktop snapshot actor list invalid.");
            foreach (var actor in list) ValidateIdentity(actor);
            if (Flag(raw, "Idle") && (!Flag(raw, "Known") || Count(raw, "ActiveTurns") != 0 || Count(raw, "PendingRequests") != 0 || Count(raw, "BackgroundTerminals") != 0)) throw new IOException("Contradictory desktop idle snapshot.");
            if (requireQuiescence && Text(raw, "State") == "quiesced" && (!Flag(raw, "Known") || !Flag(raw, "Idle") || !Flag(raw, "AllBackendsExited") || !Flag(raw, "AppExitConfirmed"))) throw new IOException("Incomplete desktop quiescence snapshot.");
        }
        internal static DesktopSessionState ReadSnapshot(string path)
        {
            string text = ReadPrivateText(path); var raw = ManagedBridge.Parse(text);
            // Older hosts could retain their private quiesced transaction after the
            // public proof was lost. Validate every structural/security field first,
            // then downgrade that historic state; never manufacture idle/exit proof.
            ValidateSnapshot(raw, false);
            object historicExit;
            bool hasHistoricExit = raw.TryGetValue("BackendExitCode", out historicExit) && historicExit != null;
            if (Text(raw, "State") == "quiesced" &&
                (!Flag(raw, "Known") || !Flag(raw, "Idle") || !Flag(raw, "AllBackendsExited") || !Flag(raw, "AppExitConfirmed") || hasHistoricExit))
            {
                raw["State"] = "recoveryRequired"; raw["Known"] = raw["Idle"] = false;
                raw["Reason"] = "desktopQuiescenceProofLost";
            }
            ValidateSnapshot(raw);
            return new JavaScriptSerializer { MaxJsonLength = 131072 }.Deserialize<DesktopSessionState>(ManagedBridge.Encode(raw));
        }
    }

    public sealed class DesktopSessionClient
    {
        private readonly string integration, sessions, configuration, home, hostExecutable, controller;
        private readonly Func<List<ProcessBlocker>> inspect;
        private readonly Func<string, DesktopPackageTarget> validatePrerequisites;
        private readonly HashSet<string> touched = new HashSet<string>(StringComparer.Ordinal);
        private DesktopContinuationCheckpoint continuationRestore;
        private string continuationOperation;
        public bool HadManagedDesktop { get; private set; }
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint pid);
        public DesktopSessionClient(string storage, string home) : this(storage, home, ProcessInspector.Inspect) { }
        internal DesktopSessionClient(string storage, string home, Func<List<ProcessBlocker>> inspect) : this(storage, home, inspect, null) { }
        internal DesktopSessionClient(string storage, string home, Func<List<ProcessBlocker>> inspect,
            Func<string, DesktopPackageTarget> validatePrerequisites)
        {
            integration = Path.Combine(Path.GetFullPath(storage), "integrations", "desktop"); sessions = Path.Combine(integration, "sessions");
            configuration = Path.Combine(integration, "config.json"); this.home = DesktopSessionFiles.Canonical(home); this.inspect = inspect;
            controller = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CodexAccountSwitcher.exe");
            hostExecutable = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DesktopRuntimeHost.exe");
            // Public and ordinary construction always retain the real package/policy
            // validation. The internal delegate lets isolated tests stop inside the
            // existing waiter without requiring or starting an installed desktop App.
            this.validatePrerequisites = validatePrerequisites ?? new Func<string, DesktopPackageTarget>(ValidateLaunchPrerequisites);
        }
        public bool Enabled
        {
            get
            {
                if (!File.Exists(configuration)) return false;
                try { var config = ReadConfiguration(); return config.Enabled && config.ResolveControlRequested(); }
                catch { return false; }
            }
        }
        private DesktopIntegrationConfiguration ReadConfiguration()
        {
            bool missing = false;
            try
            {
                var attributes = File.GetAttributes(configuration);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                    throw new IOException("Desktop configuration is not a regular file.");
            }
            catch (FileNotFoundException) { missing = true; }
            catch (DirectoryNotFoundException) { missing = true; }
            if (missing)
            {
                // File.Exists also returns false for access failures and directories.
                // The on default is allowed only after positive absence; an unreadable,
                // redirected or non-directory parent must remain an unknown state.
                for (var parent = new DirectoryInfo(Path.GetDirectoryName(configuration)); parent != null; parent = parent.Parent)
                {
                    try
                    {
                        var attributes = File.GetAttributes(parent.FullName);
                        if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
                            throw new IOException("Desktop configuration directory changed.");
                    }
                    catch (FileNotFoundException) { }
                    catch (DirectoryNotFoundException) { }
                }
                return new DesktopIntegrationConfiguration { ProtocolVersion = 1, Home = home };
            }
            var config = DesktopSessionFiles.ReadConfiguration(configuration);
            if (!String.Equals(DesktopSessionFiles.Canonical(config.Home), home, StringComparison.OrdinalIgnoreCase)) throw new IOException("Desktop configuration home changed.");
            return config;
        }
        public bool ControlRequested { get { return ReadConfiguration().ResolveControlRequested(); } }
        public void InitializeControlPreference()
        {
            var config = ReadConfiguration();
            if (config.ControlRequested.HasValue) return;
            config.ControlRequested = config.ResolveControlRequested();
            DesktopSessionFiles.CreatePrivate(integration); DesktopSessionFiles.Write(configuration, config);
        }
        public void SetControlRequested(bool value)
        {
            var config = ReadConfiguration(); config.ControlRequested = value;
            // Requested mode is a durable user preference, never a substitute for the
            // first-enable guard, an authorization generation, or a live status proof.
            if (!value) config.Enabled = false;
            DesktopSessionFiles.CreatePrivate(integration); DesktopSessionFiles.Write(configuration, config);
        }
        public bool BlockLocalWork { get { return ReadConfiguration().BlockLocalWork; } }
        public void SetBlockLocalWork(bool value)
        {
            var config = ReadConfiguration(); config.BlockLocalWork = value;
            // A preference cannot authorize control or create an authorization generation.
            // A running host with a different choice becomes uncontrolled until normal Quit.
            DesktopSessionFiles.CreatePrivate(integration); DesktopSessionFiles.Write(configuration, config);
        }
        private List<DesktopSessionState> Records()
        {
            var result = new List<DesktopSessionState>();
            if (!Directory.Exists(sessions)) return result;
            DesktopSessionFiles.VerifyPrivate(sessions, false);
            var folders = Directory.GetDirectories(sessions);
            if (folders.Length > 64) throw new UserError("桌面整合記錄過多，請先檢查先前工作階段。");
            foreach (string folder in folders)
            {
                string path = Path.Combine(folder, "session.json"); if (!File.Exists(path)) continue;
                try
                {
                    var record = DesktopSessionFiles.ReadSnapshot(path); Guid id;
                    if (record == null || record.ProtocolVersion != 1 || !Guid.TryParseExact(record.SessionId, "N", out id) || Path.GetFileName(folder) != record.SessionId ||
                        record.Generation < 0 || record.Host == null || record.Host.Pid <= 0 || record.OwnedActors == null || record.OwnedActors.Count > 256 ||
                        !String.Equals(DesktopSessionFiles.Canonical(record.Home), home, StringComparison.OrdinalIgnoreCase) ||
                        String.IsNullOrEmpty(record.PipeName) || record.PipeName != "CodexAccountSwitcher.desktop." + record.SessionId)
                        throw new UserError("桌面整合記錄驗證失敗，已停止自動控制。");
                    if (!CurrentInstallation(record))
                    {
                        // An older installation authenticates only its original controller. Its
                        // private catalog may identify blockers, but never grants this controller
                        // permission to query/control it or exempt any of its actors from guards.
                        if (!DesktopNative.Same(record.Host) && !record.OwnedActors.Any(DesktopNative.Same)) continue;
                        record.Known = record.Idle = record.ControlEnabled = false;
                        record.AllBackendsExited = record.AppExitConfirmed = record.RecoveryAccepted = false;
                        record.State = "unknown"; record.Reason = "previousInstallationStillRunning";
                    }
                    result.Add(record);
                }
                catch (UserError) { throw; }
                catch (Exception error)
                { throw new UserError("無法驗證桌面整合記錄（" + error.GetType().Name + "／" + unchecked((uint)error.HResult).ToString("X8") + "），已停止切換。請重新整理後再試。"); }
            }
            return result;
        }
        private bool CurrentInstallation(DesktopSessionState record)
        {
            return record != null && record.Host != null &&
                String.Equals(DesktopSessionFiles.Canonical(record.Host.ExecutablePath), DesktopSessionFiles.Canonical(hostExecutable), StringComparison.OrdinalIgnoreCase);
        }
        public Task EnableAndLaunchAsync(string appExe, CancellationToken cancellation)
        { return EnableAndLaunchAsync(appExe, null, cancellation); }
        public async Task EnableAndLaunchAsync(string appExe, Action<string> status, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            validatePrerequisites(appExe);
            SetControlRequested(true);
            await DesktopFirstEnableWaiter.WaitAsync(async delegate
            {
                var blockers = inspect();
                if (blockers == null) throw new UserError("無法確認背景程序，已停止首次啟用。");
                foreach (var state in await InspectAsync(cancellation).ConfigureAwait(false))
                    foreach (var actor in new[] { state.Host }.Concat(state.OwnedActors))
                        if (DesktopNative.Same(actor) && !blockers.Any(p => p.Pid == actor.Pid))
                            blockers.Add(new ProcessBlocker { Pid = actor.Pid, Kind = "Integration", Name = Path.GetFileName(actor.ExecutablePath),
                                CreationTime = actor.StartedUtc, Description = DesktopSessionFiles.IdentityEquals(actor, state.Host) ?
                                    "尚未退出的桌面控制協調器" : "尚未退出的受管桌面程序" });
                return blockers;
            }, status, cancellation, TimeSpan.FromMilliseconds(700), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5)).ConfigureAwait(false);
            await LaunchCore(appExe, cancellation, true).ConfigureAwait(false);
        }
        public Task LaunchIfEnabledAsync(string appExe, CancellationToken cancellation)
        { return LaunchIfEnabledAsync(appExe, null, cancellation); }
        public Task LaunchIfEnabledAsync(string appExe, Action<string> status, CancellationToken cancellation)
        { return LaunchIfEnabledAsync(appExe, status, cancellation, false); }
        public async Task OpenManagedAsync(string appExe, Action<string> status, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!ControlRequested) throw new UserError("桌面控制已關閉，已停止受管啟動；登入資料未變更。");
            if (Enabled) await LaunchIfEnabledAsync(appExe, status, cancellation, true).ConfigureAwait(false);
            else await EnableAndLaunchAsync(appExe, status, cancellation).ConfigureAwait(false);
        }
        private async Task LaunchIfEnabledAsync(string appExe, Action<string> status, CancellationToken cancellation, bool waitForUnmanaged)
        {
            if (!Enabled) return;
            var states = await InspectAsync(cancellation).ConfigureAwait(false);
            if (states.Any(s => s.Reason == "previousInstallationStillRunning"))
            {
                cancellation.ThrowIfCancellationRequested();
                validatePrerequisites(appExe);
                if (status != null) status("偵測到舊版桌面整合，正在撤銷舊控制並等候正常退出。請保持切換器開啟，再從 Codex 選單選「結束 ChatGPT / Quit」。");
                await DisableAsync(cancellation).ConfigureAwait(false);
                await EnableAndLaunchAsync(appExe, status, cancellation).ConfigureAwait(false);
                return;
            }
            foreach (var state in states.Where(s => DesktopNative.Same(s.Host)))
            {
                if (!state.Known || !state.ControlEnabled) throw new UserError("既有桌面整合狀態未知，請先正常 Quit 後恢復，避免重複啟動。");
                if (state.State == "quiesced" && state.AllBackendsExited && state.AppExitConfirmed)
                { touched.Add(state.SessionId); HadManagedDesktop = true; await RestoreAsync().ConfigureAwait(false); return; }
                if (state.State == "running") return;
                throw new UserError("桌面整合仍在處理先前的正常退出，請稍後重試。");
            }
            if (states.Any(s => s.OwnedActors.Any(DesktopNative.Same)) || inspect().Any(p => p.Kind == "Desktop"))
            {
                if (!waitForUnmanaged) throw new UserError("仍有未受控的桌面程序，請先從 Codex 選單正常 Quit；不會啟動第二個工作階段。");
                cancellation.ThrowIfCancellationRequested();
                if (!ControlRequested) throw new UserError("桌面控制已關閉，已停止受管啟動；登入資料未變更。");
                await EnableAndLaunchAsync(appExe, status, cancellation).ConfigureAwait(false);
                return;
            }
            await LaunchCore(appExe, cancellation, false).ConfigureAwait(false);
        }
        private DesktopPackageTarget ValidateLaunchPrerequisites(string appExe)
        {
            var target = DesktopPackageTarget.Resolve(appExe);
            DesktopSessionFiles.ValidateExternalStartup(target);
            DesktopSessionFiles.ValidateLocalWorkCliOverride(Environment.GetEnvironmentVariable("CODEX_TPP_LOCAL_EXECUTOR_CLI_PATH"), target.CliExecutable, BlockLocalWork);
            if (BlockLocalWork) DesktopLocalWorkPolicy.ValidateTarget(target);
            string relay = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DesktopAppRelay.exe");
            if (!File.Exists(hostExecutable) || !File.Exists(controller) || !File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "desktop-start.ps1")) ||
                !File.Exists(relay) || (File.GetAttributes(relay) & FileAttributes.ReparsePoint) != 0)
                throw new UserError("桌面整合元件不完整，請使用完整發行資料夾。");
            DesktopSessionFiles.CapturePath("CODEX_ELECTRON_USER_DATA_PATH"); DesktopSessionFiles.CapturePath("CODEX_SQLITE_HOME");
            return target;
        }
        private async Task LaunchCore(string appExe, CancellationToken cancellation, bool firstEnable)
        {
            cancellation.ThrowIfCancellationRequested();
            var target = validatePrerequisites(appExe);
            var config = ReadConfiguration();
            if (!config.ResolveControlRequested()) throw new UserError("桌面控制已關閉，已停止啟動；登入資料未變更。");
            if (config.BlockLocalWork) DesktopLocalWorkPolicy.ValidateTarget(target);
            string uiProfile = DesktopSessionFiles.CapturePath("CODEX_ELECTRON_USER_DATA_PATH"), sqliteHome = DesktopSessionFiles.CapturePath("CODEX_SQLITE_HOME");
            DesktopSessionFiles.CreatePrivate(integration); DesktopSessionFiles.CreatePrivate(sessions);
            string authorization = firstEnable ? Guid.NewGuid().ToString("N") : config.AuthorizationGeneration;
            string id = Guid.NewGuid().ToString("N"), directory = Path.Combine(sessions, id);
            DesktopSessionFiles.CreatePrivate(directory);
            config.Enabled = true; config.ControlRequested = true; config.AuthorizationGeneration = authorization;
            DesktopSessionFiles.Write(configuration, config);
            var specification = new DesktopHostSpecification { ProtocolVersion = 1, SessionId = id, Directory = directory, Home = home,
                ConfigurationPath = configuration, AuthorizationGeneration = authorization, AppExecutable = target.AppExecutable,
                ControllerExecutable = controller, HostExecutable = hostExecutable, PackageFullName = target.PackageFullName,
                PackageFamilyName = target.PackageFamilyName, AppId = target.AppId, BlockLocalWork = config.BlockLocalWork };
            specification.FirstEnable = firstEnable;
            specification.UiProfile = uiProfile; specification.SqliteHome = sqliteHome;
            string specPath = Path.Combine(directory, "launch.json"); DesktopSessionFiles.Write(specPath, specification);
            using (var process = Process.Start(new ProcessStartInfo(hostExecutable, "--launch " + ManagedBridge.QuoteArgument(specPath)) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory })) { }
            var clock = Stopwatch.StartNew(); long? readySince = null; int readyGeneration = -1;
            while (clock.Elapsed < TimeSpan.FromSeconds(120))
            {
                cancellation.ThrowIfCancellationRequested();
                if (!Enabled) throw new UserError("桌面自動控制已停用，已停止啟動等待；已開啟的程式可繼續正常運作。");
                var states = await InspectAsync(cancellation).ConfigureAwait(false);
                var launched = states.SingleOrDefault(s => s.SessionId == id);
                string statePath = Path.Combine(directory, "session.json");
                var observed = launched;
                if (observed == null && File.Exists(statePath)) observed = DesktopSessionFiles.ReadSnapshot(statePath);
                if (observed != null && observed.BackendExitCode.HasValue)
                    throw new UserError("受管後端啟動後已退出（代碼 " + observed.BackendExitCode.Value + "）。請先停用桌面自動控制、正常 Quit，再重新啟用；可先從開始功能表使用一般 Codex。");
                if (IsLaunchTransportReady(launched, id))
                {
                    if (!readySince.HasValue || readyGeneration != launched.Generation)
                    { readySince = clock.ElapsedMilliseconds; readyGeneration = launched.Generation; }
                    if (clock.ElapsedMilliseconds - readySince.Value >= 3000) return;
                }
                else readySince = null;
                if (File.Exists(Path.Combine(directory, "launch-error.json"))) throw new UserError("Windows 未能啟動桌面整合；現有工作未被關閉。請先正常 Quit 後再試。");
                await Task.Delay(300, cancellation).ConfigureAwait(false);
            }
            throw new UserError("桌面整合尚未確認準備完成。請檢查 Codex 視窗；請勿重複啟動或強制結束工作。");
        }
        internal static bool IsLaunchTransportReady(DesktopSessionState state, string sessionId)
        {
            // Readiness establishes this connection only. Known/Idle remain mandatory
            // in all process-filtering, quiescence and account-application gates.
            return state != null && state.SessionId == sessionId && state.Generation > 0 &&
                state.TransportReady && state.ControlEnabled && state.State == "running" && !state.BackendExitCode.HasValue;
        }
        public async Task DisableAsync(CancellationToken cancellation)
        {
            // Revocation is durable before any optional pipe operation, including cancellation.
            if (File.Exists(configuration))
            {
                var config = ReadConfiguration(); config.ControlRequested = config.ResolveControlRequested(); config.Enabled = false;
                DesktopSessionFiles.Write(configuration, config);
            }
            foreach (var record in Records())
            {
                cancellation.ThrowIfCancellationRequested();
                if (!CurrentInstallation(record)) continue;
                try { await Request(record, "disable", cancellation, false).ConfigureAwait(false); }
                catch { cancellation.ThrowIfCancellationRequested(); }
            }
        }
        private async Task<DesktopSessionState> Request(DesktopSessionState record, string method, CancellationToken cancellation, bool recovery,
            Dictionary<string, object> payload = null, Action<Dictionary<string, object>> observe = null)
        {
            if (!CurrentInstallation(record)) throw new UserError("舊版桌面整合尚未退出，請從 Codex 選單正常 Quit；新版不會接管舊版控制管線。");
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                timeout.CancelAfter(method == "quiesce" ? 120000 : method == "restore" || method.StartsWith("continuation", StringComparison.Ordinal) ? 20000 : 10000);
                try
                {
                    var connectClock = Stopwatch.StartNew(); NamedPipeClientStream pipe = null;
                    while (pipe == null)
                    {
                        timeout.Token.ThrowIfCancellationRequested();
                        if (!DesktopNative.Same(record.Host)) throw new UserError("桌面整合協調程序已改變，請正常 Quit 後重新啟用。");
                        var candidate = new NamedPipeClientStream(".", record.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                        bool retry = false;
                        try { await candidate.ConnectAsync(1500, timeout.Token).ConfigureAwait(false); pipe = candidate; }
                        catch (Exception error)
                        {
                            candidate.Dispose();
                            if (!(error is IOException || error is TimeoutException) || !recovery || connectClock.ElapsedMilliseconds > 12000) throw;
                            retry = true;
                        }
                        if (retry) await Task.Delay(100, timeout.Token).ConfigureAwait(false);
                    }
                    using (pipe)
                    using (timeout.Token.Register(() => { try { pipe.Dispose(); } catch { } }))
                    {
                        uint actual;
                        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out actual) || actual != record.Host.Pid || !DesktopNative.Same(record.Host))
                            throw new UserError("無法驗證桌面整合管線的擁有程序。");
                        using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true))
                        using (var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true))
                        {
                            string id = Guid.NewGuid().ToString("N");
                            var request = new Dictionary<string, object> { { "id", id }, { "method", method },
                                { "expectedSessionId", record.SessionId }, { "expectedGeneration", record.Generation } };
                            if (payload != null) foreach (var entry in payload)
                            {
                                if (request.ContainsKey(entry.Key)) throw new IOException("Continuation cannot override request identity.");
                                request.Add(entry.Key, entry.Value);
                            }
                            await writer.WriteLineAsync(new JavaScriptSerializer().Serialize(request)).ConfigureAwait(false);
                            await writer.FlushAsync().ConfigureAwait(false); // No mutation is ever retried after this write.
                            var line = new StringBuilder(); var one = new char[1];
                            while (line.Length < 131072)
                            { int count = await reader.ReadAsync(one, 0, 1).ConfigureAwait(false); if (count == 0) throw new IOException("Desktop control response was lost."); if (one[0] == '\n') break; line.Append(one[0]); }
                            if (line.Length >= 131072) throw new IOException("Desktop response exceeds its bound.");
                            var response = ManagedBridge.Parse(line.ToString());
                            if (DesktopSessionFiles.Text(response, "id") != id || response.ContainsKey("error")) throw new UserError("桌面整合未接受要求，已停止切換。請重新檢查工作狀態。");
                            object raw; if (!response.TryGetValue("result", out raw)) throw new IOException("Desktop state missing.");
                            DesktopSessionFiles.ValidateSnapshot(raw as Dictionary<string, object>);
                            var state = new JavaScriptSerializer().Deserialize<DesktopSessionState>(new JavaScriptSerializer().Serialize(raw));
                            if (state == null || state.SessionId != record.SessionId || !DesktopSessionFiles.IdentityEquals(state.Host, record.Host) ||
                                (!recovery && state.Generation != record.Generation) || state.Generation < record.Generation || state.OwnedActors == null || state.OwnedActors.Count > 256 ||
                                !String.Equals(DesktopSessionFiles.Canonical(state.Home), home, StringComparison.OrdinalIgnoreCase)) throw new IOException("Desktop response identity changed.");
                            if (state.Idle && (!state.Known || state.ActiveTurns != 0 || state.PendingRequests != 0 || state.BackgroundTerminals != 0)) throw new IOException("Contradictory desktop state.");
                            if (observe != null) observe(response);
                            return state;
                        }
                    }
                }
                catch
                {
                    // Closing a pipe at its deadline can surface as an IO/disposal
                    // error. Preserve the cancellation cause while its source is live.
                    timeout.Token.ThrowIfCancellationRequested();
                    throw;
                }
            }
        }
        public async Task<List<DesktopSessionState>> InspectAsync(CancellationToken cancellation)
        {
            var result = new List<DesktopSessionState>();
            foreach (var record in Records())
            {
                cancellation.ThrowIfCancellationRequested();
                if (!DesktopNative.Same(record.Host) && !record.OwnedActors.Any(DesktopNative.Same)) continue;
                if (!CurrentInstallation(record)) { result.Add(record); continue; }
                try { result.Add(await Request(record, "status", cancellation, false).ConfigureAwait(false)); }
                catch (Exception error)
                {
                    cancellation.ThrowIfCancellationRequested(); record.Known = false; record.Idle = false; record.ControlEnabled = false; record.State = "unknown";
                    record.Reason = error is OperationCanceledException || error is TimeoutException ? "desktopStatusTimeout" :
                        error is UserError ? "desktopStatusRejected" : "desktopStatusUnavailable";
                    result.Add(record);
                }
            }
            return result;
        }
        private void ValidateContinuationBinding(DesktopContinuationCheckpoint checkpoint)
        {
            Guid session, thread;
            if (checkpoint == null || !Guid.TryParseExact(checkpoint.SessionId, "N", out session) || checkpoint.Generation < 1 ||
                !Guid.TryParseExact(checkpoint.ThreadId, "D", out thread) || String.IsNullOrWhiteSpace(checkpoint.TurnId) ||
                !String.Equals(DesktopSessionFiles.Canonical(checkpoint.Home), home, StringComparison.OrdinalIgnoreCase))
                throw new UserError("續聊紀錄與目前桌面不符，已停止自動送出。");
        }
        public async Task<DesktopSessionState> GetContinuationSessionAsync(DesktopContinuationCheckpoint checkpoint, CancellationToken cancellation)
        {
            ValidateContinuationBinding(checkpoint);
            var states = await InspectAsync(cancellation).ConfigureAwait(false);
            var state = states.SingleOrDefault(s => s.SessionId == checkpoint.SessionId);
            if (state == null || !CurrentInstallation(state) || !state.Known || !state.ControlEnabled || !DesktopNative.Same(state.Host))
                throw new UserError("續聊所屬的受管桌面尚未確認，已停止自動送出。");
            return state;
        }
        public async Task<DesktopContinuationCheckpoint> CaptureContinuationAsync(CancellationToken cancellation)
        {
            if (!ControlRequested) return null;
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(30))
            {
                cancellation.ThrowIfCancellationRequested();
                if (!ControlRequested || !Enabled) throw new UserError("桌面自動控制已停用，已停止擷取續聊。");
                var states = await InspectAsync(cancellation).ConfigureAwait(false);
                if (states.Count == 0) return null;
                if (states.Count != 1 || !states[0].Known || !states[0].ControlEnabled || !states[0].TransportReady || states[0].State != "running")
                    throw new UserError("無法確認單一受管桌面，已停止自動中止與續聊。");
                object raw = null; Exception captureFailure = null;
                try { await Request(states[0], "continuationCapture", cancellation, false, null, response => response.TryGetValue("continuation", out raw)).ConfigureAwait(false); }
                catch (Exception error) { captureFailure = error; }
                cancellation.ThrowIfCancellationRequested();
                if (captureFailure != null)
                {
                    var fresh = (await InspectAsync(cancellation).ConfigureAwait(false)).SingleOrDefault(item => item.SessionId == states[0].SessionId);
                    if (fresh == null || !DesktopSessionFiles.IdentityEquals(fresh.Host, states[0].Host) || fresh.Generation <= states[0].Generation) throw captureFailure;
                    await Task.Delay(300, cancellation).ConfigureAwait(false); continue;
                }
                if (raw == null) return null;
                if (DesktopSessionFiles.Flag(raw as Dictionary<string, object>, "captureWaiting"))
                { await Task.Delay(300, cancellation).ConfigureAwait(false); continue; }
                var checkpoint = new JavaScriptSerializer().ConvertToType<DesktopContinuationCheckpoint>(raw);
                ValidateContinuationBinding(checkpoint);
                if (checkpoint.SessionId != states[0].SessionId || checkpoint.Generation != states[0].Generation)
                    throw new UserError("擷取續聊時桌面連線已變更，已停止自動中止。");
                return checkpoint;
            }
            throw new UserError("其他桌面連線仍有尚未結束的唯讀查詢，已停止擷取續聊；尚未中止或送出訊息。");
        }
        public void BindContinuationRestore(DesktopContinuationCheckpoint checkpoint, string operationId)
        {
            ValidateContinuationBinding(checkpoint); Guid operation;
            if (!Guid.TryParseExact(operationId, "N", out operation)) throw new IOException("Invalid continuation operation.");
            if (continuationRestore != null && continuationOperation != operationId) throw new UserError("另一筆續聊尚未處理，已停止建立新操作。");
            continuationRestore = Json.Decode<DesktopContinuationCheckpoint>(Json.Encode(checkpoint)); continuationOperation = operationId;
        }
        private static Dictionary<string, object> ContinuationPayload(DesktopContinuationCheckpoint checkpoint, string operationId)
        {
            var payload = new Dictionary<string, object> { { "continuation", checkpoint } };
            if (operationId != null) payload.Add("operationId", operationId);
            return payload;
        }
        public async Task WaitContinuationStoppedAsync(DesktopContinuationCheckpoint checkpoint, CancellationToken cancellation)
        {
            ValidateContinuationBinding(checkpoint); var clock = Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromMinutes(5))
            {
                cancellation.ThrowIfCancellationRequested();
                var state = await GetContinuationSessionAsync(checkpoint, cancellation).ConfigureAwait(false);
                if (state.Generation != checkpoint.Generation) throw new UserError("中止期間後端代次已變更，已停止切換。");
                Dictionary<string, object> data = null;
                await Request(state, "continuationStopped", cancellation, false, ContinuationPayload(checkpoint, null),
                    response => data = Json.Map(response, "continuation")).ConfigureAwait(false);
                if (data != null && DesktopSessionFiles.Flag(data, "stopped")) return;
                await Task.Delay(400, cancellation).ConfigureAwait(false);
            }
            throw new UserError("對話或背景工作尚未正常結束，未套用帳號；不會強制關閉。");
        }
        public async Task<DesktopContinuationCheckpoint> PrepareContinuationAsync(DesktopContinuationCheckpoint checkpoint,
            string expectedAccountId, string expectedEmail, CancellationToken cancellation)
        {
            ValidateContinuationBinding(checkpoint); var clock = Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(30))
            {
                cancellation.ThrowIfCancellationRequested();
                if (!ControlRequested || !Enabled) throw new UserError("桌面自動控制已停用，已停止續聊。");
                DesktopSessionState state = null; Exception lookupFailure = null;
                try { state = await GetContinuationSessionAsync(checkpoint, cancellation).ConfigureAwait(false); }
                catch (Exception error) { lookupFailure = error; }
                cancellation.ThrowIfCancellationRequested();
                if (lookupFailure != null && !(lookupFailure is UserError || lookupFailure is IOException)) throw lookupFailure;
                if (state == null || state.Generation <= checkpoint.Generation || !state.RecoveryAccepted || state.State != "running" || !state.TransportReady)
                { await Task.Delay(300, cancellation).ConfigureAwait(false); continue; }
                var payload = ContinuationPayload(checkpoint, continuationOperation);
                payload.Add("expectedAccountId", expectedAccountId); payload.Add("expectedEmail", expectedEmail);
                Dictionary<string, object> prepared = null; Exception prepareFailure = null;
                try
                {
                    await Request(state, "continuationPrepare", cancellation, false, payload,
                        response => prepared = Json.Map(response, "continuation")).ConfigureAwait(false);
                }
                catch (Exception error) { prepareFailure = error; }
                cancellation.ThrowIfCancellationRequested();
                if (prepareFailure != null)
                {
                    // Adding an independently initialized account connection can
                    // change group generation during a read-only preflight. Only
                    // that positively observed change permits another read.
                    var fresh = (await InspectAsync(cancellation).ConfigureAwait(false)).SingleOrDefault(item => item.SessionId == checkpoint.SessionId);
                    if (fresh == null || !DesktopSessionFiles.IdentityEquals(fresh.Host, state.Host) || fresh.Generation <= state.Generation) throw prepareFailure;
                    await Task.Delay(300, cancellation).ConfigureAwait(false); continue;
                }
                if (prepared != null && DesktopSessionFiles.Flag(prepared, "ready"))
                {
                    var restored = Json.Decode<DesktopContinuationCheckpoint>(Json.Encode(checkpoint)); restored.Generation = state.Generation;
                    return restored;
                }
                if (prepared == null || !DesktopSessionFiles.Flag(prepared, "waitingForConversation"))
                    throw new UserError("續聊帳號、模型或權限尚未確認，已停止送出。");
                await Task.Delay(300, cancellation).ConfigureAwait(false);
            }
            throw new UserError("原對話尚未在完整受管桌面載入，已停止自動續聊；尚未送出訊息。");
        }
        public async Task<string> GetContinuationTurnAsync(DesktopContinuationCheckpoint checkpoint, string operationId, CancellationToken cancellation)
        {
            var state = await GetContinuationSessionAsync(checkpoint, cancellation).ConfigureAwait(false);
            Dictionary<string, object> receipt = null;
            await Request(state, "continuationReceipt", cancellation, false, ContinuationPayload(checkpoint, operationId),
                response => receipt = Json.Map(response, "continuation")).ConfigureAwait(false);
            return receipt == null ? null : DesktopSessionFiles.Text(receipt, "turnId");
        }
        public async Task CancelContinuationAsync(DesktopContinuationCheckpoint checkpoint, string operationId, CancellationToken cancellation)
        {
            ValidateContinuationBinding(checkpoint);
            try
            {
                var record = Records().SingleOrDefault(s => s.SessionId == checkpoint.SessionId);
                if (record == null || !CurrentInstallation(record) || !DesktopNative.Same(record.Host)) return;
                await Request(record, "continuationRelease", cancellation, true, ContinuationPayload(checkpoint, operationId)).ConfigureAwait(false);
            }
            finally
            {
                if (continuationOperation == operationId) { continuationRestore = null; continuationOperation = null; }
            }
        }
        public List<ProcessBlocker> FilterOwnedProcesses(List<ProcessBlocker> processes, IList<DesktopSessionState> states)
        {
            if (processes == null || states == null) throw new ArgumentNullException();
            return processes.Where(p => !states.Any(s => CurrentInstallation(s) && s.Known && s.ControlEnabled && DesktopNative.Same(s.Host) &&
                s.OwnedActors.Any(actor => actor.Pid == p.Pid && DesktopNative.Same(actor)))).ToList();
        }
        public async Task WaitForExitAsync(Action<string> status, CancellationToken cancellation)
        {
            HadManagedDesktop = false; var clock = Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromMinutes(5))
            {
                cancellation.ThrowIfCancellationRequested();
                var states = await InspectAsync(cancellation).ConfigureAwait(false);
                if (status != null && states.Count != 0) status(String.Join("\n", states.Select(s => s.Describe())));
                bool quiet = true;
                foreach (var state in states)
                {
                    if (!state.Known || !state.ControlEnabled) { quiet = false; continue; }
                    if (state.State == "quiesced" && state.AllBackendsExited && state.AppExitConfirmed)
                    { touched.Add(state.SessionId); HadManagedDesktop = true; continue; }
                    quiet = false; if (!state.Idle) continue;
                    touched.Add(state.SessionId); HadManagedDesktop = true;
                    var stopped = await Request(state, "quiesce", cancellation, false).ConfigureAwait(false);
                    if (!stopped.Known || !stopped.Idle || stopped.State != "quiesced" || !stopped.AllBackendsExited || !stopped.AppExitConfirmed)
                        throw new UserError("桌面程式未確認完整正常結束，登入資料尚未變更。請正常 Quit 或恢復整合後重試。");
                }
                if (quiet) { await ConfirmQuietAsync(cancellation).ConfigureAwait(false); return; }
                await Task.Delay(700, cancellation).ConfigureAwait(false);
            }
            throw new UserError("等待桌面工作超過 5 分鐘，已停止自動切換；不會中斷工作。");
        }
        public async Task ConfirmQuietAsync(CancellationToken cancellation)
        {
            var states = await InspectAsync(cancellation).ConfigureAwait(false);
            if (states.Any(s => !s.Known || !s.ControlEnabled || s.State != "quiesced" || !s.AllBackendsExited || !s.AppExitConfirmed))
                throw new UserError("桌面工作階段尚未確認正常暫停，已取消套用帳號。");
            if (FilterOwnedProcesses(inspect(), states).Any(p => p.Kind == "Desktop")) throw new UserError("偵測到未受控或重新啟動的桌面程序，已取消套用帳號。");
        }
        public bool CredentialProcessesRunning()
        {
            try
            {
                var records = Records();
                foreach (var r in records)
                {
                    if (!r.Known || !r.ControlEnabled || r.State != "quiesced" || !r.AllBackendsExited || !r.AppExitConfirmed)
                        if (r.OwnedActors.Any(DesktopNative.Same)) return true;
                    if (r.OwnedActors.Any(a => !DesktopSessionFiles.IdentityEquals(a, r.Host) && DesktopNative.Same(a))) return true;
                }
                return FilterOwnedProcesses(inspect(), records).Any(p => p.Kind == "Desktop" || p.Kind == "CLI" || p.Kind == "Unknown");
            }
            catch { return true; }
        }
        public async Task RestoreAsync()
        {
            bool failed = false;
            foreach (string id in touched.ToArray())
            {
                try
                {
                    var record = Records().SingleOrDefault(s => s.SessionId == id);
                    if (record == null || !DesktopNative.Same(record.Host)) throw new IOException("Desktop recovery host missing.");
                    if (!Enabled) throw new IOException("Desktop recovery control revoked.");
                    DesktopSessionState accepted = record;
                    bool previouslyAccepted = record.RecoveryAccepted && (record.State == "starting" || record.State == "running" || record.State == "orphaned");
                    if (!previouslyAccepted)
                    {
                        var continuation = continuationRestore != null && continuationRestore.SessionId == record.SessionId ?
                            ContinuationPayload(continuationRestore, continuationOperation) : null;
                        accepted = await Request(record, "restore", CancellationToken.None, true, continuation).ConfigureAwait(false);
                        if (!accepted.RecoveryAccepted) throw new IOException("Desktop recovery was not accepted.");
                    }
                    await DesktopRecoveryReadiness.WaitAsync(touched, id, accepted.Generation, () => Enabled, async token =>
                    {
                        var fresh = Records().SingleOrDefault(s => s.SessionId == id);
                        if (fresh == null || !DesktopNative.Same(fresh.Host)) throw new IOException("Desktop recovery host changed.");
                        return await Request(fresh, "status", token, false).ConfigureAwait(false);
                    }, TimeSpan.FromSeconds(120), TimeSpan.FromMilliseconds(400), CancellationToken.None).ConfigureAwait(false);
                }
                catch { failed = true; }
            }
            if (failed) throw new UserError("部分桌面工作階段尚未確認恢復，狀態已保留。請先從 Codex 正常 Quit；若仍為未知狀態，先「停用桌面自動控制」，等程序正常結束後再啟用。未重送聊天訊息。");
        }
        public async Task RestoreDetectedAsync(CancellationToken cancellation)
        {
            foreach (var record in await InspectAsync(cancellation).ConfigureAwait(false))
                if (record.State == "quiesced" || record.State == "prepared" || record.State == "recoveryRequired") touched.Add(record.SessionId);
            await RestoreAsync().ConfigureAwait(false);
        }
    }

    internal static class DesktopFirstEnableWaiter
    {
        internal static async Task WaitAsync(Func<Task<List<ProcessBlocker>>> read, Action<string> status, CancellationToken cancellation,
            TimeSpan pollDelay, TimeSpan quietPeriod, TimeSpan maxWait)
        {
            var clock = Stopwatch.StartNew();
            Action checkDeadline = delegate
            {
                if (clock.Elapsed >= maxWait)
                    throw new UserError("首次啟用等待已超過 5 分鐘，尚未啟用。請檢查畫面列出的程序，再重新按啟用；不會強制結束程式。");
            };
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                checkDeadline();
                var blockers = await read().ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                checkDeadline();
                if (blockers == null) throw new UserError("無法確認背景程序，已停止首次啟用。");
                if (blockers.Count == 0)
                {
                    if (status != null) status("首次啟用：正在確認相關程序已穩定退出… 可按「取消」。");
                    await Task.Delay(quietPeriod, cancellation).ConfigureAwait(false);
                    checkDeadline();
                    blockers = await read().ConfigureAwait(false);
                    cancellation.ThrowIfCancellationRequested();
                    checkDeadline();
                    if (blockers == null) throw new UserError("無法確認背景程序，已停止首次啟用。");
                    if (blockers.Count == 0)
                    {
                        if (status != null) status("相關程序已退出，正在自動啟用並開啟受管桌面…");
                        cancellation.ThrowIfCancellationRequested();
                        checkDeadline();
                        return;
                    }
                }
                if (status != null) status("首次啟用已排隊等待。請現在從 Codex 選單選「結束 ChatGPT / Quit」。\n" +
                    "請保持切換器開啟；程序退出後會自動開啟受管桌面，不必先手動重開 App。可按「取消」。\n" + ProcessInspector.Describe(blockers));
                checkDeadline();
                await Task.Delay(pollDelay, cancellation).ConfigureAwait(false);
            }
        }
    }

    internal static class DesktopRecoveryReadiness
    {
        internal static bool Ready(DesktopSessionState state, string sessionId, int minimumGeneration)
        { return state != null && state.SessionId == sessionId && state.Generation >= minimumGeneration && state.Known && state.ControlEnabled && state.State == "running" && state.RecoveryAccepted; }
        internal static async Task WaitAsync(HashSet<string> touched, string sessionId, int minimumGeneration, Func<bool> enabled,
            Func<CancellationToken, Task<DesktopSessionState>> readFreshStatus, TimeSpan limit, TimeSpan pollDelay, CancellationToken cancellation)
        {
            var clock = Stopwatch.StartNew();
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                deadline.CancelAfter(limit);
                while (clock.Elapsed < limit)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (!enabled()) throw new UserError("桌面自動控制已停用，已停止恢復等待並保留工作階段。");
                    DesktopSessionState state = null;
                    try { state = await readFreshStatus(deadline.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { cancellation.ThrowIfCancellationRequested(); }
                    catch (Exception) { /* Fresh status may race a reconnect generation; retry status only. */ }
                    cancellation.ThrowIfCancellationRequested();
                    if (!enabled() || state != null && !state.ControlEnabled) throw new UserError("桌面自動控制已停用，已停止恢復等待並保留工作階段。");
                    if (clock.Elapsed >= limit || deadline.IsCancellationRequested) break;
                    if (Ready(state, sessionId, minimumGeneration)) { touched.Remove(sessionId); return; }
                    if (clock.Elapsed >= limit || deadline.IsCancellationRequested) break;
                    try { await Task.Delay(pollDelay, deadline.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { cancellation.ThrowIfCancellationRequested(); break; }
                }
            }
            throw new UserError("桌面恢復已接受，但尚未確認準備完成；工作階段已保留，可稍後重新檢查。");
        }
    }

    // Deliberately independent from sockets/processes so race and failure gates have deterministic tests.
    internal interface IDesktopSessionMember
    {
        Dictionary<string, object> Status();
        Dictionary<string, object> Prepare();
        Dictionary<string, object> Commit();
        void Unprepare();
        bool Exited0 { get; }
        bool Disconnected { get; }
    }
    internal interface IDesktopSessionBoundedCommit
    {
        Dictionary<string, object> CommitForShutdown(Func<bool> mayContinue, int budgetMs);
    }

    internal sealed class DesktopSessionTransaction
    {
        internal const int GracefulShutdownBudgetMs = 90000;
        internal Func<long> MonotonicMilliseconds = () => (long)(Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency));
        internal readonly object Admission = new object();
        internal readonly List<IDesktopSessionMember> Members = new List<IDesktopSessionMember>();
        internal int Generation;
        internal volatile bool Frozen;
        internal bool EofCommitted;
        internal string Stage = "starting", Reason;
        internal Func<bool> Enabled, AppGone;
        internal Func<DesktopShutdownResult> Shutdown;
        internal Action Freeze, Unfreeze, Persist;
        internal bool Admit(IDesktopSessionMember member)
        {
            lock (Admission)
            {
                // A desktop can own independent chat and account connections.
                // Each remains a separately initialized backend; quiescence must
                // freeze and positively retire the entire admitted group.
                if (Frozen || EofCommitted || Members.Any(m => !m.Exited0 && m.Disconnected) || Members.Count(m => !m.Exited0) >= 16 || Members.Count >= 128) return false;
                Members.Add(member); Generation++; EofCommitted = false; Stage = "running"; if (Persist != null) Persist(); return true;
            }
        }
        internal bool Quiesce(Func<bool> alive)
        {
            lock (Admission)
            {
                long began = MonotonicMilliseconds();
                Func<int> remaining = () => {
                    long elapsed = MonotonicMilliseconds() - began;
                    return elapsed < 0 || elapsed >= GracefulShutdownBudgetMs ? 0 : GracefulShutdownBudgetMs - (int)elapsed;
                };
                if (!Enabled() || !alive()) return false;
                if (Stage == "quiesced") return Members.All(m => m.Exited0) && AppGone();
                if (Stage != "running" && Stage != "orphaned" && Stage != "prepared") return false;
                bool previouslyFrozen = Frozen; string previousStage = Stage;
                Frozen = true; Freeze(); int generation = Generation; var captured = Members.ToArray(); Stage = "preparing"; Persist();
                var prepared = new List<IDesktopSessionMember>();
                try
                {
                    foreach (var member in captured.Where(m => !m.Exited0))
                    {
                        if (!Enabled() || !alive() || remaining() < 100) throw new OperationCanceledException();
                        var result = member.Prepare(); prepared.Add(member);
                        if (!DesktopSessionFiles.Quiet(result) || !DesktopSessionFiles.Flag(result, "prepared")) throw new IOException("Desktop member is busy or unknown.");
                    }
                    if (!Enabled() || !alive() || remaining() < 100 || generation != Generation || captured.Length != Members.Count) throw new OperationCanceledException();
                    Stage = "prepared"; Persist();
                    foreach (var member in prepared)
                    {
                        if (!Enabled() || !alive() || remaining() < 100) throw new OperationCanceledException();
                        Stage = "committing"; Persist();
                        // A lost/failed reply can follow a committed EOF. Never roll back after attempting commit.
                        EofCommitted = true;
                        var bounded = member as IDesktopSessionBoundedCommit;
                        var result = bounded == null ? member.Commit() : bounded.CommitForShutdown(() => Enabled() && alive() && remaining() >= 100, remaining());
                        if (!DesktopSessionFiles.Quiet(result) || DesktopSessionFiles.Text(result, "state") != "quiesced" || !member.Exited0)
                            throw new IOException("Owned backend EOF/exit0 was not confirmed.");
                    }
                    if (!captured.All(m => m.Exited0) || generation != Generation || !Enabled() || !alive()) throw new IOException("Desktop membership or authorization changed.");
                    Stage = "closingApp"; Persist();
                    var closed = Shutdown();
                    if (!closed.Success || closed.ExitCode != 0) throw new IOException("Desktop App refused cooperative shutdown or exit0 was not confirmed.");
                    // Electron's primary process can exit before its renderer/utility children.
                    // The admission barrier remains held while their normal exits settle.
                    var familyExit = Stopwatch.StartNew();
                    while (Enabled() && !AppGone() && familyExit.ElapsedMilliseconds < 7000) Thread.Sleep(50);
                    if (!Enabled() || !AppGone()) throw new IOException("Desktop process family exit was not confirmed.");
                    Stage = "quiesced"; Reason = null; Persist(); return true;
                }
                catch (Exception error)
                {
                    if (!EofCommitted && !prepared.Any(m => m.Exited0))
                    {
                        foreach (var member in prepared) { try { member.Unprepare(); } catch { } }
                        Frozen = previouslyFrozen; if (!previouslyFrozen) Unfreeze(); Stage = previousStage;
                    }
                    else Stage = "recoveryRequired";
                    Reason = error is OperationCanceledException ? "cancelledBeforeEof" : "normalCloseUnconfirmed"; Persist(); return false;
                }
            }
        }
        internal bool Quiesce(Func<bool> alive, int expectedGeneration)
        { lock (Admission) { if (Generation != expectedGeneration) return false; return Quiesce(alive); } }
        internal void CancelBeforeEof()
        {
            lock (Admission)
            {
                if (EofCommitted) return;
                foreach (var member in Members) member.Unprepare(); Frozen = false; Unfreeze(); Stage = "running"; Reason = null; Persist();
            }
        }
    }
}
