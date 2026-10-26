using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CodexAccountSwitcher
{
    public enum AutoSwitchState { Disabled, Monitoring, CheckingCurrent, CheckingCandidate, WaitingForQuota, WaitingForExit, Rechecking, Switching, Stopping, Faulted }

    public sealed class AutoSwitchHost
    {
        public Func<Account> ReadCurrent { get; set; }
        public Func<IList<Account>> ReadAccounts { get; set; }
        // The host binds the response to this account and finishes owned-process cleanup before completing its Task.
        public Func<Account, CancellationToken, Task<AccountUsage>> QueryUsage { get; set; }
        public Func<CancellationToken, Task> WaitForRuntimeExit { get; set; }
        public Func<CancellationToken, Task> ConfirmRuntimeQuiet { get; set; }
        public Action<Account, string> Apply { get; set; }
        public Func<Task> Launch { get; set; }
        public Func<Task> RestoreRuntimes { get; set; }
        public Func<Account, Account, Func<CancellationToken, Task>, CancellationToken, Task> PrepareContinuation { get; set; }
        public Func<Account, CancellationToken, Task<bool>> ContinueAfterSwitch { get; set; }
        public Func<Task> CancelContinuation { get; set; }
        public Func<string> ReadQuotaWaitSource { get; set; }
        public Func<Account, CancellationToken, Task<bool>> ObserveQuotaWait { get; set; }
        public Func<Account, Account, Func<CancellationToken, Task>, CancellationToken, Task> PrepareQuotaContinuation { get; set; }
        public Action<Account> RetainCurrentAccount { get; set; }
        public Func<Task> CancelQuotaWait { get; set; }
        public Func<DateTimeOffset> UtcNow { get; set; }
    }

    // The UI owns the opt-in setting and timer. This class never reads files or stops processes.
    public sealed class AutoSwitchController
    {
        private static readonly TimeSpan MaxUsageAge = TimeSpan.FromMinutes(1);
        private readonly AutoSwitchHost host;
        private readonly object gate = new object();
        private bool enabled;
        private bool running;
        private int stopRequests;
        private Task flight;
        private CancellationTokenSource cancellation;
        public bool Enabled { get { lock (gate) return enabled; } }
        public bool IsRunning { get { lock (gate) return running; } }
        public AutoSwitchState State { get; private set; }
        public string CurrentAccountId { get; private set; }
        public string CandidateAccountId { get; private set; }
        public string StatusMessage { get; private set; }
        public event Action Changed;

        public AutoSwitchController(AutoSwitchHost host)
        {
            if (host == null || host.ReadCurrent == null || host.ReadAccounts == null || host.QueryUsage == null
                || host.WaitForRuntimeExit == null || host.ConfirmRuntimeQuiet == null || host.Apply == null || host.Launch == null)
                throw new ArgumentException("The automatic switch host is incomplete.", "host");
            this.host = host;
            State = AutoSwitchState.Disabled;
            StatusMessage = "自動切換已停用。";
        }

        public void Enable()
        {
            lock (gate)
            {
                if (stopRequests > 0 || flight != null && !flight.IsCompleted) throw new UserError("請等待目前操作停止後，再啟用自動切換。");
                enabled = true;
            }
            SetState(AutoSwitchState.Monitoring, "自動切換已啟用，等待檢查目前帳號。", null, null);
        }

        public Task TickAsync()
        {
            lock (gate)
            {
                if (!enabled) return Task.FromResult(0);
                if (flight != null && !flight.IsCompleted) return flight;
                cancellation = new CancellationTokenSource();
                running = true;
                flight = RunAsync(cancellation);
                return flight;
            }
        }

        public async Task StopAsync(bool preserveUnclaimedQuotaWait = false)
        {
            Task pending;
            CancellationTokenSource source;
            lock (gate) { enabled = false; stopRequests++; pending = flight; source = cancellation; }
            try
            {
                if (pending != null && !pending.IsCompleted)
                    SetState(AutoSwitchState.Stopping, "正在停止自動切換並完成清理…", CurrentAccountId, CandidateAccountId);
                bool cancellationFailed = false;
                if (source != null)
                {
                    try { source.Cancel(); }
                    catch (ObjectDisposedException) { }
                    catch (Exception) { cancellationFailed = true; }
                }
                if (pending != null) await pending;
                if (!preserveUnclaimedQuotaWait && host.CancelQuotaWait != null)
                {
                    try { await host.CancelQuotaWait(); }
                    catch { cancellationFailed = true; }
                }
                if (cancellationFailed) SetState(AutoSwitchState.Faulted, "取消處理發生錯誤，自動切換已停用。請確認相關程式狀態。", null, null);
                else if (State != AutoSwitchState.Faulted) SetState(AutoSwitchState.Disabled, "自動切換已停用。", null, null);
            }
            finally { lock (gate) stopRequests--; }
        }

        private sealed class Attempt
        {
            public bool WaitStarted, Committed, LaunchCompleted, ContinuationStarted, ContinuationCompleted, QuotaRecovery;
            public string SourceId, CandidateId;
        }

        private async Task RunAsync(CancellationTokenSource source)
        {
            // Publish the flight before invoking host code, including hosts that complete synchronously.
            await Task.Yield();
            var attempt = new Attempt();
            string outcome = "等待下次檢查目前帳號。";
            bool faulted = false;
            try { outcome = await RunAttemptAsync(attempt, source.Token); }
            catch (OperationCanceledException)
            { lock (gate) enabled = false; outcome = "自動切換已取消。"; }
            catch (Exception)
            { lock (gate) enabled = false; faulted = true; outcome = attempt.Committed && attempt.LaunchCompleted ?
                "Codex 已重開，但自動續聊尚未確認；已停止自動送出，請檢查原對話。" :
                "自動切換未完成，已停用。請確認登入與相關程式後手動重新啟用。"; }
            // Keep asynchronous recovery after the handled operation rather than in a finally
            // block: the bundled .NET Framework compiler supports C# 5.
            try
            {
                if (attempt.WaitStarted && (!attempt.Committed || !attempt.LaunchCompleted) && host.RestoreRuntimes != null)
                {
                    try { await host.RestoreRuntimes(); }
                    catch (Exception)
                    {
                        lock (gate) enabled = false;
                        faulted = true;
                        outcome = "相關程式未能恢復，自動切換已停用。請手動開啟程式並確認帳號。";
                    }
                }
                if (attempt.ContinuationStarted && !attempt.ContinuationCompleted && host.CancelContinuation != null)
                {
                    try { await host.CancelContinuation(); }
                    catch { lock (gate) enabled = false; faulted = true; outcome = "自動續聊狀態尚未確認，已停止自動送出；請檢查原對話。"; }
                }
            }
            finally
            {
                lock (gate)
                {
                    if (ReferenceEquals(cancellation, source)) cancellation = null;
                    running = false;
                    source.Dispose();
                }
            }
            SetState(faulted ? AutoSwitchState.Faulted : Enabled ?
                (QuotaWaitSource() == null ? AutoSwitchState.Monitoring : AutoSwitchState.WaitingForQuota) : AutoSwitchState.Disabled,
                outcome, attempt.Committed ? attempt.CandidateId : attempt.SourceId, null);
        }

        private async Task<string> RunAttemptAsync(Attempt attempt, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Account current = ReadCurrent();
            var ordered = ReadAccounts();
            string waitingSource = QuotaWaitSource();
            if (waitingSource != null && (!ValidAccount(current) || current.Id != waitingSource || Find(ordered, waitingSource) == null))
            {
                if (host.CancelQuotaWait != null) await host.CancelQuotaWait();
                return "目前登入或已儲存帳號已變更，已取消等待額度續跑。";
            }
            if (!ValidAccount(current) || Find(ordered, current.Id) == null)
                return "目前登入不是已儲存帳號，等待下次檢查。";
            attempt.QuotaRecovery = waitingSource != null;
            attempt.SourceId = current.Id;
            SetState(AutoSwitchState.CheckingCurrent, "正在檢查目前帳號額度…", current.Id, null);
            AccountUsage currentUsage = await QueryAsync(current, token);
            if (!StillCurrent(current.Id)) return "目前登入已變更，等待下次重新檢查。";
            if (attempt.QuotaRecovery && Availability(currentUsage) == UsageAvailability.Unknown)
                return "等待額度重置：目前帳號額度尚未確認，稍後重新查詢。";
            if (!attempt.QuotaRecovery && Availability(currentUsage) != UsageAvailability.Exhausted)
                return Availability(currentUsage) == UsageAvailability.Available ? "目前帳號仍有額度，繼續使用。" : "目前帳號額度未知，等待下次檢查。";

            Account candidate = null;
            AccountUsage candidateUsage = null;
            foreach (var account in ordered)
            {
                token.ThrowIfCancellationRequested();
                if (!ValidAccount(account)) continue;
                if (account.Id == current.Id)
                {
                    if (!attempt.QuotaRecovery) continue;
                    if (Availability(currentUsage) == UsageAvailability.Available) { candidate = current; candidateUsage = currentUsage; }
                    // Waiting work may use this account or one above it, never a lower priority.
                    break;
                }
                if (!StillCurrent(current.Id) || !SameCandidates(ordered, ReadAccounts(), current.Id))
                    return "登入或帳號順序已變更，等待下次重新檢查。";
                SetState(AutoSwitchState.CheckingCandidate, "正在依優先順序檢查候選帳號…", current.Id, account.Id);
                var usage = await QueryAsync(account, token);
                if (Availability(usage) == UsageAvailability.Available) { candidate = account; candidateUsage = usage; break; }
            }
            if (candidate == null)
            {
                if (!StillCurrent(current.Id) || !SameCandidates(ordered, ReadAccounts(), current.Id))
                    return "登入或帳號順序已變更，等待下次重新檢查。";
                if (host.ObserveQuotaWait != null && (attempt.QuotaRecovery || Availability(currentUsage) == UsageAvailability.Exhausted))
                {
                    bool waiting = await host.ObserveQuotaWait(current, token);
                    token.ThrowIfCancellationRequested();
                    if (waiting) return "等待額度重置：原對話因額度用完而中止；每 60 秒檢查較高順位及目前帳號，恢復後續跑。";
                }
                return "目前沒有已確認可用的其他帳號，等待下次檢查。";
            }
            if (!StillCurrent(current.Id) || !SameCandidates(ordered, ReadAccounts(), current.Id))
                return "登入或帳號順序已變更，等待下次重新檢查。";
            if (!UsableSource(currentUsage, attempt.QuotaRecovery) || Availability(candidateUsage) != UsageAvailability.Available)
                return "額度資料已過期，等待下次重新檢查。";

            attempt.CandidateId = candidate.Id;
            if (attempt.QuotaRecovery)
            {
                if (host.PrepareQuotaContinuation == null || host.RetainCurrentAccount == null || host.ContinueAfterSwitch == null)
                    throw new UserError("等待額度續跑尚未完整連線，已停止自動送出。");
                attempt.ContinuationStarted = true;
                SetState(AutoSwitchState.WaitingForExit, "額度已恢復，正在確認等待中的原對話…", current.Id, candidate.Id);
                await host.PrepareQuotaContinuation(current, candidate,
                    recheckToken => RevalidateBeforeInterruptAsync(attempt.SourceId, attempt.CandidateId, ordered, recheckToken, true), token);
                token.ThrowIfCancellationRequested();
            }
            else if (host.PrepareContinuation != null)
            {
                attempt.ContinuationStarted = true;
                SetState(AutoSwitchState.WaitingForExit, "已找到可用帳號，正在準備原對話的正常中止與續聊…", current.Id, candidate.Id);
                await host.PrepareContinuation(current, candidate,
                    recheckToken => RevalidateBeforeInterruptAsync(attempt.SourceId, attempt.CandidateId, ordered, recheckToken), token);
                token.ThrowIfCancellationRequested();
            }
            SetState(AutoSwitchState.WaitingForExit, "已找到可用帳號，等待相關程式正常結束…", current.Id, candidate.Id);
            token.ThrowIfCancellationRequested();
            attempt.WaitStarted = true;
            await host.WaitForRuntimeExit(token);
            token.ThrowIfCancellationRequested();

            current = ReadCurrent();
            if (!ValidAccount(current) || current.Id != attempt.SourceId || Find(ReadAccounts(), current.Id) == null)
                return "目前登入已變更，未套用候選帳號。";
            if (!SameCandidates(ordered, ReadAccounts(), current.Id)) return "候選帳號或順序已變更，等待下次重新檢查。";
            SetState(AutoSwitchState.Rechecking, "正在再次確認目前與候選帳號額度…", current.Id, candidate.Id);
            // Use the active account's latest token after shutdown; same-identity rotation is normal.
            currentUsage = await QueryAsync(current, token);
            if (!StillCurrent(current.Id) || !UsableSource(currentUsage, attempt.QuotaRecovery))
                return "目前帳號已恢復額度或狀態改變，未套用候選帳號。";
            var latest = ReadAccounts();
            if (!SameCandidates(ordered, latest, current.Id)) return "候選帳號或順序已變更，等待下次重新檢查。";
            candidate = Find(latest, candidate.Id);
            candidateUsage = candidate.Id == current.Id ? currentUsage : await QueryAsync(candidate, token);
            if (Availability(candidateUsage) != UsageAvailability.Available)
                return "候選帳號已無可確認的額度，未變更登入，等待下次檢查。";

            // Both QueryUsage tasks have completed cleanup before this final quiet-period check.
            await host.ConfirmRuntimeQuiet(token);
            token.ThrowIfCancellationRequested();
            if (!StillCurrent(current.Id) || !SameCandidates(ordered, ReadAccounts(), current.Id))
                return "登入或候選帳號已變更，未套用候選帳號。";
            if (!UsableSource(currentUsage, attempt.QuotaRecovery) || Availability(candidateUsage) != UsageAvailability.Available)
                return "額度資料已過期或已到重設時間，未變更登入。";

            SetState(AutoSwitchState.Switching, candidate.Id == current.Id ? "目前帳號已恢復額度，正在重開原對話…" : "正在套用候選帳號並恢復相關程式…", current.Id, candidate.Id);
            lock (gate)
            {
                token.ThrowIfCancellationRequested();
                if (!enabled) throw new OperationCanceledException(token);
                // State observers may have initiated a manual change or cancellation.
                if (!StillCurrent(current.Id) || !SameCandidates(ordered, ReadAccounts(), current.Id))
                    return "登入或候選帳號已變更，未套用候選帳號。";
                if (!UsableSource(currentUsage, attempt.QuotaRecovery) || Availability(candidateUsage) != UsageAvailability.Available)
                    return "額度資料已過期或已到重設時間，未變更登入。";
                // No await between the final check and commit. The host checks expected identity atomically with its write.
                if (candidate.Id == attempt.SourceId) host.RetainCurrentAccount(candidate);
                else host.Apply(candidate, attempt.SourceId);
                attempt.Committed = true;
            }
            // A stop after commit must still await runtime recovery. Do not pass the cancelled token.
            await host.Launch();
            attempt.LaunchCompleted = true;
            if (host.ContinueAfterSwitch != null)
            {
                if (token.IsCancellationRequested || !Enabled) throw new OperationCanceledException(token);
                bool continued = await host.ContinueAfterSwitch(candidate, token);
                attempt.ContinuationCompleted = true;
                if (continued) return candidate.Id == attempt.SourceId ?
                    "目前帳號額度已恢復，已保留帳號並在原對話送出一次「繼續執行」。" :
                    "已切換至可用帳號，並在原對話送出「繼續執行」；繼續監看目前帳號。";
            }
            return "已切換至下一個可用帳號，繼續監看目前帳號。";
        }

        private async Task RevalidateBeforeInterruptAsync(string sourceId, string candidateId, IList<Account> ordered, CancellationToken token, bool quotaRecovery = false)
        {
            token.ThrowIfCancellationRequested();
            Account source = ReadCurrent(); var latest = ReadAccounts();
            if (!ValidAccount(source) || source.Id != sourceId || Find(latest, sourceId) == null || !SameCandidates(ordered, latest, sourceId))
                throw ChangedContinuationPrecondition();
            // Another runtime may have delayed the handoff for several minutes.
            // Use the live source token, then query the still-ordered candidate;
            // all lookups finish cleanup before the workflow may send Stop.
            var sourceUsage = await QueryAsync(source, token);
            token.ThrowIfCancellationRequested(); latest = ReadAccounts();
            if (!StillCurrent(sourceId) || !SameCandidates(ordered, latest, sourceId) || !UsableSource(sourceUsage, quotaRecovery))
                throw ChangedContinuationPrecondition();
            Account candidate = Find(latest, candidateId);
            if (!ValidAccount(candidate)) throw ChangedContinuationPrecondition();
            var candidateUsage = sourceId == candidateId ? sourceUsage : await QueryAsync(candidate, token);
            token.ThrowIfCancellationRequested();
            if (!StillCurrent(sourceId) || !SameCandidates(ordered, ReadAccounts(), sourceId) ||
                !UsableSource(sourceUsage, quotaRecovery) || Availability(candidateUsage) != UsageAvailability.Available)
                throw ChangedContinuationPrecondition();
        }
        private static UserError ChangedContinuationPrecondition()
        { return new UserError("額度、登入或帳號順序已變更，尚未中止原對話或套用帳號；已停止自動切換。"); }

        private async Task<AccountUsage> QueryAsync(Account account, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                AccountUsage usage = await host.QueryUsage(account, token);
                token.ThrowIfCancellationRequested();
                return usage;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { token.ThrowIfCancellationRequested(); return null; }
        }

        private UsageAvailability Availability(AccountUsage usage)
        { return usage == null ? UsageAvailability.Unknown : usage.GetAvailability(Now(), MaxUsageAge); }
        private bool UsableSource(AccountUsage usage, bool quotaRecovery)
        { return quotaRecovery ? Availability(usage) != UsageAvailability.Unknown : Availability(usage) == UsageAvailability.Exhausted; }
        private string QuotaWaitSource() { return host.ReadQuotaWaitSource == null ? null : host.ReadQuotaWaitSource(); }
        private DateTimeOffset Now() { return host.UtcNow == null ? DateTimeOffset.UtcNow : host.UtcNow(); }
        private Account ReadCurrent()
        { try { return Copy(host.ReadCurrent()); } catch { return null; } }
        private bool StillCurrent(string id)
        { Account current = ReadCurrent(); return ValidAccount(current) && current.Id == id && Find(ReadAccounts(), id) != null; }
        private List<Account> ReadAccounts()
        {
            var copy = new List<Account>();
            var accounts = host.ReadAccounts();
            if (accounts != null) foreach (var account in accounts) if (account != null) copy.Add(Copy(account));
            return copy;
        }
        private static bool ValidAccount(Account account)
        { return account != null && !String.IsNullOrWhiteSpace(account.Id) && !String.IsNullOrWhiteSpace(account.Auth); }
        private static Account Find(IList<Account> accounts, string id)
        { foreach (var account in accounts) if (account.Id == id) return account; return null; }
        private static bool SameCandidates(IList<Account> before, IList<Account> after, string sourceId)
        {
            if (before.Count != after.Count) return false;
            for (int index = 0; index < before.Count; index++)
                if (before[index].Id != after[index].Id || before[index].Id != sourceId && before[index].Auth != after[index].Auth) return false;
            return true;
        }
        private static Account Copy(Account account)
        {
            return account == null ? null : new Account { Id = account.Id, Auth = account.Auth, Workspace = account.Workspace,
                Plan = account.Plan, Email = account.Email, Label = account.Label, SavedAt = account.SavedAt, PendingLogin = account.PendingLogin };
        }
        private void SetState(AutoSwitchState state, string message, string currentId, string candidateId)
        {
            State = state; StatusMessage = message; CurrentAccountId = currentId; CandidateAccountId = candidateId;
            var changed = Changed;
            if (changed != null) { try { changed(); } catch { } }
        }
    }
}
