using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CodexAccountSwitcher
{
    public static class QuotaResetIntegrationTests
    {
        private static void Check(bool condition, string message)
        { if (!condition) throw new Exception(message); }
        private static void Run(Func<Task> action)
        { Task.Run(action).GetAwaiter().GetResult(); }
        private static async Task Finish(Task operation)
        {
            Check(await Task.WhenAny(operation, Task.Delay(10000)) == operation, "Quota reset integration timed out.");
            await operation;
        }

        // These tests exercise the real controller, workflow and encrypted
        // ticket store. Only desktop and account transport operations are fake.
        private sealed class Fixture : IDisposable
        {
            internal readonly string Root;
            internal readonly List<Account> Accounts = new List<Account>();
            internal readonly Dictionary<string, double> Remaining = new Dictionary<string, double>();
            internal readonly DateTimeOffset Now = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
            internal readonly DesktopContinuationCheckpoint Checkpoint;
            internal readonly DesktopContinuationOperations Operations;
            internal ContinuationTicketStore Store;
            internal DesktopContinuationWorkflow Workflow;
            internal AutoSwitchController Controller;
            internal Account Current;
            internal int Applies, Retains, Launches, Starts, Releases, Interrupts, Binds, Waits;
            internal bool LostStartReply, ContinuationEnabled = true;
            internal string LastOperation;

            internal Fixture()
            {
                Root = Path.Combine(Path.GetTempPath(), "CodexAccountSwitcher-quota-integration-" + Guid.NewGuid().ToString("N"));
                Files.PrivateDirectory(Root);
                Accounts.Add(new Account { Id = "higher", Email = "higher@example.com", Auth = "synthetic-higher-auth" });
                Accounts.Add(new Account { Id = "current", Email = "current@example.com", Auth = "synthetic-current-auth" });
                Current = Accounts[1]; Remaining["higher"] = 0; Remaining["current"] = 0;
                Checkpoint = new DesktopContinuationCheckpoint
                {
                    SessionId = Guid.NewGuid().ToString("N"), Generation = 1, ThreadId = Guid.NewGuid().ToString(),
                    TurnId = "quota-failed-original", Home = Root, Cwd = Root, OriginalTurnStatus = "failed",
                    RolloutPath = Path.Combine(Root, "sessions", "synthetic-rollout.jsonl"), Model = "fixture-model", ModelProvider = "fixture",
                    FrontendContextJson = ContinuationAdmission.ProjectFrontendContext(new Dictionary<string, object>(), null),
                    PermissionsJson = "{\"approvalPolicy\":\"never\",\"approvalsReviewer\":\"user\",\"sandbox\":{\"type\":\"readOnly\",\"networkAccess\":false},\"permissions\":null}"
                };
                Operations = new DesktopContinuationOperations
                {
                    Enabled = () => ContinuationEnabled,
                    ReadCurrentId = () => Current.Id,
                    Capture = token => { token.ThrowIfCancellationRequested(); return Task.FromResult(Clone(Checkpoint)); },
                    BeforeInterrupt = (checkpoint, token) => { token.ThrowIfCancellationRequested(); return Task.FromResult(0); },
                    BindRestore = (checkpoint, operation, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        Check(Store.Read(operation).State == ContinuationTicketState.Captured,
                            "The desktop was bound before durable quota-wait claim.");
                        LastOperation = operation; Binds++; return Task.FromResult(0);
                    },
                    Interrupt = (checkpoint, token) => { Interrupts++; throw new Exception("An already quota-failed turn must not be interrupted again."); },
                    WaitStopped = (checkpoint, token) => { token.ThrowIfCancellationRequested(); return Task.FromResult(0); },
                    PrepareAfterRestore = (checkpoint, target, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        Check(Launches == 1 && Current.Id == target.Id, "The workflow preflight ran before the chosen account restarted.");
                        var result = Clone(checkpoint); result.Generation++; return Task.FromResult(result);
                    },
                    Open = (checkpoint, token) => { token.ThrowIfCancellationRequested(); return Task.FromResult(0); },
                    Start = (checkpoint, operation, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        var durable = Store.Read(operation);
                        Check(durable.State == ContinuationTicketState.Sending && durable.Message == "繼續執行",
                            "Start crossed the transport before the durable single-send reservation.");
                        Starts++;
                        if (LostStartReply) throw new IOException("Synthetic start acknowledgement lost after submission.");
                        return Task.FromResult("continued-after-reset");
                    },
                    Release = (checkpoint, operation, token) => { Releases++; return Task.FromResult(0); }
                };
                Reopen();
            }
            private static DesktopContinuationCheckpoint Clone(DesktopContinuationCheckpoint value)
            { return Json.Decode<DesktopContinuationCheckpoint>(Json.Encode(value)); }
            internal void Reopen()
            {
                Store = new ContinuationTicketStore(Root, Root);
                Workflow = new DesktopContinuationWorkflow(Store, Operations);
                Controller = new AutoSwitchController(new AutoSwitchHost
                {
                    ReadCurrent = () => Current, ReadAccounts = () => Accounts, UtcNow = () => Now,
                    QueryUsage = (account, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        return Task.FromResult(new AccountUsage { FetchedAt = Now,
                            Weekly = new UsageWindow { RemainingPercent = Remaining[account.Id], ResetsAt = Now.AddDays(1) } });
                    },
                    ReadQuotaWaitSource = () => Workflow.WaitingForQuotaSourceId,
                    ObserveQuotaWait = (source, token) => Workflow.ObserveQuotaWaitAsync(source, token),
                    PrepareQuotaContinuation = (source, target, revalidate, token) => Workflow.PrepareQuotaResetAsync(source, target, revalidate, token),
                    PrepareContinuation = (source, target, revalidate, token) => Workflow.PrepareAsync(source, target, revalidate, token),
                    WaitForRuntimeExit = token =>
                    {
                        token.ThrowIfCancellationRequested();
                        Check(Store.Read(LastOperation).State == ContinuationTicketState.Stopped, "Runtime shutdown preceded confirmed terminal state.");
                        Waits++; Workflow.MarkQuiesced(); return Task.FromResult(0);
                    },
                    ConfirmRuntimeQuiet = token => { token.ThrowIfCancellationRequested(); return Task.FromResult(0); },
                    Apply = (target, expected) =>
                    {
                        Check(Current.Id == expected && target.Id != expected, "The credential operation was not a genuine account switch.");
                        Applies++; Current = target; Workflow.MarkAccountApplied(target);
                    },
                    RetainCurrentAccount = target =>
                    {
                        Check(Current.Id == target.Id, "The retained account was not the source.");
                        Retains++; Workflow.MarkAccountApplied(target);
                    },
                    Launch = () =>
                    {
                        Check(Store.Read(LastOperation).State == ContinuationTicketState.AccountApplied, "Restart preceded the durable account decision.");
                        Launches++; return Task.FromResult(0);
                    },
                    ContinueAfterSwitch = (target, token) => Workflow.ContinueAsync(target, token),
                    CancelContinuation = () => Workflow.CancelAsync(),
                    CancelQuotaWait = () => Workflow.WaitingForQuotaSourceId == null ? Task.FromResult(0) : Workflow.CancelAsync(),
                    RestoreRuntimes = () => Task.FromResult(0)
                });
            }
            internal async Task EstablishWait()
            {
                Controller.Enable(); await Finish(Controller.TickAsync());
                var pending = Store.ReadPending();
                Check(Controller.State == AutoSwitchState.WaitingForQuota && pending.Count == 1 &&
                    pending[0].State == ContinuationTicketState.WaitingForQuota && pending[0].Version == 2,
                    "The controller did not persist a recoverable quota wait through the real workflow.");
                Check(Binds == 0 && Waits == 0 && Interrupts == 0 && Starts == 0, "Waiting changed a desktop or sent a continuation.");
            }
            public void Dispose()
            {
                string parent = Path.GetDirectoryName(Path.GetFullPath(Root));
                Check(String.Equals(parent.TrimEnd('\\'), Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase), "Unexpected integration cleanup root.");
                ManagedBridge.EnsureNoReparsePoint(Root); Directory.Delete(Root, true);
            }
        }

        public static void Register(Action<string, Action> test)
        {
            foreach (bool sameAccount in new[] { false, true })
            {
                bool retain = sameAccount;
                test("Quota reset full controller workflow continues once using " + (retain ? "the same account" : "a higher-priority account"), delegate { Run(async delegate
                {
                    using (var f = new Fixture())
                    {
                        await f.EstablishWait(); f.Remaining[retain ? "current" : "higher"] = 80;
                        await Finish(f.Controller.TickAsync());
                        var durable = f.Store.ReadRecovery().Single();
                        Check(durable.State == ContinuationTicketState.Continued && durable.NewTurnId == "continued-after-reset" &&
                            durable.SourceAccountId == "current" && durable.TargetAccountId == (retain ? "current" : "higher"),
                            "The final ticket did not bind the original and selected accounts.");
                        Check(f.Starts == 1 && f.Binds == 1 && f.Waits == 1 && f.Launches == 1 && f.Interrupts == 0 &&
                            f.Applies == (retain ? 0 : 1) && f.Retains == (retain ? 1 : 0), "The recovery sequence repeated or skipped a required step.");
                        await Finish(f.Controller.TickAsync()); Check(f.Starts == 1 && f.Launches == 1, "The completed quota recovery was sent again.");
                    }
                }); });
            }
            test("Quota reset waiting survives a workflow and controller restart before allowance returns", delegate { Run(async delegate
            {
                using (var f = new Fixture())
                {
                    await f.EstablishWait(); string operation = f.Store.ReadPending().Single().OperationId;
                    f.Reopen(); await Finish(f.Workflow.RecoverPendingAsync());
                    Check(f.Releases == 0 && f.Workflow.WaitingForQuotaSourceId == "current", "Startup released or erased an unclaimed wait.");
                    f.Remaining["current"] = 60; f.Controller.Enable(); await Finish(f.Controller.TickAsync());
                    Check(f.Starts == 1 && f.Retains == 1 && f.Store.Read(operation).State == ContinuationTicketState.Continued,
                        "Restarted waiting work did not use its original durable operation once.");
                }
            }); });
            test("Quota reset uncertain delivery stays nonretryable after workflow and controller restart", delegate { Run(async delegate
            {
                using (var f = new Fixture())
                {
                    await f.EstablishWait(); string operation = f.Store.ReadPending().Single().OperationId;
                    f.Remaining["current"] = 60; f.LostStartReply = true; await Finish(f.Controller.TickAsync());
                    Check(f.Starts == 1 && f.Controller.State == AutoSwitchState.Faulted &&
                        f.Store.Read(operation).State == ContinuationTicketState.NeedsAttention, "Lost delivery acknowledgement remained retryable.");
                    f.Reopen(); await Finish(f.Workflow.RecoverPendingAsync()); f.Controller.Enable(); await Finish(f.Controller.TickAsync());
                    Check(f.Starts == 1 && f.Launches == 1 && f.Workflow.WaitingForQuotaSourceId == null &&
                        f.Store.Read(operation).State == ContinuationTicketState.NeedsAttention, "Restart reconstructed an uncertain continuation.");
                }
            }); });
            test("Quota reset manual turn changes cancel durable waiting before account application", delegate { Run(async delegate
            {
                using (var f = new Fixture())
                {
                    await f.EstablishWait(); string operation = f.Store.ReadPending().Single().OperationId;
                    f.Checkpoint.TurnId = "new-user-turn"; f.Checkpoint.OriginalTurnStatus = "inProgress";
                    f.Remaining["higher"] = 80; await Finish(f.Controller.TickAsync());
                    Check(f.Store.Read(operation).State == ContinuationTicketState.Cancelled && f.Applies == 0 && f.Retains == 0 &&
                        f.Waits == 0 && f.Binds == 0 && f.Starts == 0 && f.Interrupts == 0, "A newer manual turn was interrupted or automatically continued.");
                }
            }); });
            test("Quota reset monitor stop cancels durable intent without desktop release or a future send", delegate { Run(async delegate
            {
                using (var f = new Fixture())
                {
                    await f.EstablishWait(); string operation = f.Store.ReadPending().Single().OperationId;
                    await Finish(f.Controller.StopAsync());
                    Check(f.Store.Read(operation).State == ContinuationTicketState.Cancelled && f.Releases == 0 && f.Workflow.WaitingForQuotaSourceId == null,
                        "Stopping an idle wait did not persist cancellation or unnecessarily touched desktop control.");
                    f.Remaining["current"] = 80; f.Controller.Enable(); await Finish(f.Controller.TickAsync());
                    Check(f.Starts == 0 && f.Binds == 0 && f.Launches == 0, "A cancelled reset resumed after monitoring was re-enabled.");
                }
            }); });
        }
    }
}
