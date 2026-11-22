using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CodexAccountSwitcher
{
    internal enum DesktopAutoStartDecision
    {
        Disabled,
        Connected,
        WaitingForExit,
        Launch
    }

    internal static class DesktopAutoStart
    {
        // Select only an independently running App whose entire blocker inventory is
        // attributable to its verified process tree. Failure to prove this is a veto.
        internal static DesktopProcessIdentity FindUnmanagedApp(IList<DesktopSessionState> states,
            IList<ProcessBlocker> blockers, IList<DesktopProcessIdentity> actors, string executable, int sessionId,
            Func<DesktopProcessIdentity, bool> live, Func<DesktopProcessIdentity, bool> hasWindow)
        {
            if (states == null || blockers == null || actors == null || live == null || hasWindow == null ||
                sessionId < 0 || String.IsNullOrWhiteSpace(executable) || !System.IO.Path.IsPathRooted(executable)) return null;
            try
            {
                string target = System.IO.Path.GetFullPath(executable);
                var sessions = states.ToArray(); var candidates = blockers.ToArray(); var snapshot = actors.ToArray();
                if (sessions.Any(s => s == null || s.Host == null || s.Host.Pid <= 0 || (s.App != null && s.App.Pid <= 0) || s.OwnedActors == null ||
                    s.OwnedActors.Any(a => a == null || a.Pid <= 0)) ||
                    candidates.Any(b => b == null || b.Pid <= 0 || b.Kind != "Desktop") ||
                    candidates.Select(b => b.Pid).Distinct().Count() != candidates.Length ||
                    snapshot.Any(a => a == null || a.Pid <= 0) || snapshot.Select(a => a.Pid).Distinct().Count() != snapshot.Length)
                    return null;
                if (sessions.Any(s => live(s.Host) || (s.App != null && live(s.App)) || s.OwnedActors.Any(live))) return null;
                var byPid = snapshot.ToDictionary(a => a.Pid);
                var verified = new List<DesktopProcessIdentity>();
                foreach (var blocker in candidates)
                {
                    DesktopProcessIdentity actor; DateTimeOffset started, creation;
                    if (!byPid.TryGetValue(blocker.Pid, out actor) || actor.SessionId != sessionId ||
                        String.IsNullOrWhiteSpace(actor.ExecutablePath) ||
                        !DateTimeOffset.TryParse(actor.StartedUtc, System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.RoundtripKind, out started) ||
                        !TryReadWmiCreation(blocker.CreationTime, out creation)) return null;
                    if (creation.UtcDateTime.Ticks / 10 != started.UtcDateTime.Ticks / 10 || !live(actor)) return null;
                    verified.Add(actor);
                }
                var apps = verified.Where(a => String.Equals(System.IO.Path.GetFullPath(a.ExecutablePath), target,
                    StringComparison.OrdinalIgnoreCase) && hasWindow(a)).ToArray();
                if (apps.Length != 1) return null;
                var app = apps[0];
                foreach (var actor in verified)
                {
                    if (actor.Pid == app.Pid) continue;
                    var current = actor; var seen = new HashSet<int>();
                    while (current.Pid != app.Pid)
                    {
                        if (!seen.Add(current.Pid) || seen.Count > 32 || current.ParentPid <= 0) return null;
                        DesktopProcessIdentity parent;
                        if (current.ParentIdentity != null && current.ParentIdentity.Pid == current.ParentPid)
                            parent = current.ParentIdentity;
                        else if (!byPid.TryGetValue(current.ParentPid, out parent)) return null;
                        DateTimeOffset childBorn, parentBorn;
                        if (parent.SessionId != sessionId || !live(parent) ||
                            !DateTimeOffset.TryParse(current.StartedUtc, System.Globalization.CultureInfo.InvariantCulture,
                                System.Globalization.DateTimeStyles.RoundtripKind, out childBorn) ||
                            !DateTimeOffset.TryParse(parent.StartedUtc, System.Globalization.CultureInfo.InvariantCulture,
                                System.Globalization.DateTimeStyles.RoundtripKind, out parentBorn) || childBorn < parentBorn) return null;
                        if (parent.Pid == app.Pid && !DesktopNative.Equal(parent, app)) return null;
                        current = parent;
                    }
                }
                return app;
            }
            catch { return null; }
        }

        private static bool TryReadWmiCreation(string value, out DateTimeOffset result)
        {
            result = default(DateTimeOffset);
            DateTime clock;
            int minutes;
            // DMTF carries its own UTC offset. Compare directly against FILETIME;
            // a conversion through local time can lose the offset in a DST fold.
            if (value == null || value.Length != 25 || (value[21] != '+' && value[21] != '-') ||
                !DateTime.TryParseExact(value.Substring(0, 21), "yyyyMMddHHmmss.ffffff", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out clock) ||
                !Int32.TryParse(value.Substring(22), NumberStyles.None, CultureInfo.InvariantCulture, out minutes) || minutes > 840)
                return false;
            result = new DateTimeOffset(clock, TimeSpan.FromMinutes(value[21] == '-' ? -minutes : minutes));
            return true;
        }
        internal static DesktopProcessIdentity FindSoleBackend(IList<ProcessBlocker> blockers, IList<DesktopProcessIdentity> actors, DesktopPackageTarget target,
            DesktopProcessIdentity host = null)
        {
            if (blockers == null || blockers.Count != 1 || blockers[0].Kind != "Desktop" || actors == null || target == null ||
                actors.Any(a => String.Equals(a.ExecutablePath, target.AppExecutable, StringComparison.OrdinalIgnoreCase))) return null;
            var blocker = blockers[0];
            if (actors.Any(actor => actor.Pid != blocker.Pid && !DesktopNative.Equal(actor, host))) return null;
            var backends = actors.Where(actor => actor.Pid == blocker.Pid &&
                String.Equals(actor.ExecutablePath, target.CliExecutable, StringComparison.OrdinalIgnoreCase)).ToArray();
            DateTimeOffset born, reported;
            return backends.Length == 1 && TryReadWmiCreation(blocker.CreationTime, out reported) &&
                DateTimeOffset.TryParse(backends[0].StartedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out born) &&
                born.UtcDateTime.Ticks / 10 == reported.UtcDateTime.Ticks / 10 ? backends[0] : null;
        }

        internal static bool BelongsToClosedApp(DesktopProcessIdentity candidate, DesktopProcessIdentity app,
            IList<DesktopProcessIdentity> owned, Func<DesktopProcessIdentity, bool> exited)
        {
            return candidate != null && app != null && owned != null && owned.Any(actor => DesktopNative.Equal(actor, candidate)) && exited(app);
        }

        internal static DesktopProcessIdentity FindStartupRecoveryBackend(IList<ProcessBlocker> blockers, IList<DesktopProcessIdentity> actors,
            DesktopPackageTarget target, DesktopSessionState previous, DesktopProcessIdentity observedApp, IList<DesktopProcessIdentity> observedBackends,
            Func<DesktopProcessIdentity, bool> live, Func<DesktopProcessIdentity, bool> exited)
        {
            if (blockers == null || blockers.Count == 0 || blockers.Count > 2) return null;
            var host = previous != null && previous.IsRetiringOwner && previous.Host != null && live(previous.Host) &&
                String.Equals(System.IO.Path.GetFileName(previous.Host.ExecutablePath), "DesktopRuntimeHost.exe", StringComparison.OrdinalIgnoreCase) ? previous.Host : null;
            var scoped = host == null ? blockers : blockers.Where(p => p.Kind != "Integration" || p.Pid != host.Pid).ToList();
            var backend = FindSoleBackend(scoped, actors, target, host);
            if (backend == null || !live(backend)) return null;
            if (previous == null) return BelongsToClosedApp(backend, observedApp, observedBackends, exited) ? backend : null;
            if (!BelongsToClosedApp(backend, previous.App, previous.OwnedActors, exited)) return null;
            // A live legacy host can drain after its original child exits. Keep it
            // outside the termination scope and verify the actual parent identity.
            if (host == null) return exited(previous.Host) ? backend : null;
            return backend.ParentPid == host.Pid && DesktopNative.Equal(backend.ParentIdentity, host) ? backend : null;
        }

        // This decides whether startup should request an ordinary guarded launch.
        // Connected is connection information, never permission to stop a chat or
        // apply credentials. Those operations retain their existing fresh gates.
        internal static DesktopAutoStartDecision Decide(bool requested, IList<DesktopSessionState> states,
            IList<ProcessBlocker> processes, Func<DesktopProcessIdentity, bool> live)
        {
            if (!requested) return DesktopAutoStartDecision.Disabled;
            if (states == null || processes == null || live == null) return DesktopAutoStartDecision.WaitingForExit;
            try
            {
                var sessions = states.ToArray();
                var candidates = processes.ToArray();
                if (sessions.Any(state => state == null || state.Host == null || state.Host.Pid <= 0 ||
                    state.OwnedActors == null || state.OwnedActors.Any(actor => actor == null || actor.Pid <= 0)) ||
                    candidates.Any(process => process == null || process.Pid <= 0))
                    return DesktopAutoStartDecision.WaitingForExit;

                var observed = new Dictionary<DesktopProcessIdentity, bool>();
                Func<DesktopProcessIdentity, bool> alive = actor =>
                {
                    if (actor == null) return false;
                    bool value;
                    if (!observed.TryGetValue(actor, out value)) observed.Add(actor, value = live(actor));
                    return value;
                };
                var retained = sessions.Where(state => alive(state.Host) || alive(state.App) || state.OwnedActors.Any(alive)).ToArray();
                if (retained.Length == 0)
                    return candidates.Length == 0 ? DesktopAutoStartDecision.Launch : DesktopAutoStartDecision.WaitingForExit;

                // A busy but connected managed App does not need a second instance.
                // Old hosts, orphans, incomplete startup and unrelated candidates
                // instead remain pending; startup never initiates their shutdown.
                bool connected = retained.All(state => alive(state.Host) && alive(state.App) &&
                    state.ControlEnabled && state.TransportReady && state.State == "running" && !state.BackendExitCode.HasValue);
                if (!connected) return DesktopAutoStartDecision.WaitingForExit;
                var owned = retained.SelectMany(state => new[] { state.Host, state.App }.Concat(state.OwnedActors))
                    .Where(alive).ToArray();
                return candidates.All(process => owned.Any(actor => actor.Pid == process.Pid)) ?
                    DesktopAutoStartDecision.Connected : DesktopAutoStartDecision.WaitingForExit;
            }
            catch
            {
                // A failed identity probe or changing inventory cannot establish
                // absence. Keep the preference on without starting another App.
                return DesktopAutoStartDecision.WaitingForExit;
            }
        }
    }
}
