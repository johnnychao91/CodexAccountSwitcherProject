using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Threading;
using System.Threading.Tasks;

namespace CodexAccountSwitcher
{
    // Diagnostic metadata only. Never read command lines, credentials, or process memory.
    public sealed class ProcessSnapshot
    {
        public int Pid { get; set; }
        public int ParentPid { get; set; }
        public int SessionId { get; set; }
        public string Name { get; set; }
        public string ExecutablePath { get; set; }
        public string ParentName { get; set; }
        public string CreationTime { get; set; }
    }

    public sealed class ProcessBlocker
    {
        public int Pid { get; set; }
        public string Kind { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string CreationTime { get; set; }
    }

    public static class ProcessInspector
    {
        private static string BaseName(string name)
        {
            string value = (name ?? "").Trim();
            return value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? value.Substring(0, value.Length - 4) : value;
        }

        private static bool IsName(string name, params string[] expected)
        { return expected.Any(value => String.Equals(BaseName(name), value, StringComparison.OrdinalIgnoreCase)); }

        private static bool Contains(string path, string fragment)
        { return path.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0; }

        public static bool IsCandidate(string name)
        { return IsName(name, "codex", "chatgpt", "ManagedBridge", "DesktopRuntimeHost", "DesktopAppRelay"); }

        // Classification only affects the explanation: every returned candidate blocks a switch.
        // Parent names are a diagnostic hint, never authority to terminate another process.
        public static ProcessBlocker Classify(ProcessSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException("snapshot");
            if (!IsCandidate(snapshot.Name)) throw new ArgumentException("Not a Codex process.", "snapshot");
            string path = (snapshot.ExecutablePath ?? "").Replace('/', '\\');
            string kind, description;
            if (IsName(snapshot.Name, "DesktopAppRelay"))
            { kind = "Desktop"; description = "Codex 桌面受管傳輸程序"; }
            else if (IsName(snapshot.Name, "ManagedBridge", "DesktopRuntimeHost"))
            { kind = "Integration"; description = IsName(snapshot.Name, "DesktopRuntimeHost") ? "桌面控制協調器" : "VS Code 實驗整合協調器"; }
            else if (Contains(path, "\\.vscode\\extensions\\") || Contains(path, "\\.vscode-insiders\\extensions\\") ||
                IsName(snapshot.ParentName, "code", "code-insiders"))
            { kind = "VSCode"; description = "VS Code 的 Codex 擴充套件"; }
            else if (Contains(path, "\\.cursor\\extensions\\") || Contains(path, "\\.windsurf\\extensions\\") ||
                IsName(snapshot.ParentName, "cursor", "windsurf", "devenv", "rider64", "rider", "idea64", "idea", "pycharm64", "pycharm", "webstorm64", "webstorm", "clion64", "clion", "studio64", "antigravity"))
            { kind = "OtherIde"; description = "其他 IDE 的 Codex 工作程序"; }
            else if (IsName(snapshot.Name, "chatgpt") || IsName(snapshot.ParentName, "chatgpt") ||
                (Contains(path, "\\openai.codex_") && Contains(path, "\\app\\")) || Contains(path, "\\OpenAI" + "\\Codex" + "\\bin\\"))
            { kind = "Desktop"; description = "ChatGPT / Codex 桌面程式或背景程序"; }
            else if (!String.IsNullOrWhiteSpace(path))
            { kind = "CLI"; description = "Codex CLI 或其他 Codex 工作程序"; }
            else
            { kind = "Unknown"; description = "來源尚未辨識的 Codex 工作程序"; }
            return new ProcessBlocker { Pid = snapshot.Pid, Kind = kind, Name = snapshot.Name ?? "codex", Description = description, CreationTime = snapshot.CreationTime };
        }

        public static List<ProcessBlocker> FromSnapshots(IEnumerable<ProcessSnapshot> snapshots, int sessionId)
        {
            if (snapshots == null) throw new ArgumentNullException("snapshots");
            var all = snapshots.ToList();
            var names = all.ToDictionary(value => value.Pid);
            var result = new List<ProcessBlocker>();
            foreach (var snapshot in all.Where(value => value.SessionId == sessionId && IsCandidate(value.Name)))
            {
                ProcessSnapshot parent;
                string parentName = snapshot.ParentName;
                if (String.IsNullOrEmpty(parentName) && names.TryGetValue(snapshot.ParentPid, out parent) && parent.SessionId == sessionId)
                    parentName = parent.Name;
                result.Add(Classify(new ProcessSnapshot { Pid = snapshot.Pid, ParentPid = snapshot.ParentPid,
                    SessionId = snapshot.SessionId, Name = snapshot.Name, ExecutablePath = snapshot.ExecutablePath, ParentName = parentName, CreationTime = snapshot.CreationTime }));
            }
            return result.OrderBy(value => value.Kind).ThenBy(value => value.Pid).ToList();
        }

