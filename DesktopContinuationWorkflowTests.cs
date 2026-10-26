using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CodexAccountSwitcher
{
    public static class DesktopContinuationWorkflowTests
    {
        private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        private sealed class Fixture : IDisposable
        {
            internal readonly string Root;
            internal readonly ContinuationTicketStore Store;
            internal readonly DesktopContinuationCheckpoint Checkpoint;
            internal readonly DesktopContinuationOperations Operations;
            internal readonly Account Source = new Account { Id = "source", Email = "source@example.com" };
            internal readonly Account Target = new Account { Id = "target", Email = "target@example.com" };
            internal string Current = "source";
            internal int Interrupts, Starts, Releases, Preflights;
            internal Fixture()
            {
                Root = Path.Combine(Path.GetTempPath(), "CodexAccountSwitcher-continuation-flow-" + Guid.NewGuid().ToString("N"));
                Files.PrivateDirectory(Root); Store = new ContinuationTicketStore(Root, Root);
                Checkpoint = new DesktopContinuationCheckpoint { SessionId = Guid.NewGuid().ToString("N"), Generation = 1,
                    ThreadId = Guid.NewGuid().ToString(), TurnId = "original-turn", Home = Root, Cwd = Root,
                    RolloutPath = Path.Combine(Root, "sessions", "synthetic-rollout.jsonl"), Model = "fixture-model",
                    FrontendContextJson = ContinuationAdmission.ProjectFrontendContext(new System.Collections.Generic.Dictionary<string, object>(), null),
                    ModelProvider = "fixture", OriginalTurnStatus = "inProgress", PermissionsJson = "{\"approvalPolicy\":\"never\",\"approvalsReviewer\":\"user\",\"sandbox\":{\"type\":\"readOnly\",\"networkAccess\":false},\"permissions\":null}" };
                Operations = new DesktopContinuationOperations {
                    Enabled = () => true, ReadCurrentId = () => Current, Capture = token => Task.FromResult(Checkpoint),
                    BindRestore = (cp, operation, token) => Task.FromResult(0),
                    Interrupt = (cp, token) => { Interrupts++; return Task.FromResult(0); },
                    WaitStopped = (cp, token) => Task.FromResult(0),
                    PrepareAfterRestore = (cp, target, token) => { Preflights++; return Task.FromResult(cp); },
                    Open = (cp, token) => Task.FromResult(0),
                    Start = (cp, operation, token) => {
                        Check(Store.Read(operation).State == ContinuationTicketState.Sending, "The packet preceded the durable send reservation.");
                        Starts++; return Task.FromResult("continued-turn");
                    },
                    Release = (cp, operation, token) => { Releases++; return Task.FromResult(0); }
                };
            }
            internal DesktopContinuationWorkflow Workflow() { return new DesktopContinuationWorkflow(Store, Operations); }
            internal void Commit(DesktopContinuationWorkflow workflow)
            { workflow.MarkQuiesced(); Current = Target.Id; workflow.MarkAccountApplied(Target); }
            public void Dispose()
            {
                string parent = Path.GetDirectoryName(Path.GetFullPath(Root));
                Check(String.Equals(parent.TrimEnd('\\'), Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase), "Unexpected cleanup root.");
                ManagedBridge.EnsureNoReparsePoint(Root); Directory.Delete(Root, true);
            }
        }
        public static void Register(Action<string, Action> test)
        {
            test("Quota wait survives a switcher restart without binding stopping releasing or sending", delegate
            {
                using (var f = new Fixture())
                {
                    f.Checkpoint.OriginalTurnStatus = "failed"; int binds = 0, stoppedChecks = 0;
                    f.Operations.BindRestore = (cp, operation, token) => { binds++; return Task.FromResult(0); };
                    f.Operations.WaitStopped = (cp, token) => { stoppedChecks++; return Task.FromResult(0); };
                    var first = f.Workflow();
                    Check(first.ObserveQuotaWaitAsync(f.Source, CancellationToken.None).GetAwaiter().GetResult(), "The quota failure was not retained.");
                    var reopened = f.Workflow(); reopened.RecoverPendingAsync().GetAwaiter().GetResult();
                    Check(reopened.WaitingForQuotaSourceId == f.Source.Id && !reopened.HasHandoff &&
                        reopened.ObserveQuotaWaitAsync(f.Source, CancellationToken.None).GetAwaiter().GetResult() &&
                        f.Store.ReadPending().Count == 1 && binds == 0 && stoppedChecks == 0 && f.Interrupts == 0 && f.Starts == 0 && f.Releases == 0,
                        "A pure wait acquired desktop control, mutated the turn, or was not recoverable.");
                }
            });
            foreach (bool sameAccount in new[] { false, true })
            {
                bool same = sameAccount;
                test("Quota reset reuses one verified restart and sends once for " + (same ? "the current account" : "a higher-priority account"), delegate
                {
                    using (var f = new Fixture())
                    {
                        f.Checkpoint.OriginalTurnStatus = "failed"; bool prerequisites = false, revalidated = false;
                        f.Operations.BeforeInterrupt = (cp, token) => { prerequisites = true; return Task.FromResult(0); };
                        f.Operations.BindRestore = (cp, operation, token) => {
                            Check(prerequisites && revalidated && f.Store.Read(operation).State == "Captured", "Restore preceded the durable claim or fresh quota.");
                            return Task.FromResult(0);
                        };
                        var workflow = f.Workflow(); workflow.ObserveQuotaWaitAsync(f.Source, CancellationToken.None).GetAwaiter().GetResult();
                        Account target = same ? f.Source : f.Target;
                        workflow.PrepareQuotaResetAsync(f.Source, target, token => { revalidated = true; return Task.FromResult(0); }, CancellationToken.None).GetAwaiter().GetResult();
                        Check(workflow.WaitingForQuotaSourceId == null && workflow.HasHandoff && f.Interrupts == 0, "Quota failure was interrupted or remained claimable.");
                        workflow.MarkQuiesced(); f.Current = target.Id; workflow.MarkAccountApplied(target);
                        Check(workflow.ContinueAsync(target, CancellationToken.None).GetAwaiter().GetResult() && f.Starts == 1 && f.Preflights == 2,
                            "The selected account did not complete one validated continuation.");
                        bool rejected = false;
                        try { workflow.PrepareQuotaResetAsync(f.Source, target, null, CancellationToken.None).GetAwaiter().GetResult(); } catch { rejected = true; }
                        Check(rejected && f.Starts == 1 && f.Store.ReadRecovery().Single().State == "Continued", "The reset was processed twice.");
                    }
                });
            }
            foreach (string state in new[] { "inProgress", "interrupted", "completed", "none" })
            {
                string capturedState = state;
                test("Quota wait does not capture " + capturedState + " turns", delegate
                {
                    using (var f = new Fixture())
                    {
                        f.Checkpoint.OriginalTurnStatus = capturedState;
                        if (capturedState == "none") f.Operations.Capture = token => Task.FromResult<DesktopContinuationCheckpoint>(null);
                        var workflow = f.Workflow();
                        Check(!workflow.ObserveQuotaWaitAsync(f.Source, CancellationToken.None).GetAwaiter().GetResult() &&
                            workflow.WaitingForQuotaSourceId == null && f.Store.ReadRecovery().Count == 0 && f.Starts == 0 && f.Interrupts == 0,
                            "An active manually stopped completed or missing turn became an automatic wait.");
                    }
                });
            }
            foreach (string change in new[] { "turn", "thread", "generation", "model", "permissions", "source" })
            {
                string capturedChange = change;
                test("Quota waiting cancels when the original " + capturedChange + " changes", delegate
                {
                    using (var f = new Fixture())
                    {
                        f.Checkpoint.OriginalTurnStatus = "failed"; var workflow = f.Workflow();
                        workflow.ObserveQuotaWaitAsync(f.Source, CancellationToken.None).GetAwaiter().GetResult();
                        if (capturedChange == "turn") f.Checkpoint.TurnId = "manual-turn";
                        if (capturedChange == "thread") f.Checkpoint.ThreadId = Guid.NewGuid().ToString();
                        if (capturedChange == "generation") f.Checkpoint.Generation++;
                        if (capturedChange == "model") f.Checkpoint.Model = "another-model";
                        if (capturedChange == "permissions") f.Checkpoint.PermissionsJson = null;
                        if (capturedChange == "source") f.Current = "manual-account";
                        Check(!workflow.ObserveQuotaWaitAsync(f.Source, CancellationToken.None).GetAwaiter().GetResult() &&
                            workflow.WaitingForQuotaSourceId == null && f.Store.ReadRecovery().Single().State == "Cancelled" && f.Releases == 0 && f.Starts == 0,
                            "A changed conversation or account retained an old automatic intent.");
                    }
                });
            }
            test("Quota wait recaptures the exact terminal turn after waiting and before claiming", delegate
            {
                using (var f = new Fixture())
                {
                    f.Checkpoint.OriginalTurnStatus = "failed"; int binds = 0;
                    f.Operations.BeforeInterrupt = (cp, token) => { f.Checkpoint.TurnId = "new-manual-turn"; return Task.FromResult(0); };
                    f.Operations.BindRestore = (cp, operation, token) => { binds++; return Task.FromResult(0); };
                    var workflow = f.Workflow(); workflow.ObserveQuotaWaitAsync(f.Source, CancellationToken.None).GetAwaiter().GetResult();
                    bool rejected = false;
                    try { workflow.PrepareQuotaResetAsync(f.Source, f.Target, token => Task.FromResult(0), CancellationToken.None).GetAwaiter().GetResult(); } catch (UserError) { rejected = true; }
                    Check(rejected && binds == 0 && f.Starts == 0 && workflow.WaitingForQuotaSourceId == null && f.Store.ReadRecovery().Single().State == "Cancelled",
                        "A manual turn that appeared during the prerequisite wait was resumed automatically.");
                }
            });
            test("Disabling automatic continuation cancels an unclaimed quota wait without contacting the App", delegate
            {
                using (var f = new Fixture())
                {
                    f.Checkpoint.OriginalTurnStatus = "failed"; var workflow = f.Workflow();
                    workflow.ObserveQuotaWaitAsync(f.Source, CancellationToken.None).GetAwaiter().GetResult();
                    f.Operations.Enabled = () => false;
                    Check(!workflow.ObserveQuotaWaitAsync(f.Source, CancellationToken.None).GetAwaiter().GetResult() && workflow.WaitingForQuotaSourceId == null &&
                        f.Releases == 0 && f.Starts == 0 && f.Store.ReadRecovery().Single().State == "Cancelled", "Disabling left a hidden automatic wait.");
                }
            });
            test("A quota claim interrupted before binding is never restored as an automatic wait", delegate
            {
                using (var f = new Fixture())
                {
                    f.Checkpoint.OriginalTurnStatus = "failed"; var workflow = f.Workflow();
                    workflow.ObserveQuotaWaitAsync(f.Source, CancellationToken.None).GetAwaiter().GetResult();
                    var pending = f.Store.ReadPending().Single(); Check(f.Store.ClaimQuotaWait(pending, f.Source.Id), "Could not model a committed claim.");
                    var reopened = f.Workflow(); reopened.RecoverPendingAsync().GetAwaiter().GetResult();
                    Check(reopened.WaitingForQuotaSourceId == null && f.Starts == 0 && f.Store.ReadPending().Single().State == "NeedsAttention",
                        "A crash after claiming reconstructed an automatic send.");
                }
            });
            test("A same-account quota continuation with a lost acknowledgement never becomes a wait again", delegate
            {
                using (var f = new Fixture())
                {
                    f.Checkpoint.OriginalTurnStatus = "failed";
                    f.Operations.Start = (cp, operation, token) => { f.Starts++; throw new IOException("Synthetic lost same-account reply."); };
                    var workflow = f.Workflow(); workflow.ObserveQuotaWaitAsync(f.Source, CancellationToken.None).GetAwaiter().GetResult();
                    workflow.PrepareQuotaResetAsync(f.Source, f.Source, token => Task.FromResult(0), CancellationToken.None).GetAwaiter().GetResult();
                    workflow.MarkQuiesced(); workflow.MarkAccountApplied(f.Source);
                    try { workflow.ContinueAsync(f.Source, CancellationToken.None).GetAwaiter().GetResult(); } catch (IOException) { }
                    var reopened = f.Workflow(); bool rejected = false;
                    try { reopened.ObserveQuotaWaitAsync(f.Source, CancellationToken.None).GetAwaiter().GetResult(); } catch (UserError) { rejected = true; }
                    Check(rejected && f.Starts == 1 && reopened.WaitingForQuotaSourceId == null && f.Store.ReadPending().Single().State == "NeedsAttention",
                        "An uncertain same-account send was recreated as a reset wait.");
                }
            });
            test("Desktop handoff revalidates quota after other runtimes finish and before Stop", delegate
            {
                using (var f = new Fixture())
                {
                    bool ready = false, checkedQuota = false, bound = false;
                    f.Operations.BeforeInterrupt = (cp, token) => { ready = true; return Task.FromResult(0); };
                    f.Operations.BindRestore = (cp, operation, token) => { Check(checkedQuota, "Restore was bound before revalidation."); bound = true; return Task.FromResult(0); };
                    var workflow = f.Workflow(); bool rejected = false;
                    try
                    {
                        workflow.PrepareAsync(f.Source, f.Target, token => {
                            Check(ready, "Quota was checked before waiting ended."); checkedQuota = true;
                            throw new UserError("Synthetic quota reset while waiting.");
                        }, CancellationToken.None).GetAwaiter().GetResult();
                    }
                    catch (UserError) { rejected = true; }
                    Check(rejected && checkedQuota && !bound && f.Interrupts == 0 && f.Starts == 0 && f.Current == f.Source.Id,
                        "A changed quota still interrupted the original turn or bound a restore.");
                }
            });
            test("Desktop handoff startup bounds old reservation cleanup and never reconstructs sends", delegate
            {
                using (var f = new Fixture())
                {
                    for (int index = 0; index < 6; index++)
                    {
                        f.Checkpoint.ThreadId = Guid.NewGuid().ToString();
                        f.Checkpoint.TurnId = "old-turn-" + index;
                        f.Store.Create(f.Source.Id, f.Target.Id, f.Checkpoint);
                    }
                    var released = new System.Collections.Generic.List<string>();
                    f.Operations.Release = (cp, operation, token) => { released.Add(cp.TurnId); return Task.FromResult(0); };
                    var workflow = f.Workflow(); workflow.RecoverPendingAsync().GetAwaiter().GetResult();
                    Check(released.Count == 4 && released[0] == "old-turn-5" && released[3] == "old-turn-2",
                        "Startup did not bound cleanup to the newest reservations.");
                    Check(f.Starts == 0 && f.Store.ReadPending().All(item => item.State == ContinuationTicketState.NeedsAttention),
                        "Startup reconstructed a send or left old tickets retryable.");
                }
            });
            test("Desktop handoff reserves one send after fresh account UI and backend checks", delegate
            {
                using (var f = new Fixture())
                {
                    var workflow = f.Workflow(); workflow.PrepareAsync(f.Source, f.Target, CancellationToken.None).GetAwaiter().GetResult();
                    Check(f.Interrupts == 1 && f.Starts == 0, "Prepare sent continuation before switching.");
                    f.Commit(workflow);
                    Check(workflow.ContinueAsync(f.Target, CancellationToken.None).GetAwaiter().GetResult(), "Continuation was not sent.");
                    var ticket = f.Store.ReadPending();
                    Check(f.Preflights == 2 && f.Starts == 1 && ticket.Count == 0, "Fresh checks or final durable acknowledgement were missing.");
                    bool rejected = false;
                    try { workflow.ContinueAsync(f.Target, CancellationToken.None).GetAwaiter().GetResult(); } catch { rejected = true; }
                    Check(rejected && f.Starts == 1, "A repeated completion started another turn.");
                }
            });
            test("Desktop handoff reconciles a quota failure racing Stop without retrying a mutation", delegate
            {
                using (var f = new Fixture())
                {
                    f.Operations.Interrupt = (cp, token) => { f.Interrupts++; throw new UserError("The exact turn already ended for quota."); };
                    var workflow = f.Workflow(); workflow.PrepareAsync(f.Source, f.Target, CancellationToken.None).GetAwaiter().GetResult();
                    f.Commit(workflow); workflow.ContinueAsync(f.Target, CancellationToken.None).GetAwaiter().GetResult();
                    Check(f.Interrupts == 1 && f.Starts == 1, "A confirmed quota failure repeated Stop or lost its one continuation.");
                }
                using (var f = new Fixture())
                {
                    f.Operations.Interrupt = (cp, token) => { f.Interrupts++; throw new UserError("Synthetic Stop failure."); };
                    f.Operations.WaitStopped = (cp, token) => { throw new IOException("The backend did not establish the exact terminal turn."); };
                    var workflow = f.Workflow(); bool rejected = false;
                    try { workflow.PrepareAsync(f.Source, f.Target, CancellationToken.None).GetAwaiter().GetResult(); } catch (UserError) { rejected = true; }
                    Check(rejected && f.Interrupts == 1 && f.Starts == 0 && f.Current == f.Source.Id,
                        "A Stop failure without authoritative evidence advanced the handoff.");
                }
            });
            test("Desktop handoff never retries a lost continuation acknowledgement after reopening", delegate
            {
                using (var f = new Fixture())
                {
                    f.Operations.Start = (cp, operation, token) => { f.Starts++; throw new IOException("Synthetic lost acknowledgement after a possible send."); };
                    var workflow = f.Workflow(); workflow.PrepareAsync(f.Source, f.Target, CancellationToken.None).GetAwaiter().GetResult(); f.Commit(workflow);
                    try { workflow.ContinueAsync(f.Target, CancellationToken.None).GetAwaiter().GetResult(); } catch (IOException) { }
                    Check(f.Starts == 1 && f.Store.ReadPending().Single().State == ContinuationTicketState.NeedsAttention, "The uncertain send became retryable.");
                    f.Current = f.Source.Id; var reopened = f.Workflow(); bool rejected = false;
                    try { reopened.PrepareAsync(f.Source, f.Target, CancellationToken.None).GetAwaiter().GetResult(); } catch { rejected = true; }
                    Check(rejected && f.Starts == 1, "A restart repeated the same original turn.");
                }
            });
            test("Desktop handoff retains a completed send when cleanup fails and releases it after reopening", delegate
            {
                using (var f = new Fixture())
                {
                    f.Operations.Release = (cp, operation, token) => { f.Releases++; throw new IOException("Synthetic lost release acknowledgement."); };
                    var workflow = f.Workflow(); workflow.PrepareAsync(f.Source, f.Target, CancellationToken.None).GetAwaiter().GetResult(); f.Commit(workflow);
                    bool rejected = false;
                    try { workflow.ContinueAsync(f.Target, CancellationToken.None).GetAwaiter().GetResult(); } catch (IOException) { rejected = true; }
                    Check(rejected && f.Starts == 1 && f.Store.ReadPending().Count == 0 && workflow.StatusMessage.Contains("已送出一次"),
                        "A failed cleanup erased a confirmed send or silently claimed normal control.");
                    f.Operations.Release = (cp, operation, token) => { f.Releases++; return Task.FromResult(0); };
                    var reopened = f.Workflow(); reopened.RecoverPendingAsync().GetAwaiter().GetResult();
                    Check(f.Releases == 2 && f.Starts == 1, "Reopening did not release the completed reservation without sending again.");
                }
            });
            test("Desktop handoff refuses a changed target account before any continuation packet", delegate
            {
                using (var f = new Fixture())
                {
                    var workflow = f.Workflow(); workflow.PrepareAsync(f.Source, f.Target, CancellationToken.None).GetAwaiter().GetResult(); f.Commit(workflow);
                    f.Current = "another-account"; bool rejected = false;
                    try { workflow.ContinueAsync(f.Target, CancellationToken.None).GetAwaiter().GetResult(); } catch { rejected = true; }
                    Check(rejected && f.Starts == 0, "A different account received the continuation.");
                    workflow.CancelAsync().GetAwaiter().GetResult(); Check(f.Releases == 1, "The UI admission barrier was not released.");
                }
            });
            test("Desktop handoff does not interrupt an already failed quota turn or send for an idle App", delegate
            {
                using (var f = new Fixture())
                {
                    f.Checkpoint.OriginalTurnStatus = "failed";
                    var workflow = f.Workflow(); workflow.PrepareAsync(f.Source, f.Target, CancellationToken.None).GetAwaiter().GetResult();
                    Check(f.Interrupts == 0 && workflow.HasHandoff && f.Starts == 0, "A completed quota failure was interrupted again.");
                }
                using (var f = new Fixture())
                {
                    f.Operations.Capture = token => Task.FromResult<DesktopContinuationCheckpoint>(null);
                    var workflow = f.Workflow(); workflow.PrepareAsync(f.Source, f.Target, CancellationToken.None).GetAwaiter().GetResult();
                    Check(!workflow.HasHandoff && f.Interrupts == 0 && !workflow.ContinueAsync(f.Target, CancellationToken.None).GetAwaiter().GetResult(), "An idle App received an unsolicited message.");
                }
            });
            test("Desktop handoff cancellation after restoring never sends and releases the UI gate", delegate
            {
                using (var f = new Fixture())
                using (var cancellation = new CancellationTokenSource())
                {
                    var workflow = f.Workflow(); workflow.PrepareAsync(f.Source, f.Target, cancellation.Token).GetAwaiter().GetResult(); f.Commit(workflow);
                    cancellation.Cancel(); bool cancelled = false;
                    try { workflow.ContinueAsync(f.Target, cancellation.Token).GetAwaiter().GetResult(); } catch (OperationCanceledException) { cancelled = true; }
                    workflow.CancelAsync().GetAwaiter().GetResult();
                    Check(cancelled && f.Starts == 0 && f.Releases == 1, "A stopped monitor still sent continuation.");
                }
            });
        }
    }
}
