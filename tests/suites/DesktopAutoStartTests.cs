using System;
using System.Collections.Generic;
using System.Linq;

namespace CodexAccountSwitcher
{
    public static class DesktopAutoStartTests
    {
        private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        private static DesktopProcessIdentity Actor(int pid)
        { return new DesktopProcessIdentity { Pid = pid, StartedUtc = "synthetic-start-" + pid, ExecutablePath = "fixture-actor" }; }
        private static DesktopSessionState Session(DesktopProcessIdentity host, DesktopProcessIdentity app)
        {
            return new DesktopSessionState { Host = host, App = app, State = "running", Known = true, Idle = true,
                ControlEnabled = true, TransportReady = true, OwnedActors = new List<DesktopProcessIdentity> { host, app } };
        }
        private static ProcessBlocker Process(int pid, string kind)
        { return new ProcessBlocker { Pid = pid, Kind = kind, Name = "synthetic-candidate" }; }

        public static void Register(Action<string, Action> test)
        {
            test("Unmanaged startup selects only one verified app and related desktop descendants", delegate
            {
                string path = System.IO.Path.GetFullPath(@"fixture\ChatGPT.exe");
                string born = "2026-01-01T00:00:00.0000000Z";
                var app = new DesktopProcessIdentity { Pid = 110, SessionId = 1, ExecutablePath = path, StartedUtc = born };
                var child = new DesktopProcessIdentity { Pid = 111, ParentPid = 110, SessionId = 1,
                    ExecutablePath = System.IO.Path.GetFullPath(@"fixture\codex.exe"), StartedUtc = born, ParentIdentity = app };
                Func<int, ProcessBlocker> blocker = pid => new ProcessBlocker { Pid = pid, Kind = "Desktop",
                    CreationTime = System.Management.ManagementDateTimeConverter.ToDmtfDateTime(DateTime.Parse(born).ToUniversalTime()) };
                Func<IList<DesktopSessionState>, IList<ProcessBlocker>, IList<DesktopProcessIdentity>, DesktopProcessIdentity> find =
                    (states, items, actors) => DesktopAutoStart.FindUnmanagedApp(states, items, actors, path, 1, a => true, a => a.Pid == 110);
                var empty = new DesktopSessionState[0]; var blockers = new[] { blocker(110), blocker(111) };
                Check(find(empty, blockers, new[] { app, child }) == app, "Verified child prevented App selection.");
                blockers[0].CreationTime = "20251231183000.000000-330";
                blockers[1].CreationTime = "20260101084500.000000+525";
                Check(find(empty, blockers, new[] { app, child }) == app, "Different WMI offsets changed the same UTC identity.");
                blockers[0].CreationTime = "20251231183000.000000-360";
                Check(find(empty, blockers, new[] { app, child }) == null, "A different UTC creation time was accepted.");
                blockers[0] = blocker(110);
                blockers[1] = blocker(111);
                child.ParentIdentity = null; child.ParentPid = 999;
                Check(find(empty, blockers, new[] { app, child }) == null, "Unrelated desktop blocker was accepted.");
                child.ParentPid = 110; child.ParentIdentity = app; child.StartedUtc = "2025-01-01T00:00:00Z";
                Check(find(empty, blockers, new[] { app, child }) == null, "Mismatched creation time was accepted.");
                child.StartedUtc = born; blockers[1].Kind = "CLI";
                Check(find(empty, blockers, new[] { app, child }) == null, "CLI blocker was accepted.");
                blockers[1].Kind = "Desktop";
                Check(find(new[] { Session(Actor(112), Actor(113)) }, blockers, new[] { app, child }) == null,
                    "A live retained session was ignored.");
                Check(find(empty, blockers, new[] { app }) == null, "Missing identity was accepted.");
                Check(find(empty, new[] { blocker(110), blocker(110) }, new[] { app }) == null, "Duplicate blocker was accepted.");
                Check(DesktopAutoStart.FindUnmanagedApp(empty, new[] { blocker(110) }, new[] { app }, path, 2, a => true, a => true) == null,
                    "Another Windows session was accepted.");
                Check(DesktopAutoStart.FindUnmanagedApp(empty, new[] { blocker(110) }, new[] { app }, path, 1, a => true, a => false) == null,
                    "An App without a window was accepted.");
                Check(DesktopAutoStart.FindUnmanagedApp(empty, new[] { blocker(110) }, new[] { app }, path, 1, a => { throw new Exception(); }, a => true) == null,
                    "Failed identity probe was accepted.");
            });
            test("Desktop startup recovery requires the sole original backend and a confirmed closed owner", delegate
            {
                var target = new DesktopPackageTarget { AppExecutable = System.IO.Path.GetFullPath(@"fixture\ChatGPT.exe"),
                    CliExecutable = System.IO.Path.GetFullPath(@"fixture\codex.exe") };
                var app = new DesktopProcessIdentity { Pid = 310, StartedUtc = "2026-01-01T00:00:00Z", ExecutablePath = target.AppExecutable };
                var backend = new DesktopProcessIdentity { Pid = 311, ParentPid = app.Pid, StartedUtc = "2026-01-01T00:00:01Z", ExecutablePath = target.CliExecutable };
                var blocker = Process(backend.Pid, "Desktop"); blocker.CreationTime = "20260101000001.000000+000";
                Check(DesktopAutoStart.FindSoleBackend(new[] { blocker }, new[] { backend }, target) == backend &&
                    DesktopAutoStart.BelongsToClosedApp(backend, app, new[] { backend }, actor => actor == app), "The original closed App's backend was not recovered.");
                Check(!DesktopAutoStart.BelongsToClosedApp(backend, app, new[] { backend }, actor => false) &&
                    !DesktopAutoStart.BelongsToClosedApp(backend, app, new DesktopProcessIdentity[0], actor => true), "An open or unobserved owner authorized recovery.");
                Check(DesktopAutoStart.FindSoleBackend(new[] { blocker }, new[] { app, backend }, target) == null &&
                    DesktopAutoStart.FindSoleBackend(new[] { blocker, Process(313, "VSCode") }, new[] { backend }, target) == null,
                    "A new window or unrelated blocker allowed recovery.");
                blocker.CreationTime = "20260101000002.000000+000";
                Check(DesktopAutoStart.FindSoleBackend(new[] { blocker }, new[] { backend }, target) == null, "A reused PID allowed recovery.");
            });
            test("Queued desktop recovery identifies one old backend under a still-live retiring host", delegate
            {
                var target = new DesktopPackageTarget { AppExecutable = System.IO.Path.GetFullPath(@"fixture\ChatGPT.exe"),
                    CliExecutable = System.IO.Path.GetFullPath(@"fixture\codex.exe") };
                var host = new DesktopProcessIdentity { Pid = 620, StartedUtc = "2026-01-01T00:00:00Z",
                    ExecutablePath = System.IO.Path.GetFullPath(@"fixture\previous-release\DesktopRuntimeHost.exe") };
                var backend = new DesktopProcessIdentity { Pid = 621, ParentPid = host.Pid, ParentIdentity = host,
                    StartedUtc = "2026-01-01T00:00:01Z", ExecutablePath = target.CliExecutable };
                var app = new DesktopProcessIdentity { Pid = 622, StartedUtc = "2026-01-01T00:00:02Z", ExecutablePath = target.AppExecutable };
                var state = new DesktopSessionState { Host = host, App = app, State = "unknown", RetirementRecorded = true,
                    Reason = "previousInstallationStillRunning", OwnedActors = new List<DesktopProcessIdentity> { host, backend } };
                var blocker = Process(backend.Pid, "Desktop"); blocker.CreationTime = "20260101000001.000000+000";
                var blockers = new[] { Process(host.Pid, "Integration"), blocker };
                Func<IList<DesktopProcessIdentity>, Func<DesktopProcessIdentity, bool>, DesktopProcessIdentity> find = (actors, gone) =>
                    DesktopAutoStart.FindStartupRecoveryBackend(blockers, actors, target, state, null, null, actor => actor != app, gone);
                Check(find(new[] { host, backend }, actor => actor == app) == backend, "Legacy recovery did not select only the original backend.");
                Check(find(new[] { host, backend, app }, actor => true) == null && find(new[] { host, backend }, actor => false) == null,
                    "A live or unconfirmed App allowed cleanup.");
                backend.ParentIdentity = null;
                Check(find(new[] { host, backend }, actor => actor == app) == null, "A PID without its verified parent allowed cleanup.");
                backend.ParentIdentity = host; state.RetirementRecorded = false;
                Check(find(new[] { host, backend }, actor => actor == app) == null, "An unknown host stage allowed cleanup.");
            });
            test("Desktop startup keeps an explicit off preference without probing or launching", delegate
            {
                Check(DesktopAutoStart.Decide(false, null, null, actor => { throw new Exception("Must not probe disabled startup."); }) ==
                    DesktopAutoStartDecision.Disabled, "An explicit off preference attempted startup.");
            });
            test("Desktop startup waits for every unrelated Codex candidate including VS Code and CLI", delegate
            {
                foreach (string kind in new[] { "Desktop", "VSCode", "OtherIde", "CLI", "Unknown", "Integration" })
                    Check(DesktopAutoStart.Decide(true, new DesktopSessionState[0], new[] { Process(51001, kind) }, actor => false) ==
                        DesktopAutoStartDecision.WaitingForExit, "Startup ignored an unrelated " + kind + " candidate.");
            });
            test("Desktop startup recognizes a connected busy App without granting a second launch", delegate
            {
                var host = Actor(51011); var app = Actor(51012); var state = Session(host, app);
                var candidates = new[] { Process(host.Pid, "Integration"), Process(app.Pid, "Desktop") };
                foreach (bool busy in new[] { false, true })
                {
                    state.Idle = !busy; state.ActiveTurns = busy ? 2 : 0; state.BackgroundTerminals = busy ? 1 : 0;
                    Check(DesktopAutoStart.Decide(true, new[] { state }, candidates, actor => true) == DesktopAutoStartDecision.Connected,
                        "A connected App requested another instance.");
                }
                state.Known = false; state.Idle = false;
                Check(DesktopAutoStart.Decide(true, new[] { state }, candidates, actor => true) == DesktopAutoStartDecision.Connected,
                    "Transport connection was confused with full work-state readiness.");
                Check(!RuntimeCoordinator.MayBeginShutdown(new RuntimeInspection(), new[] { state }, true),
                    "Connection detection granted shutdown of an unknown App.");
            });
            test("Desktop startup does not mistake an independent executor or second App for a managed connection", delegate
            {
                var host = Actor(51021); var app = Actor(51022); var state = Session(host, app);
                foreach (string kind in new[] { "Desktop", "VSCode", "CLI", "Unknown" })
                {
                    var candidates = new[] { Process(host.Pid, "Integration"), Process(app.Pid, "Desktop"), Process(51023, kind) };
                    Check(DesktopAutoStart.Decide(true, new[] { state }, candidates, actor => true) == DesktopAutoStartDecision.WaitingForExit,
                        "A connected App concealed an independent " + kind + " actor.");
                }
            });
            test("Desktop startup retains orphan and old-version blockers after a host exits", delegate
            {
                var host = Actor(51031); var app = Actor(51032); var orphan = Actor(51033);
                var old = Session(host, app); old.Known = old.ControlEnabled = old.TransportReady = false;
                old.State = "unknown"; old.Reason = "previousInstallationStillRunning"; old.OwnedActors.Add(orphan);
                foreach (var remaining in new[] { host, app, orphan })
                    Check(DesktopAutoStart.Decide(true, new[] { old }, new ProcessBlocker[0], actor => ReferenceEquals(actor, remaining)) ==
                        DesktopAutoStartDecision.WaitingForExit, "A retained old-version actor allowed another launch.");
                var currentHost = Actor(51034); var currentApp = Actor(51035); var current = Session(currentHost, currentApp);
                Check(DesktopAutoStart.Decide(true, new[] { current, old }, new ProcessBlocker[0], actor => true) ==
                    DesktopAutoStartDecision.WaitingForExit, "A new connection concealed an old live session.");
            });
            test("Desktop startup launches only after candidate and retained-identity inventories are both empty", delegate
            {
                var host = Actor(51041); var app = Actor(51042); var dead = Session(host, app);
                Check(DesktopAutoStart.Decide(true, new DesktopSessionState[0], new ProcessBlocker[0], actor => false) ==
                    DesktopAutoStartDecision.Launch, "Empty inventories did not permit a guarded launch.");
                Check(DesktopAutoStart.Decide(true, new[] { dead }, new ProcessBlocker[0], actor => false) ==
                    DesktopAutoStartDecision.Launch, "Expired records prevented a new guarded launch.");
                Check(DesktopAutoStart.Decide(true, new[] { dead }, new[] { Process(host.Pid, "Integration") }, actor => false) ==
                    DesktopAutoStartDecision.WaitingForExit, "A reused candidate PID was covered by an expired identity.");
            });
            test("Desktop startup never restores or replaces a partially started prepared or failed session automatically", delegate
            {
                foreach (string stage in new[] { "starting", "prepared", "preparing", "quiesced", "recoveryRequired", "closingApp", "orphaned" })
                {
                    var state = Session(Actor(51051), Actor(51052)); state.State = stage;
                    Check(DesktopAutoStart.Decide(true, new[] { state }, new ProcessBlocker[0], actor => true) ==
                        DesktopAutoStartDecision.WaitingForExit, "Startup tried to replace a " + stage + " session.");
                }
                var failed = Session(Actor(51053), Actor(51054)); failed.BackendExitCode = 0;
                Check(DesktopAutoStart.Decide(true, new[] { failed }, new ProcessBlocker[0], actor => true) ==
                    DesktopAutoStartDecision.WaitingForExit, "A completed backend was treated as a live connection.");
            });
            test("Desktop startup fails closed when inventories or identity probes cannot be trusted", delegate
            {
                var state = Session(Actor(51061), Actor(51062));
                Check(DesktopAutoStart.Decide(true, null, new ProcessBlocker[0], actor => false) == DesktopAutoStartDecision.WaitingForExit,
                    "Missing session inventory implied absence.");
                Check(DesktopAutoStart.Decide(true, new DesktopSessionState[0], null, actor => false) == DesktopAutoStartDecision.WaitingForExit,
                    "Missing process inventory implied absence.");
                Check(DesktopAutoStart.Decide(true, new[] { state }, new ProcessBlocker[0], actor => { throw new InvalidOperationException(); }) ==
                    DesktopAutoStartDecision.WaitingForExit, "A failed identity probe permitted startup.");
                Check(DesktopAutoStart.Decide(true, new[] { state }, new ProcessBlocker[0], null) == DesktopAutoStartDecision.WaitingForExit,
                    "A missing identity probe permitted startup.");
                Check(DesktopAutoStart.Decide(true, new DesktopSessionState[] { null }, new ProcessBlocker[0], actor => false) ==
                    DesktopAutoStartDecision.WaitingForExit, "A malformed session permitted startup.");
                Check(DesktopAutoStart.Decide(true, new DesktopSessionState[0], new ProcessBlocker[] { null }, actor => false) ==
                    DesktopAutoStartDecision.WaitingForExit, "A malformed process candidate permitted startup.");
                state.OwnedActors = null;
                Check(DesktopAutoStart.Decide(true, new[] { state }, new ProcessBlocker[0], actor => false) == DesktopAutoStartDecision.WaitingForExit,
                    "Missing retained identities implied absence.");
            });
        }
    }
}