        public static List<ProcessBlocker> Inspect()
        {
            try
            {
                using (var current = Process.GetCurrentProcess()) return FromSnapshots(ReadSnapshots(), current.SessionId);
            }
            catch (Exception)
            { throw new UserError("無法確認 Codex 背景程序是否已結束，已停止切換。請稍後重試；登入資料尚未變更。"); }
        }

        private static List<ProcessSnapshot> ReadSnapshots()
        {
            var snapshots = new List<ProcessSnapshot>();
            var options = new EnumerationOptions { ReturnImmediately = false, Timeout = TimeSpan.FromSeconds(5) };
            using (var searcher = new ManagementObjectSearcher("root\\CIMV2",
                "SELECT Name, ProcessId, ParentProcessId, ExecutablePath, SessionId, CreationDate FROM Win32_Process", options))
            using (var results = searcher.Get())
                foreach (ManagementObject process in results)
                    using (process)
                    {
                        object pid = process["ProcessId"], parentPid = process["ParentProcessId"], session = process["SessionId"];
                        string name = process["Name"] as string;
                        if (pid == null || parentPid == null || session == null || String.IsNullOrWhiteSpace(name))
                            throw new InvalidOperationException("Incomplete process inspection.");
                        snapshots.Add(new ProcessSnapshot { Pid = Convert.ToInt32(pid), ParentPid = Convert.ToInt32(parentPid),
                            SessionId = Convert.ToInt32(session), Name = name, ExecutablePath = process["ExecutablePath"] as string, CreationTime = process["CreationDate"] as string });
                    }
            return snapshots;
        }

        internal static List<DesktopProcessIdentity> FindCodexStopTargets()
        {
            using (var current = Process.GetCurrentProcess())
                return FindCodexStopTargets(ReadSnapshots(), current.SessionId, current.Id, DesktopNative.ReadStandaloneIdentity);
        }

        internal static List<DesktopProcessIdentity> FindCodexStopTargets(IEnumerable<ProcessSnapshot> snapshots,
            int sessionId, int controllerPid, Func<int, DesktopProcessIdentity> readIdentity)
        {
            var result = new List<DesktopProcessIdentity>();
            foreach (var item in snapshots.Where(item => item.SessionId == sessionId && item.Pid != controllerPid && IsCandidate(item.Name)))
            {
                var identity = readIdentity(item.Pid);
                if (identity == null) continue;
                if (MatchesSnapshot(item, identity) && (!IsName(item.Name, "ChatGPT") || IsCodexDesktop(identity.ExecutablePath))) result.Add(identity);
                else DisposeIdentity(identity);
            }
            return result;
        }

        private static bool MatchesSnapshot(ProcessSnapshot snapshot, DesktopProcessIdentity identity)
        {
            return identity.Pid == snapshot.Pid && identity.ParentPid == snapshot.ParentPid && identity.SessionId == snapshot.SessionId &&
                IsName(Path.GetFileName(identity.ExecutablePath), BaseName(snapshot.Name));
        }

        internal static bool IsCodexDesktop(string executable)
        {
            var directory = new DirectoryInfo(Path.GetDirectoryName(executable));
            if (!String.Equals(directory.Name, "app", StringComparison.OrdinalIgnoreCase) || directory.Parent == null) return false;
            var package = directory.Parent;
            if (!package.Name.StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) ||
                !package.Name.EndsWith("_2p2nqsd0c76g0", StringComparison.OrdinalIgnoreCase)) return false;
            try { return String.Equals(DesktopNative.PackagePath(package.Name), package.FullName, StringComparison.OrdinalIgnoreCase); }
            catch (UserError) { return false; }
        }

        internal static void DisposeIdentity(DesktopProcessIdentity identity)
        { if (identity.Lifetime != null) identity.Lifetime.Dispose(); }

        internal static List<DesktopProcessIdentity> StopCodexTargets(IList<DesktopProcessIdentity> targets,
            Func<DesktopProcessIdentity, bool> isApp, Func<DesktopProcessIdentity, bool> hasWindow,
            Func<DesktopProcessIdentity, bool> closeNormally, Func<DesktopProcessIdentity, bool> exited,
            Action<IList<DesktopProcessIdentity>> waitForExit, Func<bool> desktopAppPresent, Action<DesktopProcessIdentity> forceStop)
        {
            var desktop = targets.Where(isApp).ToList();
            foreach (var window in desktop.Where(hasWindow))
                if (!exited(window) && !closeNormally(window)) return targets.Where(target => !exited(target)).ToList();
            if (desktop.Count > 0) waitForExit(desktop);
            if (desktop.Any(target => !exited(target))) return targets.Where(target => !exited(target)).ToList();

            bool hostPresent = targets.Any(target => IsName(Path.GetFileName(target.ExecutablePath), "DesktopRuntimeHost"));
            // Let managed transports and coordinators retire after the App has flushed and exited.
            if (desktop.Count > 0 || hostPresent) waitForExit(targets);
            var pending = targets.Where(target => !exited(target)).ToList();
            if (pending.Count == 0 || desktopAppPresent()) return pending;
            var coordinators = pending.Where(target => IsName(Path.GetFileName(target.ExecutablePath), "ManagedBridge", "DesktopRuntimeHost")).ToList();
            foreach (var target in pending.Except(coordinators))
                if (!exited(target)) forceStop(target);
            if (coordinators.Count > 0) waitForExit(coordinators);
            if (coordinators.Any(target => !exited(target)) && !desktopAppPresent())
                foreach (var target in coordinators.Where(target => !exited(target))) forceStop(target);
            return targets.Where(target => !exited(target)).ToList();
        }

