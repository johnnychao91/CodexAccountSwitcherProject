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
using System.Threading.Tasks;

namespace CodexAccountSwitcher
{
    public sealed class ManagedRuntimeRecord
    {
        public int ProtocolVersion { get; set; }
        public string Kind { get; set; }
        public string InstanceId { get; set; }
        public string Generation { get; set; }
        public string PipeName { get; set; }
        public int WrapperPid { get; set; }
        public string WrapperStartedUtc { get; set; }
        public int OwnerPid { get; set; }
        public string OwnerStartedUtc { get; set; }
        public int BackendPid { get; set; }
        public string BackendStartedUtc { get; set; }
        public string CodexHome { get; set; }
        public string State { get; set; }
    }

    public sealed class RuntimeState
    {
        public ManagedRuntimeRecord Record { get; set; }
        public bool Known { get; set; }
        public bool Idle { get; set; }
        public bool RequiresReload { get; set; }
        public bool ResumeRequested { get; set; }
        public bool ControlEnabled { get; set; }
        public int ActiveTurns { get; set; }
        public int PendingRequests { get; set; }
        public int BackgroundTerminals { get; set; }
        public string State { get; set; }
    }

    public sealed class RuntimeInspection
    {
        public List<RuntimeState> Managed { get; private set; }
        public List<ProcessBlocker> Unmanaged { get; private set; }
        public List<string> AdditionalStatus { get; private set; }
        public RuntimeInspection() { Managed = new List<RuntimeState>(); Unmanaged = new List<ProcessBlocker>(); AdditionalStatus = new List<string>(); }
        public string Describe()
        {
            var parts = new List<string>();
            parts.AddRange(AdditionalStatus);
            if (Unmanaged.Count > 0)
                parts.Add("下列程序的工作狀態未知，需先從來源程式正常結束。\n" + ProcessInspector.Describe(Unmanaged));
            foreach (var runtime in Managed)
                parts.Add("VS Code 實驗整合：" + (!runtime.Known ? "工作狀態未知" : !runtime.ControlEnabled ? "自動控制已停用，請正常重新載入視窗" : runtime.State == "quiesced" ? "已暫停，等待恢復" : runtime.Idle ? "閒置" :
                    "忙碌，執行中回合 " + runtime.ActiveTurns + "、待處理要求 " + runtime.PendingRequests + "、背景工作 " + runtime.BackgroundTerminals) + "。");
            return parts.Count == 0 ? "沒有偵測到仍在執行的 Codex 程序。" : String.Join("\n", parts);
        }
    }

    // Only a user-installed supervisor may control its own child. A process name is never authority to stop it.
    public sealed class ManagedRuntimeClient
    {
        private readonly string directory, codexHome;
        private readonly Func<List<ProcessBlocker>> inspectProcesses;
        private readonly List<ManagedRuntimeRecord> paused = new List<ManagedRuntimeRecord>();
        public bool DesktopWasRunning { get; private set; }
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint processId);

        public ManagedRuntimeClient(string storage, string home)
            : this(storage, home, ProcessInspector.Inspect)
        { }

        internal ManagedRuntimeClient(string storage, string home, Func<List<ProcessBlocker>> inspectProcesses)
        {
            if (inspectProcesses == null) throw new ArgumentNullException("inspectProcesses");
            directory = Path.Combine(storage, "integrations", "vscode", "runtimes");
            codexHome = Path.GetFullPath(home).TrimEnd(Path.DirectorySeparatorChar);
            this.inspectProcesses = inspectProcesses;
        }

        internal static bool SameProcess(int pid, string started)
        {
            DateTimeOffset expected;
            if (pid <= 0 || !DateTimeOffset.TryParse(started, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out expected)) return false;
            try
            {
                using (var process = Process.GetProcessById(pid))
                    return !process.HasExited && process.SessionId == Process.GetCurrentProcess().SessionId &&
                        process.StartTime.ToUniversalTime().Ticks == expected.UtcDateTime.Ticks;
            }
            catch { return false; }
        }

