using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CodexAccountSwitcher
{
    public static class AutoSwitchTests
    {
        private static void Check(bool condition, string message)
        { if (!condition) throw new Exception(message); }
        private static TaskCompletionSource<bool> Signal()
        { return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); }
        private static async Task Finish(Task operation)
        {
            Check(await Task.WhenAny(operation, Task.Delay(5000)) == operation, "The automatic switch operation did not finish.");
            await operation;
        }
        private static void Run(Func<Task> operation) { Task.Run(operation).GetAwaiter().GetResult(); }

        private sealed class Fixture
        {
            public DateTimeOffset Now = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
            public Account Current;
            public readonly List<Account> Accounts = new List<Account>();
            public readonly List<string> Queries = new List<string>();
            public int Waits, QuietChecks, Applies, Launches, Restores, OwnedQueries;
            public AutoSwitchHost Host;
            public AutoSwitchController Controller;

            public Fixture()
            {
                Current = Profile("current");
                Accounts.Add(Current); Accounts.Add(Profile("next"));
                Host = new AutoSwitchHost
                {
                    ReadCurrent = () => Current,
                    ReadAccounts = () => Accounts,
                    UtcNow = () => Now,
                    QueryUsage = Query,
                    WaitForRuntimeExit = token => { token.ThrowIfCancellationRequested(); Check(OwnedQueries == 0, "A query process was still owned while waiting for runtimes."); Waits++; return Task.FromResult(0); },
                    ConfirmRuntimeQuiet = token => { token.ThrowIfCancellationRequested(); Check(OwnedQueries == 0, "A query process was still owned during the final quiet check."); QuietChecks++; return Task.FromResult(0); },
                    Apply = (target, expectedSourceId) =>
                    {
                        Check(OwnedQueries == 0, "A query process survived until Apply.");
                        Check(Current.Id == expectedSourceId, "Apply received an unexpected source identity.");
                        Applies++; Current = target;
                    },
                    Launch = () => { Launches++; return Task.FromResult(0); },
                    RestoreRuntimes = () => { Restores++; return Task.FromResult(0); }
                };
                Controller = new AutoSwitchController(Host);
            }

            public static Account Profile(string id)
            { return new Account { Id = id, Auth = "synthetic-auth-" + id, Workspace = "synthetic-workspace-" + id, Plan = "plus", Email = id + "@example.com" }; }
            public AccountUsage Usage(double remaining)
            { return new AccountUsage { Weekly = new UsageWindow { RemainingPercent = remaining, ResetsAt = Now.AddDays(1) }, FetchedAt = Now }; }
            public async Task<AccountUsage> Query(Account account, CancellationToken token)
            {
                Queries.Add(account.Id); OwnedQueries++;
                try { await Task.Yield(); token.ThrowIfCancellationRequested(); return Usage(account.Id == "current" ? 0 : 75); }
                finally { OwnedQueries--; }
            }
            public async Task Start() { Controller.Enable(); await Finish(Controller.TickAsync()); }
        }

        public static void Register(Action<string, Action> test)
        {
            test("Automatic quota handoff interrupts only after selecting a candidate and continues only after launch", delegate { Run(async delegate
            {
                var f = new Fixture(); var trace = new List<string>();
                var wait = f.Host.WaitForRuntimeExit; var launch = f.Host.Launch;
                f.Host.PrepareContinuation = async (source, target, revalidate, token) => { Check(source.Id == "current" && target.Id == "next", "The handoff bound the wrong accounts."); trace.Add("prepare"); await revalidate(token); };
                f.Host.WaitForRuntimeExit = async token => { trace.Add("wait"); await wait(token); };
                f.Host.Launch = async delegate { trace.Add("launch"); await launch(); };
                f.Host.ContinueAfterSwitch = (target, token) => { Check(f.Current.Id == target.Id && f.Applies == 1 && f.Launches == 1, "Continuation preceded account application or restart."); trace.Add("continue"); return Task.FromResult(true); };
                f.Host.CancelContinuation = () => { trace.Add("cancel"); return Task.FromResult(0); };
                await f.Start();
                Check(trace.SequenceEqual(new[] { "prepare", "wait", "launch", "continue" }) && f.Controller.StatusMessage.Contains("繼續執行"), "The successful handoff order was incorrect.");
            }); });
            test("An uncertain continuation never repeats account application or launch and releases its handoff", delegate { Run(async delegate
            {
                var f = new Fixture(); int prepares = 0, continuations = 0, cancellations = 0;
                f.Host.PrepareContinuation = async (source, target, revalidate, token) => { prepares++; await revalidate(token); };
                f.Host.ContinueAfterSwitch = (target, token) => { continuations++; throw new System.IO.IOException("Synthetic lost continuation reply."); };
                f.Host.CancelContinuation = () => { cancellations++; return Task.FromResult(0); };
                await f.Start(); await f.Controller.TickAsync();
                Check(prepares == 1 && continuations == 1 && cancellations == 1 && f.Applies == 1 && f.Launches == 1 && f.Restores == 0 &&
                    f.Controller.State == AutoSwitchState.Faulted && !f.Controller.Enabled, "An uncertain continuation retried or lost its applied-account state.");
            }); });
            foreach (string change in new[] { "reset", "unknown", "priority", "candidate", "stale", "reset-during-candidate" })
            {
                string captured = change;
                test("Automatic quota handoff revalidates " + captured + " after waiting and before any interrupt", delegate { Run(async delegate
                {
                    var f = new Fixture(); bool waited = false; int interrupts = 0, releases = 0;
                    f.Host.QueryUsage = (account, token) =>
                    {
                        f.Queries.Add(account.Id); token.ThrowIfCancellationRequested();
                        if (waited && account.Id == "current" && captured == "unknown") return Task.FromResult<AccountUsage>(null);
                        if (waited && account.Id == "next" && (captured == "stale" || captured == "reset-during-candidate"))
                            f.Now = f.Now.AddMinutes(2);
                        var usage = f.Usage(account.Id == "current" ? waited && captured == "reset" ? 50 : 0 : waited && captured == "candidate" ? 0 : 75);
                        if (waited && account.Id == "current" && captured == "reset-during-candidate") usage.Weekly.ResetsAt = f.Now.AddMinutes(1);
                        return Task.FromResult(usage);
                    };
                    f.Host.PrepareContinuation = async (source, target, revalidate, token) =>
                    {
                        // Models the real workflow's read-only prerequisite wait.
                        await Task.Yield(); waited = true; f.Now = f.Now.AddMinutes(3);
                        if (captured == "priority") f.Accounts.Reverse();
                        await revalidate(token); interrupts++;
                    };
                    f.Host.CancelContinuation = () => { releases++; return Task.FromResult(0); };
                    await f.Start();
                    Check(interrupts == 0 && f.Waits == 0 && f.Applies == 0 && f.Launches == 0 && f.Restores == 0 && releases == 1 &&
                        f.Controller.State == AutoSwitchState.Faulted && !f.Controller.Enabled,
                        "A changed, reset or expired precondition reached Stop, runtime shutdown or credential application.");
                }); });
            }
            test("Automatic quota handoff freshly queries the rotated source and ordered candidate before Stop", delegate { Run(async delegate
            {
                var f = new Fixture(); int reads = 0, interrupts = 0;
                f.Host.QueryUsage = async (account, token) =>
                {
                    if (account.Id == "current" && ++reads == 2) Check(account.Auth == "synthetic-rotated-before-stop", "The pre-interrupt query used a stale source token.");
                    return await f.Query(account, token);
                };
                f.Host.PrepareContinuation = async (source, target, revalidate, token) =>
                {
                    await Task.Yield(); f.Now = f.Now.AddMinutes(3);
                    f.Current = Fixture.Profile("current"); f.Current.Auth = "synthetic-rotated-before-stop";
                    await revalidate(token); Check(f.OwnedQueries == 0, "A quota lookup survived until the normal interrupt."); interrupts++;
                };
                await f.Start();
                Check(interrupts == 1 && f.Applies == 1 && f.Launches == 1 && f.Queries.SequenceEqual(new[] { "current", "next", "current", "next", "current", "next" }),
                    "The normal handoff omitted a fresh source/candidate check or changed query order.");
            }); });
            test("Automatic switching is opt-in and checks only a usable current account", delegate { Run(async delegate
            {
                var f = new Fixture();
                await f.Controller.TickAsync();
                Check(f.Queries.Count == 0 && !f.Controller.Enabled, "A disabled controller queried accounts.");
                f.Host.QueryUsage = (account, token) => { f.Queries.Add(account.Id); return Task.FromResult(f.Usage(40)); };
                await f.Start();
                Check(f.Queries.SequenceEqual(new[] { "current" }) && f.Applies == 0 && f.Controller.Enabled, "A usable current account caused a switch or candidate queries.");
            }); });
            test("Unsaved current accounts and unknown usage do not trigger automatic switching", delegate { Run(async delegate
            {
                var f = new Fixture(); f.Current = Fixture.Profile("unsaved");
                await f.Start();
                Check(f.Queries.Count == 0 && f.Applies == 0 && f.Controller.Enabled, "An unsaved current account was queried or switched.");
                f.Current = f.Accounts[0];
                f.Host.QueryUsage = (account, token) => Task.FromResult<AccountUsage>(null);
                await Finish(f.Controller.TickAsync());
                Check(f.Waits == 0 && f.Applies == 0 && f.Controller.Enabled, "Unknown current usage caused a switch.");
            }); });
            test("Automatic switching follows saved order and accepts a weekly-only candidate", delegate { Run(async delegate
            {
                var f = new Fixture(); f.Accounts.Insert(1, Fixture.Profile("unavailable"));
                f.Host.QueryUsage = async (account, token) => account.Id == "unavailable" ? null : await f.Query(account, token);
                await f.Start();
                Check(f.Applies == 1 && f.Launches == 1 && f.Restores == 0 && f.Current.Id == "next", "The weekly-only candidate was not applied and launched exactly once.");
                Check(f.Queries.SequenceEqual(new[] { "current", "next", "current", "next" }), "Current and candidate were not freshly queried before and after runtime shutdown.");
                Check(f.Waits == 1 && f.QuietChecks == 1 && f.OwnedQueries == 0 && f.Controller.Enabled, "The shutdown and final quiet check sequence was incomplete.");
            }); });
            test("Candidate query failure or no usable candidate waits for a later tick", delegate { Run(async delegate
            {
                var f = new Fixture();
                f.Host.QueryUsage = (account, token) => { f.Queries.Add(account.Id); if (account.Id == "next") throw new Exception("synthetic-server-detail"); return Task.FromResult(f.Usage(0)); };
                await f.Start();
                Check(f.Waits == 0 && f.Applies == 0 && f.Controller.Enabled && !f.Controller.StatusMessage.Contains("synthetic-server-detail"), "A failed candidate query stopped monitoring or exposed diagnostics.");
                Check(f.Queries.Count == 2, "No-candidate handling looped within one tick.");
            }); });
            test("Changing the current identity during lookup abandons that switch attempt", delegate { Run(async delegate
            {
                var f = new Fixture();
                f.Host.QueryUsage = (account, token) => { f.Current = Fixture.Profile("different"); return Task.FromResult(f.Usage(0)); };
                await f.Start();
                Check(f.Waits == 0 && f.Applies == 0, "A foreign current account was replaced.");
            }); });
            test("Current token rotation uses the latest credential and preserves source identity", delegate { Run(async delegate
            {
                var f = new Fixture();
                f.Host.WaitForRuntimeExit = token => { f.Waits++; f.Current = Fixture.Profile("current"); f.Current.Auth = "synthetic-rotated-current"; return Task.FromResult(0); };
                int currentReads = 0;
                f.Host.QueryUsage = async (account, token) =>
                {
                    if (account.Id == "current" && ++currentReads == 2) Check(account.Auth == "synthetic-rotated-current", "The current account was rechecked with stale credentials.");
                    return await f.Query(account, token);
                };
                f.Host.ConfirmRuntimeQuiet = token => { f.QuietChecks++; f.Current.Auth = "synthetic-rotated-again"; return Task.FromResult(0); };
                await f.Start();
                Check(f.Applies == 1 && f.Launches == 1, "Normal same-identity rotation blocked the validated switch.");
            }); });
            test("Current identity changes during shutdown restore runtimes without applying", delegate { Run(async delegate
            {
                var f = new Fixture();
                f.Host.WaitForRuntimeExit = token => { f.Waits++; f.Current = Fixture.Profile("different"); return Task.FromResult(0); };
                await f.Start();
                Check(f.Applies == 0 && f.Restores == 1 && f.Launches == 0, "An identity change after shutdown did not restore without applying.");
            }); });
            test("Current allowance recovery after shutdown restores runtimes and keeps the login", delegate { Run(async delegate
            {
                var f = new Fixture();
                f.Host.QueryUsage = (account, token) => Task.FromResult(f.Usage(account.Id == "current" && f.Waits == 0 ? 0 : 50));
                await f.Start();
                Check(f.Applies == 0 && f.Restores == 1 && f.Controller.Enabled, "Recovered current allowance still triggered a switch.");
            }); });
            test("Candidate depletion or reset during final validation prevents applying", delegate { Run(async delegate
            {
                var depleted = new Fixture();
                depleted.Host.QueryUsage = (account, token) => Task.FromResult(depleted.Usage(account.Id == "current" || depleted.Waits > 0 ? 0 : 60));
                await depleted.Start();
                Check(depleted.Applies == 0 && depleted.Restores == 1, "A depleted final candidate was applied.");
                var reset = new Fixture();
                reset.Host.QueryUsage = (account, token) =>
                {
                    var usage = reset.Usage(account.Id == "current" ? 0 : 50);
                    if (account.Id == "next") usage.Weekly.ResetsAt = reset.Now.AddSeconds(1);
                    return Task.FromResult(usage);
                };
                reset.Host.ConfirmRuntimeQuiet = token => { reset.QuietChecks++; reset.Now = reset.Now.AddSeconds(2); return Task.FromResult(0); };
                await reset.Start();
                Check(reset.Applies == 0 && reset.Restores == 1, "A snapshot crossing its reset time was applied.");
            }); });
            test("Candidate credentials or priority changes after querying invalidate the attempt", delegate { Run(async delegate
            {
                foreach (bool changeOrder in new[] { false, true })
                {
                    var f = new Fixture(); f.Accounts.Add(Fixture.Profile("third"));
                    f.Host.ConfirmRuntimeQuiet = token =>
                    {
                        if (changeOrder) { var next = f.Accounts[1]; f.Accounts[1] = f.Accounts[2]; f.Accounts[2] = next; }
                        else f.Accounts[1].Auth = "synthetic-relogged-candidate";
                        return Task.FromResult(0);
                    };
                    await f.Start();
                    Check(f.Applies == 0 && f.Restores == 1, "Changed candidate credentials or order were ignored.");
                }
            }); });
            test("Concurrent ticks share one flight and stopping waits for query cleanup", delegate { Run(async delegate
            {
                var f = new Fixture(); var entered = Signal(); var cleaning = Signal(); var release = Signal();
                f.Host.QueryUsage = async (account, token) =>
                {
                    f.Queries.Add(account.Id); f.OwnedQueries++; entered.TrySetResult(true);
                    bool cancelled = false;
                    try { await Task.Delay(Timeout.Infinite, token); }
                    catch (OperationCanceledException) { cancelled = true; }
                    cleaning.TrySetResult(true); await release.Task; f.OwnedQueries--;
                    if (cancelled) throw new OperationCanceledException(token);
                    return f.Usage(0);
                };
                f.Controller.Enable(); var first = f.Controller.TickAsync();
                await Finish(entered.Task);
                Check(ReferenceEquals(first, f.Controller.TickAsync()) && f.Queries.Count == 1, "Concurrent ticks started more than one query.");
                var stopped = f.Controller.StopAsync(); await Finish(cleaning.Task);
                Check(!stopped.IsCompleted && f.Applies == 0, "Stop finished before query cleanup.");
                release.TrySetResult(true); await Finish(stopped);
                Check(f.OwnedQueries == 0 && !f.Controller.Enabled && f.Restores == 0 && !f.Controller.IsRunning, "Stopping a lookup left work or enabled monitoring behind.");
            }); });
            test("Stopping during runtime shutdown awaits one restoration before completing", delegate { Run(async delegate
            {
                var f = new Fixture(); var waiting = Signal(); var restoring = Signal(); var release = Signal();
                f.Host.WaitForRuntimeExit = async token => { f.Waits++; waiting.TrySetResult(true); await Task.Delay(Timeout.Infinite, token); };
                f.Host.RestoreRuntimes = async () => { f.Restores++; restoring.TrySetResult(true); await release.Task; };
                f.Controller.Enable(); var flight = f.Controller.TickAsync(); await Finish(waiting.Task);
                var stopped = f.Controller.StopAsync(); await Finish(restoring.Task);
                Check(!stopped.IsCompleted && f.Restores == 1 && f.Applies == 0, "Stop did not await exactly one restoration.");
                release.TrySetResult(true); await Finish(stopped); await Finish(flight);
                Check(f.Restores == 1 && !f.Controller.Enabled && f.Controller.State == AutoSwitchState.Disabled, "Cancellation did not remain disabled after restoration.");
            }); });
            test("Stopping at the final quiet check prevents commit and restores runtimes", delegate { Run(async delegate
            {
                var f = new Fixture(); var quiet = Signal();
                f.Host.ConfirmRuntimeQuiet = async token => { quiet.TrySetResult(true); await Task.Delay(Timeout.Infinite, token); };
                f.Controller.Enable(); var flight = f.Controller.TickAsync(); await Finish(quiet.Task);
                await Finish(f.Controller.StopAsync()); await Finish(flight);
                Check(f.Applies == 0 && f.Restores == 1, "A cancelled final check still applied credentials.");
            }); });
            test("Apply or launch failures disable monitoring and attempt recovery once", delegate { Run(async delegate
            {
                var apply = new Fixture(); apply.Host.Apply = (target, expected) => { throw new Exception("synthetic-apply-detail"); };
                await apply.Start();
                Check(!apply.Controller.Enabled && apply.Controller.State == AutoSwitchState.Faulted && apply.Restores == 1 && apply.Launches == 0, "Apply failure did not stop and restore.");
                var launch = new Fixture(); launch.Host.Launch = () => { launch.Launches++; throw new Exception("synthetic-launch-detail"); };
                await launch.Start(); await Finish(launch.Controller.TickAsync());
                Check(!launch.Controller.Enabled && launch.Controller.State == AutoSwitchState.Faulted && launch.Applies == 1 && launch.Launches == 1 && launch.Restores == 1, "Launch failure retried or did not recover once.");
                Check(!launch.Controller.StatusMessage.Contains("synthetic-launch-detail"), "Internal error details leaked into the status.");
            }); });
            test("A stop requested inside commit still completes launch without another restoration", delegate { Run(async delegate
            {
                var f = new Fixture(); Task stopped = null;
                var apply = f.Host.Apply;
                f.Host.Apply = (target, expected) => { apply(target, expected); stopped = f.Controller.StopAsync(); };
                await f.Start(); await Finish(stopped);
                Check(f.Applies == 1 && f.Launches == 1 && f.Restores == 0 && !f.Controller.Enabled, "A stop after commit left the runtimes closed.");
            }); });
            test("Stopping after commit waits for asynchronous runtime launch to finish", delegate { Run(async delegate
            {
                var f = new Fixture(); var launching = Signal(); var release = Signal();
                f.Host.Launch = async () => { f.Launches++; launching.TrySetResult(true); await release.Task; };
                f.Controller.Enable(); var flight = f.Controller.TickAsync(); await Finish(launching.Task);
                var stopped = f.Controller.StopAsync();
                Check(f.Applies == 1 && !stopped.IsCompleted && f.Restores == 0, "Stop returned before committed runtime launch finished.");
                release.TrySetResult(true); await Finish(stopped); await Finish(flight);
                Check(f.Launches == 1 && f.Restores == 0 && !f.Controller.Enabled, "Committed runtime launch was cancelled or repeated.");
            }); });
            test("A stop or identity change announced immediately before commit prevents applying", delegate { Run(async delegate
            {
                foreach (bool stop in new[] { false, true })
                {
                    var f = new Fixture(); Task stopped = null;
                    f.Controller.Changed += delegate
                    {
                        if (f.Controller.State != AutoSwitchState.Switching) return;
                        if (stop) stopped = f.Controller.StopAsync();
                        else f.Current = Fixture.Profile("different");
                    };
                    await f.Start(); if (stopped != null) await Finish(stopped);
                    Check(f.Applies == 0 && f.Restores == 1, "A change at the commit boundary was ignored.");
                }
            }); });
            test("Monitoring cannot resume while a requested stop is still restoring runtimes", delegate { Run(async delegate
            {
                var f = new Fixture(); var waiting = Signal(); var restoring = Signal(); var release = Signal();
                f.Host.WaitForRuntimeExit = async token => { waiting.TrySetResult(true); await Task.Delay(Timeout.Infinite, token); };
                f.Host.RestoreRuntimes = async () => { restoring.TrySetResult(true); await release.Task; };
                f.Controller.Enable(); var flight = f.Controller.TickAsync(); await Finish(waiting.Task);
                var stopped = f.Controller.StopAsync(); await Finish(restoring.Task);
                bool rejected = false; try { f.Controller.Enable(); } catch (UserError) { rejected = true; }
                Check(rejected, "Monitoring resumed while stop cleanup was active.");
                release.TrySetResult(true); await Finish(stopped); await Finish(flight);
                f.Controller.Enable(); Check(f.Controller.Enabled, "Monitoring could not be resumed after cleanup.");
            }); });
            test("Successful switches monitor the new account without returning to recovered higher priority", delegate { Run(async delegate
            {
                var f = new Fixture(); await f.Start();
                int queried = f.Queries.Count;
                await Finish(f.Controller.TickAsync());
                Check(f.Applies == 1 && f.Queries.Count == queried + 1 && f.Queries.Last() == "next", "A higher-priority account caused an unnecessary return switch.");
            }); });
            test("Restoration failure is surfaced and cannot silently re-enable monitoring", delegate { Run(async delegate
            {
                var f = new Fixture();
                f.Host.WaitForRuntimeExit = token => { throw new Exception("synthetic-runtime-failure"); };
                f.Host.RestoreRuntimes = () => { f.Restores++; throw new Exception("synthetic-restore-failure"); };
                await f.Start(); await Finish(f.Controller.StopAsync());
                Check(f.Restores == 1 && f.Controller.State == AutoSwitchState.Faulted && !f.Controller.Enabled, "Failed recovery was hidden or retried.");
            }); });
        }
    }
}
