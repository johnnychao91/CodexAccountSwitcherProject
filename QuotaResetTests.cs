using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CodexAccountSwitcher
{
    public static class QuotaResetTests
    {
        private static void Check(bool condition, string message)
        { if (!condition) throw new Exception(message); }
        private static void Run(Func<Task> action)
        { Task.Run(action).GetAwaiter().GetResult(); }
        private static TaskCompletionSource<bool> Signal()
        { return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); }
        private static async Task Finish(Task operation)
        {
            Check(await Task.WhenAny(operation, Task.Delay(5000)) == operation, "Quota reset operation did not finish.");
            await operation;
        }

        // No real accounts, Apps or processes are used. The workflow tests own
        // durable storage and authenticated quota-failure capture; this fixture
        // checks account choice, fresh allowance, cancellation and commit order.
        private sealed class Fixture
        {
            internal readonly DateTimeOffset InitialNow = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
            internal DateTimeOffset Now;
            internal readonly List<Account> Accounts = new List<Account>();
            internal readonly Dictionary<string, double> Remaining = new Dictionary<string, double>();
            internal readonly List<string> Queries = new List<string>();
            internal Account Current;
            internal string PendingSource;
            internal bool CaptureFailedForQuota = true;
            internal int Observations, Preparations, Waits, QuietChecks, Applies, Retains, Launches, Starts, Restores, WaitCancellations, ContinuationCancellations;
            internal readonly AutoSwitchHost Host;
            internal readonly AutoSwitchController Controller;

            internal Fixture()
            {
                Now = InitialNow;
                Accounts.Add(Profile("higher")); Accounts.Add(Profile("current"));
                Current = Accounts[1]; Remaining["higher"] = 0; Remaining["current"] = 0;
                Host = new AutoSwitchHost
                {
                    ReadCurrent = () => Current,
                    ReadAccounts = () => Accounts,
                    QueryUsage = Query,
                    UtcNow = () => Now,
                    ReadQuotaWaitSource = () => PendingSource,
                    ObserveQuotaWait = (account, token) =>
                    {
                        token.ThrowIfCancellationRequested(); Observations++;
                        if (CaptureFailedForQuota) PendingSource = account.Id;
                        return Task.FromResult(CaptureFailedForQuota);
                    },
                    PrepareQuotaContinuation = async (source, target, revalidate, token) =>
                    {
                        Check(PendingSource == source.Id, "A quota continuation had no matching wait intent.");
                        Preparations++;
                        await revalidate(token); token.ThrowIfCancellationRequested();
                        PendingSource = null;
                    },
                    WaitForRuntimeExit = token => { token.ThrowIfCancellationRequested(); Waits++; return Task.FromResult(0); },
                    ConfirmRuntimeQuiet = token => { token.ThrowIfCancellationRequested(); QuietChecks++; return Task.FromResult(0); },
                    Apply = (target, expectedSourceId) =>
                    {
                        Check(Current.Id == expectedSourceId && target.Id != Current.Id, "The reset path rewrote the same account or changed an unexpected login.");
                        Applies++; Current = target;
                    },
                    RetainCurrentAccount = target =>
                    {
                        Check(Current.Id == target.Id && Waits == 1 && QuietChecks == 1, "Retaining the source preceded runtime shutdown and final validation.");
                        Retains++;
                    },
                    Launch = () => { Check(Applies + Retains == 1, "Restart preceded one account decision."); Launches++; return Task.FromResult(0); },
                    ContinueAfterSwitch = (target, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        Check(Current.Id == target.Id && Launches == 1, "Continuation preceded the chosen account or restart.");
                        Starts++; return Task.FromResult(true);
                    },
                    CancelQuotaWait = () => { WaitCancellations++; PendingSource = null; return Task.FromResult(0); },
                    CancelContinuation = () => { ContinuationCancellations++; PendingSource = null; return Task.FromResult(0); },
                    RestoreRuntimes = () => { Restores++; return Task.FromResult(0); }
                };
                Controller = new AutoSwitchController(Host);
            }
            internal static Account Profile(string id)
            { return new Account { Id = id, Auth = "synthetic-auth-" + id, Email = id + "@example.com", Plan = "plus" }; }
            internal AccountUsage Usage(double remaining)
            { return new AccountUsage { FetchedAt = Now, Weekly = new UsageWindow { RemainingPercent = remaining, ResetsAt = Now.AddDays(1) } }; }
            internal Task<AccountUsage> Query(Account account, CancellationToken token)
            { token.ThrowIfCancellationRequested(); Queries.Add(account.Id); return Task.FromResult(Usage(Remaining[account.Id])); }
            internal async Task Start()
            { Controller.Enable(); await Finish(Controller.TickAsync()); }
            internal async Task EstablishWait()
            {
                await Start();
                Check(PendingSource == "current" && Controller.Enabled && Controller.State == AutoSwitchState.WaitingForQuota,
                    "Exhausted accounts with a captured quota failure did not enter waiting state.");
                Check(Waits == 0 && Applies == 0 && Retains == 0 && Launches == 0 && Starts == 0,
                    "Entering quota wait stopped a runtime, wrote credentials or sent a message.");
            }
            internal void CheckNoCommit()
            { Check(Applies == 0 && Retains == 0 && Launches == 0 && Starts == 0, "An unconfirmed reset committed or continued."); }
        }

        public static void Register(Action<string, Action> test)
        {
            test("Quota reset wait leaves the App idle until fresh allowance exists", delegate { Run(async delegate
            {
                var f = new Fixture(); await f.EstablishWait();
                await Finish(f.Controller.TickAsync());
                Check(f.Controller.Enabled && f.Controller.State == AutoSwitchState.WaitingForQuota && !f.Controller.IsRunning,
                    "Waiting held a flight open or stopped the monitor.");
                Check(f.Waits == 0 && f.QuietChecks == 0 && f.Preparations == 0, "Waiting bound or stopped a desktop before any allowance returned.");
                f.CheckNoCommit();
            }); });
            test("Quota reset of a higher priority account switches and continues once", delegate { Run(async delegate
            {
                var f = new Fixture(); await f.EstablishWait(); f.Remaining["higher"] = 75;
                await Finish(f.Controller.TickAsync());
                Check(f.Current.Id == "higher" && f.Applies == 1 && f.Retains == 0 && f.Preparations == 1 && f.Launches == 1 && f.Starts == 1,
                    "A higher-priority reset did not use the single continuation path.");
                await Finish(f.Controller.TickAsync());
                Check(f.Starts == 1 && f.Applies == 1 && f.Launches == 1, "An already completed reset was repeated on the next tick.");
            }); });
            test("Quota reset of the current account retains credentials and continues once", delegate { Run(async delegate
            {
                var f = new Fixture(); await f.EstablishWait(); string originalAuth = f.Current.Auth;
                f.Remaining["current"] = 50; await Finish(f.Controller.TickAsync());
                Check(f.Current.Id == "current" && f.Current.Auth == originalAuth && f.Applies == 0 && f.Retains == 1 && f.Preparations == 1 &&
                    f.Waits == 1 && f.QuietChecks == 1 && f.Launches == 1 && f.Starts == 1,
                    "The current-account reset wrote credentials or skipped validated restart/continuation.");
                await Finish(f.Controller.TickAsync()); Check(f.Starts == 1 && f.Retains == 1, "The same-account continuation repeated.");
            }); });
            test("Simultaneous quota resets choose the highest priority before the current account", delegate { Run(async delegate
            {
                var f = new Fixture(); await f.EstablishWait(); f.Remaining["higher"] = 40; f.Remaining["current"] = 60;
                await Finish(f.Controller.TickAsync());
                Check(f.Current.Id == "higher" && f.Applies == 1 && f.Retains == 0 && f.Starts == 1,
                    "The source's early Available result hid a higher-priority reset.");
            }); });
            test("A single-account quota reset can continue without a credential write", delegate { Run(async delegate
            {
                var f = new Fixture(); f.Accounts.RemoveAt(0); await f.EstablishWait();
                f.Remaining["current"] = 80; await Finish(f.Controller.TickAsync());
                Check(f.Applies == 0 && f.Retains == 1 && f.Starts == 1, "One account could not recover after its allowance reset.");
            }); });
            test("Recovered weekly-only allowance remains eligible for quota continuation", delegate { Run(async delegate
            {
                var f = new Fixture(); await f.EstablishWait(); f.Current.Plan = "prolite";
                f.Remaining["current"] = 99; await Finish(f.Controller.TickAsync());
                Check(f.Retains == 1 && f.Starts == 1, "A missing five-hour window made a valid weekly-only reset unusable.");
            }); });
            test("A restored waiting intent handles an already refreshed current allowance", delegate { Run(async delegate
            {
                var f = new Fixture(); f.PendingSource = "current"; f.Remaining["current"] = 90;
                await f.Start();
                Check(f.Retains == 1 && f.Starts == 1, "An existing safe wait was lost to the normal Available early return.");
            }); });
            test("An available account without a quota wait never sends a continuation", delegate { Run(async delegate
            {
                var f = new Fixture(); f.Remaining["current"] = 90; f.Remaining["higher"] = 90;
                await f.Start(); f.CheckNoCommit();
                Check(f.Observations == 0 && f.Preparations == 0, "An ordinary idle account acquired an automatic continuation.");
            }); });
            test("Exhaustion without an authoritative quota-stopped conversation does not create a wait", delegate { Run(async delegate
            {
                var f = new Fixture(); f.CaptureFailedForQuota = false; await f.Start();
                Check(f.PendingSource == null && f.Controller.Enabled && f.Controller.State != AutoSwitchState.WaitingForQuota,
                    "A quota-zero sample alone created a continuation intent.");
                f.Remaining["current"] = 80; await Finish(f.Controller.TickAsync()); f.CheckNoCommit();
            }); });
            test("Quota wait never chooses a lower-priority reset", delegate { Run(async delegate
            {
                var f = new Fixture(); f.Accounts.Add(Fixture.Profile("lower")); f.Remaining["lower"] = 0;
                await f.EstablishWait(); f.Queries.Clear(); f.Remaining["lower"] = 100;
                await Finish(f.Controller.TickAsync()); f.CheckNoCommit();
                Check(f.PendingSource == "current" && !f.Queries.Contains("lower"), "A lower-priority account displaced the recorded source while waiting.");
            }); });
            foreach (string invalid in new[] { "unknown-source", "stale-source", "reset-source", "unknown-target", "stale-target", "reset-target" })
            {
                string mode = invalid;
                test("Quota reset requires fresh allowance: " + mode, delegate { Run(async delegate
                {
                    var f = new Fixture(); await f.EstablishWait(); f.Remaining["higher"] = 70;
                    f.Host.QueryUsage = (account, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        bool affected = mode.EndsWith("source", StringComparison.Ordinal) ? account.Id == "current" : account.Id == "higher";
                        if (affected && mode.StartsWith("unknown", StringComparison.Ordinal)) return Task.FromResult<AccountUsage>(null);
                        var result = f.Usage(f.Remaining[account.Id]);
                        if (affected && mode.StartsWith("stale", StringComparison.Ordinal)) result.FetchedAt = f.Now.AddMinutes(-2);
                        if (affected && mode.StartsWith("reset", StringComparison.Ordinal)) result.Weekly.ResetsAt = f.Now;
                        return Task.FromResult(result);
                    };
                    await Finish(f.Controller.TickAsync()); f.CheckNoCommit();
                    Check(f.Preparations == 0 && f.Waits == 0, "Unconfirmed reset data reached continuation preparation.");
                }); });
            }
            test("Stopping an idle quota wait cancels it before future allowance can resume", delegate { Run(async delegate
            {
                var f = new Fixture(); await f.EstablishWait(); await Finish(f.Controller.StopAsync());
                Check(f.PendingSource == null && f.WaitCancellations > 0 && !f.Controller.Enabled, "Stopping did not clear the outstanding wait intent.");
                f.Remaining["current"] = 90; f.Controller.Enable(); await Finish(f.Controller.TickAsync()); f.CheckNoCommit();
            }); });
            test("Closing during a quota query preserves an unclaimed wait for one later continuation", delegate { Run(async delegate
            {
                var f = new Fixture(); await f.EstablishWait(); var entered = Signal();
                var query = f.Host.QueryUsage;
                f.Host.QueryUsage = async (account, token) =>
                {
                    entered.TrySetResult(true); await Task.Delay(Timeout.Infinite, token);
                    return f.Usage(0);
                };
                var flight = f.Controller.TickAsync(); await Finish(entered.Task);
                await Finish(f.Controller.StopAsync(true)); await Finish(flight);
                Check(f.PendingSource == "current" && f.WaitCancellations == 0 && f.ContinuationCancellations == 0 &&
                    !f.Controller.Enabled && !f.Controller.IsRunning, "Closing a read-only quota check cancelled the safe waiting intent.");
                f.CheckNoCommit();
                f.Host.QueryUsage = query; f.Remaining["current"] = 70;
                f.Controller.Enable(); await Finish(f.Controller.TickAsync()); await Finish(f.Controller.TickAsync());
                Check(f.Retains == 1 && f.Applies == 0 && f.Starts == 1 && f.Launches == 1,
                    "The preserved wait was lost or resumed more than once after monitoring restarted.");
            }); });
            foreach (bool claimed in new[] { false, true })
            {
                bool afterClaim = claimed;
                test("Closing during quota preparation cancels an owned continuation " + (afterClaim ? "after claim" : "before claim"), delegate { Run(async delegate
                {
                    var f = new Fixture(); await f.EstablishWait(); f.Remaining["current"] = 70;
                    var entered = Signal();
                    if (afterClaim)
                    {
                        f.Host.WaitForRuntimeExit = async token =>
                        {
                            Check(f.PendingSource == null, "Runtime shutdown began before the wait was claimed.");
                            f.Waits++; entered.TrySetResult(true); await Task.Delay(Timeout.Infinite, token);
                        };
                    }
                    else
                    {
                        f.Host.PrepareQuotaContinuation = async (source, target, revalidate, token) =>
                        { f.Preparations++; entered.TrySetResult(true); await Task.Delay(Timeout.Infinite, token); };
                    }
                    var flight = f.Controller.TickAsync(); await Finish(entered.Task);
                    await Finish(f.Controller.StopAsync(true)); await Finish(flight);
                    Check(f.PendingSource == null && f.ContinuationCancellations == 1 && f.WaitCancellations == 0 &&
                        f.Restores == (afterClaim ? 1 : 0) && !f.Controller.Enabled,
                        "Preserve-on-close retained a claimed/preparing continuation or skipped runtime recovery.");
                    f.CheckNoCommit();
                    f.Controller.Enable(); await Finish(f.Controller.TickAsync()); f.CheckNoCommit();
                }); });
            }
            test("A manual login change invalidates the quota wait", delegate { Run(async delegate
            {
                var f = new Fixture(); await f.EstablishWait();
                f.Current = f.Accounts[0]; f.Remaining["higher"] = 90;
                await Finish(f.Controller.TickAsync()); f.CheckNoCommit();
                Check(f.PendingSource == null, "A wait survived changing the active account.");
            }); });
            test("A changed priority before preparation validation never shuts down a runtime", delegate { Run(async delegate
            {
                var f = new Fixture(); await f.EstablishWait(); f.Remaining["higher"] = 90;
                f.Host.PrepareQuotaContinuation = async (source, target, revalidate, token) =>
                {
                    f.Preparations++; f.Accounts.Reverse(); await revalidate(token);
                    throw new Exception("Changed account order passed pre-claim validation.");
                };
                await Finish(f.Controller.TickAsync()); f.CheckNoCommit();
                Check(f.Waits == 0 && f.ContinuationCancellations > 0, "Priority changes reached runtime shutdown or left a handoff owned.");
            }); });
            test("A selected account exhausted during prerequisite waiting is not resumed", delegate { Run(async delegate
            {
                var f = new Fixture(); await f.EstablishWait(); f.Remaining["current"] = 90;
                f.Host.PrepareQuotaContinuation = async (source, target, revalidate, token) =>
                {
                    f.Preparations++; f.Remaining["current"] = 0; await revalidate(token);
                    throw new Exception("Depleted account passed pre-claim validation.");
                };
                await Finish(f.Controller.TickAsync()); f.CheckNoCommit(); Check(f.Waits == 0, "A depleted reset reached shutdown.");
            }); });
            foreach (string change in new[] { "depleted", "priority", "identity", "stale" })
            {
                string mode = change;
                test("Quota reset final commit rejects " + mode + " after shutdown", delegate { Run(async delegate
                {
                    var f = new Fixture(); await f.EstablishWait(); f.Remaining["higher"] = 90;
                    var originalWait = f.Host.WaitForRuntimeExit;
                    f.Host.WaitForRuntimeExit = async token =>
                    {
                        await originalWait(token);
                        if (mode == "depleted") f.Remaining["higher"] = 0;
                        else if (mode == "priority") f.Accounts.Reverse();
                        else if (mode == "identity") f.Current = f.Accounts[0];
                    };
                    if (mode == "stale") f.Host.ConfirmRuntimeQuiet = token =>
                    { f.QuietChecks++; f.Now = f.Now.AddMinutes(2); return Task.FromResult(0); };
                    await Finish(f.Controller.TickAsync()); f.CheckNoCommit();
                    Check(f.Restores == 1, "Aborting after shutdown failed to recover the existing runtime.");
                }); });
            }
            test("Concurrent reset ticks share one preparation and cancellation stops before commit", delegate { Run(async delegate
            {
                var f = new Fixture(); await f.EstablishWait(); f.Remaining["current"] = 90;
                var entered = Signal();
                f.Host.PrepareQuotaContinuation = async (source, target, revalidate, token) =>
                { f.Preparations++; entered.TrySetResult(true); await Task.Delay(Timeout.Infinite, token); };
                var first = f.Controller.TickAsync(); await Finish(entered.Task);
                Check(ReferenceEquals(first, f.Controller.TickAsync()) && f.Preparations == 1, "Concurrent ticks created competing reset preparations.");
                await Finish(f.Controller.StopAsync()); await Finish(first); f.CheckNoCommit();
                Check(f.PendingSource == null && !f.Controller.Enabled && f.Waits == 0, "Cancellation retained or executed the reset intent.");
            }); });
            test("An uncertain reset continuation is never sent again", delegate { Run(async delegate
            {
                var f = new Fixture(); await f.EstablishWait(); f.Remaining["current"] = 90;
                f.Host.ContinueAfterSwitch = (target, token) => { f.Starts++; throw new System.IO.IOException("Synthetic lost start acknowledgement."); };
                await Finish(f.Controller.TickAsync()); await Finish(f.Controller.TickAsync());
                Check(f.Starts == 1 && f.Retains == 1 && f.Applies == 0 && f.Launches == 1 && !f.Controller.Enabled &&
                    f.Controller.State == AutoSwitchState.Faulted && f.ContinuationCancellations > 0,
                    "An uncertain reset continuation was repeated or monitoring stayed armed.");
            }); });
        }
    }
}
