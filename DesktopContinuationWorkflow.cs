using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CodexAccountSwitcher
{
    public sealed class DesktopContinuationOperations
    {
        public Func<bool> Enabled;
        public Func<string> ReadCurrentId;
        public Func<CancellationToken, Task<DesktopContinuationCheckpoint>> Capture;
        public Func<DesktopContinuationCheckpoint, CancellationToken, Task> BeforeInterrupt;
        public Func<DesktopContinuationCheckpoint, string, CancellationToken, Task> BindRestore;
        public Func<DesktopContinuationCheckpoint, CancellationToken, Task> Interrupt;
        public Func<DesktopContinuationCheckpoint, CancellationToken, Task> WaitStopped;
        public Func<DesktopContinuationCheckpoint, Account, CancellationToken, Task<DesktopContinuationCheckpoint>> PrepareAfterRestore;
        public Func<DesktopContinuationCheckpoint, CancellationToken, Task> Open;
        public Func<DesktopContinuationCheckpoint, string, CancellationToken, Task<string>> Start;
        public Func<DesktopContinuationCheckpoint, string, CancellationToken, Task> Release;
        public Action<string> Status;
    }

    // Owns a single quota handoff. The ticket and restored bridge both reserve the
    // operation before any continuation can cross the process boundary.
    public sealed class DesktopContinuationWorkflow
    {
        private readonly ContinuationTicketStore store;
        private readonly DesktopContinuationOperations operations;
        private ContinuationTicket ticket;
        private readonly List<ContinuationTicket> interruptedOwners = new List<ContinuationTicket>();
        public string StatusMessage { get; private set; }
        public bool HasHandoff { get { return ticket != null && ticket.State != ContinuationTicketState.WaitingForQuota; } }
        public string WaitingForQuotaSourceId
        { get { return ticket != null && ticket.State == ContinuationTicketState.WaitingForQuota ? ticket.SourceAccountId : null; } }

        public DesktopContinuationWorkflow(ContinuationTicketStore store, DesktopContinuationOperations operations)
        {
            if (store == null || operations == null || operations.Enabled == null || operations.ReadCurrentId == null ||
                operations.Capture == null || operations.BindRestore == null || operations.Interrupt == null || operations.WaitStopped == null ||
                operations.PrepareAfterRestore == null || operations.Open == null || operations.Start == null || operations.Release == null)
                throw new ArgumentException("Incomplete desktop continuation operations.");
            this.store = store; this.operations = operations;
            int uncertain = 0;
            var waiting = new List<ContinuationTicket>();
            foreach (var pending in store.ReadRecovery())
            {
                // Only a durable record that has never been claimed can survive
                // a GUI restart as an automatic wait. It holds no admission lock.
                if (pending.Version == 2 && pending.State == ContinuationTicketState.WaitingForQuota)
                { waiting.Add(pending); continue; }
                // A restarted GUI cannot know whether the previous owner sent a
                // request. Never reconstruct an automatic send from old state.
                if (pending.State != ContinuationTicketState.Continued && pending.State != ContinuationTicketState.Cancelled)
                { store.MarkNeedsAttention(pending); uncertain++; }
                // A completed/cancelled send may still have lost its Release
                // acknowledgement. It is also eligible for cleanup, never send.
                interruptedOwners.Add(pending);
            }
            if (waiting.Count == 1) ticket = waiting[0];
            else foreach (var pending in waiting) { store.MarkNeedsAttention(pending); uncertain++; }
            SetStatus(uncertain != 0 ? "有先前未完成的續聊記錄；不會自動重送，請檢查原對話。" : ticket != null ?
                "原對話正在等待額度恢復；會重新確認帳號與對話後再續跑。" : "桌面自動續聊已準備就緒。");
        }

        private void SetStatus(string value)
        { StatusMessage = value; if (operations.Status != null) operations.Status(value); }
        private void CheckEnabled(CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!operations.Enabled()) throw new OperationCanceledException("桌面自動續聊已關閉。", cancellation);
        }
        private void CheckCurrent(string expected)
        {
            if (String.IsNullOrEmpty(expected) || operations.ReadCurrentId() != expected)
                throw new UserError("目前帳號已變更，已停止自動續聊；不會送出訊息。");
        }
        public async Task RecoverPendingAsync()
        {
            // Release only an authenticated matching old reservation. Never send
            // a continuation from startup reconciliation, including Sending.
            // Old failed tickets can accumulate. Reconcile the newest four so
            // unreachable old Hosts cannot delay startup for several minutes.
            for (int index = interruptedOwners.Count - 1, checkedOwners = 0;
                index >= 0 && checkedOwners < 4; index--, checkedOwners++)
            {
                var pending = interruptedOwners[index];
                try
                {
                    using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                        await operations.Release(pending.Checkpoint, pending.OperationId, timeout.Token);
                }
                catch { }
            }
            interruptedOwners.Clear();
        }
        public Task PrepareAsync(Account source, Account target, CancellationToken cancellation)
        { return PrepareAsync(source, target, null, cancellation); }
        public async Task PrepareAsync(Account source, Account target, Func<CancellationToken, Task> revalidate, CancellationToken cancellation)
        {
            if (WaitingForQuotaSourceId != null) throw new UserError("原對話仍在等待額度，請先完成或取消該次續聊。");
            ticket = null;
            if (!operations.Enabled()) return;
            CheckEnabled(cancellation); CheckCurrent(source.Id);
            DesktopContinuationCheckpoint checkpoint;
            SetStatus("正在確認原對話與可保留的執行設定…");
            try
            {
                checkpoint = await operations.Capture(cancellation);
                if (checkpoint == null)
                {
                    SetStatus("目前沒有需要續聊的桌面回合，會只切換帳號並開啟 Codex。");
                    return;
                }
                CheckEnabled(cancellation); CheckCurrent(source.Id);
                ticket = store.Create(source.Id, target.Id, checkpoint);
            }
            catch (OperationCanceledException) { SetStatus("桌面自動續聊已取消，尚未送出訊息。"); throw; }
            catch (UserError error) { SetStatus(error.Message); throw; }
            catch { SetStatus("無法確認原對話或完整保留其設定，已停止自動續聊；尚未送出訊息。"); throw; }
            try
            {
                if (operations.BeforeInterrupt != null) await operations.BeforeInterrupt(checkpoint, cancellation);
                CheckEnabled(cancellation); CheckCurrent(source.Id);
                // Other runtimes can take minutes to finish. Recheck live quota
                // and the chosen priority after waiting, before binding restore
                // or interrupting a turn that may now have usable quota again.
                if (revalidate != null) await revalidate(cancellation);
                CheckEnabled(cancellation); CheckCurrent(source.Id);
                SetStatus("額度已耗盡，正在正常中止原對話並保存續聊狀態…");
                await operations.BindRestore(checkpoint, ticket.OperationId, cancellation);
                CheckEnabled(cancellation);
                Exception interruptFailure = null;
                if (checkpoint.OriginalTurnStatus == "inProgress")
                {
                    try { await operations.Interrupt(checkpoint, cancellation); }
                    catch (Exception error) { cancellation.ThrowIfCancellationRequested(); interruptFailure = error; }
                }
                if (interruptFailure == null) await operations.WaitStopped(checkpoint, cancellation);
                else
                {
                    // The exact turn may have failed for quota while another
                    // runtime was finishing, or the Stop acknowledgement was
                    // lost. Reconcile only authoritative terminal/idle evidence;
                    // never repeat the interrupt or infer success from its error.
                    bool stopped = false;
                    using (var reconciliation = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                    {
                        reconciliation.CancelAfter(TimeSpan.FromSeconds(30));
                        try { await operations.WaitStopped(checkpoint, reconciliation.Token); stopped = true; }
                        catch { cancellation.ThrowIfCancellationRequested(); }
                    }
                    if (!stopped) throw interruptFailure;
                }
                CheckEnabled(cancellation); CheckCurrent(source.Id);
                store.MarkState(ticket, ContinuationTicketState.Stopped);
                SetStatus("原回合已正常結束，正在等待切換帳號。");
            }
            catch (Exception error)
            {
                try { store.MarkNeedsAttention(ticket); } catch { }
                if (error is UserError) SetStatus(error.Message);
                throw;
            }
        }

        public async Task<bool> ObserveQuotaWaitAsync(Account source, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!operations.Enabled()) { if (WaitingForQuotaSourceId != null) await CancelAsync(); return false; }
            if (source == null || String.IsNullOrEmpty(source.Id) || operations.ReadCurrentId() != source.Id)
            { if (WaitingForQuotaSourceId != null) await CancelAsync(); return false; }
            if (ticket != null && ticket.State != ContinuationTicketState.WaitingForQuota)
            {
                if (ticket.State != ContinuationTicketState.Continued && ticket.State != ContinuationTicketState.Cancelled)
                    throw new UserError("先前續聊狀態尚未確認，無法建立新的額度等待。");
                ticket = null;
            }
            if (ticket != null)
            {
                var durable = store.Read(ticket.OperationId);
                if (durable.State != ContinuationTicketState.WaitingForQuota) { ticket = null; return false; }
                if (durable.SourceAccountId != source.Id) { await CancelAsync(); return false; }
            }
            // Capture supplies only an active turn or an authoritative
            // usageLimitExceeded failure. Waiting must never interrupt activity
            // merely because a quota poll happened to return zero.
            var checkpoint = await operations.Capture(cancellation);
            CheckEnabled(cancellation); CheckCurrent(source.Id);
            if (checkpoint == null || checkpoint.OriginalTurnStatus != "failed" ||
                ticket != null && !ContinuationTicketStore.SameCheckpoint(ticket.Checkpoint, checkpoint))
            {
                if (ticket != null) await CancelAsync();
                SetStatus("目前沒有可確認的額度中止回合；不會自動續聊。"); return false;
            }
            if (ticket == null) ticket = store.CreateQuotaWait(source.Id, checkpoint);
            SetStatus("原對話已因額度用完而停止，正在等待較高順位或目前帳號恢復額度。");
            return true;
        }

        public async Task PrepareQuotaResetAsync(Account source, Account target,
            Func<CancellationToken, Task> revalidate, CancellationToken cancellation)
        {
            CheckEnabled(cancellation);
            if (source == null || target == null || String.IsNullOrEmpty(target.Id) ||
                ticket == null || ticket.State != ContinuationTicketState.WaitingForQuota || ticket.SourceAccountId != source.Id)
                throw new UserError("額度等待記錄尚未確認，已停止自動續聊。");
            CheckCurrent(source.Id);
            var waiting = ticket;
            var durable = store.Read(waiting.OperationId);
            if (durable.State != ContinuationTicketState.WaitingForQuota)
            { ticket = null; throw new UserError("這次額度恢復已由其他操作領取，不會重複續聊。"); }
            var checkpoint = await operations.Capture(cancellation);
            CheckEnabled(cancellation); CheckCurrent(source.Id);
            if (!ContinuationTicketStore.SameCheckpoint(waiting.Checkpoint, checkpoint))
            { await CancelAsync(); throw new UserError("等待期間原對話或執行設定已變更，已取消自動續聊。"); }
            if (operations.BeforeInterrupt != null) await operations.BeforeInterrupt(checkpoint, cancellation);
            CheckEnabled(cancellation); CheckCurrent(source.Id);
            if (revalidate != null) await revalidate(cancellation);
            CheckEnabled(cancellation); CheckCurrent(source.Id);
            // A different turn can start while other runtimes are finishing.
            // Recapture before claiming; never send to a newer manual turn.
            checkpoint = await operations.Capture(cancellation);
            CheckEnabled(cancellation); CheckCurrent(source.Id);
            if (!ContinuationTicketStore.SameCheckpoint(waiting.Checkpoint, checkpoint))
            { await CancelAsync(); throw new UserError("等待期間原對話或執行設定已變更，已取消自動續聊。"); }
            if (!store.ClaimQuotaWait(waiting, target.Id))
            { ticket = null; throw new UserError("這次額度恢復已由其他操作領取，不會重複續聊。"); }
            try
            {
                SetStatus("額度已恢復，正在驗證原回合並準備續跑…");
                await operations.BindRestore(checkpoint, waiting.OperationId, cancellation);
                await operations.WaitStopped(checkpoint, cancellation);
                CheckEnabled(cancellation); CheckCurrent(source.Id);
                store.MarkState(waiting, ContinuationTicketState.Stopped);
            }
            catch
            {
                try { store.MarkNeedsAttention(waiting); } catch { }
                throw;
            }
        }
        public void MarkQuiesced()
        { if (ticket != null) store.MarkState(ticket, ContinuationTicketState.Quiesced); }
        public void MarkAccountApplied(Account target)
        {
            if (ticket == null) return;
            // Credential application has already committed. A ticket write error
            // must never disguise that commit or prevent restoring the App.
            try
            {
                if (target.Id != ticket.TargetAccountId) throw new UserError("續聊目標帳號不符。");
                store.MarkState(ticket, ContinuationTicketState.AccountApplied);
            }
            catch
            {
                try { store.MarkNeedsAttention(ticket); } catch { }
                SetStatus("帳號已確認，但續聊記錄尚未確認；會恢復 App，不會自動送出訊息。");
            }
        }
        public async Task<bool> ContinueAsync(Account target, CancellationToken cancellation)
        {
            if (ticket == null) return false;
            CheckEnabled(cancellation); CheckCurrent(ticket.TargetAccountId);
            if (target.Id != ticket.TargetAccountId || ticket.State != ContinuationTicketState.AccountApplied)
                throw new UserError("續聊狀態尚未確認；已停止自動送出，請檢查原對話。");
            bool delivered = false;
            try
            {
                store.MarkState(ticket, ContinuationTicketState.Restored);
                SetStatus("已重開 Codex，正在驗證目前帳號並載入原對話…");
                var restored = await operations.PrepareAfterRestore(ticket.Checkpoint, target, cancellation);
                CheckEnabled(cancellation); CheckCurrent(ticket.TargetAccountId);
                await operations.Open(restored, cancellation);
                // Read the owning backend again after UI hydration. The restored
                // admission guard prevents a manual send from racing this check.
                restored = await operations.PrepareAfterRestore(ticket.Checkpoint, target, cancellation);
                CheckEnabled(cancellation); CheckCurrent(ticket.TargetAccountId);
                if (!store.TryBeginSend(ticket)) throw new UserError("這次續聊已送出或結果未確認，不會重複送出。");
                string turnId = await operations.Start(restored, ticket.OperationId, cancellation);
                if (String.IsNullOrWhiteSpace(turnId) || turnId == ticket.Checkpoint.TurnId)
                    throw new UserError("續聊回覆尚未確認；不會重複送出。");
                store.Complete(ticket, turnId);
                delivered = true;
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                    await operations.Release(ticket.Checkpoint, ticket.OperationId, timeout.Token);
                SetStatus("已在原對話送出一次「繼續執行」。");
                return true;
            }
            catch
            {
                try { store.MarkNeedsAttention(ticket); } catch { }
                SetStatus(delivered ? "已送出一次「繼續執行」，但桌面控制尚待釋放；已停止自動切換，重新開啟切換器後會嘗試恢復控制。" :
                    "自動續聊結果尚未確認，已停止重送；請檢查原對話。");
                throw;
            }
        }
        public async Task CancelAsync()
        {
            if (ticket == null) return;
            var cancelling = ticket;
            try
            {
                // A pure wait never bound the desktop restore or admission.
                if (cancelling.State != ContinuationTicketState.WaitingForQuota)
                    using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                        await operations.Release(cancelling.Checkpoint, cancelling.OperationId, timeout.Token);
                if (cancelling.State == ContinuationTicketState.Continued)
                    SetStatus("已送出一次「繼續執行」並恢復桌面控制；自動監看已停止，不會重送。");
            }
            finally
            {
                if (cancelling.State == ContinuationTicketState.WaitingForQuota) store.CancelQuotaWait(cancelling);
                else store.Cancel(cancelling);
                if (Object.ReferenceEquals(ticket, cancelling)) ticket = null;
            }
        }
    }
}
