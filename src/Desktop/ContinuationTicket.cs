using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace CodexAccountSwitcher
{
    public sealed class DesktopContinuationCheckpoint
    {
        public DesktopContinuationCheckpoint() { RuntimeWorkspaceRoots = new string[0]; FrontendContextJson = ContinuationAdmission.EmptyFrontendContextJson; }
        public string SessionId { get; set; }
        public int Generation { get; set; }
        public string ThreadId { get; set; }
        public string TurnId { get; set; }
        public string Home { get; set; }
        public string UiProfile { get; set; }
        public string SqliteHome { get; set; }
        public string Cwd { get; set; }
        public string Model { get; set; }
        public string ModelProvider { get; set; }
        public string Effort { get; set; }
        public string OriginalTurnStatus { get; set; }
        public string PermissionsJson { get; set; }
        public string[] RuntimeWorkspaceRoots { get; set; }
        public string RolloutPath { get; set; }
        public string FrontendContextJson { get; set; }
    }

    public static class ContinuationTicketState
    {
        public const string WaitingForQuota = "WaitingForQuota", Captured = "Captured", Stopped = "Stopped", Quiesced = "Quiesced", AccountApplied = "AccountApplied",
            Restored = "Restored", Sending = "Sending", Continued = "Continued", NeedsAttention = "NeedsAttention", Cancelled = "Cancelled";
    }

    public sealed class ContinuationTicket
    {
        public int Version { get; set; }
        public string OperationId { get; set; }
        public string SourceAccountId { get; set; }
        public string TargetAccountId { get; set; }
        public DesktopContinuationCheckpoint Checkpoint { get; set; }
        public string State { get; set; }
        public string Message { get { return "繼續執行"; } }
        public string NewTurnId { get; set; }
        public string CreatedUtc { get; set; }
        public string UpdatedUtc { get; set; }
    }

    // The durable Sending transition is an at-most-once send attempt, not an
    // acknowledgement that a remote turn was accepted. An uncertain attempt is
    // deliberately never made retryable by restarting this tool.
    public sealed class ContinuationTicketStore
    {
        private const int MaximumClearBytes = 32768, MaximumFiles = 256;
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CodexAccountSwitcher/continuation/v1");
        private readonly string directory, storage, home, mutexName;
        private readonly Func<DateTimeOffset> utcNow;
        private static readonly string[] States = { "WaitingForQuota", "Captured", "Stopped", "Quiesced", "AccountApplied", "Restored", "Sending", "Continued", "NeedsAttention", "Cancelled" };

        public ContinuationTicketStore(string storage, string home)
            : this(storage, home, () => DateTimeOffset.UtcNow) { }

        internal ContinuationTicketStore(string storage, string home, Func<DateTimeOffset> utcNow)
        {
            if (utcNow == null) throw new ArgumentNullException("utcNow");
            this.utcNow = utcNow;
            this.home = CanonicalPath(home);
            this.storage = CanonicalPath(storage);
            directory = Path.Combine(this.storage, "integrations", "continuation");
            string owner;
            using (var identity = WindowsIdentity.GetCurrent()) owner = identity.User.Value;
            using (var sha = SHA256.Create())
                // Global scope also excludes a second GUI in another Windows login
                // session for this same user and private storage directory.
                mutexName = "Global\\CodexAccountSwitcher.Continuation." + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(owner + "\n" + directory.ToUpperInvariant()))).Replace("-", "");
        }

        public ContinuationTicket Create(string sourceAccountId, string targetAccountId, DesktopContinuationCheckpoint checkpoint)
        {
            return Locked(delegate
            {
                EnsureDirectory(); ValidateCheckpoint(checkpoint);
                RequireText(sourceAccountId, 1024); RequireText(targetAccountId, 1024);
                if (sourceAccountId == targetAccountId) throw Invalid();
                string[] files = TicketFiles();
                if (files.Length >= MaximumFiles) throw new UserError("續聊記錄已達上限，請先檢查先前操作。");
                foreach (string file in files)
                {
                    var previous = ReadCore(Path.GetFileNameWithoutExtension(file));
                    // A new operation ID cannot turn an uncertain send for the same
                    // original turn into another automatic delivery attempt.
                    if (previous.Checkpoint.ThreadId == checkpoint.ThreadId && previous.Checkpoint.TurnId == checkpoint.TurnId)
                        throw new UserError("這個回合已有續聊記錄，請先確認原對話；不會重複建立自動續聊要求。");
                }
                string now = utcNow().ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
                var ticket = new ContinuationTicket { Version = 1, OperationId = Guid.NewGuid().ToString("N"), SourceAccountId = sourceAccountId,
                    TargetAccountId = targetAccountId, Checkpoint = Copy(checkpoint), State = "Captured", CreatedUtc = now, UpdatedUtc = now };
                WriteCore(ticket, false); return ticket;
            });
        }

        public ContinuationTicket Read(string operationId)
        { return Locked(delegate { EnsureDirectory(); return ReadCore(operationId); }); }

        // Version 2 records a verified quota failure before there is a usable
        // destination. It contains no credentials and has not reserved a send.
        public ContinuationTicket CreateQuotaWait(string sourceAccountId, DesktopContinuationCheckpoint checkpoint)
        {
            return Locked(delegate
            {
                EnsureDirectory(); ValidateCheckpoint(checkpoint); RequireText(sourceAccountId, 1024);
                if (checkpoint.OriginalTurnStatus != "failed") throw Invalid();
                string[] files = TicketFiles();
                foreach (string file in files)
                {
                    var previous = ReadCore(Path.GetFileNameWithoutExtension(file));
                    if (previous.Checkpoint.ThreadId == checkpoint.ThreadId && previous.Checkpoint.TurnId == checkpoint.TurnId)
                    {
                        if (previous.Version == 2 && previous.State == ContinuationTicketState.WaitingForQuota &&
                            previous.SourceAccountId == sourceAccountId && SameCheckpoint(previous.Checkpoint, checkpoint)) return previous;
                        throw new UserError("這個回合已有續聊記錄，請先確認原對話；不會重複建立自動續聊要求。");
                    }
                    if (previous.State == ContinuationTicketState.WaitingForQuota)
                        throw new UserError("已有另一個對話正在等待額度，已停止新增自動續聊要求。");
                }
                if (files.Length >= MaximumFiles) throw new UserError("續聊記錄已達上限，請先檢查先前操作。");
                string now = utcNow().ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
                var ticket = new ContinuationTicket { Version = 2, OperationId = Guid.NewGuid().ToString("N"),
                    SourceAccountId = sourceAccountId, TargetAccountId = sourceAccountId, Checkpoint = Copy(checkpoint),
                    State = ContinuationTicketState.WaitingForQuota, CreatedUtc = now, UpdatedUtc = now };
                WriteCore(ticket, false); return ticket;
            });
        }

        // This is the only operation that can choose a waiting record's target.
        // Persist the claim under the same cross-process mutex before binding the
        // desktop restore. A crash after this point is never a resumable wait.
        public bool ClaimQuotaWait(ContinuationTicket ticket, string targetAccountId)
        {
            return Locked(delegate
            {
                EnsureDirectory(); ValidateTicket(ticket); RequireText(targetAccountId, 1024);
                if (ticket.Version != 2 || ticket.State != ContinuationTicketState.WaitingForQuota) return false;
                var current = ReadCore(ticket.OperationId);
                if (current.Version != ticket.Version || current.SourceAccountId != ticket.SourceAccountId ||
                    current.CreatedUtc != ticket.CreatedUtc || !SameCheckpoint(current.Checkpoint, ticket.Checkpoint)) throw Invalid();
                if (current.State != ContinuationTicketState.WaitingForQuota) return false;
                if (Binding(current) != Binding(ticket)) throw Invalid();
                current.TargetAccountId = targetAccountId; current.State = ContinuationTicketState.Captured;
                AdvanceTimestamp(current); WriteCore(current, true); Synchronize(ticket, current); return true;
            });
        }

        public bool CancelQuotaWait(ContinuationTicket ticket)
        {
            return Locked(delegate
            {
                EnsureDirectory(); ValidateTicket(ticket);
                if (ticket.Version != 2 || ticket.State != ContinuationTicketState.WaitingForQuota) return false;
                var current = ReadCore(ticket.OperationId);
                if (current.Version != ticket.Version || current.SourceAccountId != ticket.SourceAccountId ||
                    current.CreatedUtc != ticket.CreatedUtc || !SameCheckpoint(current.Checkpoint, ticket.Checkpoint)) throw Invalid();
                // A different GUI may have claimed this same-account recovery.
                // Cancellation of its old wait must not cancel the new owner.
                if (current.State != ContinuationTicketState.WaitingForQuota) return false;
                if (Binding(current) != Binding(ticket)) throw Invalid();
                current.State = ContinuationTicketState.Cancelled;
                AdvanceTimestamp(current); WriteCore(current, true); Synchronize(ticket, current); return true;
            });
        }

        internal static bool SameCheckpoint(DesktopContinuationCheckpoint left, DesktopContinuationCheckpoint right)
        { return left != null && right != null && Json.Encode(left) == Json.Encode(right); }

        public IList<ContinuationTicket> ReadPending()
        { return ReadRecovery().Where(ticket => ticket.State != "Continued" && ticket.State != "Cancelled").ToList(); }

        // Recovery can release an authenticated old admission barrier even when
        // delivery completed or cancellation was committed. Reading this list
        // never resets a ticket or makes another delivery eligible.
        public IList<ContinuationTicket> ReadRecovery()
        {
            return Locked<IList<ContinuationTicket>>(delegate
            {
                EnsureDirectory();
                return TicketFiles().Select(file => ReadCore(Path.GetFileNameWithoutExtension(file)))
                    .OrderBy(ticket => ticket.CreatedUtc, StringComparer.Ordinal).ToList();
            });
        }

        public void MarkState(ContinuationTicket ticket, string nextState)
        {
            Mutate(ticket, current =>
            {
                if (current.State == nextState) return;
                string next = current.State == "Captured" ? "Stopped" : current.State == "Stopped" ? "Quiesced" :
                    current.State == "Quiesced" ? "AccountApplied" : current.State == "AccountApplied" ? "Restored" : null;
                if (next != nextState) throw Invalid();
                current.State = nextState;
            });
        }

        public bool TryBeginSend(ContinuationTicket ticket)
        {
            bool claimed = false;
            Mutate(ticket, current =>
            {
                if (current.State == "Sending" || current.State == "Continued" || current.State == "NeedsAttention" || current.State == "Cancelled") return;
                if (current.State != "Restored") throw Invalid();
                current.State = "Sending"; claimed = true;
            });
            return claimed;
        }

        public void Complete(ContinuationTicket ticket, string newTurnId)
        {
            RequireText(newTurnId, 256);
            Mutate(ticket, current =>
            {
                if (current.State == "Continued" && current.NewTurnId == newTurnId) return;
                if (current.State != "Sending" || current.Checkpoint.TurnId == newTurnId) throw Invalid();
                current.NewTurnId = newTurnId; current.State = "Continued";
            });
        }

        public void MarkNeedsAttention(ContinuationTicket ticket)
        {
            Mutate(ticket, current =>
            {
                if (current.State == "Continued" || current.State == "Cancelled") return;
                current.State = "NeedsAttention";
            });
        }

        public void Cancel(ContinuationTicket ticket)
        {
            if (ticket != null && ticket.State == ContinuationTicketState.WaitingForQuota) { CancelQuotaWait(ticket); return; }
            Mutate(ticket, current =>
            {
                if (current.State == "Continued" || current.State == "Cancelled" || current.State == "NeedsAttention") return;
                current.State = current.State == "Sending" ? "NeedsAttention" : "Cancelled";
            });
        }

        private void Mutate(ContinuationTicket ticket, Action<ContinuationTicket> change)
        {
            Locked(delegate
            {
                EnsureDirectory(); ValidateTicket(ticket);
                var current = ReadCore(ticket.OperationId);
                if (Binding(current) != Binding(ticket)) throw Invalid();
                string before = Json.Encode(current);
                change(current); ValidateTicket(current);
                if (Json.Encode(current) != before)
                {
                    // Wall-clock corrections must not invalidate an already
                    // durable state transition or make its send claim retryable.
                    AdvanceTimestamp(current);
                    WriteCore(current, true);
                }
                Synchronize(ticket, current); return true;
            });
        }

        private static string Binding(ContinuationTicket ticket)
        {
            return Json.Encode(new { ticket.Version, ticket.OperationId, ticket.SourceAccountId, ticket.TargetAccountId, ticket.Checkpoint, ticket.CreatedUtc });
        }
        private static void Synchronize(ContinuationTicket destination, ContinuationTicket source)
        {
            destination.TargetAccountId = source.TargetAccountId;
            destination.State = source.State; destination.NewTurnId = source.NewTurnId; destination.UpdatedUtc = source.UpdatedUtc;
        }
        private void AdvanceTimestamp(ContinuationTicket current)
        {
            var previous = DateTimeOffset.ParseExact(current.UpdatedUtc, "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            var observed = utcNow().ToUniversalTime();
            current.UpdatedUtc = (observed < previous ? previous.ToUniversalTime() : observed).ToString("o", CultureInfo.InvariantCulture);
        }
        private static DesktopContinuationCheckpoint Copy(DesktopContinuationCheckpoint checkpoint)
        { return Json.Decode<DesktopContinuationCheckpoint>(Json.Encode(checkpoint)); }

        private T Locked<T>(Func<T> action)
        {
            bool created, acquired = false;
            var security = new MutexSecurity();
            security.AddAccessRule(new MutexAccessRule(WindowsIdentity.GetCurrent().User, MutexRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), MutexRights.FullControl, AccessControlType.Allow));
            using (var mutex = new Mutex(false, mutexName, out created, security))
            {
                try
                {
                    try { acquired = mutex.WaitOne(10000); } catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired) throw new UserError("另一個續聊操作尚未完成，已停止本次送出。");
                    return action();
                }
                finally { if (acquired) mutex.ReleaseMutex(); }
            }
        }

        private void EnsureDirectory()
        {
            // Never repair a broad or redirected existing directory silently.
            CheckAncestors(directory);
            EnsurePrivateDirectory(storage);
            EnsurePrivateDirectory(Path.Combine(storage, "integrations"));
            EnsurePrivateDirectory(directory);
        }
        private static void EnsurePrivateDirectory(string path)
        {
            bool exists = true;
            try { if ((File.GetAttributes(path) & FileAttributes.Directory) == 0) throw Invalid(); }
            catch (FileNotFoundException) { exists = false; }
            catch (DirectoryNotFoundException) { exists = false; }
            if (exists) DesktopSessionFiles.VerifyPrivate(path, false);
            else DesktopSessionFiles.CreatePrivate(path);
        }
        private static void CheckAncestors(string path)
        {
            for (string cursor = Path.GetFullPath(path); cursor != null; )
            {
                try
                {
                    var attributes = File.GetAttributes(cursor);
                    if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0) throw Invalid();
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                var parent = Directory.GetParent(cursor); cursor = parent == null ? null : parent.FullName;
            }
        }
        private string[] TicketFiles()
        {
            DesktopSessionFiles.VerifyPrivate(directory, false);
            string[] files = Directory.GetFiles(directory, "*.dpapi", SearchOption.TopDirectoryOnly);
            if (files.Length > MaximumFiles) throw new UserError("續聊記錄已達上限，請先檢查先前操作。");
            foreach (string file in files) RequireId(Path.GetFileNameWithoutExtension(file));
            return files;
        }
        private string TicketPath(string id) { RequireId(id); return Path.Combine(directory, id + ".dpapi"); }

        private ContinuationTicket ReadCore(string id)
        {
            string path = TicketPath(id), encoded = DesktopSessionFiles.ReadPrivateText(path);
            if (Encoding.UTF8.GetByteCount(encoded) > 65536) throw Invalid();
            var envelope = Json.Object(encoded);
            RequireKeys(envelope, new[] { "Version", "Ciphertext" });
            if (!IsInt(envelope["Version"]) || Convert.ToInt32(envelope["Version"], CultureInfo.InvariantCulture) != 1 || !(envelope["Ciphertext"] is string)) throw Invalid();
            byte[] protectedBytes, clear = null;
            try
            {
                protectedBytes = Convert.FromBase64String((string)envelope["Ciphertext"]);
                if (protectedBytes.Length == 0 || protectedBytes.Length > 49152) throw Invalid();
                clear = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
                if (clear.Length == 0 || clear.Length > MaximumClearBytes) throw Invalid();
                string text = new UTF8Encoding(false, true).GetString(clear);
                ValidateRawTicket(Json.Object(text));
                var ticket = Json.Decode<ContinuationTicket>(text); ValidateTicket(ticket);
                if (ticket.OperationId != id) throw Invalid();
                return ticket;
            }
            catch (UserError) { throw; }
            catch (Exception) { throw new UserError("續聊記錄無法驗證，已保留原檔且不會送出訊息。"); }
            finally { if (clear != null) Array.Clear(clear, 0, clear.Length); }
        }

        private void WriteCore(ContinuationTicket ticket, bool replacing)
        {
            ValidateTicket(ticket); string path = TicketPath(ticket.OperationId);
            if (replacing) DesktopSessionFiles.VerifyPrivate(path, true);
            else if (File.Exists(path) || Directory.Exists(path)) throw Invalid();
            byte[] clear = Encoding.UTF8.GetBytes(Json.Encode(ticket));
            try
            {
                if (clear.Length > MaximumClearBytes) throw Invalid();
                byte[] encrypted = ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser);
                byte[] envelope = Encoding.UTF8.GetBytes(Json.Encode(new { Version = 1, Ciphertext = Convert.ToBase64String(encrypted) }));
                if (envelope.Length > 65536) throw Invalid();
                DesktopSessionFiles.VerifyPrivate(directory, false);
                Files.AtomicWrite(path, envelope);
                DesktopSessionFiles.VerifyPrivate(path, true);
            }
            finally { Array.Clear(clear, 0, clear.Length); }
        }

        private void ValidateTicket(ContinuationTicket ticket)
        {
            if (ticket == null || ticket.Version != 1 && ticket.Version != 2) throw Invalid();
            RequireId(ticket.OperationId); RequireText(ticket.SourceAccountId, 1024); RequireText(ticket.TargetAccountId, 1024);
            if (!States.Contains(ticket.State) || ticket.Version == 1 &&
                (ticket.SourceAccountId == ticket.TargetAccountId || ticket.State == ContinuationTicketState.WaitingForQuota)) throw Invalid();
            if (ticket.Version == 2 && (ticket.Checkpoint == null || ticket.Checkpoint.OriginalTurnStatus != "failed" ||
                ticket.State == ContinuationTicketState.WaitingForQuota && ticket.SourceAccountId != ticket.TargetAccountId)) throw Invalid();
            ValidateCheckpoint(ticket.Checkpoint);
            DateTimeOffset created, updated;
            if (!DateTimeOffset.TryParseExact(ticket.CreatedUtc, "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out created) ||
                !DateTimeOffset.TryParseExact(ticket.UpdatedUtc, "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out updated) || updated < created) throw Invalid();
            if (ticket.State == "Continued") { RequireText(ticket.NewTurnId, 256); if (ticket.NewTurnId == ticket.Checkpoint.TurnId) throw Invalid(); }
            else if (ticket.NewTurnId != null) throw Invalid();
        }

        private void ValidateCheckpoint(DesktopContinuationCheckpoint checkpoint)
        {
            if (checkpoint == null || checkpoint.Generation <= 0) throw Invalid();
            RequireId(checkpoint.SessionId); RequireText(checkpoint.ThreadId, 256); RequireText(checkpoint.TurnId, 256);
            if (!String.Equals(CanonicalPath(checkpoint.Home), home, StringComparison.OrdinalIgnoreCase)) throw Invalid();
            CanonicalPath(checkpoint.Cwd); OptionalPath(checkpoint.UiProfile); OptionalPath(checkpoint.SqliteHome);
            ContinuationAdmission.CanonicalRolloutPath(checkpoint.RolloutPath);
            RequireText(checkpoint.Model, 256); if (checkpoint.ModelProvider != null) RequireText(checkpoint.ModelProvider, 256);
            if (checkpoint.Effort != null && !new[] { "none", "minimal", "low", "medium", "high", "xhigh", "max", "ultra" }.Contains(checkpoint.Effort)) throw Invalid();
            if (!new[] { "inProgress", "interrupted", "failed" }.Contains(checkpoint.OriginalTurnStatus)) throw Invalid();
            ValidatePermissions(checkpoint.PermissionsJson);
            ContinuationAdmission.ValidateFrontendContextJson(checkpoint.FrontendContextJson);
            if (ContinuationAdmission.MergeFrontendEnvironment(checkpoint.FrontendContextJson,
                Json.Object(checkpoint.FrontendContextJson)["environments"], checkpoint.Cwd) != ContinuationAdmission.ValidateFrontendContextJson(checkpoint.FrontendContextJson)) throw Invalid();
            if (checkpoint.RuntimeWorkspaceRoots == null || checkpoint.RuntimeWorkspaceRoots.Length > 64) throw Invalid();
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in checkpoint.RuntimeWorkspaceRoots)
                if (!roots.Add(CanonicalPath(root))) throw Invalid();
        }

        private static void ValidateRawTicket(Dictionary<string, object> raw)
        {
            RequireKeys(raw, new[] { "Version", "OperationId", "SourceAccountId", "TargetAccountId", "Checkpoint", "State", "Message", "NewTurnId", "CreatedUtc", "UpdatedUtc" });
            if (!IsInt(raw["Version"]) || !Object.Equals(raw["Message"], "繼續執行")) throw Invalid();
            foreach (string key in new[] { "OperationId", "SourceAccountId", "TargetAccountId", "State", "CreatedUtc", "UpdatedUtc" }) if (!(raw[key] is string)) throw Invalid();
            if (raw["NewTurnId"] != null && !(raw["NewTurnId"] is string)) throw Invalid();
            var cp = raw["Checkpoint"] as Dictionary<string, object>;
            RequireKeys(cp, new[] { "SessionId", "Generation", "ThreadId", "TurnId", "Home", "UiProfile", "SqliteHome", "Cwd", "Model", "ModelProvider", "Effort", "OriginalTurnStatus", "PermissionsJson", "RuntimeWorkspaceRoots", "RolloutPath", "FrontendContextJson" });
            if (!IsInt(cp["Generation"])) throw Invalid();
            foreach (string key in new[] { "SessionId", "ThreadId", "TurnId", "Home", "Cwd", "Model", "OriginalTurnStatus", "RolloutPath", "FrontendContextJson" }) if (!(cp[key] is string)) throw Invalid();
            foreach (string key in new[] { "UiProfile", "SqliteHome", "ModelProvider", "Effort", "PermissionsJson" }) if (cp[key] != null && !(cp[key] is string)) throw Invalid();
            var roots = cp["RuntimeWorkspaceRoots"] as IList;
            if (roots == null || roots.Count > 64 || roots.Cast<object>().Any(root => !(root is string))) throw Invalid();
        }

        private static void ValidatePermissions(string text)
        {
            if (text == null) return;
            if (Encoding.UTF8.GetByteCount(text) > 16384) throw Invalid();
            var raw = Json.Object(text);
            if (raw == null || raw.Keys.Any(key => !new[] { "approvalPolicy", "approvalsReviewer", "sandbox", "permissions" }.Contains(key))) throw Invalid();
            object value;
            if (raw.TryGetValue("approvalPolicy", out value) && value != null)
            {
                if (value is string) { if (!new[] { "untrusted", "on-request", "never" }.Contains((string)value)) throw Invalid(); }
                else
                {
                    var policy = value as Dictionary<string, object>; RequireKeys(policy, new[] { "granular" });
                    var granular = policy["granular"] as Dictionary<string, object>;
                    if (granular == null || granular.Keys.Any(key => !new[] { "sandbox_approval", "rules", "skill_approval", "mcp_elicitations", "request_permissions" }.Contains(key)) || granular.Values.Any(item => !(item is bool))) throw Invalid();
                }
            }
            if (raw.TryGetValue("approvalsReviewer", out value) && value != null && (!(value is string) || !new[] { "user", "auto_review", "guardian_subagent" }.Contains((string)value))) throw Invalid();
            if (raw.TryGetValue("permissions", out value) && value != null)
            {
                var profile = value as Dictionary<string, object>;
                if (profile == null || !profile.ContainsKey("id") || profile.Keys.Any(key => key != "id" && key != "extends")) throw Invalid();
                RequireText(profile["id"] as string, 256);
                if (profile.TryGetValue("extends", out value) && value != null) RequireText(value as string, 256);
            }
            if (raw.TryGetValue("sandbox", out value) && value != null)
            {
                var sandbox = value as Dictionary<string, object>;
                if (sandbox == null || !sandbox.ContainsKey("type") || sandbox.Keys.Any(key => !new[] { "type", "writableRoots", "networkAccess", "excludeSlashTmp", "excludeTmpdirEnvVar" }.Contains(key))) throw Invalid();
                string type = sandbox["type"] as string;
                if (!new[] { "readOnly", "workspaceWrite", "dangerFullAccess", "externalSandbox" }.Contains(type)) throw Invalid();
                foreach (string key in new[] { "excludeSlashTmp", "excludeTmpdirEnvVar" }) if (sandbox.TryGetValue(key, out value) && !(value is bool)) throw Invalid();
                if (sandbox.TryGetValue("networkAccess", out value) && !(value is bool) && !(type == "externalSandbox" && value is string && new[] { "restricted", "enabled" }.Contains((string)value))) throw Invalid();
                if (sandbox.TryGetValue("writableRoots", out value))
                {
                    var roots = value as IList; if (roots == null || roots.Count > 64) throw Invalid();
                    foreach (object root in roots) CanonicalPath(root as string);
                }
            }
        }

        private static string CanonicalPath(string value)
        {
            RequireText(value, 4096);
            string requestedRoot = Path.GetPathRoot(value);
            if (!Path.IsPathRooted(value) || String.IsNullOrEmpty(requestedRoot) || requestedRoot.Length < 3 || requestedRoot.EndsWith(":", StringComparison.Ordinal)) throw Invalid();
            string full = Path.GetFullPath(value), root = Path.GetPathRoot(full);
            return full.Length == root.Length ? full : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        private static void OptionalPath(string value) { if (value != null) CanonicalPath(value); }
        private static void RequireId(string value) { Guid id; if (value == null || !Guid.TryParseExact(value, "N", out id)) throw Invalid(); }
        private static void RequireText(string value, int maximum)
        { if (String.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(Char.IsControl)) throw Invalid(); }
        private static bool IsInt(object value) { return value is int; }
        private static void RequireKeys(Dictionary<string, object> raw, string[] keys)
        { if (raw == null || raw.Count != keys.Length || keys.Any(key => !raw.ContainsKey(key))) throw Invalid(); }
        private static UserError Invalid() { return new UserError("續聊記錄格式、身分或狀態不符，已停止自動送出。"); }
    }
}
