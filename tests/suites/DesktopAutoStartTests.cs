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
