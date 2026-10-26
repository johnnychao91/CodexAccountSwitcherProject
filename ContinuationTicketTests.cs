using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CodexAccountSwitcher
{
    public static class ContinuationTicketTests
    {
        private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        private static bool Reject(Action operation) { try { operation(); return false; } catch { return true; } }
        private sealed class Fixture : IDisposable
        {
            internal readonly string Root, Home, Directory;
            internal readonly ContinuationTicketStore Store;
            internal Fixture()
            {
                Root = Path.Combine(Path.GetTempPath(), "CodexAccountSwitcher-continuation-" + Guid.NewGuid().ToString("N"));
                Home = Path.Combine(Root, "home"); DesktopSessionFiles.CreatePrivate(Root); DesktopSessionFiles.CreatePrivate(Home);
                Store = new ContinuationTicketStore(Root, Home); Directory = Path.Combine(Root, "integrations", "continuation");
            }
            internal DesktopContinuationCheckpoint Checkpoint()
            {
                return new DesktopContinuationCheckpoint { SessionId = Guid.NewGuid().ToString("N"), Generation = 1,
                    ThreadId = "fixture-thread-" + Guid.NewGuid().ToString("N"), TurnId = "fixture-turn-" + Guid.NewGuid().ToString("N"),
                    Home = Home, Cwd = Home, Model = "synthetic-model", ModelProvider = "synthetic-provider", Effort = "medium", OriginalTurnStatus = "inProgress",
                    RolloutPath = Path.Combine(Home, "sessions", "synthetic-" + Guid.NewGuid().ToString("N") + ".jsonl"),
                    PermissionsJson = "{\"approvalPolicy\":\"on-request\",\"approvalsReviewer\":\"user\",\"sandbox\":{\"type\":\"readOnly\",\"networkAccess\":false},\"permissions\":{\"id\":\":read-only\",\"extends\":null}}" };
            }
            internal ContinuationTicket Create() { return Store.Create("synthetic-source-account", "synthetic-target-account", Checkpoint()); }
            internal void Restored(ContinuationTicket ticket)
            {
                foreach (string state in new[] { "Stopped", "Quiesced", "AccountApplied", "Restored" }) Store.MarkState(ticket, state);
            }
            public void Dispose()
            {
                string parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
                Check(Path.GetDirectoryName(Path.GetFullPath(Root)).TrimEnd(Path.DirectorySeparatorChar) == parent && Path.GetFileName(Root).StartsWith("CodexAccountSwitcher-continuation-", StringComparison.Ordinal), "Unsafe fixture cleanup target.");
                ManagedBridge.EnsureNoReparsePoint(Root); System.IO.Directory.Delete(Root, true);
            }
        }

        public static void Register(Action<string, Action> test)
        {
            test("Quota wait records survive store restart privately without reserving a send", delegate
            {
                using (var f = new Fixture())
                {
                    var cp = f.Checkpoint(); cp.OriginalTurnStatus = "failed";
                    var ticket = f.Store.CreateQuotaWait("source", cp);
                    var again = f.Store.CreateQuotaWait("source", cp);
                    var reopened = new ContinuationTicketStore(f.Root, f.Home); var saved = reopened.Read(ticket.OperationId);
                    string raw = File.ReadAllText(Path.Combine(f.Directory, ticket.OperationId + ".dpapi"));
                    Check(saved.Version == 2 && saved.State == "WaitingForQuota" && saved.TargetAccountId == "source" &&
                        saved.OperationId == again.OperationId && !raw.Contains(cp.ThreadId) && !raw.Contains("WaitingForQuota"),
                        "A waiting record was duplicated, lost on reopen, or exposed private context.");
                    cp.Model = "caller-mutated-model";
                    Check(saved.Checkpoint.Model != cp.Model && Reject(() => reopened.TryBeginSend(saved)) &&
                        Reject(() => reopened.MarkState(saved, "Stopped")), "Waiting could send or skip its durable claim.");
                }
            });
            test("Quota waits require a verified failed turn and preserve legacy same-account rejection", delegate
            {
                using (var f = new Fixture())
                {
                    foreach (string state in new[] { "inProgress", "interrupted", "completed" })
                    { var cp = f.Checkpoint(); cp.OriginalTurnStatus = state; Check(Reject(() => f.Store.CreateQuotaWait("source", cp)), "A non-quota terminal state became resumable."); }
                    var failed = f.Checkpoint(); failed.OriginalTurnStatus = "failed";
                    Check(Reject(() => f.Store.Create("source", "source", failed)), "Version 1 gained implicit same-account behavior.");
                    var legacy = f.Store.Create("source", "target", failed);
                    Check(legacy.Version == 1 && Reject(() => f.Store.CreateQuotaWait("source", failed)), "A new wait bypassed an existing original-turn record.");
                }
            });
            test("Only one competing process can claim a quota wait and choose its immutable destination", delegate
            {
                using (var f = new Fixture())
                {
                    var cp = f.Checkpoint(); cp.OriginalTurnStatus = "failed"; var ticket = f.Store.CreateQuotaWait("source", cp);
                    var second = new ContinuationTicketStore(f.Root, f.Home); var left = f.Store.Read(ticket.OperationId); var right = second.Read(ticket.OperationId);
                    int claims = 0;
                    Task.WaitAll(Task.Run(() => { if (f.Store.ClaimQuotaWait(left, "source")) Interlocked.Increment(ref claims); }),
                        Task.Run(() => { if (second.ClaimQuotaWait(right, "higher")) Interlocked.Increment(ref claims); }));
                    var claimed = f.Store.Read(ticket.OperationId);
                    Check(claims == 1 && claimed.State == "Captured" && !f.Store.ClaimQuotaWait(ticket, "another") &&
                        Reject(() => f.Store.CreateQuotaWait("source", cp)), "A wait was claimed twice or recreated after claim.");
                    claimed.TargetAccountId = "forged";
                    Check(Reject(() => f.Store.MarkState(claimed, "Stopped")), "The claimed destination could be changed.");
                }
            });
            test("Same-account quota recovery keeps the durable one-send rule across a lost acknowledgement", delegate
            {
                using (var f = new Fixture())
                {
                    var cp = f.Checkpoint(); cp.OriginalTurnStatus = "failed"; var ticket = f.Store.CreateQuotaWait("source", cp);
                    Check(f.Store.ClaimQuotaWait(ticket, "source"), "The original account could not claim restored quota.");
                    f.Restored(ticket); Check(f.Store.TryBeginSend(ticket), "A validated same-account restore could not send.");
                    var reopened = new ContinuationTicketStore(f.Root, f.Home); var saved = reopened.Read(ticket.OperationId);
                    Check(!reopened.TryBeginSend(saved) && saved.State == "Sending" && !reopened.ClaimQuotaWait(saved, "other"),
                        "Restart made a same-account uncertain send retryable.");
                    reopened.MarkNeedsAttention(saved); Check(Reject(() => reopened.CreateQuotaWait("source", cp)), "Attention state was reset into a wait.");
                }
            });
            test("Cancelling a stale quota wait cannot cancel another process's already claimed same-account recovery", delegate
            {
                using (var f = new Fixture())
                {
                    var cp = f.Checkpoint(); cp.OriginalTurnStatus = "failed"; var waiting = f.Store.CreateQuotaWait("source", cp);
                    var claimant = f.Store.Read(waiting.OperationId);
                    Check(f.Store.ClaimQuotaWait(claimant, "source") && !f.Store.CancelQuotaWait(waiting) &&
                        f.Store.Read(waiting.OperationId).State == "Captured", "A stale GUI cancelled another owner's claimed recovery.");
                    f.Restored(claimant); Check(f.Store.TryBeginSend(claimant), "The winning claim was invalidated by a stale cancellation.");
                }
            });
            test("A quota wait cannot be rebound to a changed checkpoint or reopened after cancellation", delegate
            {
                using (var f = new Fixture())
                {
                    var cp = f.Checkpoint(); cp.OriginalTurnStatus = "failed"; var ticket = f.Store.CreateQuotaWait("source", cp);
                    var tampered = f.Store.Read(ticket.OperationId); tampered.Checkpoint.Generation++;
                    Check(Reject(() => f.Store.ClaimQuotaWait(tampered, "target")), "A changed capture generation was claimed.");
                    cp.Model = "changed-model"; Check(Reject(() => f.Store.CreateQuotaWait("source", cp)), "An idempotent wait changed its context.");
                    f.Store.Cancel(ticket);
                    Check(ticket.State == "Cancelled" && !f.Store.ClaimQuotaWait(ticket, "target") &&
                        Reject(() => f.Store.CreateQuotaWait("source", ticket.Checkpoint)), "Cancellation allowed another automatic send for the old turn.");
                }
            });
            test("Clock corrections cannot invalidate durable continuation states or make an uncertain send retryable", delegate
            {
                using (var fixture = new Fixture())
                {
                    var initial = new DateTimeOffset(2026, 10, 26, 1, 0, 0, TimeSpan.Zero); var now = initial;
                    var store = new ContinuationTicketStore(fixture.Root, fixture.Home, () => now);
                    var ticket = store.Create("source", "target", fixture.Checkpoint()); string created = ticket.CreatedUtc;
                    now = initial.AddSeconds(-1);
                    foreach (string state in new[] { "Stopped", "Quiesced", "AccountApplied", "Restored" }) store.MarkState(ticket, state);
                    Check(ticket.State == "Restored" && ticket.UpdatedUtc == created && store.Read(ticket.OperationId).State == "Restored",
                        "A backwards clock invalidated a normal durable state transition.");
                    now = initial.AddMinutes(5); Check(store.TryBeginSend(ticket), "A restored ticket could not claim its one delivery."); string sendingAt = ticket.UpdatedUtc;
                    now = initial.AddHours(-1);
                    var reopened = new ContinuationTicketStore(fixture.Root, fixture.Home, () => now); var pending = reopened.Read(ticket.OperationId);
                    Check(!reopened.TryBeginSend(pending) && pending.State == "Sending" && pending.UpdatedUtc == sendingAt,
                        "A reopened ticket changed its durable Sending claim after the clock moved backwards.");
                    reopened.MarkNeedsAttention(pending);
                    Check(pending.UpdatedUtc == sendingAt && !store.TryBeginSend(ticket) && ticket.State == "NeedsAttention" &&
                        reopened.Read(ticket.OperationId).CreatedUtc == created, "A lost acknowledgement or clock correction reset the operation's identity or eligibility.");
                }
            });
            test("Tickets accept the exact local Rust rollout spelling without applying legacy MAX_PATH normalization", delegate
            {
                using (var fixture = new Fixture())
                {
                    string normal = Path.Combine(fixture.Home, "sessions", new string('a', 120), new string('b', 120), "rollout.jsonl");
                    var checkpoint = fixture.Checkpoint(); checkpoint.RolloutPath = "\\\\?\\" + normal;
                    var ticket = fixture.Store.Create("source", "target", checkpoint); fixture.Restored(ticket);
                    var pending = fixture.Store.Read(ticket.OperationId);
                    Check(normal.Length > 260 && ContinuationAdmission.CanonicalRolloutPath(pending.Checkpoint.RolloutPath) ==
                        ContinuationAdmission.CanonicalRolloutPath(normal) && fixture.Store.TryBeginSend(pending),
                        "The exact authenticated local rollout alias was rejected or lost its durable transition binding.");
                }
            });
            test("Durable frontend context stores only encrypted capability semantics and instruction digest with immutable local binding", delegate
            {
                using (var fixture = new Fixture())
                {
                    var checkpoint = fixture.Checkpoint();
                    string summary = ContinuationAdmission.ProjectFrontendContext(new Dictionary<string, object> { { "developerInstructions", "Synthetic private instruction text that must never appear in a ticket." } }, null);
                    checkpoint.FrontendContextJson = ContinuationAdmission.MergeFrontendEnvironment(summary, new object[] { new Dictionary<string, object> {
                        { "environmentId", "local" }, { "cwd", fixture.Home }, { "runtimeWorkspaceRoots", new[] { fixture.Home } } } }, fixture.Home);
                    var ticket = fixture.Store.Create("source", "target", checkpoint);
                    string path = Path.Combine(fixture.Directory, ticket.OperationId + ".dpapi"), bytes = File.ReadAllText(path);
                    var loaded = fixture.Store.Read(ticket.OperationId);
                    Check(loaded.Checkpoint.FrontendContextJson == checkpoint.FrontendContextJson && !loaded.Checkpoint.FrontendContextJson.Contains("Synthetic private instruction") &&
                        !bytes.Contains("sha256") && !bytes.Contains("environmentId"), "The frontend baseline was lost or retained raw private context.");
                    loaded.Checkpoint.FrontendContextJson = ContinuationAdmission.EmptyFrontendContextJson;
                    Check(Reject(() => fixture.Store.MarkState(loaded, "Stopped")) && File.ReadAllText(path) == bytes, "The captured frontend capability baseline was editable.");
                    foreach (string invalid in new[] { null, "{}", "{\"version\":1,\"config\":{},\"developerInstructions\":\"raw text\",\"environments\":[]}",
                        "{\"version\":1,\"config\":{\"token\":\"secret\"},\"developerInstructions\":null,\"environments\":[]}" })
                    {
                        var next = fixture.Checkpoint(); next.FrontendContextJson = invalid;
                        Check(Reject(() => fixture.Store.Create("source", "target", next)), "An invalid raw frontend summary was accepted.");
                    }
                    var wrongCwd = fixture.Checkpoint(); wrongCwd.FrontendContextJson = checkpoint.FrontendContextJson.Replace(fixture.Home.Replace("\\", "\\\\"), (fixture.Home + "-other").Replace("\\", "\\\\"));
                    Check(Reject(() => fixture.Store.Create("source", "target", wrongCwd)) && System.IO.Directory.GetFiles(fixture.Directory, "*.dpapi").Length == 1,
                        "A local environment escaped the captured working directory or wrote a rejected record.");
                }
            });
            test("Captured rollout identity is encrypted immutable and requires a fully qualified path", delegate
            {
                using (var fixture = new Fixture())
                {
                    var ticket = fixture.Create(); string path = Path.Combine(fixture.Directory, ticket.OperationId + ".dpapi"), before = File.ReadAllText(path);
                    var loaded = fixture.Store.Read(ticket.OperationId);
                    Check(loaded.Checkpoint.RolloutPath == ticket.Checkpoint.RolloutPath && !before.Contains(ticket.Checkpoint.RolloutPath), "The pinned rollout identity was not retained privately.");
                    loaded.Checkpoint.RolloutPath = Path.Combine(fixture.Home, "different-rollout.jsonl");
                    Check(Reject(() => fixture.Store.MarkState(loaded, "Stopped")) && File.ReadAllText(path) == before, "The captured rollout identity was editable after capture.");
                    foreach (string invalid in new[] { null, "relative-rollout.jsonl", "C:drive-relative.jsonl", "\\missing-drive.jsonl" })
                    { var checkpoint = fixture.Checkpoint(); checkpoint.RolloutPath = invalid; Check(Reject(() => fixture.Store.Create("source", "target", checkpoint)), "An unbound rollout path was accepted."); }
                    Check(System.IO.Directory.GetFiles(fixture.Directory, "*.dpapi").Length == 1, "A rejected rollout path wrote another ticket.");
                }
            });
            test("Captured runtime roots are encrypted immutable absolute bounded context rather than editable settings", delegate
            {
                using (var fixture = new Fixture())
                {
                    var checkpoint = fixture.Checkpoint();
                    checkpoint.RuntimeWorkspaceRoots = new[] { fixture.Home, Path.Combine(fixture.Home, "visualizations", "synthetic-thread") };
                    var ticket = fixture.Store.Create("source", "target", checkpoint);
                    string path = Path.Combine(fixture.Directory, ticket.OperationId + ".dpapi"), before = File.ReadAllText(path);
                    var reopened = fixture.Store.Read(ticket.OperationId);
                    Check(reopened.Checkpoint.RuntimeWorkspaceRoots.SequenceEqual(checkpoint.RuntimeWorkspaceRoots) && !before.Contains("visualizations"),
                        "Captured runtime roots were dropped or stored as public text.");
                    reopened.Checkpoint.RuntimeWorkspaceRoots[1] = Path.Combine(fixture.Home, "another-root");
                    Check(Reject(() => fixture.Store.MarkState(reopened, "Stopped")) && File.ReadAllText(path) == before,
                        "Runtime roots changed after capturing the immutable ticket.");
                    foreach (string kind in new[] { "null", "relative", "drive-relative", "missing-drive", "duplicate", "too-many", "control" })
                    {
                        var invalid = fixture.Checkpoint();
                        if (kind == "null") invalid.RuntimeWorkspaceRoots = null;
                        if (kind == "relative") invalid.RuntimeWorkspaceRoots = new[] { "relative-root" };
                        if (kind == "drive-relative") invalid.RuntimeWorkspaceRoots = new[] { "C:relative-root" };
                        if (kind == "missing-drive") invalid.RuntimeWorkspaceRoots = new[] { "\\relative-root" };
                        if (kind == "duplicate") invalid.RuntimeWorkspaceRoots = new[] { fixture.Home, fixture.Home.ToUpperInvariant() };
                        if (kind == "too-many") invalid.RuntimeWorkspaceRoots = Enumerable.Range(0, 65).Select(index => Path.Combine(fixture.Home, "root-" + index)).ToArray();
                        if (kind == "control") invalid.RuntimeWorkspaceRoots = new[] { fixture.Home + "\nunsafe" };
                        Check(Reject(() => fixture.Store.Create("source", "target", invalid)), "Invalid " + kind + " runtime roots were accepted.");
                    }
                    Check(System.IO.Directory.GetFiles(fixture.Directory, "*.dpapi").Length == 1, "Invalid roots wrote another durable ticket.");
                }
            });
            test("Continuation recovery includes completed and cancelled barriers without making either ticket sendable", delegate
            {
                using (var fixture = new Fixture())
                {
                    var completed = fixture.Create(); fixture.Restored(completed);
                    Check(fixture.Store.TryBeginSend(completed), "The first send reservation failed.");
                    fixture.Store.Complete(completed, "acknowledged-new-turn");
                    var cancelled = fixture.Create(); fixture.Store.Cancel(cancelled);
                    var pending = fixture.Create();
                    var reopened = new ContinuationTicketStore(fixture.Root, fixture.Home);
                    var recovery = reopened.ReadRecovery();
                    Check(recovery.Count == 3 && recovery.Any(item => item.OperationId == completed.OperationId && item.State == "Continued") &&
                        recovery.Any(item => item.OperationId == cancelled.OperationId && item.State == "Cancelled") &&
                        recovery.Any(item => item.OperationId == pending.OperationId && item.State == "Captured"), "Recovery omitted a terminal admission barrier.");
                    Check(recovery.Select(item => item.CreatedUtc).SequenceEqual(recovery.Select(item => item.CreatedUtc).OrderBy(value => value, StringComparer.Ordinal)),
                        "Recovery tickets were not in creation order.");
                    Check(reopened.ReadPending().Count == 1 && !reopened.TryBeginSend(recovery.Single(item => item.OperationId == completed.OperationId)) &&
                        !reopened.TryBeginSend(recovery.Single(item => item.OperationId == cancelled.OperationId)), "Recovery reset terminal state or changed pending semantics.");
                    Check(reopened.Read(completed.OperationId).State == "Continued" && reopened.Read(cancelled.OperationId).State == "Cancelled",
                        "Recovery changed a durable terminal record.");
                }
            });
            test("Canonical captured permission metadata survives ticket storage and the actual desktop turn request passes admission once", delegate
            {
                using (var fixture = new Fixture())
                {
                    foreach (bool namedProfile in new[] { false, true })
                    {
                        var checkpoint = fixture.Checkpoint();
                        checkpoint.RuntimeWorkspaceRoots = new[] { fixture.Home };
                        checkpoint.FrontendContextJson = ContinuationAdmission.MergeFrontendEnvironment(ContinuationAdmission.EmptyFrontendContextJson,
                            new object[] { new Dictionary<string, object> { { "environmentId", "local" }, { "cwd", fixture.Home }, { "runtimeWorkspaceRoots", new[] { fixture.Home } } } }, fixture.Home);
                        if (!namedProfile)
                        {
                            var policy = Json.Object(checkpoint.PermissionsJson); policy["permissions"] = null; checkpoint.PermissionsJson = Json.Encode(policy);
                        }
                        var ticket = fixture.Store.Create("source", "target", checkpoint); fixture.Restored(ticket);
                        var restored = fixture.Store.Read(ticket.OperationId);
                        var admission = new ContinuationAdmission(restored.Checkpoint.ThreadId, restored.OperationId, restored.Checkpoint.Model,
                            restored.Checkpoint.Effort, restored.Checkpoint.PermissionsJson, restored.Checkpoint.Cwd, restored.Checkpoint.ModelProvider, restored.Checkpoint.RuntimeWorkspaceRoots,
                            restored.Checkpoint.RolloutPath, restored.Checkpoint.FrontendContextJson, null);
                        var request = Json.Object(Json.Encode(DesktopConversationClient.CreateTurnRequest(restored.Checkpoint, restored.OperationId)));
                        var missingEnvironment = Json.Object(Json.Encode(request)); missingEnvironment["environments"] = null;
                        Check(!admission.ShouldAllow("turn/start", missingEnvironment) && !admission.WasClaimed, "The actual IPC request silently lost the source local environment binding.");
                        Check(fixture.Store.TryBeginSend(restored) && admission.ShouldAllow("turn/start", request) && !admission.ShouldAllow("turn/start", request),
                            "The captured ticket and desktop request admission chain failed or allowed duplicate admission.");
                        Check(namedProfile ? request.ContainsKey("permissions") && !request.ContainsKey("sandboxPolicy") :
                            request.ContainsKey("sandboxPolicy") && !request.ContainsKey("permissions"), "Captured named and resolved policies were mixed on the wire.");
                    }
                }
            });
            test("Continuation tickets are encrypted private durable records with an exact fixed message", delegate
            {
                using (var fixture = new Fixture())
                {
                    var ticket = fixture.Create(); string path = Path.Combine(fixture.Directory, ticket.OperationId + ".dpapi");
                    string disk = File.ReadAllText(path); DesktopSessionFiles.VerifyPrivate(path, true);
                    Check(ticket.Message == "繼續執行" && !disk.Contains(ticket.Checkpoint.ThreadId) && !disk.Contains(ticket.Checkpoint.Home) &&
                        !disk.Contains(ticket.SourceAccountId) && !disk.Contains("繼續執行"), "Ticket data was readable on disk.");
                    var reopened = new ContinuationTicketStore(fixture.Root, fixture.Home).Read(ticket.OperationId);
                    Check(reopened.State == "Captured" && reopened.Message == "繼續執行" && reopened.Checkpoint.ThreadId == ticket.Checkpoint.ThreadId &&
                        reopened.Checkpoint.PermissionsJson == ticket.Checkpoint.PermissionsJson, "Encrypted ticket did not survive reopening.");
                }
            });
            test("Continuation Sending claim is durable before delivery and reopening cannot send twice", delegate
            {
                using (var fixture = new Fixture())
                {
                    var ticket = fixture.Create(); fixture.Restored(ticket);
                    Check(fixture.Store.TryBeginSend(ticket), "The first restored ticket did not claim delivery.");
                    var reopened = new ContinuationTicketStore(fixture.Root, fixture.Home); var pending = reopened.Read(ticket.OperationId);
                    Check(pending.State == "Sending" && !reopened.TryBeginSend(pending), "A crash after the durable Sending claim made delivery retryable.");
                    reopened.MarkNeedsAttention(pending);
                    Check(!fixture.Store.TryBeginSend(ticket) && ticket.State == "NeedsAttention" && reopened.ReadPending().Count == 1,
                        "An uncertain acknowledgement allowed a repeated send or disappeared.");
                }
            });
            test("Concurrent continuation stores can claim a restored ticket only once", delegate
            {
                using (var fixture = new Fixture())
                using (var start = new ManualResetEvent(false))
                {
                    var ticket = fixture.Create(); fixture.Restored(ticket);
                    var other = new ContinuationTicketStore(fixture.Root, fixture.Home); var second = other.Read(ticket.OperationId);
                    int claims = 0;
                    var firstTask = Task.Run(delegate { start.WaitOne(); if (fixture.Store.TryBeginSend(ticket)) Interlocked.Increment(ref claims); });
                    var secondTask = Task.Run(delegate { start.WaitOne(); if (other.TryBeginSend(second)) Interlocked.Increment(ref claims); });
                    start.Set(); Check(Task.WaitAll(new[] { firstTask, secondTask }, 10000), "Concurrent claims failed to finish.");
                    Check(claims == 1 && fixture.Store.Read(ticket.OperationId).State == "Sending", "Concurrent stores both claimed the same continuation.");
                }
            });
            test("A new operation cannot bypass continuation deduplication for the same original turn", delegate
            {
                using (var fixture = new Fixture())
                {
                    var checkpoint = fixture.Checkpoint(); var ticket = fixture.Store.Create("source", "target", checkpoint); fixture.Restored(ticket);
                    Check(fixture.Store.TryBeginSend(ticket), "Initial claim failed.");
                    Check(Reject(() => new ContinuationTicketStore(fixture.Root, fixture.Home).Create("source", "another-target", checkpoint)),
                        "A fresh operation ID bypassed the original turn's uncertain send claim.");
                    Check(System.IO.Directory.GetFiles(fixture.Directory, "*.dpapi").Length == 1, "A rejected duplicate operation wrote another ticket.");
                }
            });
            test("Completed continuation records reject replay and require a distinct acknowledged turn", delegate
            {
                using (var fixture = new Fixture())
                {
                    var ticket = fixture.Create(); fixture.Restored(ticket); fixture.Store.TryBeginSend(ticket);
                    Check(Reject(() => fixture.Store.Complete(ticket, ticket.Checkpoint.TurnId)), "The original interrupted turn was accepted as the continuation.");
                    fixture.Store.Complete(ticket, "synthetic-new-turn"); fixture.Store.Complete(ticket, "synthetic-new-turn");
                    var reopened = new ContinuationTicketStore(fixture.Root, fixture.Home);
                    Check(!reopened.TryBeginSend(ticket) && reopened.Read(ticket.OperationId).NewTurnId == "synthetic-new-turn" && reopened.ReadPending().Count == 0,
                        "A completed continuation became sendable again.");
                    Check(Reject(() => fixture.Store.Complete(ticket, "different-new-turn")), "A conflicting acknowledgement rewrote a completed turn.");
                }
            });
            test("Continuation cancellation before sending is terminal and cancellation after Sending stays uncertain", delegate
            {
                using (var fixture = new Fixture())
                {
                    var early = fixture.Create(); fixture.Store.Cancel(early);
                    Check(early.State == "Cancelled" && !fixture.Store.TryBeginSend(early), "A cancelled pre-send ticket was retryable.");
                    var late = fixture.Create(); fixture.Restored(late); fixture.Store.TryBeginSend(late); fixture.Store.Cancel(late);
                    Check(late.State == "NeedsAttention" && !fixture.Store.TryBeginSend(late), "Cancellation incorrectly promised an already attempted send was undone.");
                }
            });
            test("Continuation immutable bindings and out-of-order transitions fail without overwriting disk", delegate
            {
                using (var fixture = new Fixture())
                {
                    var ticket = fixture.Create(); string path = Path.Combine(fixture.Directory, ticket.OperationId + ".dpapi"), before = File.ReadAllText(path);
                    Check(Reject(() => fixture.Store.MarkState(ticket, "Restored")), "A ticket skipped shutdown and account application.");
                    ticket.TargetAccountId = "changed-target";
                    Check(Reject(() => fixture.Store.MarkState(ticket, "Stopped")), "A mutable caller changed the bound destination account.");
                    Check(File.ReadAllText(path) == before, "A rejected transition rewrote the durable record.");
                    var wrongHome = new ContinuationTicketStore(fixture.Root, Path.Combine(fixture.Root, "other-home"));
                    Check(Reject(() => wrongHome.Read(ticket.OperationId)), "A different CODEX_HOME accepted the ticket.");
                }
            });
            test("Corrupt continuation envelopes are preserved and cannot be treated as empty or reauthorized", delegate
            {
                using (var fixture = new Fixture())
                {
                    var ticket = fixture.Create(); string path = Path.Combine(fixture.Directory, ticket.OperationId + ".dpapi");
                    foreach (string corrupt in new[] { "{}", "{\"Version\":true,\"Ciphertext\":\"invalid\"}", "{\"Version\":1,\"Ciphertext\":\"invalid\"}", new string('x', 65537) })
                    {
                        File.WriteAllText(path, corrupt);
                        Check(Reject(() => fixture.Store.Read(ticket.OperationId)) && Reject(() => fixture.Store.ReadPending()), "A corrupt ticket was ignored or accepted.");
                        Check(File.ReadAllText(path) == corrupt, "A corrupt ticket was overwritten during failed validation.");
                    }
                }
            });
            test("Continuation records reject arbitrary instruction content in captured permission metadata", delegate
            {
                using (var fixture = new Fixture())
                {
                    var cp = fixture.Checkpoint(); cp.PermissionsJson = "{\"approvalPolicy\":\"never\",\"developerInstructions\":\"private-fixture\"}";
                    Check(Reject(() => fixture.Store.Create("source", "target", cp)), "Instructions were admitted into policy metadata.");
                    cp.PermissionsJson = "{\"permissions\":{\"id\":\"profile\",\"prompt\":\"private-fixture\"}}";
                    Check(Reject(() => fixture.Store.Create("source", "target", cp)), "Arbitrary permission profile content was persisted.");
                    cp.PermissionsJson = "{\"sandbox\":{\"type\":\"readOnly\",\"writableRoots\":[\"relative-path\"]}}";
                    Check(Reject(() => fixture.Store.Create("source", "target", cp)), "A relative write root was admitted.");
                }
            });
            test("Continuation store rejects broad ACLs without silently repairing the original record", delegate
            {
                using (var fixture = new Fixture())
                {
                    var ticket = fixture.Create(); string path = Path.Combine(fixture.Directory, ticket.OperationId + ".dpapi"); string before = File.ReadAllText(path);
                    var original = File.GetAccessControl(path); var broad = File.GetAccessControl(path);
                    broad.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Read, AccessControlType.Allow));
                    File.SetAccessControl(path, broad);
                    try { Check(Reject(() => fixture.Store.Read(ticket.OperationId)) && Reject(() => fixture.Store.TryBeginSend(ticket)), "A broadly readable ticket was accepted."); }
                    finally { File.SetAccessControl(path, original); }
                    Check(File.ReadAllText(path) == before, "Security rejection changed the existing ticket.");
                }
            });
        }
    }
}