        internal static bool HasDesktopProcesses()
        {
            return Inspect().Any(item => item.Kind == "Desktop" || IsName(item.Name, "DesktopRuntimeHost"));
        }

        internal static bool HasCodexDesktopApp()
        {
            using (var current = Process.GetCurrentProcess())
                return ReadSnapshots().Any(item => item.SessionId == current.SessionId &&
                    (IsName(item.Name, "ChatGPT") && String.IsNullOrEmpty(item.ExecutablePath) ||
                     IsName(item.Name, "ChatGPT", "codex") && !String.IsNullOrEmpty(item.ExecutablePath) &&
                     IsCodexDesktop(item.ExecutablePath)));
        }

        internal static async Task<bool> WaitForDesktopQuietAsync(Func<bool> hasBlockers, CancellationToken cancellation,
            TimeSpan quietPeriod, TimeSpan pollDelay, TimeSpan timeout)
        {
            var clock = Stopwatch.StartNew();
            TimeSpan? quietSince = null;
            while (clock.Elapsed < timeout)
            {
                cancellation.ThrowIfCancellationRequested();
                bool blocked = hasBlockers();
                cancellation.ThrowIfCancellationRequested();
                TimeSpan now = clock.Elapsed;
                if (now >= timeout) break;
                if (blocked) quietSince = null;
                else
                {
                    if (!quietSince.HasValue) quietSince = now;
                    if (now - quietSince.Value >= quietPeriod) return true;
                }
                await Task.Delay(pollDelay, cancellation).ConfigureAwait(false);
            }
            return false;
        }

        internal static void WaitForProcessExit(IList<DesktopProcessIdentity> actors)
        {
            var deadline = Stopwatch.StartNew();
            while (actors.Any(actor => !DesktopNative.ConfirmedExited(actor)) && deadline.Elapsed < TimeSpan.FromSeconds(4))
                Thread.Sleep(100);
        }

        internal static bool HasChildWork(int pid)
        {
            var options = new EnumerationOptions { ReturnImmediately = false, Timeout = TimeSpan.FromSeconds(5) };
            using (var searcher = new ManagementObjectSearcher("root\\CIMV2",
                "SELECT Name FROM Win32_Process WHERE ParentProcessId = " + pid.ToString(System.Globalization.CultureInfo.InvariantCulture), options))
            using (var results = searcher.Get())
                foreach (ManagementObject process in results)
                    using (process)
                        if (!IsName(process["Name"] as string, "conhost")) return true;
            return false;
        }

        public static string Describe(IList<ProcessBlocker> blockers)
        {
            if (blockers == null) throw new ArgumentNullException("blockers");
            if (blockers.Count == 0) return "Codex 相關程序已結束，可以套用帳號。";
            var details = new List<string>();
            foreach (var group in blockers.GroupBy(value => value.Kind).OrderBy(group => group.Key == "Desktop" ? 1 : 0))
            {
                string pids = String.Join("、", group.Take(3).Select(value => value.Pid.ToString()));
                if (group.Count() > 3) pids += " 等 " + group.Count() + " 個";
                string action;
                switch (group.Key)
                {
                    case "VSCode": action = "請正常停止 Codex 擴充套件；未啟用實驗整合時，需儲存工作並關閉對應 VS Code 視窗。"; break;
                    case "Integration": action = "請由桌面控制或 VS Code 整合正常暫停，或從對應桌面／IDE 程式正常結束。"; break;
                    case "OtherIde": action = "請先儲存工作，再關閉使用 Codex 的 IDE 或其 Codex 擴充套件。"; break;
                    case "Desktop": action = "請從選單或系統匣選「結束 / Quit」。"; break;
                    case "CLI": action = "請結束終端機中的 Codex 工作與其他使用 Codex 的工具。"; break;
                    default: action = "請依 PID 在工作管理員確認來源，並從該程式正常結束。"; break;
                }
                details.Add("• " + group.First().Description + "（PID " + pids + "）\n  " + action);
            }
            return "仍在等待：\n" + String.Join("\n", details);
        }
    }
}
