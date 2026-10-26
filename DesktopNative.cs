using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32.SafeHandles;

namespace CodexAccountSwitcher
{
    public sealed class DesktopProcessIdentity
    {
        public int Pid { get; set; }
        public string StartedUtc { get; set; }
        public string ExecutablePath { get; set; }
        public int ParentPid { get; set; }
        public int SessionId { get; set; }
        internal SafeProcessHandle Lifetime;
        internal DesktopProcessIdentity ParentIdentity;
    }

    public sealed class DesktopShutdownResult
    {
        public bool Success { get; set; }
        public int? ExitCode { get; set; }
        public bool Refused { get; set; }
        public string Reason { get; set; }
        public bool FlushVerified { get { return false; } }
    }

    public sealed class DesktopPackageTarget
    {
        public string PackageFamilyName { get; set; }
        public string PackageFullName { get; set; }
        public string AppId { get; set; }
        public string AppExecutable { get; set; }
        public string CliExecutable { get; set; }

        public static DesktopPackageTarget Resolve(string executable)
        {
            var target = DesktopLauncher.Resolve(executable);
            if (String.IsNullOrEmpty(target.AppUserModelId)) throw new UserError("桌面控制目前僅支援已安裝的 Windows Codex 套件。");
            string full = Path.GetFullPath(executable);
            for (var directory = new DirectoryInfo(Path.GetDirectoryName(full)); directory != null; directory = directory.Parent)
            {
                if (!File.Exists(Path.Combine(directory.FullName, "AppxManifest.xml"))) continue;
                string registered = DesktopNative.PackagePath(directory.Name);
                if (!String.Equals(registered.TrimEnd('\\'), directory.FullName.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    throw new UserError("無法核對已安裝的 Codex 套件位置。");
                string cli = Path.Combine(Path.GetDirectoryName(full), "resources", "codex.exe");
                if (!File.Exists(cli)) throw new UserError("找不到桌面套件隨附的 Codex 後端。");
                ManagedBridge.EnsureNoReparsePoint(Path.GetDirectoryName(full));
                ManagedBridge.EnsureNoReparsePoint(Path.GetDirectoryName(cli));
                int separator = target.AppUserModelId.LastIndexOf('!');
                if (separator <= 0 || separator == target.AppUserModelId.Length - 1) throw new UserError("無法核對桌面套件的啟動識別。");
                return new DesktopPackageTarget { PackageFamilyName = target.AppUserModelId.Substring(0, separator),
                    PackageFullName = directory.Name, AppId = target.AppUserModelId.Substring(separator + 1), AppExecutable = full,
                    CliExecutable = Path.GetFullPath(cli) };
            }
            throw new UserError("無法核對已安裝的桌面套件。");
        }
    }

    public static class DesktopNative
    {
        [StructLayout(LayoutKind.Sequential)] private struct FileTime { public uint Low, High; }
        [StructLayout(LayoutKind.Sequential)] private struct Basic { public IntPtr Reserved1, Peb, Reserved2, Reserved3, Pid, ParentPid; }
        [StructLayout(LayoutKind.Sequential)] private struct UniqueProcess { public int Pid; public FileTime Started; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct ProcessInfo
        {
            public UniqueProcess Process;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string AppName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Service;
            public int AppType;
            public uint Status, Session;
            [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
        }
        [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(SafeProcessHandle process, out FileTime created, out FileTime exited, out FileTime kernel, out FileTime user);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint code);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ProcessIdToSessionId(int pid, out int session);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
        [DllImport("kernel32.dll")] private static extern int GetCurrentProcessId();
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int info, IntPtr buffer, int size, out int returned);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(SafeProcessHandle process, int infoClass, out Basic information, int size, out int returned);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetCurrentPackageFullName(ref int length, StringBuilder fullName);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetPackagePathByFullName(string fullName, ref int length, StringBuilder path);
        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] private static extern int RmStartSession(out uint handle, int flags, StringBuilder key);
        [DllImport("rstrtmgr.dll")] private static extern int RmEndSession(uint handle);
        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] private static extern int RmRegisterResources(uint handle, uint fileCount, string[] files, uint processCount, UniqueProcess[] processes, uint serviceCount, string[] services);
        [DllImport("rstrtmgr.dll")] private static extern int RmGetList(uint handle, out uint needed, ref uint count, [In, Out] ProcessInfo[] processes, ref uint reasons);
        [DllImport("rstrtmgr.dll")] private static extern int RmShutdown(uint handle, uint flags, IntPtr callback);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindowAsync(IntPtr window, int command);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr window);