        private List<ManagedRuntimeRecord> Records()
        {
            var result = new List<ManagedRuntimeRecord>();
            if (!Directory.Exists(directory)) return result;
            for (var parent = new DirectoryInfo(directory); parent != null; parent = parent.Parent)
                if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new UserError("整合資料夾不支援重新導向連結。");
            var owner = Directory.GetAccessControl(directory).GetOwner(typeof(SecurityIdentifier));
            if (!owner.Equals(WindowsIdentity.GetCurrent().User)) throw new UserError("無法確認實驗整合資料夾的擁有者。");
            foreach (string path in Directory.GetFiles(directory, "*.json").Take(64))
            {
                try
                {
                    var file = new FileInfo(path);
                    if ((file.Attributes & FileAttributes.ReparsePoint) != 0 || file.Length > 32768) continue;
                    var record = Json.Decode<ManagedRuntimeRecord>(File.ReadAllText(path, Encoding.UTF8));
                    if (record == null || record.ProtocolVersion != 1 || record.Kind != "vscode-stdio" ||
                        String.IsNullOrEmpty(record.PipeName) || record.PipeName.Length > 200 || record.PipeName.IndexOfAny(new[] { '\\', '/' }) >= 0 ||
                        String.IsNullOrEmpty(record.Generation) || record.InstanceId != Path.GetFileNameWithoutExtension(path)) continue;
                    if (!SameProcess(record.WrapperPid, record.WrapperStartedUtc) || !SameProcess(record.OwnerPid, record.OwnerStartedUtc)) continue;
                    if (String.IsNullOrEmpty(record.CodexHome) || !String.Equals(Path.GetFullPath(record.CodexHome).TrimEnd(Path.DirectorySeparatorChar), codexHome, StringComparison.OrdinalIgnoreCase)) continue;
                    result.Add(record);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
            }
            return result;
        }

        private static bool Flag(Dictionary<string, object> data, string name)
        { object value; return data != null && data.TryGetValue(name, out value) && value is bool && (bool)value; }
        private static int Count(Dictionary<string, object> data, string name)
        { object value; return data != null && data.TryGetValue(name, out value) && value is int && (int)value >= 0 ? (int)value : -1; }

        internal static RuntimeState ParseState(Dictionary<string, object> data, ManagedRuntimeRecord record)
        {
            var result = new RuntimeState { Record = record, State = Json.Str(data, "state"),
                ActiveTurns = Count(data, "activeTurns"), PendingRequests = Count(data, "pendingRequests"),
                BackgroundTerminals = Count(data, "backgroundTerminals"), RequiresReload = Flag(data, "requiresReload"),
                ResumeRequested = Flag(data, "resumeRequested"), ControlEnabled = Flag(data, "controlEnabled") };
            object idle;
            bool valid = Count(data, "protocolVersion") == 1 && result.ActiveTurns >= 0 && result.PendingRequests >= 0 &&
                result.BackgroundTerminals >= 0 && (result.State == "running" || result.State == "quiesced") &&
                data.TryGetValue("idle", out idle) && idle is bool;
            result.Known = valid && Flag(data, "known");
            result.Idle = result.Known && Flag(data, "idle") && result.ActiveTurns == 0 && result.PendingRequests == 0 && result.BackgroundTerminals == 0;
            if (Flag(data, "idle") && !result.Idle) result.Known = false;
            return result;
        }

        private static async Task<NamedPipeClientStream> Connect(ManagedRuntimeRecord record, bool waitForBusyPipe, CancellationToken cancellation)
        {
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!SameProcess(record.WrapperPid, record.WrapperStartedUtc) || !SameProcess(record.OwnerPid, record.OwnerStartedUtc))
                    throw new UserError("實驗整合程序已改變，已停止控制要求。");
                var pipe = new NamedPipeClientStream(".", record.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                try
                {
                    using (cancellation.Register(() => { try { pipe.Dispose(); } catch { } }))
                        await pipe.ConnectAsync(waitForBusyPipe ? 10000 : 2000, cancellation).ConfigureAwait(false);
                    return pipe;
                }
                catch (IOException error)
                {
                    pipe.Dispose();
                    int code = error.HResult & 0xffff;
                    // A cancelled quiesce can still occupy the server's only pipe while it finishes cleanup.
                    // No request has been written here: never retry a mutation after this connection phase.
                    if (!waitForBusyPipe || (code != 121 && code != 231)) throw;
                }
                catch (TimeoutException)
                {
                    pipe.Dispose();
                    if (!waitForBusyPipe) throw;
                }
                catch { pipe.Dispose(); throw; }
                await Task.Delay(100, cancellation).ConfigureAwait(false);
            }
        }

