using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CodexAccountSwitcher
{
    public sealed class RuntimeCoordinator
    {
        private readonly ManagedRuntimeClient vscode;
        private readonly object snapshotGate = new object();
        private List<DesktopSessionState> desktopSnapshots = new List<DesktopSessionState>();
        private readonly string diagnosticPath;
        private readonly object diagnosticGate = new object();
        private readonly List<object> diagnosticEvents = new List<object>();
        private string lastDiagnosticKey;
        public DesktopSessionClient Desktop { get; private set; }
        public bool DesktopWasRunning { get; private set; }
        public bool HadManagedDesktop { get { return Desktop.HadManagedDesktop; } }

        public RuntimeCoordinator(string storage, string home)
        {
            diagnosticPath = System.IO.Path.Combine(System.IO.Path.GetFullPath(storage), "integrations", "diagnostics", "switch-history.json");
            Desktop = new DesktopSessionClient(storage, home);
            vscode = new ManagedRuntimeClient(storage, home, delegate
            {
                List<DesktopSessionState> snapshots;
                lock (snapshotGate) snapshots = desktopSnapshots.ToList();
                return Desktop.FilterOwnedProcesses(ProcessInspector.Inspect(), snapshots);
            });
        }
        private async Task<List<DesktopSessionState>> ReadDesktop(CancellationToken cancellation)
        {
            var states = await Desktop.InspectAsync(cancellation).ConfigureAwait(false);
            lock (snapshotGate) desktopSnapshots = states;
            return states;
        }
        public async Task<RuntimeInspection> InspectAsync(CancellationToken cancellation)
        {
            var desktops = await ReadDesktop(cancellation).ConfigureAwait(false);
            var result = await vscode.InspectAsync(cancellation).ConfigureAwait(false);
            result.AdditionalStatus.AddRange(desktops.Select(s => s.Describe()));
            return result;
        }
        internal static bool MayBeginShutdown(RuntimeInspection inspection, IList<DesktopSessionState> desktops, bool allowInterrupt)
        {
            if (inspection == null || desktops == null || inspection.Unmanaged.Count != 0) return false;
            if (desktops.Any(s => s == null || !s.Known || !s.ControlEnabled || !s.Idle || s.ActiveTurns != 0 || s.PendingRequests != 0 || s.BackgroundTerminals != 0)) return false;
            return inspection.Managed.All(s => s != null && s.Known && s.ControlEnabled &&
                (s.Idle || allowInterrupt && s.ActiveTurns > 0));
        }
        internal static bool MayInterruptDesktopForContinuation(RuntimeInspection inspection, IList<DesktopSessionState> desktops,
            DesktopContinuationCheckpoint checkpoint, bool allowVSCodeInterrupt)
        {
            if (checkpoint == null || inspection == null || desktops == null || inspection.Unmanaged.Count != 0) return false;
            var target = desktops.Where(s => s != null && s.SessionId == checkpoint.SessionId && s.Generation == checkpoint.Generation).ToArray();
            if (target.Length != 1) return false;
            if (desktops.Any(s => s == null || !s.Known || !s.ControlEnabled || !s.TransportReady || s.State != "running" ||
                s.SessionId != checkpoint.SessionId && (!s.Idle || s.ActiveTurns != 0 || s.PendingRequests != 0 || s.BackgroundTerminals != 0))) return false;
            if (target[0].PendingRequests != 0) return false;
            return inspection.Managed.All(s => s != null && s.Known && s.ControlEnabled && s.PendingRequests == 0 && s.BackgroundTerminals == 0 &&
                (s.Idle || allowVSCodeInterrupt && s.ActiveTurns > 0));
        }
        public Task WaitForManualExitAsync(Action<string> status, CancellationToken cancellation)
        { return WaitForExitAsync(false, status, cancellation); }

        public async Task WaitForExitAsync(bool allowInterrupt, Action<string> status, CancellationToken cancellation)
        {
            try { await WaitForExitCoreAsync(allowInterrupt, status, cancellation).ConfigureAwait(false); }
            catch (Exception error)
            {
                RecordSwitchDiagnostic(error is OperationCanceledException ? "cancelled" : "failed", null, null);
                throw;
            }
        }
        private async Task WaitForExitCoreAsync(bool allowInterrupt, Action<string> status, CancellationToken cancellation)
        {
            DesktopWasRunning = false;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                var desktops = await ReadDesktop(cancellation).ConfigureAwait(false);
                var inspection = await vscode.InspectAsync(cancellation).ConfigureAwait(false);
                inspection.AdditionalStatus.AddRange(desktops.Select(s => s.Describe()));
                RecordSwitchDiagnostic("waiting", inspection, desktops);
                if (inspection.Unmanaged.Any(p => p.Kind == "Desktop") || desktops.Any(s => DesktopNative.Same(s.App))) DesktopWasRunning = true;
                if (status != null) status(inspection.Describe());
                if (MayBeginShutdown(inspection, desktops, allowInterrupt)) break;
                if (clock.Elapsed >= TimeSpan.FromMinutes(5)) throw new UserError("等待相關對話或背景工作超過 5 分鐘，帳號尚未套用。請確認畫面列出的來源。");
                await Task.Delay(700, cancellation).ConfigureAwait(false);
            }
            // Desktop never inherits the VS Code interrupt preference. Both clients retain
            // their own prepared/recovery ownership if cancellation arrives during shutdown.
            RecordSwitchDiagnostic("shutdown", null, null);
            await Desktop.WaitForExitAsync(status, cancellation).ConfigureAwait(false);
            await ReadDesktop(cancellation).ConfigureAwait(false);
            await vscode.WaitForExitAsync(allowInterrupt, status, cancellation).ConfigureAwait(false);
            await ConfirmQuietAsync(cancellation).ConfigureAwait(false);
            RecordSwitchDiagnostic("quiesced", null, null);
        }
        internal static object CreateSwitchDiagnostic(string phase, RuntimeInspection inspection, IList<DesktopSessionState> desktops)
        {
            string[] phases = { "waiting", "shutdown", "quiesced", "restoring", "restored", "failed", "cancelled" };
            string[] reasons = { "initializing", "runtimeQueryUnavailable", "runtimeChangedDuringQuery", "desktopTransportDisconnected",
                "previousInstallationStillRunning", "desktopMetadataUnavailable", "localWorkPolicyChanged", "desktopStatusTimeout",
                "desktopStatusRejected", "desktopStatusUnavailable", "desktopQuiescenceProofLost", "backendExitedUnexpectedly" };
            string[] stages = { "running", "starting", "unknown", "orphaned", "prepared", "preparing", "quiesced", "committing", "closingApp", "recoveryRequired", "exited" };
            string[] kinds = { "Desktop", "VSCode", "OtherIde", "CLI", "Unknown", "Integration" };
            return new
            {
                phase = phases.Contains(phase) ? phase : "other",
                unmanaged = inspection == null ? null : inspection.Unmanaged.GroupBy(p => kinds.Contains(p.Kind) ? p.Kind : "Unknown")
                    .Select(g => new { kind = g.Key, count = g.Count() }).ToArray(),
                desktops = desktops == null ? null : desktops.Take(64).Select(s => new { state = stages.Contains(s.State) ? s.State : "other",
                    s.Generation, s.Known, s.Idle, s.ControlEnabled, s.TransportReady, s.ActiveTurns, s.PendingRequests, s.BackgroundTerminals,
                    reason = s.Reason == null ? null : reasons.Contains(s.Reason) ? s.Reason : "other" }).ToArray(),
                vscode = inspection == null ? null : inspection.Managed.Take(64).Select(s => new { s.Known, s.Idle, s.ControlEnabled,
                    s.ActiveTurns, s.PendingRequests, s.BackgroundTerminals }).ToArray()
            };
        }
        private void RecordSwitchDiagnostic(string phase, RuntimeInspection inspection, IList<DesktopSessionState> desktops)
        {
            // Bounded, content-free evidence outside the project. Logging failure cannot
            // authorize a shutdown, alter credentials or prevent normal recovery.
            try
            {
                object projected = CreateSwitchDiagnostic(phase, inspection, desktops);
                string key = Json.Encode(projected);
                lock (diagnosticGate)
                {
                    if (key == lastDiagnosticKey) return;
                    diagnosticEvents.Add(new { utc = DateTimeOffset.UtcNow.ToString("o"), status = projected });
                    while (diagnosticEvents.Count > 32) diagnosticEvents.RemoveAt(0);
                    while (diagnosticEvents.Count > 1 && System.Text.Encoding.UTF8.GetByteCount(Json.Encode(new { version = 1, events = diagnosticEvents.ToArray() })) > 60000)
                        diagnosticEvents.RemoveAt(0);
                    DesktopSessionFiles.CreatePrivate(System.IO.Path.GetDirectoryName(diagnosticPath));
                    DesktopSessionFiles.Write(diagnosticPath, new { version = 1, events = diagnosticEvents.ToArray() });
                    lastDiagnosticKey = key;
                }
            }
            catch { }
        }
        public async Task ConfirmQuietAsync(CancellationToken cancellation)
        {
            await Desktop.ConfirmQuietAsync(cancellation).ConfigureAwait(false);
            await ReadDesktop(cancellation).ConfigureAwait(false);
            await vscode.ConfirmQuietAsync(cancellation).ConfigureAwait(false);
            await Desktop.ConfirmQuietAsync(cancellation).ConfigureAwait(false);
        }
        public bool CredentialProcessesRunning()
        {
            try
            {
                // Metadata on disk is not the final permission to change the login.
                // A live authenticated status confirms the desktop barrier immediately before Apply.
                Desktop.ConfirmQuietAsync(CancellationToken.None).ConfigureAwait(false).GetAwaiter().GetResult();
                var states = Desktop.InspectAsync(CancellationToken.None).ConfigureAwait(false).GetAwaiter().GetResult();
                lock (snapshotGate) desktopSnapshots = states;
                return Desktop.CredentialProcessesRunning() || vscode.CredentialProcessesRunning();
            }
            catch { return true; }
        }
        public async Task RestoreAsync()
        {
            RecordSwitchDiagnostic("restoring", null, null);
            Exception failure = null;
            try { await Desktop.RestoreAsync().ConfigureAwait(false); }
            catch (Exception error) { failure = error; }
            try { await vscode.RestoreAsync().ConfigureAwait(false); }
            catch (Exception error) { if (failure == null) failure = error; }
            if (failure != null) throw new UserError("程式恢復未完成。請檢查桌面整合或重新載入 VS Code；沒有重送聊天訊息。");
            await ReadDesktop(CancellationToken.None).ConfigureAwait(false);
            RecordSwitchDiagnostic("restored", null, null);
        }
        public async Task RestoreDetectedAsync(CancellationToken cancellation)
        {
            Exception failure = null;
            try { await Desktop.RestoreDetectedAsync(cancellation).ConfigureAwait(false); }
            catch (Exception error) { failure = error; }
            try { await vscode.RestoreDetectedAsync(cancellation).ConfigureAwait(false); }
            catch (Exception error) { if (failure == null) failure = error; }
            if (failure != null) throw new UserError("部分暫停中的程式尚未恢復，請依來源正常 Quit／重新載入後再試。");
            await ReadDesktop(cancellation).ConfigureAwait(false);
        }
        public async Task LaunchDesktopAsync(string executable)
        {
            if (HadManagedDesktop) return; // The owned desktop has already been restored.
            if (Desktop.ControlRequested)
            {
                if (Desktop.Enabled) await Desktop.LaunchIfEnabledAsync(executable, CancellationToken.None).ConfigureAwait(false);
                else await Desktop.EnableAndLaunchAsync(executable, CancellationToken.None).ConfigureAwait(false);
            }
            else Installation.Launch(executable);
        }
    }

    public static class RuntimeCoordinatorTests
    {
        private static void Check(bool condition) { if (!condition) throw new Exception("The runtime coordination gate accepted an unsafe shutdown."); }
        public static void Register(Action<string, Action> test)
        {
            test("Desktop continuation may interrupt only its exact captured session while every other runtime is safe", delegate
            {
                var cp = new DesktopContinuationCheckpoint { SessionId = "fixture-session", Generation = 2 };
                var state = new DesktopSessionState { SessionId = cp.SessionId, Generation = cp.Generation, State = "running", Known = true,
                    ControlEnabled = true, TransportReady = true, ActiveTurns = 1 };
                var inspection = new RuntimeInspection();
                Check(RuntimeCoordinator.MayInterruptDesktopForContinuation(inspection, new[] { state }, cp, false));
                inspection.Unmanaged.Add(new ProcessBlocker { Pid = 12, Kind = "VSCode" });
                Check(!RuntimeCoordinator.MayInterruptDesktopForContinuation(inspection, new[] { state }, cp, true));
                inspection.Unmanaged.Clear(); state.PendingRequests = 1;
                Check(!RuntimeCoordinator.MayInterruptDesktopForContinuation(inspection, new[] { state }, cp, true));
                state.PendingRequests = 0; cp.Generation++;
                Check(!RuntimeCoordinator.MayInterruptDesktopForContinuation(inspection, new[] { state }, cp, true));
                cp.Generation--; var other = new DesktopSessionState { SessionId = "another-session", Generation = 1, Known = true,
                    ControlEnabled = true, TransportReady = true, State = "running", ActiveTurns = 1 };
                Check(!RuntimeCoordinator.MayInterruptDesktopForContinuation(inspection, new[] { state, other }, cp, true));
            });
            test("Switch diagnostics retain blocker causes without private accounts paths process IDs or arbitrary messages", delegate
            {
                string secret = "private-fixture-marker";
                var inspection = new RuntimeInspection();
                inspection.Unmanaged.Add(new ProcessBlocker { Pid = 45678, Kind = "VSCode", Name = secret, Description = secret });
                inspection.Unmanaged.Add(new ProcessBlocker { Pid = 45679, Kind = secret, Name = secret });
                var session = new DesktopSessionState { SessionId = secret, Home = secret, Reason = secret, State = secret,
                    Host = new DesktopProcessIdentity { Pid = 45678, ExecutablePath = secret } };
                string report = Json.Encode(RuntimeCoordinator.CreateSwitchDiagnostic(secret, inspection, new[] { session }));
                Check(!report.Contains(secret) && !report.Contains("45678") && !report.Contains("45679") && !report.Contains("Home") &&
                    !report.Contains("SessionId") && report.Contains("VSCode") && report.Contains("Unknown") && report.Contains("other"));
                session.Reason = "desktopStatusTimeout"; session.State = "unknown";
                report = Json.Encode(RuntimeCoordinator.CreateSwitchDiagnostic("waiting", inspection, new[] { session }));
                Check(report.Contains("desktopStatusTimeout") && report.Contains("waiting"));
                string detail = session.Describe();
                Check(detail.Contains("逾時") && !detail.Contains("已停用"));
            });
            test("Coordinated manual switching waits for Desktop and VS Code to be idle together", delegate
            {
                var inspection = new RuntimeInspection();
                var desktop = new DesktopSessionState { Known = true, Idle = true, ControlEnabled = true };
                var editor = new RuntimeState { Known = true, Idle = false, ControlEnabled = true, ActiveTurns = 1 };
                inspection.Managed.Add(editor);
                Check(!RuntimeCoordinator.MayBeginShutdown(inspection, new[] { desktop }, false));
                Check(RuntimeCoordinator.MayBeginShutdown(inspection, new[] { desktop }, true));
                editor.Idle = true; editor.ActiveTurns = 0;
                Check(RuntimeCoordinator.MayBeginShutdown(inspection, new[] { desktop }, false));
            });
            test("VS Code interruption permission never permits a busy or uncertain Desktop shutdown", delegate
            {
                foreach (string reason in new[] { "active", "pending", "background", "unknown", "revoked" })
                {
                    var desktop = new DesktopSessionState { Known = true, Idle = true, ControlEnabled = true };
                    if (reason == "active") { desktop.Idle = false; desktop.ActiveTurns = 1; }
                    if (reason == "pending") desktop.PendingRequests = 1;
                    if (reason == "background") desktop.BackgroundTerminals = 1;
                    if (reason == "unknown") desktop.Known = false;
                    if (reason == "revoked") desktop.ControlEnabled = false;
                    Check(!RuntimeCoordinator.MayBeginShutdown(new RuntimeInspection(), new[] { desktop }, true));
                }
            });
            test("Unmanaged or unknown IDE processes block closing even a known idle Desktop", delegate
            {
                var inspection = new RuntimeInspection();
                inspection.Unmanaged.Add(new ProcessBlocker { Pid = 1, Kind = "Unknown" });
                Check(!RuntimeCoordinator.MayBeginShutdown(inspection, new[] { new DesktopSessionState { Known = true, Idle = true, ControlEnabled = true } }, false));
                inspection.Unmanaged.Clear(); inspection.Managed.Add(new RuntimeState { Known = false, Idle = false, ControlEnabled = true });
                Check(!RuntimeCoordinator.MayBeginShutdown(inspection, new DesktopSessionState[0], true));
            });
        }
    }
}
