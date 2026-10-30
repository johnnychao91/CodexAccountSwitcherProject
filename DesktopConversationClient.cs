using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CodexAccountSwitcher
{
    internal static class DesktopContinuationCompatibility
    {
        internal const long LegacyArchiveLength = 640129388;
        internal const string LegacyArchiveSha256 = "04862A659C6A90FC50514D8C0B8016709D97DECE58839726C57495ED72F1D9F0";
        internal const long UpdatedArchiveLength = 674296148;
        internal const string UpdatedArchiveSha256 = "6FBEDF054A8D8CAFAF5C745B40AA59176512E106E0BD6308275AE123B60FD074";
        internal const int OwnerVersion = 1, InterruptVersion = 4, StartVersion = 2;
        private const string UnsupportedVersion = "目前 Codex 版本尚未通過自動續聊相容性驗證，已停止自動中止與續聊；請更新帳號切換器。";
        internal static void ValidateTarget(DesktopPackageTarget target)
        {
            DesktopLocalWorkPolicy.ValidateArchive(target,
                length => length == LegacyArchiveLength || length == UpdatedArchiveLength,
                ValidateArchiveDigest, UnsupportedVersion);
        }
        internal static void ValidateArchiveDigest(long length, byte[] digest)
        {
            string expected = length == LegacyArchiveLength ? LegacyArchiveSha256 :
                length == UpdatedArchiveLength ? UpdatedArchiveSha256 : null;
            if (expected == null || digest == null || digest.Length != 32 ||
                !String.Equals(BitConverter.ToString(digest).Replace("-", ""), expected, StringComparison.Ordinal))
                throw new UserError(UnsupportedVersion);
        }
    }

    // The desktop thread manager owns these operations. This client
    // registers its own identity; it never adopts a renderer's existing client ID.
    public sealed class DesktopConversationClient : IDisposable
    {
        private readonly DesktopSessionState state;
        private readonly Func<CancellationToken, Task<IDesktopConversationTransport>> connect;
        private readonly Action validate;
        private IDesktopConversationTransport transport;
        private string owner;
        private bool disposed;
        private int startAttempted, interruptAttempted;

        public DesktopConversationClient(DesktopSessionState state)
        {
            this.state = state;
            bool packageValidated = false;
            validate = delegate
            {
                ValidateLiveState(state);
                if (!packageValidated)
                {
                    // Package paths are immutable versioned installation paths.
                    // Recheck live process identity on every operation, but hash
                    // this exact installed package only once per connection.
                    DesktopContinuationCompatibility.ValidateTarget(DesktopPackageTarget.Resolve(state.App.ExecutablePath));
                    packageValidated = true;
                }
            };
            connect = async token => await DesktopConversationPipe.ConnectAsync(state, token).ConfigureAwait(false);
        }
        internal DesktopConversationClient(DesktopSessionState state, Action validate,
            Func<CancellationToken, Task<IDesktopConversationTransport>> connect)
        { this.state = state; this.validate = validate; this.connect = connect; }

        private static void ValidateLiveState(DesktopSessionState state)
        {
            if (state == null || !state.ControlEnabled || !state.TransportReady || state.State != "running" || state.BackendExitCode.HasValue ||
                !DesktopNative.Same(state.Host) || !DesktopNative.Same(state.App))
                throw new UserError("桌面對話的受管連線已改變，已停止自動中止與續聊。");
        }
        internal static void ValidateCheckpoint(DesktopSessionState state, DesktopContinuationCheckpoint checkpoint)
        {
            Guid session, thread;
            if (state == null || checkpoint == null || !Guid.TryParseExact(checkpoint.SessionId, "N", out session) ||
                !Guid.TryParseExact(checkpoint.ThreadId, "D", out thread) || checkpoint.SessionId != state.SessionId || checkpoint.Generation != state.Generation ||
                String.IsNullOrWhiteSpace(checkpoint.TurnId) || !Regex.IsMatch(checkpoint.TurnId, "^[A-Za-z0-9_-]{1,128}$") ||
                !String.Equals(DesktopSessionFiles.Canonical(checkpoint.Home), DesktopSessionFiles.Canonical(state.Home), StringComparison.OrdinalIgnoreCase))
                throw new UserError("原對話與受管桌面身分不符，已停止自動續聊。");
        }
        private void Check(DesktopContinuationCheckpoint checkpoint)
        {
            if (disposed) throw new ObjectDisposedException("DesktopConversationClient");
            ValidateCheckpoint(state, checkpoint); validate();
        }
        public async Task OpenAsync(DesktopContinuationCheckpoint checkpoint, CancellationToken cancellation)
        {
            Check(checkpoint);
            if (transport == null) transport = await connect(cancellation).ConfigureAwait(false);
            // Navigation is supplied as a validated argument to the fresh managed
            // App by its Host. Never activate a global URI handler here: that can
            // open an unrelated default profile or create a second App instance.
            var clock = Stopwatch.StartNew(); owner = null;
            while (clock.Elapsed < TimeSpan.FromSeconds(30))
            {
                cancellation.ThrowIfCancellationRequested(); Check(checkpoint);
                var response = await transport.RequestAsync("thread-owner-discovery", DesktopContinuationCompatibility.OwnerVersion,
                    new Dictionary<string, object> { { "hostId", "local" }, { "conversationId", checkpoint.ThreadId } }, null, cancellation).ConfigureAwait(false);
                string resultType = Json.Str(response, "resultType");
                if (resultType == "success")
                {
                    string found = Json.Str(response, "handledByClientId"); Guid id;
                    if (!Guid.TryParseExact(found, "D", out id)) throw new UserError("無法驗證桌面對話的擁有視窗。");
                    owner = found; return;
                }
                if (Json.Str(response, "error") != "no-client-found") throw new UserError("桌面尚未接受原對話的連線，已停止自動續聊。");
                await Task.Delay(300, cancellation).ConfigureAwait(false);
            }
            throw new UserError("原對話尚未在受管 Codex 載入完成，已停止自動續聊；不會重複送出訊息。");
        }
        public async Task InterruptAsync(DesktopContinuationCheckpoint checkpoint, CancellationToken cancellation)
        {
            await OpenAsync(checkpoint, cancellation).ConfigureAwait(false);
            Check(checkpoint);
            if (Interlocked.CompareExchange(ref interruptAttempted, 1, 0) != 0) throw new UserError("此回合的中止要求已送出，請先確認原對話狀態。");
            var response = await transport.RequestAsync("thread-follower-interrupt-turn", DesktopContinuationCompatibility.InterruptVersion,
                new Dictionary<string, object> { { "hostId", "local" }, { "conversationId", checkpoint.ThreadId },
                    { "mode", "system" }, { "expectedTurnId", checkpoint.TurnId } }, owner, cancellation).ConfigureAwait(false);
            EnsureOwnerReply(response);
            var result = Json.Map(response, "result");
            if (result == null || !Object.Equals(Get(result, "ok"), true) || Json.Str(result, "interruptedTurnId") != checkpoint.TurnId)
                throw new UserError("未確認原回合已正常中止，已停止自動切換。");
        }
        public async Task<string> StartTurnAsync(DesktopContinuationCheckpoint checkpoint, string messageId, CancellationToken cancellation)
        {
            Guid operation;
            if (!Guid.TryParseExact(messageId, "N", out operation) && !Guid.TryParseExact(messageId, "D", out operation))
                throw new UserError("續聊操作識別無效，尚未送出訊息。");
            // The caller durably commits Sending before invoking this one-shot
            // operation. Neither connection failures nor missing ACKs are retried.
            Check(checkpoint);
            if (transport == null || String.IsNullOrEmpty(owner)) throw new UserError("原對話尚未完成連線，尚未送出續聊訊息。");
            if (Interlocked.CompareExchange(ref startAttempted, 1, 0) != 0) throw new UserError("此續聊要求已嘗試送出，已停止重送，請查看原對話。");
            var request = CreateTurnRequest(checkpoint, messageId);
            var response = await transport.RequestAsync("thread-follower-start-turn", DesktopContinuationCompatibility.StartVersion,
                new Dictionary<string, object> { { "hostId", "local" }, { "conversationId", checkpoint.ThreadId },
                    { "turnStart", new { request = request, context = new { useAppServerPermissionDefault = false,
                        inheritThreadSettings = false, writingBlockContextPrepared = true } } } }, owner, cancellation).ConfigureAwait(false);
            EnsureOwnerReply(response);
            string turn = Json.Str(Json.Map(Json.Map(Json.Map(response, "result"), "result"), "turn"), "id");
            if (String.IsNullOrWhiteSpace(turn) || turn == checkpoint.TurnId || !Regex.IsMatch(turn, "^[A-Za-z0-9_-]{1,128}$"))
                throw new UserError("續聊要求已送出，但尚未確認新回合；已停止重送，請查看原對話。");
            return turn;
        }
        internal static Dictionary<string, object> CreateTurnRequest(DesktopContinuationCheckpoint checkpoint, string messageId)
        {
            var request = new Dictionary<string, object> { { "threadId", checkpoint.ThreadId },
                { "input", new[] { new { type = "text", text = "繼續執行" } } }, { "clientUserMessageId", messageId } };
            if (!String.IsNullOrEmpty(checkpoint.Cwd)) request["cwd"] = checkpoint.Cwd;
            if (!String.IsNullOrEmpty(checkpoint.Model)) request["model"] = checkpoint.Model;
            if (!String.IsNullOrEmpty(checkpoint.Effort)) request["effort"] = checkpoint.Effort;
            string frontend = ContinuationAdmission.ValidateFrontendContextJson(checkpoint.FrontendContextJson);
            var frontendContext = Json.Object(frontend);
            object environments = frontendContext["environments"];
            if (ContinuationAdmission.MergeFrontendEnvironment(frontend, environments, checkpoint.Cwd) != frontend)
                throw new UserError("原對話的本機執行環境已改變，尚未送出續聊訊息。");
            var capturedEnvironments = environments as System.Collections.IList;
            if (capturedEnvironments != null && capturedEnvironments.Count != 0) request["environments"] = environments;
            if (String.IsNullOrWhiteSpace(checkpoint.PermissionsJson) || checkpoint.PermissionsJson.Length > 65536)
                throw new UserError("原對話權限尚未確認，尚未送出續聊訊息。");
            var permissions = Json.Object(checkpoint.PermissionsJson);
            if (permissions == null || !permissions.ContainsKey("approvalPolicy") || !permissions.ContainsKey("approvalsReviewer") ||
                !permissions.ContainsKey("sandbox") || !permissions.ContainsKey("permissions") || Json.Map(permissions, "sandbox") == null ||
                (permissions["permissions"] != null && Json.Map(permissions, "permissions") == null))
                throw new UserError("原對話權限資料不完整，尚未送出續聊訊息。");
            request["approvalPolicy"] = permissions["approvalPolicy"];
            request["approvalsReviewer"] = permissions["approvalsReviewer"];
            var profile = Json.Map(permissions, "permissions");
            if (profile == null) request["sandboxPolicy"] = permissions["sandbox"];
            else
            {
                string id = Json.Str(profile, "id");
                if (String.IsNullOrWhiteSpace(id) || id.Length > 256) throw new UserError("原對話的權限設定檔無法確認，尚未送出續聊訊息。");
                request["permissions"] = id;
            }
            return request;
        }
        private void EnsureOwnerReply(Dictionary<string, object> response)
        {
            if (Json.Str(response, "resultType") != "success" || Json.Str(response, "handledByClientId") != owner)
                throw new UserError("桌面對話的擁有視窗已改變或未接受要求，已停止自動重送。");
        }
        private static object Get(Dictionary<string, object> value, string key)
        { object found; return value != null && value.TryGetValue(key, out found) ? found : null; }
        public void Dispose()
        { disposed = true; if (transport != null) transport.Dispose(); transport = null; owner = null; }
    }

    internal interface IDesktopConversationTransport : IDisposable
    {
        Task<Dictionary<string, object>> RequestAsync(string method, int version, Dictionary<string, object> parameters,
            string targetClientId, CancellationToken cancellation);
    }

    internal sealed class DesktopConversationPipe : IDesktopConversationTransport
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint pid);
        internal const int MaxFrameBytes = 1048576;
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
        private readonly Stream stream;
        private readonly Action verify;
        private readonly SemaphoreSlim flight = new SemaphoreSlim(1, 1);
        private string clientId = "initializing-client";
        private bool disposed;
        internal DesktopConversationPipe(Stream stream, Action verify) { this.stream = stream; this.verify = verify; }
        internal static bool IsOwnedRouter(DesktopSessionState state, int pid, Func<DesktopProcessIdentity, bool> same)
        {
            if (state == null || state.App == null || state.Host == null || state.OwnedActors == null || same == null ||
                !same(state.Host) || !same(state.App)) return false;
            return new[] { state.App }.Concat(state.OwnedActors).Any(actor => actor != null && actor.Pid == pid &&
                String.Equals(actor.ExecutablePath, state.App.ExecutablePath, StringComparison.OrdinalIgnoreCase) && same(actor));
        }
        internal static async Task<IDesktopConversationTransport> ConnectAsync(DesktopSessionState state, CancellationToken cancellation)
        {
            var pipe = new NamedPipeClientStream(".", "codex-ipc", PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(3000, cancellation).ConfigureAwait(false);
                Action verify = delegate
                {
                    uint pid;
                    if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out pid) || pid > Int32.MaxValue ||
                        !IsOwnedRouter(state, (int)pid, DesktopNative.Same))
                        throw new UserError("桌面通訊服務不屬於目前受管 Codex，已停止自動中止與續聊。");
                };
                verify();
                var result = new DesktopConversationPipe(pipe, verify);
                await result.InitializeAsync(cancellation).ConfigureAwait(false);
                return result;
            }
            catch { pipe.Dispose(); throw; }
        }
        internal async Task InitializeAsync(CancellationToken cancellation)
        {
            var response = await RequestAsync("initialize", 0, new Dictionary<string, object> { { "clientType", "account-switcher" } }, null, cancellation).ConfigureAwait(false);
            string id = Json.Str(Json.Map(response, "result"), "clientId"); Guid parsed;
            if (Json.Str(response, "resultType") != "success" || !Guid.TryParseExact(id, "D", out parsed) || Json.Str(response, "handledByClientId") != id)
                throw new UserError("無法建立獨立桌面通訊身分。");
            clientId = id;
        }
        public async Task<Dictionary<string, object>> RequestAsync(string method, int version, Dictionary<string, object> parameters,
            string targetClientId, CancellationToken cancellation)
        {
            if (disposed) throw new ObjectDisposedException("DesktopConversationPipe");
            if (method != "initialize" && clientId == "initializing-client") throw new IOException("Desktop IPC initialization is required.");
            await flight.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                {
                    deadline.CancelAfter(TimeSpan.FromSeconds(method == "thread-follower-interrupt-turn" ? 30 : 15));
                    using (deadline.Token.Register(() => { try { stream.Dispose(); } catch { } }))
                    {
                        try
                        {
                            verify(); string id = Guid.NewGuid().ToString("D");
                            var request = new Dictionary<string, object> { { "type", "request" }, { "requestId", id }, { "sourceClientId", clientId },
                                { "method", method }, { "version", version }, { "params", parameters }, { "timeoutMs", method == "thread-follower-interrupt-turn" ? 25000 : 12000 } };
                            if (targetClientId != null) request["targetClientId"] = targetClientId;
                            // For local follower methods, omit top-level hostId. The
                            // installed protocol adds a distinct version for it.
                            await WriteFrameAsync(stream, request, deadline.Token).ConfigureAwait(false);
                            for (int index = 0; index < 128; index++)
                            {
                                var message = await ReadFrameAsync(stream, deadline.Token).ConfigureAwait(false);
                                string type = Json.Str(message, "type");
                                if (type == "response" && Json.Str(message, "requestId") == id)
                                { verify(); return message; }
                                if (type == "client-discovery-request")
                                    await WriteFrameAsync(stream, new { type = "client-discovery-response", requestId = Json.Str(message, "requestId"), response = new { canHandle = false } }, deadline.Token).ConfigureAwait(false);
                                else if (type == "request")
                                    await WriteFrameAsync(stream, new { type = "response", requestId = Json.Str(message, "requestId"), resultType = "error", error = "no-handler-for-request" }, deadline.Token).ConfigureAwait(false);
                            }
                            throw new IOException("Desktop IPC response bound exceeded.");
                        }
                        catch { deadline.Token.ThrowIfCancellationRequested(); throw; }
                    }
                }
            }
            finally { flight.Release(); }
        }
        internal static async Task WriteFrameAsync(Stream stream, object value, CancellationToken cancellation)
        {
            byte[] bytes = Utf8.GetBytes(Json.Encode(value));
            if (bytes.Length == 0 || bytes.Length > MaxFrameBytes) throw new IOException("Desktop IPC frame exceeds its bound.");
            byte[] length = BitConverter.GetBytes((uint)bytes.Length);
            if (!BitConverter.IsLittleEndian) Array.Reverse(length);
            await stream.WriteAsync(length, 0, length.Length, cancellation).ConfigureAwait(false);
            await stream.WriteAsync(bytes, 0, bytes.Length, cancellation).ConfigureAwait(false);
            await stream.FlushAsync(cancellation).ConfigureAwait(false);
        }
        internal static async Task<Dictionary<string, object>> ReadFrameAsync(Stream stream, CancellationToken cancellation)
        {
            byte[] length = new byte[4]; await ReadExactlyAsync(stream, length, cancellation).ConfigureAwait(false);
            if (!BitConverter.IsLittleEndian) Array.Reverse(length);
            uint count = BitConverter.ToUInt32(length, 0);
            if (count == 0 || count > MaxFrameBytes) throw new IOException("Desktop IPC frame exceeds its bound.");
            byte[] bytes = new byte[(int)count]; await ReadExactlyAsync(stream, bytes, cancellation).ConfigureAwait(false);
            Dictionary<string, object> result;
            try { result = Json.Object(Utf8.GetString(bytes)); }
            catch (ArgumentException) { throw new IOException("Invalid desktop IPC message."); }
            catch (InvalidOperationException) { throw new IOException("Invalid desktop IPC message."); }
            if (result == null) throw new IOException("Invalid desktop IPC message.");
            return result;
        }
        private static async Task ReadExactlyAsync(Stream stream, byte[] bytes, CancellationToken cancellation)
        {
            int offset = 0;
            while (offset < bytes.Length)
            {
                int count = await stream.ReadAsync(bytes, offset, bytes.Length - offset, cancellation).ConfigureAwait(false);
                if (count == 0) throw new EndOfStreamException("Desktop IPC closed before a complete frame.");
                offset += count;
            }
        }
        public void Dispose() { disposed = true; stream.Dispose(); }
    }
}
