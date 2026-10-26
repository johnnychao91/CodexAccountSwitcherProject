using System;
using System.Collections.Generic;
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
