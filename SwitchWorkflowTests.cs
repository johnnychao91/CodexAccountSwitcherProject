using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CodexAccountSwitcher
{
    public static class SwitchWorkflowTests
    {
        private static void Check(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
        private static void Run(Func<Task<bool>> probe, Action apply, Action launch, CancellationToken token, TimeSpan timeout)
        { SwitchWorkflow.RunAsync(probe, apply, launch, token, TimeSpan.Zero, TimeSpan.Zero, timeout).GetAwaiter().GetResult(); }

        public static void Register(Action<string, Action> test)
        {
            test("Wait for the IDE backend as well as desktop, then apply before launching", delegate
            {
                var snapshots = new Queue<bool>(new[] { true, true, false, false });
                var events = new List<string>();
                Run(() => Task.FromResult(snapshots.Dequeue()), () => events.Add("applied"), () => events.Add("launched"), CancellationToken.None, TimeSpan.FromSeconds(5));
                Check(snapshots.Count == 0 && String.Join(",", events) == "applied,launched");
            });
            test("A process reappearing during the quiet interval delays credential changes", delegate
            {
                var snapshots = new Queue<bool>(new[] { false, true, false, false }); int applied = 0;
                Run(() => Task.FromResult(snapshots.Dequeue()), () => { Check(snapshots.Count == 0); applied++; }, () => { }, CancellationToken.None, TimeSpan.FromSeconds(5));
                Check(applied == 1);
            });
            test("Cancelling while an IDE is running never writes credentials or launches", delegate
            {
                using (var cancel = new CancellationTokenSource())
                {
                    bool touched = false, cancelled = false;
                    try { Run(() => { cancel.Cancel(); return Task.FromResult(true); }, () => touched = true, () => touched = true, cancel.Token, TimeSpan.FromSeconds(5)); }
                    catch (OperationCanceledException) { cancelled = true; }
                    Check(cancelled && !touched);
                }
            });
            test("A blocked switch times out without applying or launching", delegate
            {
                bool touched = false, failed = false;
                try { Run(() => Task.FromResult(true), () => touched = true, () => touched = true, CancellationToken.None, TimeSpan.Zero); }
                catch (UserError) { failed = true; }
                Check(failed && !touched);
            });
            test("Process inspection failure cannot be mistaken for an exited application", delegate
            {
                bool touched = false, failed = false;
                try { Run(() => { throw new UserError("Inspection unavailable"); }, () => touched = true, () => touched = true, CancellationToken.None, TimeSpan.Zero); }
                catch (UserError) { failed = true; }
                Check(failed && !touched);
            });
            test("Failed credential application never launches the desktop", delegate
            {
                bool launched = false, failed = false;
                try { Run(() => Task.FromResult(false), () => { throw new UserError("Write failed"); }, () => launched = true, CancellationToken.None, TimeSpan.FromSeconds(5)); }
                catch (UserError) { failed = true; }
                Check(failed && !launched);
            });
        }
    }
}