        private Task<RuntimeState> Request(ManagedRuntimeRecord record, string method, CancellationToken cancellation)
        { return RequestCore(record, method, cancellation, method == "resume"); }

        private async Task<RuntimeState> RequestCore(ManagedRuntimeRecord record, string method, CancellationToken cancellation, bool waitForBusyPipe)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                timeout.CancelAfter(waitForBusyPipe ? 15000 : method == "quiesce" ? 12000 : 7000);
                using (var pipe = await Connect(record, waitForBusyPipe, timeout.Token).ConfigureAwait(false))
                using (timeout.Token.Register(() => { try { pipe.Dispose(); } catch { } }))
                {
                    uint server;
                    if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out server) || server != record.WrapperPid ||
                        !SameProcess(record.WrapperPid, record.WrapperStartedUtc) || !SameProcess(record.OwnerPid, record.OwnerStartedUtc))
                        throw new UserError("實驗整合程序已改變，已停止控制要求。");
                    using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true))
                    using (var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true))
                    {
                        string id = Guid.NewGuid().ToString("N");
                        await writer.WriteLineAsync(Json.Encode(new { id = id, method = method,
                            @params = new { expectedGeneration = record.Generation, timeoutMs = 5000 } })).ConfigureAwait(false);
                        await writer.FlushAsync().ConfigureAwait(false);
                        var line = new StringBuilder(); var buffer = new char[1];
                        while (line.Length < 131072)
                        {
                            int read = await reader.ReadAsync(buffer, 0, 1).ConfigureAwait(false);
                            if (read == 0) throw new IOException("Integration connection closed.");
                            if (buffer[0] == '\n') break;
                            line.Append(buffer[0]);
                        }
                        if (line.Length >= 131072) throw new UserError("實驗整合回覆超出限制。");
                        var response = Json.Object(line.ToString());
                        var data = Json.Map(response, "result");
                        if (Json.Str(response, "id") != id || data == null || Json.Str(data, "generation") != record.Generation ||
                            Json.Str(data, "instanceId") != record.InstanceId || !Flag(data, "ownerAlive") ||
                            Count(data, "backendPid") != record.BackendPid || Json.Str(data, "backendStartedUtc") != record.BackendStartedUtc)
                            throw new UserError("無法確認實驗整合回覆，請檢查 VS Code 狀態。");
                        return ParseState(data, record);
                    }
                }
            }
        }

        public async Task<RuntimeInspection> InspectAsync(CancellationToken cancellation)
        {
            var records = await Task.Run(() => Records(), cancellation).ConfigureAwait(false);
            var inspection = new RuntimeInspection();
            foreach (var record in records)
            {
                cancellation.ThrowIfCancellationRequested();
                RuntimeState state;
                try { state = await Request(record, "status", cancellation).ConfigureAwait(false); }
                catch { cancellation.ThrowIfCancellationRequested(); state = new RuntimeState { Record = record }; }
                inspection.Managed.Add(state);
            }
            var blockers = await Task.Run(() => inspectProcesses(), cancellation).ConfigureAwait(false);
            inspection.Unmanaged.AddRange(blockers.Where(blocker => !inspection.Managed.Any(runtime => runtime.Known &&
                (runtime.Record.BackendPid == blocker.Pid && SameProcess(blocker.Pid, runtime.Record.BackendStartedUtc) ||
                 runtime.Record.WrapperPid == blocker.Pid && SameProcess(blocker.Pid, runtime.Record.WrapperStartedUtc)))));
            return inspection;
        }

        public Task WaitForManualExitAsync(Action<string> status, CancellationToken cancellation)
        { return WaitForExitAsync(false, status, cancellation); }

        public async Task WaitForExitAsync(bool allowInterrupt, Action<string> status, CancellationToken cancellation)
        {
            var elapsed = Stopwatch.StartNew();
            var interrupted = new HashSet<string>();
            DesktopWasRunning = false;
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                var inspection = await InspectAsync(cancellation).ConfigureAwait(false);
                if (inspection.Unmanaged.Any(item => item.Kind == "Desktop")) DesktopWasRunning = true;
                if (status != null) status(inspection.Describe());
                if (inspection.Unmanaged.Count == 0 && inspection.Managed.All(runtime => runtime.Known && runtime.ControlEnabled))
                {
                    bool allPaused = true;
                    foreach (var runtime in inspection.Managed)
                    {
                        if (paused.Any(record => record.Generation == runtime.Record.Generation)) continue;
                        allPaused = false;
                        if (!runtime.Idle)
                        {
                            if (allowInterrupt && runtime.ActiveTurns > 0 && interrupted.Add(runtime.Record.Generation))
                                await Request(runtime.Record, "interrupt", cancellation).ConfigureAwait(false);
                            continue;
                        }
                        // Remember the owned operation before sending: a cancelled response may still have paused it.
                        paused.Add(runtime.Record);
                        var stopped = await Request(runtime.Record, "quiesce", cancellation).ConfigureAwait(false);
                        if (!stopped.Known || !stopped.Idle || stopped.State != "quiesced")
                            throw new UserError("VS Code 尚未確認正常暫停，已停止切換。");
                    }
                    if (allPaused) { await ConfirmQuietAsync(cancellation).ConfigureAwait(false); return; }
                }
                if (elapsed.Elapsed > TimeSpan.FromMinutes(5)) throw new UserError("等待相關工作超過 5 分鐘，自動切換已停止。");
                await Task.Delay(1000, cancellation).ConfigureAwait(false);
            }
        }

        public async Task ConfirmQuietAsync(CancellationToken cancellation)
        {
            for (int check = 0; check < 2; check++)
            {
                cancellation.ThrowIfCancellationRequested();
                var remaining = await Task.Run(() => inspectProcesses(), cancellation).ConfigureAwait(false);
                if (remaining.Any(blocker => !IsPausedWrapper(blocker))) throw new UserError("相關程序仍在執行或重新啟動，已取消套用帳號。");
                if (check == 0) await Task.Delay(1000, cancellation).ConfigureAwait(false);
            }
        }

        private bool IsPausedWrapper(ProcessBlocker blocker)
        {
            return paused.Any(record => record.WrapperPid == blocker.Pid && SameProcess(record.WrapperPid, record.WrapperStartedUtc) &&
                record.BackendPid > 0 && !SameProcess(record.BackendPid, record.BackendStartedUtc));
        }

        public bool CredentialProcessesRunning()
        { return inspectProcesses().Any(blocker => !IsPausedWrapper(blocker)); }

        public async Task RestoreAsync()
        {
            bool failed = false;
            foreach (var record in paused.ToArray())
            {
                try
                {
                    if (SameProcess(record.WrapperPid, record.WrapperStartedUtc))
                    {
                        if (SameProcess(record.BackendPid, record.BackendStartedUtc))
                        {
                            RuntimeState running = null;
                            try { running = await RequestCore(record, "status", CancellationToken.None, true).ConfigureAwait(false); }
                            catch { /* If status remains unavailable, resume still requires a confirmed quiesced backend. */ }
                            if (running != null && running.Known && running.State == "running" &&
                                SameProcess(record.BackendPid, record.BackendStartedUtc))
                            {
                                // Quiesce can refuse newly arrived work before committing EOF.
                                // The same backend is still serving the editor and needs no reload.
                                paused.Remove(record); continue;
                            }
                        }
                        // The companion accepts a reload request; a replacement runtime may appear later.
                        // Do not claim that this acknowledgement confirms its startup or resumes a turn.
                        var result = await Request(record, "resume", CancellationToken.None).ConfigureAwait(false);
                        if (!result.Known || !result.Idle || result.State != "quiesced" || !result.RequiresReload || !result.ResumeRequested || !result.ControlEnabled)
                            throw new UserError("VS Code 尚未接受恢復要求。");
                    }
                    paused.Remove(record);
                }
                catch { failed = true; }
            }
            if (failed) throw new UserError("VS Code 恢復要求未完成，請手動重新載入對應視窗。");
        }

        public async Task RestoreDetectedAsync(CancellationToken cancellation)
        {
            foreach (var record in await Task.Run(() => Records(), cancellation).ConfigureAwait(false))
            {
                var state = await Request(record, "status", cancellation).ConfigureAwait(false);
                if (state.Known && state.Idle && state.State == "quiesced" && !paused.Any(item => item.Generation == record.Generation)) paused.Add(record);
            }
            await RestoreAsync().ConfigureAwait(false);
        }
    }
}