        private static long Value(FileTime time) { return ((long)time.High << 32) | time.Low; }
        private static string Utc(FileTime time) { return DateTime.FromFileTimeUtc(Value(time)).ToString("o", CultureInfo.InvariantCulture); }
        private static bool CurrentUser(SafeProcessHandle process)
        {
            IntPtr token;
            if (!OpenProcessToken(process, 8, out token)) return false;
            try
            {
                int size; GetTokenInformation(token, 1, IntPtr.Zero, 0, out size);
                if (size <= 0 || size > 65536) return false;
                IntPtr buffer = Marshal.AllocHGlobal(size);
                try { return GetTokenInformation(token, 1, buffer, size, out size) && new SecurityIdentifier(Marshal.ReadIntPtr(buffer)).Equals(WindowsIdentity.GetCurrent().User); }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            finally { CloseHandle(token); }
        }
        private static int CurrentSession()
        { int session; return ProcessIdToSessionId(GetCurrentProcessId(), out session) ? session : -1; }
        public static DesktopProcessIdentity Identity(int pid) { return ReadIdentity(pid, true); }
        private static DesktopProcessIdentity ReadIdentity(int pid, bool parent)
        {
            if (pid <= 0) return null;
            SafeProcessHandle handle = null;
            try
            {
                handle = OpenProcess(0x1000, false, pid);
                if (handle == null || handle.IsInvalid || !CurrentUser(handle)) return null;
                int session; if (!ProcessIdToSessionId(pid, out session) || session != CurrentSession()) return null;
                FileTime created, exited, kernel, user;
                if (!GetProcessTimes(handle, out created, out exited, out kernel, out user) || Value(exited) != 0) return null;
                var path = new StringBuilder(32768); int size = path.Capacity;
                if (!QueryFullProcessImageName(handle, 0, path, ref size)) return null;
                Basic basic; int returned;
                int parentPid = NtQueryInformationProcess(handle, 0, out basic, Marshal.SizeOf(typeof(Basic)), out returned) == 0 ? basic.ParentPid.ToInt32() : 0;
                var identity = new DesktopProcessIdentity { Pid = pid, StartedUtc = Utc(created), ExecutablePath = Path.GetFullPath(path.ToString()),
                    ParentPid = parentPid, SessionId = session, Lifetime = handle };
                handle = null;
                if (parent && parentPid > 0 && parentPid != pid) identity.ParentIdentity = ReadIdentity(parentPid, false);
                return identity;
            }
            catch { return null; }
            finally { if (handle != null) handle.Dispose(); }
        }
        internal static bool Equal(DesktopProcessIdentity a, DesktopProcessIdentity b)
        {
            DateTimeOffset first, second;
            return a != null && b != null && a.Pid == b.Pid && a.ParentPid == b.ParentPid && a.SessionId == b.SessionId &&
                DateTimeOffset.TryParse(a.StartedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out first) &&
                DateTimeOffset.TryParse(b.StartedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out second) && first.UtcDateTime.Ticks == second.UtcDateTime.Ticks &&
                String.Equals(a.ExecutablePath, b.ExecutablePath, StringComparison.OrdinalIgnoreCase);
        }
        public static bool Same(DesktopProcessIdentity identity)
        {
            if (identity == null) return false;
            var current = ReadIdentity(identity.Pid, false);
            try { return Equal(identity, current); }
            finally { if (current != null && current.Lifetime != null) current.Lifetime.Dispose(); }
        }
        internal static string AppToolsPipeName(string path)
        {
            const string prefix = @"\\.\pipe\";
            if (path == null || !path.StartsWith(prefix, StringComparison.Ordinal) || path.Length <= prefix.Length || path.Length > prefix.Length + 128)
                throw new IOException("The managed App broker pipe name is invalid.");
            string name = path.Substring(prefix.Length);
            if (name == "." || name == ".." || name.Any(c => c < 33 || c > 126 || c == '/' || c == '\\'))
                throw new IOException("The managed App broker pipe name is invalid.");
            return name;
        }
        internal static DesktopProcessIdentity VerifyAppToolsPipe(string path, DesktopProcessIdentity primaryApp, string verifiedAppExecutable, int timeoutMs = 2000)
        {
            string name = AppToolsPipeName(path);
            if (primaryApp == null || !Same(primaryApp) || !String.Equals(primaryApp.ExecutablePath, verifiedAppExecutable, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The managed App broker owner changed.");
            using (var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                pipe.Connect(Math.Max(100, Math.Min(3000, timeoutMs)));
                // Opening and closing is the entire probe. Never send a tool RPC.
                return VerifyConnectedAppToolsPipe(pipe, primaryApp, verifiedAppExecutable);
            }
        }
        private static DesktopProcessIdentity VerifyConnectedAppToolsPipe(NamedPipeClientStream pipe, DesktopProcessIdentity primaryApp, string verifiedAppExecutable)
        {
                uint pid;
                if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out pid) || pid == 0 || pid > Int32.MaxValue)
                    throw new IOException("The managed App broker owner is unavailable.");
                var owner = Identity((int)pid);
                if (owner == null || !String.Equals(owner.ExecutablePath, verifiedAppExecutable, StringComparison.OrdinalIgnoreCase) ||
                    !Same(primaryApp) || !Same(owner) || !(Equal(owner, primaryApp) || IsDescendant(owner, primaryApp)))
                    throw new IOException("The broker pipe is not owned by this managed App.");
                uint confirmed;
                if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out confirmed) || confirmed != pid || !Same(primaryApp) || !Same(owner))
                    throw new IOException("The managed App broker owner changed.");
                return owner;
        }
        internal static bool NavigateOriginalConversation(string path, DesktopProcessIdentity primaryApp, string verifiedAppExecutable,
            string threadId, string turnId, Func<CancellationToken, bool> claimBeforeWrite, int timeoutMs = 10000)
        {
            Guid thread;
            if (!Guid.TryParseExact(threadId, "D", out thread) || String.IsNullOrWhiteSpace(turnId) || turnId.Length > 128 ||
                turnId.Any(c => !(Char.IsLetterOrDigit(c) && c < 128 || c == '_' || c == '-')) || claimBeforeWrite == null)
                throw new IOException("Original conversation navigation binding is invalid.");
            string name = AppToolsPipeName(path);
            using (var timeout = new CancellationTokenSource(Math.Max(100, Math.Min(10000, timeoutMs))))
            using (var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous))
            using (timeout.Token.Register(() => { try { pipe.Dispose(); } catch { } }))
            {
                pipe.Connect(Math.Min(2000, Math.Max(100, timeoutMs)));
                VerifyConnectedAppToolsPipe(pipe, primaryApp, verifiedAppExecutable);
                WriteAppBrokerFrame(pipe, new { jsonrpc = "2.0", id = 1, method = "tools/list", @params = new { threadStartKind = "default" } });
                var listed = ReadAppBrokerFrame(pipe);
                VerifyConnectedAppToolsPipe(pipe, primaryApp, verifiedAppExecutable);
                if (!Object.Equals(Value(listed, "id"), 1) || listed.ContainsKey("error")) return false;
                var tools = Value(Json.Map(listed, "result"), "tools") as System.Collections.IList;
                if (tools == null || tools.Count > 512) return false;
                int matching = 0;
                foreach (object item in tools)
                {
                    var tool = item as Dictionary<string, object>;
                    if (Json.Str(tool, "namespace") == "codex_app" && Json.Str(tool, "name") == "navigate_to_codex_page") matching++;
                }
                if (matching != 1) return false;
                VerifyConnectedAppToolsPipe(pipe, primaryApp, verifiedAppExecutable);
                timeout.Token.ThrowIfCancellationRequested();
                // A positive tools/list and fresh native owner are prerequisites;
                // the Host atomically claims this bounded route attempt before bytes.
                if (!claimBeforeWrite(timeout.Token)) return false;
                timeout.Token.ThrowIfCancellationRequested();
                VerifyConnectedAppToolsPipe(pipe, primaryApp, verifiedAppExecutable);
                WriteAppBrokerFrame(pipe, new { jsonrpc = "2.0", id = 2, method = "tools/call", @params = new {
                    arguments = new { threadId = threadId }, callerSource = "codex", callId = Guid.NewGuid().ToString("N"),
                    @namespace = "codex_app", threadId = threadId, tool = "navigate_to_codex_page", turnId = turnId, hostId = "local" } });
                var result = ReadAppBrokerFrame(pipe);
                VerifyConnectedAppToolsPipe(pipe, primaryApp, verifiedAppExecutable);
                if (!AcknowledgedOriginalNavigation(result))
                    throw new IOException("Original conversation navigation was not acknowledged.");
                return true;
            }
        }
        internal static bool AcknowledgedOriginalNavigation(Dictionary<string, object> response)
        {
            if (response == null || !Object.Equals(Value(response, "id"), 2) || response.ContainsKey("error")) return false;
            var result = Json.Map(response, "result");
            if (!Object.Equals(Value(result, "success"), true)) return false;
            var items = Value(result, "contentItems") as System.Collections.IList;
            if (items == null || items.Count != 1) return false;
            var item = items[0] as Dictionary<string, object>;
            var text = Value(item, "text") as string;
            if (item == null || item.Count != 2 || Json.Str(item, "type") != "inputText" || text == null || text.Length > 128) return false;
            try
            {
                var content = new JavaScriptSerializer { MaxJsonLength = 128, RecursionLimit = 4 }.Deserialize<Dictionary<string, object>>(text);
                return content != null && content.Count == 1 && Object.Equals(Value(content, "navigated"), true);
            }
            catch { return false; }
        }
        private static object Value(Dictionary<string, object> value, string key)
        { object result; return value != null && value.TryGetValue(key, out result) ? result : null; }
        internal static void WriteAppBrokerFrame(Stream stream, object value)
        {
            byte[] bytes = new UTF8Encoding(false, true).GetBytes(Json.Encode(value));
            if (bytes.Length == 0 || bytes.Length > 8 * 1024 * 1024) throw new IOException("App broker frame exceeded its bound.");
            byte[] length = BitConverter.GetBytes((uint)bytes.Length); if (!BitConverter.IsLittleEndian) Array.Reverse(length);
            stream.Write(length, 0, length.Length); stream.Write(bytes, 0, bytes.Length); stream.Flush();
        }
        internal static Dictionary<string, object> ReadAppBrokerFrame(Stream stream)
        {
            byte[] length = new byte[4]; ReadAppBrokerExactly(stream, length); if (!BitConverter.IsLittleEndian) Array.Reverse(length);
            uint count = BitConverter.ToUInt32(length, 0);
            if (count == 0 || count > 8 * 1024 * 1024) throw new IOException("App broker frame exceeded its bound.");
            byte[] bytes = new byte[(int)count]; ReadAppBrokerExactly(stream, bytes);
            var value = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024, RecursionLimit = 64 }.Deserialize<Dictionary<string, object>>(new UTF8Encoding(false, true).GetString(bytes));
            if (value == null) throw new IOException("App broker response is unavailable."); return value;
        }
        private static void ReadAppBrokerExactly(Stream stream, byte[] bytes)
        {
            int offset = 0;
            while (offset < bytes.Length)
            { int read = stream.Read(bytes, offset, bytes.Length - offset); if (read == 0) throw new IOException("App broker response ended early."); offset += read; }
        }
        private static bool StartedMatches(FileTime created, string expected)
        {
            DateTimeOffset parsed;
            return DateTimeOffset.TryParse(expected, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out parsed) &&
                DateTime.FromFileTimeUtc(Value(created)).Ticks == parsed.UtcDateTime.Ticks;
        }
        private static bool IncludesBirth(DesktopProcessIdentity parent, DesktopProcessIdentity child)
        {
            DateTimeOffset born, parentBorn;
            if (parent == null || child == null || parent.SessionId != child.SessionId ||
                !DateTimeOffset.TryParse(child.StartedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out born) ||
                !DateTimeOffset.TryParse(parent.StartedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out parentBorn) || born < parentBorn) return false;
            if (parent.Lifetime == null || parent.Lifetime.IsClosed || parent.Lifetime.IsInvalid) return Same(parent);
            FileTime created, exited, kernel, user;
            if (!GetProcessTimes(parent.Lifetime, out created, out exited, out kernel, out user) || !StartedMatches(created, parent.StartedUtc)) return false;
            return Value(exited) == 0 || born.UtcDateTime <= DateTime.FromFileTimeUtc(Value(exited));
        }
        public static bool IsDescendant(DesktopProcessIdentity child, DesktopProcessIdentity root)
        {
            var verified = child == null ? null : ReadIdentity(child.Pid, true);
            if (!Equal(child, verified) || root == null) return false;
            if (Equal(verified, root)) return true;
            var seen = new HashSet<int>(); var current = verified;
            for (int depth = 0; depth < 32 && current != null && seen.Add(current.Pid); depth++)
            {
                if (current.ParentPid == root.Pid) return IncludesBirth(root, current);
                var parent = current.ParentIdentity ?? ReadIdentity(current.ParentPid, true);
                if (!IncludesBirth(parent, current)) return false;
                current = parent;
            }
            return false;
        }
        public static List<DesktopProcessIdentity> Snapshot()
        {
            var result = new List<DesktopProcessIdentity>();
            foreach (var process in Process.GetProcesses())
                using (process)
                    try
                    {
                        if (!ProcessInspector.IsCandidate(process.ProcessName) && !String.Equals(process.ProcessName, "DesktopRuntimeHost", StringComparison.OrdinalIgnoreCase)) continue;
                        var identity = Identity(process.Id); if (identity != null) result.Add(identity);
                    }
                    catch { }
            return result;
        }
        public static string CurrentPackageFullName()
        {
            int length = 0; if (GetCurrentPackageFullName(ref length, null) != 122 || length <= 0 || length > 32768) return null;
            var text = new StringBuilder(length); return GetCurrentPackageFullName(ref length, text) == 0 ? text.ToString() : null;
        }
        internal static string PackagePath(string fullName)
        {
            int length = 0;
            if (GetPackagePathByFullName(fullName, ref length, null) != 122 || length <= 0 || length > 32768) throw new UserError("無法核對套件註冊位置。");
            var text = new StringBuilder(length);
            if (GetPackagePathByFullName(fullName, ref length, text) != 0) throw new UserError("套件已更新或無法存取，請重新啟動整合。");
            return Path.GetFullPath(text.ToString());
        }
        public static bool TryShowApp(DesktopProcessIdentity identity)
        {
            if (!Same(identity)) return false;
            try
            {
                using (var process = Process.GetProcessById(identity.Pid))
                {
                    IntPtr window = process.MainWindowHandle;
                    uint owner;
                    if (window == IntPtr.Zero || GetWindowThreadProcessId(window, out owner) == 0 ||
                        owner != (uint)identity.Pid || !Same(identity)) return false;
                    // Restore only this retained App's own main window. Failure must
                    // never fall back to starting another App or changing its profile.
                    if (IsIconic(window) && !ShowWindowAsync(window, 9)) return false;
                    if (GetWindowThreadProcessId(window, out owner) == 0 || owner != (uint)identity.Pid ||
                        !Same(identity)) return false;
                    return SetForegroundWindow(window);
                }
            }
            catch { return false; }
        }
        public static DesktopShutdownResult Shutdown(DesktopProcessIdentity identity)
        {
            if (!Same(identity)) return new DesktopShutdownResult { Reason = "processIdentityChanged" };
            uint session;
            if (RmStartSession(out session, 0, new StringBuilder(33)) != 0) return new DesktopShutdownResult { Reason = "shutdownSessionUnavailable" };
            try
            {
                using (var handle = OpenProcess(0x1000, false, identity.Pid))
                {
                    FileTime created, exited, kernel, user;
                    if (handle.IsInvalid || !GetProcessTimes(handle, out created, out exited, out kernel, out user) || !StartedMatches(created, identity.StartedUtc))
                        return new DesktopShutdownResult { Reason = "processIdentityChanged" };
                    if (RmRegisterResources(session, 0, null, 1, new[] { new UniqueProcess { Pid = identity.Pid, Started = created } }, 0, null) != 0)
                        return new DesktopShutdownResult { Reason = "shutdownRegistrationUnavailable" };
                    uint needed, count = 0, reasons = 0;
                    int code = RmGetList(session, out needed, ref count, null, ref reasons);
                    if ((code != 0 && code != 234) || needed != 1) return new DesktopShutdownResult { Reason = "shutdownScopeUnknown" };
                    var list = new ProcessInfo[1]; count = 1;
                    if (RmGetList(session, out needed, ref count, list, ref reasons) != 0 || count != 1 || list[0].Process.Pid != identity.Pid ||
                        Value(list[0].Process.Started) != Value(created) || !Same(identity))
                        return new DesktopShutdownResult { Reason = "shutdownScopeChanged" };
                    code = RmShutdown(session, 0, IntPtr.Zero);
                    var watch = Stopwatch.StartNew(); uint exit = 259;
                    while (watch.ElapsedMilliseconds < 7000 && GetExitCodeProcess(handle, out exit) && exit == 259) System.Threading.Thread.Sleep(50);
                    bool gone = GetProcessTimes(handle, out created, out exited, out kernel, out user) && Value(exited) != 0;
                    return new DesktopShutdownResult { Success = code == 0 && gone && exit == 0, ExitCode = gone ? (int?)unchecked((int)exit) : null,
                        Refused = !gone, Reason = code == 0 && gone && exit == 0 ? null : code == 0 && gone ? "appExitFailed" : "appDeclinedShutdown" };
                }
            }
            catch { return new DesktopShutdownResult { Reason = "shutdownUnavailable" }; }
            finally { RmEndSession(session); }
        }
    }
}
