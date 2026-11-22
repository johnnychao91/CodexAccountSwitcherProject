using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CodexAccountSwitcher
{
    public static class DesktopConversationClientTests
    {
        private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        private static bool Rejected(Action operation) { try { operation(); return false; } catch { return true; } }
        private static DesktopContinuationCheckpoint Checkpoint(DesktopSessionState state)
        {
            return new DesktopContinuationCheckpoint { SessionId = state.SessionId, Generation = state.Generation,
                Home = state.Home, ThreadId = Guid.NewGuid().ToString("D"), TurnId = "original-turn", Cwd = Path.GetTempPath(),
                Model = "synthetic-model", ModelProvider = "synthetic-provider", Effort = "medium",
                PermissionsJson = Json.Encode(new { approvalPolicy = "never", approvalsReviewer = "user",
                    sandbox = new { type = "readOnly" }, permissions = (object)null }) };
        }
        private static DesktopSessionState State()
        { return new DesktopSessionState { SessionId = Guid.NewGuid().ToString("N"), Generation = 3, Home = Path.GetTempPath() }; }
        private static DesktopConversationClient Client(DesktopSessionState state, FakeTransport fake)
        { return new DesktopConversationClient(state, delegate { }, token => Task.FromResult<IDesktopConversationTransport>(fake)); }
        private static Dictionary<string, object> MapOf(object value) { return Json.Object(Json.Encode(value)); }

        public static void Register(Action<string, Action> test)
        {
            test("Continuation and Local Work independently accept their reviewed archive profiles", delegate
            {
                Func<string, byte[]> decode = hex => Enumerable.Range(0, hex.Length / 2).Select(i => Convert.ToByte(hex.Substring(i * 2, 2), 16)).ToArray();
                var legacy = decode(DesktopContinuationCompatibility.LegacyArchiveSha256);
                var updated = decode(DesktopContinuationCompatibility.UpdatedArchiveSha256);
                var current = decode(DesktopContinuationCompatibility.CurrentArchiveSha256);
                DesktopContinuationCompatibility.ValidateArchiveDigest(DesktopContinuationCompatibility.LegacyArchiveLength, legacy);
                DesktopContinuationCompatibility.ValidateArchiveDigest(DesktopContinuationCompatibility.UpdatedArchiveLength, updated);
                DesktopContinuationCompatibility.ValidateArchiveDigest(DesktopContinuationCompatibility.CurrentArchiveLength, current);
                DesktopLocalWorkPolicy.ValidateArchiveDigest(DesktopLocalWorkPolicy.UpdatedArchiveLength, updated);
                DesktopLocalWorkPolicy.ValidateArchiveDigest(DesktopLocalWorkPolicy.CurrentArchiveLength, current);
                Check(Rejected(() => DesktopContinuationCompatibility.ValidateArchiveDigest(DesktopContinuationCompatibility.CurrentArchiveLength, updated)),
                    "An older digest was accepted for the current continuation profile.");
                current[0] ^= 1;
                Check(Rejected(() => DesktopContinuationCompatibility.ValidateArchiveDigest(DesktopContinuationCompatibility.CurrentArchiveLength, current)),
                    "A modified current continuation archive was accepted.");
                Check(Rejected(() => DesktopLocalWorkPolicy.ValidateArchiveDigest(DesktopLocalWorkPolicy.CurrentArchiveLength, current)),
                    "A modified current Local Work archive was accepted.");
                Check(Rejected(() => DesktopLocalWorkPolicy.ValidateArchiveDigest(DesktopLocalWorkPolicy.UpdatedArchiveLength + 1, updated)),
                    "An unknown Local Work version was accepted.");
                Check(DesktopContinuationCompatibility.OwnerVersion == 1 && DesktopContinuationCompatibility.InterruptVersion == 4 &&
                    DesktopContinuationCompatibility.StartVersion == 2, "The reviewed IPC versions changed.");
            });
            test("Continuation compatibility refuses altered same-size archives mismatched profiles and unknown versions", delegate
            {
                var digest = Enumerable.Range(0, 32).Select(i => Convert.ToByte(DesktopContinuationCompatibility.UpdatedArchiveSha256.Substring(i * 2, 2), 16)).ToArray();
                Check(Rejected(() => DesktopContinuationCompatibility.ValidateArchiveDigest(DesktopContinuationCompatibility.LegacyArchiveLength, digest)), "A digest from another profile was accepted.");
                Check(Rejected(() => DesktopContinuationCompatibility.ValidateArchiveDigest(DesktopContinuationCompatibility.UpdatedArchiveLength + 1, digest)), "An unknown version was accepted.");
                digest[0] ^= 1;
                Check(Rejected(() => DesktopContinuationCompatibility.ValidateArchiveDigest(DesktopContinuationCompatibility.UpdatedArchiveLength, digest)), "A modified same-size archive was accepted.");
                Check(Rejected(() => DesktopContinuationCompatibility.ValidateArchiveDigest(DesktopContinuationCompatibility.UpdatedArchiveLength, null)), "A missing digest was accepted.");
            });
            test("Current package accepts the reviewed 9664 archive and rejects cross-version fingerprints", delegate
            {
                const long length = 783725118;
                const string hash = "A538C8BFA0BF6A43B06C4C62AAD93616A78C4CAD306602CC05F528844A551598";
                var digest = Enumerable.Range(0, 32).Select(i => Convert.ToByte(hash.Substring(i * 2, 2), 16)).ToArray();
                DesktopContinuationCompatibility.ValidateArchiveDigest(length, digest);
                DesktopLocalWorkPolicy.ValidateArchiveDigest(length, digest);
                Check(Rejected(() => DesktopContinuationCompatibility.ValidateArchiveDigest(DesktopContinuationCompatibility.CurrentArchiveLength, digest)),
                    "A new archive was accepted under the old size.");
                digest[0] ^= 1;
                Check(Rejected(() => DesktopContinuationCompatibility.ValidateArchiveDigest(length, digest)) &&
                    Rejected(() => DesktopLocalWorkPolicy.ValidateArchiveDigest(length, digest)), "An altered 9664 archive was accepted.");
                const string app = "F171B2B4B4011EC5CC243864B1E0FC674391EAC09C08CAD7DFBB58ABE8768003";
                const string cli = "49B0AD59A400C0812791D5F5518E9CDDB2A32E21E918801800655DBAB29382A1";
                const string oldCli = "094D4DB493DE22025712CC5963854E7654B7C97F4CEE795E3E986270A0F4F6B4";
                Check(DesktopArchiveCompatibility.ExpectedContents(app, cli) == "20E7A7AA28C5C2572DF9ECA37D3B75683EFEDFD4A14637F8462F2F0C3618F43F" &&
                    DesktopArchiveCompatibility.ExpectedContents(app, oldCli) == null, "The normalized profile allowed a mismatched CLI.");
            });
            test("Package 3838 enables Local Work protection without authorizing legacy desktop continuation", delegate
            {
                const long length = 443772048;
                const string hash = "2334574ABBF17086F34F60C228D2A32FC18DDB4044477771DB6763023299A871";
                var digest = Enumerable.Range(0, 32).Select(i => Convert.ToByte(hash.Substring(i * 2, 2), 16)).ToArray();
                DesktopLocalWorkPolicy.ValidateArchiveDigest(length, digest);
                Check(Rejected(() => DesktopContinuationCompatibility.ValidateArchiveDigest(length, digest)),
                    "The Local Work review incorrectly authorized the legacy continuation transport.");
                const string app = "AF4D57201946260D2754172BC660EFF80B5398EA146EF466126886CCD0CF2514";
                const string cli = "180CC98D8BE7B3EDC1D881875692444D8462CDC00A8209C6093BC499F0918D30";
                Check(DesktopArchiveCompatibility.ExpectedContents(app, cli, true) ==
                    "638E83CBB51AC11F198639BB88214E74CCD901635D25B507FB25498E549391D4" &&
                    DesktopArchiveCompatibility.ExpectedContents(app, cli) == null,
                    "A normalized Local Work-only archive incorrectly authorized continuation.");
            });
            test("Package 4957 keeps Local Work protection independent from desktop continuation", delegate
            {
                const long length = 443800006;
                const string hash = "8A437AAD94098770C1BF5FD5385C204E162B62C6D7917F688741D74E05BABE90";
                var digest = Enumerable.Range(0, 32).Select(i => Convert.ToByte(hash.Substring(i * 2, 2), 16)).ToArray();
                DesktopLocalWorkPolicy.ValidateArchiveDigest(length, digest);
                Check(Rejected(() => DesktopContinuationCompatibility.ValidateArchiveDigest(length, digest)),
                    "Package 4957 incorrectly authorized legacy continuation.");
                digest[0] ^= 1;
                Check(Rejected(() => DesktopLocalWorkPolicy.ValidateArchiveDigest(length, digest)), "An altered 4957 archive was accepted.");
                const string app = "21C2EAC7703B43611392234738F669A8AF23907585B231EBDE54CA3CB104CC82";
                const string cli = "4069ACDD8D101830F63EA5E2775883BFECF8A152AE87EE1B0EF9F14204F850F9";
                const string previousCli = "180CC98D8BE7B3EDC1D881875692444D8462CDC00A8209C6093BC499F0918D30";
                Check(DesktopArchiveCompatibility.ExpectedContents(app, cli, true) ==
                    "3FB5ED60A7E61CE656B6D2359151D2A981F4CE925518D4FD274AAED97258964E" &&
                    DesktopArchiveCompatibility.ExpectedContents(app, cli) == null &&
                    DesktopArchiveCompatibility.ExpectedContents(app, previousCli, true) == null,
                    "The normalized 4957 profile incorrectly accepted continuation or another CLI.");
                Check(DesktopContinuationCompatibility.CheckTarget(null) == DesktopContinuationSupport.Unverified,
                    "A missing package was treated as verified.");
            });
            test("Desktop conversation IPC frames use little-endian UTF8 lengths and tolerate partial reads", delegate
            {
                using (var bytes = new MemoryStream())
                {
                    DesktopConversationPipe.WriteFrameAsync(bytes, new { text = "繼續執行", type = "test" }, CancellationToken.None).GetAwaiter().GetResult();
                    byte[] frame = bytes.ToArray();
                    Check(BitConverter.ToUInt32(frame, 0) == frame.Length - 4 && frame[0] != 0,
                        "The native desktop IPC length header changed.");
                    using (var chunks = new PartialStream(frame))
                    {
                        var value = DesktopConversationPipe.ReadFrameAsync(chunks, CancellationToken.None).GetAwaiter().GetResult();
                        Check(Json.Str(value, "text") == "繼續執行" && chunks.Reads > 8, "Fragmented UTF8 framing lost text.");
                    }
                }
            });
            test("Desktop conversation IPC rejects oversized truncated invalid UTF8 and non-object frames", delegate
            {
                foreach (byte[] frame in new[] {
                    BitConverter.GetBytes((uint)0), BitConverter.GetBytes((uint)DesktopConversationPipe.MaxFrameBytes + 1),
                    new byte[] { 5, 0, 0, 0, 123 }, new byte[] { 2, 0, 0, 0, 0xc3, 0x28 },
                    new byte[] { 1, 0, 0, 0, 123 }, new byte[] { 2, 0, 0, 0, 91, 93 } })
                    using (var stream = new MemoryStream(frame))
                        Check(Rejected(() => DesktopConversationPipe.ReadFrameAsync(stream, CancellationToken.None).GetAwaiter().GetResult()),
                            "An unsafe IPC frame was accepted.");
            });
            test("Desktop conversation IPC only trusts a live router owned by the exact managed App", delegate
            {
                var state = State();
                state.App = new DesktopProcessIdentity { Pid = 11, ExecutablePath = Path.Combine(Path.GetTempPath(), "synthetic-app.exe") };
                state.Host = new DesktopProcessIdentity { Pid = 12, ExecutablePath = Path.Combine(Path.GetTempPath(), "synthetic-host.exe") };
                var renderer = new DesktopProcessIdentity { Pid = 13, ExecutablePath = state.App.ExecutablePath };
                var backend = new DesktopProcessIdentity { Pid = 14, ExecutablePath = Path.Combine(Path.GetTempPath(), "synthetic-backend.exe") };
                state.OwnedActors.Add(renderer); state.OwnedActors.Add(backend);
                var live = new HashSet<DesktopProcessIdentity> { state.App, state.Host, renderer, backend };
                Check(DesktopConversationPipe.IsOwnedRouter(state, 11, live.Contains) && DesktopConversationPipe.IsOwnedRouter(state, 13, live.Contains),
                    "The managed App or its owned renderer was rejected.");
                Check(!DesktopConversationPipe.IsOwnedRouter(state, 12, live.Contains) && !DesktopConversationPipe.IsOwnedRouter(state, 14, live.Contains) &&
                    !DesktopConversationPipe.IsOwnedRouter(state, 500, live.Contains), "A host, backend, or unrelated router was trusted.");
                live.Remove(state.Host);
                Check(!DesktopConversationPipe.IsOwnedRouter(state, 11, live.Contains), "A stale managed host authorized the IPC connection.");
                live.Add(state.Host); live.Remove(renderer);
                Check(!DesktopConversationPipe.IsOwnedRouter(state, 13, live.Contains), "A stale owned actor authorized the IPC connection.");
            });
            test("Desktop conversation checkpoint mismatches fail before opening a transport", delegate
            {
                var state = State(); var checkpoint = Checkpoint(state); int connections = 0;
                using (var client = new DesktopConversationClient(state, delegate { }, token =>
                    { connections++; return Task.FromResult<IDesktopConversationTransport>(new FakeTransport()); }))
                {
                    checkpoint.Generation++;
                    Check(Rejected(() => client.OpenAsync(checkpoint, CancellationToken.None).GetAwaiter().GetResult()), "A stale generation was accepted.");
                    checkpoint.Generation--; checkpoint.SessionId = Guid.NewGuid().ToString("N");
                    Check(Rejected(() => client.OpenAsync(checkpoint, CancellationToken.None).GetAwaiter().GetResult()), "An unrelated session was accepted.");
                    checkpoint.SessionId = state.SessionId; checkpoint.Home = Path.Combine(state.Home, "unrelated-profile");
                    Check(Rejected(() => client.OpenAsync(checkpoint, CancellationToken.None).GetAwaiter().GetResult()) && connections == 0,
                        "A checkpoint mismatch connected to the App.");
                }
            });
            test("Desktop conversation IPC initializes its own client and rejects ownership claims", delegate
            {
                string assigned = Guid.NewGuid().ToString("D"); int verifies = 0;
                using (var script = new RouterStream(assigned))
                using (var pipe = new DesktopConversationPipe(script, () => verifies++))
                {
                    pipe.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
                    var owner = pipe.RequestAsync("thread-owner-discovery", 1,
                        MapOf(new { hostId = "local", conversationId = Guid.NewGuid().ToString("D") }), null, CancellationToken.None).GetAwaiter().GetResult();
                    Check(script.Requests.Count == 2 && Json.Str(script.Requests[0], "sourceClientId") == "initializing-client" &&
                        Json.Str(script.Requests[1], "sourceClientId") == assigned && !script.Requests[1].ContainsKey("hostId") &&
                        Convert.ToInt32(script.Requests[1]["version"]) == 1 && verifies >= 4,
                        "IPC reused another client identity, changed the local protocol version, or skipped native identity validation.");
                    Check(script.NegativeDiscoveryReplies == 2 && script.NegativeRequestReplies == 2 && Json.Str(owner, "resultType") == "success",
                        "The automation client claimed another thread or mishandled unrelated frames.");
                }
            });
            test("Desktop conversation normally interrupts the exact turn through its freshly discovered owner", delegate
            {
                var state = State(); var checkpoint = Checkpoint(state); var fake = new FakeTransport();
                using (var client = Client(state, fake))
                {
                    client.InterruptAsync(checkpoint, CancellationToken.None).GetAwaiter().GetResult();
                    Check(fake.Discoveries == 1 && fake.Interrupts == 1 && fake.LastTarget == fake.Owner &&
                        Json.Str(fake.LastParameters, "mode") == "system" && Json.Str(fake.LastParameters, "expectedTurnId") == checkpoint.TurnId &&
                        fake.LastVersion == 4, "Normal stop did not preserve owner, system mode, or expected turn identity.");
                    Check(Rejected(() => client.InterruptAsync(checkpoint, CancellationToken.None).GetAwaiter().GetResult()) && fake.Interrupts == 1,
                        "Normal stop was resent automatically.");
                }
            });
            test("Desktop conversation continuation sends one exact message and preserves captured settings", delegate
            {
                var state = State(); var checkpoint = Checkpoint(state); var fake = new FakeTransport(); string operation = Guid.NewGuid().ToString("N");
                using (var client = Client(state, fake))
                {
                    client.OpenAsync(checkpoint, CancellationToken.None).GetAwaiter().GetResult();
                    Check(client.StartTurnAsync(checkpoint, operation, CancellationToken.None).GetAwaiter().GetResult() == "new-turn" && fake.Starts == 1,
                        "The continuation did not confirm a distinct new turn.");
                    var turn = Json.Map(fake.LastParameters, "turnStart"); var request = Json.Map(turn, "request");
                    var input = request["input"] as IList;
                    Check(input != null && input.Count == 1 && Json.Str(input[0] as Dictionary<string, object>, "text") == "繼續執行" &&
                        Json.Str(request, "threadId") == checkpoint.ThreadId && Json.Str(request, "clientUserMessageId") == operation &&
                        Json.Str(request, "model") == checkpoint.Model && Json.Str(request, "effort") == checkpoint.Effort &&
                        Json.Str(request, "cwd") == checkpoint.Cwd && Json.Str(request, "approvalPolicy") == "never" &&
                        Json.Str(request, "approvalsReviewer") == "user" && Json.Map(request, "sandboxPolicy") != null &&
                        !request.ContainsKey("permissions") && Object.Equals(Json.Map(turn, "context")["useAppServerPermissionDefault"], false) &&
                        Object.Equals(Json.Map(turn, "context")["inheritThreadSettings"], false) &&
                        Object.Equals(Json.Map(turn, "context")["writingBlockContextPrepared"], true) && fake.LastVersion == 2,
                        "Continuation text, idempotency correlation, context, or permission settings changed.");
                    Check(Rejected(() => client.StartTurnAsync(checkpoint, operation, CancellationToken.None).GetAwaiter().GetResult()) && fake.Starts == 1,
                        "The same adapter sent the continuation twice.");
                }
            });
            test("Desktop conversation permission profiles are copied by ID without weakening the captured sandbox", delegate
            {
                var checkpoint = Checkpoint(State());
                checkpoint.PermissionsJson = Json.Encode(new { approvalPolicy = "onRequest", approvalsReviewer = "user",
                    sandbox = new { type = "readOnly" }, permissions = new { id = "synthetic-profile", extends = "restricted" } });
                var request = DesktopConversationClient.CreateTurnRequest(checkpoint, Guid.NewGuid().ToString("N"));
                Check(Json.Str(request, "permissions") == "synthetic-profile" && !request.ContainsKey("sandboxPolicy") &&
                    Json.Str(request, "approvalPolicy") == "onRequest", "A named permission profile was replaced or combined with a conflicting sandbox policy.");
                checkpoint.PermissionsJson = Json.Encode(new { approvalPolicy = "never", approvalsReviewer = "user", sandbox = new { type = "readOnly" } });
                Check(Rejected(() => DesktopConversationClient.CreateTurnRequest(checkpoint, Guid.NewGuid().ToString("N"))),
                    "Missing permission-profile metadata was accepted as an unrestricted default.");
            });
            test("Desktop conversation preserves only the captured canonical local environment", delegate
            {
                var checkpoint = Checkpoint(State());
                checkpoint.FrontendContextJson = ContinuationAdmission.MergeFrontendEnvironment(
                    ContinuationAdmission.EmptyFrontendContextJson,
                    new object[] { MapOf(new { environmentId = "local", cwd = checkpoint.Cwd,
                        runtimeWorkspaceRoots = new[] { checkpoint.Cwd } }) }, checkpoint.Cwd);
                var request = DesktopConversationClient.CreateTurnRequest(checkpoint, Guid.NewGuid().ToString("N"));
                var environment = ((IList)request["environments"])[0] as Dictionary<string, object>;
                Check(Json.Str(environment, "environmentId") == "local" &&
                    String.Equals(Json.Str(environment, "cwd"), Path.GetFullPath(checkpoint.Cwd).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) &&
                    !request.ContainsKey("config") && !request.ContainsKey("developerInstructions"),
                    "Captured local environment was omitted or raw frontend context leaked into the turn.");
                checkpoint.Cwd = Path.Combine(checkpoint.Cwd, "different-fixture-directory");
                Check(Rejected(() => DesktopConversationClient.CreateTurnRequest(checkpoint, Guid.NewGuid().ToString("N"))),
                    "An environment from a different directory was reused.");
                checkpoint.FrontendContextJson = "{\"version\":1,\"config\":{},\"developerInstructions\":null,\"environments\":[{\"environmentId\":\"remote\"}]}";
                Check(Rejected(() => DesktopConversationClient.CreateTurnRequest(checkpoint, Guid.NewGuid().ToString("N"))),
                    "An unknown remote environment was accepted.");
            });
            test("Desktop conversation missing ACK and changed owner never trigger automatic mutation retries", delegate
            {
                var state = State(); var checkpoint = Checkpoint(state); var fake = new FakeTransport { LoseStartAck = true };
                using (var client = Client(state, fake))
                {
                    client.OpenAsync(checkpoint, CancellationToken.None).GetAwaiter().GetResult(); string id = Guid.NewGuid().ToString("N");
                    Check(Rejected(() => client.StartTurnAsync(checkpoint, id, CancellationToken.None).GetAwaiter().GetResult()) && fake.Starts == 1,
                        "An unconfirmed continuation was treated as acknowledged.");
                    Check(Rejected(() => client.StartTurnAsync(checkpoint, id, CancellationToken.None).GetAwaiter().GetResult()) && fake.Starts == 1,
                        "A lost continuation ACK caused a duplicate send.");
                }
                fake = new FakeTransport { WrongMutationOwner = true };
                using (var client = Client(state, fake))
                    Check(Rejected(() => client.InterruptAsync(checkpoint, CancellationToken.None).GetAwaiter().GetResult()) && fake.Interrupts == 1,
                        "A different owner's mutation response was accepted or retried.");
            });
            test("Desktop conversation may retry read-only discovery while never sending a turn during hydration", delegate
            {
                var state = State(); var checkpoint = Checkpoint(state); var fake = new FakeTransport { MissingOwners = 1 };
                using (var client = Client(state, fake))
                {
                    client.OpenAsync(checkpoint, CancellationToken.None).GetAwaiter().GetResult();
                    Check(fake.Discoveries == 2 && fake.Starts == 0 && fake.Interrupts == 0, "Hydration retried a mutation or failed read-only discovery.");
                }
                using (var client = Client(state, new FakeTransport()))
                using (var cancelled = new CancellationTokenSource())
                {
                    cancelled.Cancel();
                    Check(Rejected(() => client.OpenAsync(checkpoint, cancelled.Token).GetAwaiter().GetResult()), "Cancelled discovery proceeded.");
                }
            });
        }

        private sealed class FakeTransport : IDesktopConversationTransport
        {
            internal string Owner = Guid.NewGuid().ToString("D"), LastTarget;
            internal int Discoveries, Interrupts, Starts, LastVersion, MissingOwners;
            internal bool LoseStartAck, WrongMutationOwner;
            internal Dictionary<string, object> LastParameters;
            public Task<Dictionary<string, object>> RequestAsync(string method, int version, Dictionary<string, object> parameters, string targetClientId, CancellationToken cancellation)
            {
                cancellation.ThrowIfCancellationRequested(); LastParameters = MapOf(parameters); LastTarget = targetClientId; LastVersion = version;
                if (method == "thread-owner-discovery")
                {
                    Discoveries++;
                    if (MissingOwners-- > 0) return Task.FromResult(MapOf(new { resultType = "error", error = "no-client-found" }));
                    return Task.FromResult(MapOf(new { resultType = "success", handledByClientId = Owner, result = new { } }));
                }
                string owner = WrongMutationOwner ? Guid.NewGuid().ToString("D") : Owner;
                if (method == "thread-follower-interrupt-turn")
                { Interrupts++; return Task.FromResult(MapOf(new { resultType = "success", handledByClientId = owner, result = new { ok = true, interruptedTurnId = Json.Str(parameters, "expectedTurnId") } })); }
                if (method == "thread-follower-start-turn")
                {
                    Starts++; if (LoseStartAck) throw new IOException("Synthetic missing ACK.");
                    return Task.FromResult(MapOf(new { resultType = "success", handledByClientId = owner, result = new { result = new { turn = new { id = "new-turn" } } } }));
                }
                throw new Exception("Unexpected synthetic desktop method.");
            }
            public void Dispose() { }
        }

        private sealed class PartialStream : MemoryStream
        {
            internal int Reads;
            internal PartialStream(byte[] bytes) : base(bytes) { }
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellation)
            { Reads++; return base.ReadAsync(buffer, offset, Math.Min(count, 1), cancellation); }
        }

        // Two independent buffers emulate a duplex pipe without opening the real
        // desktop router or reading any local account/conversation data.
        private sealed class RouterStream : Stream
        {
            private readonly string assigned;
            private readonly MemoryStream write = new MemoryStream();
            private readonly Queue<byte> read = new Queue<byte>();
            internal readonly List<Dictionary<string, object>> Requests = new List<Dictionary<string, object>>();
            internal int NegativeDiscoveryReplies, NegativeRequestReplies;
            internal RouterStream(string assigned) { this.assigned = assigned; }
            public override bool CanRead { get { return true; } }
            public override bool CanWrite { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override long Length { get { throw new NotSupportedException(); } }
            public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
            private void Queue(object value)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(Json.Encode(value));
                foreach (byte b in BitConverter.GetBytes((uint)bytes.Length).Concat(bytes)) read.Enqueue(b);
            }
            public override void Flush()
            {
                byte[] bytes = write.ToArray(); write.SetLength(0);
                var message = Json.Object(Encoding.UTF8.GetString(bytes, 4, bytes.Length - 4)); string type = Json.Str(message, "type");
                if (type == "client-discovery-response")
                { if (Object.Equals(Json.Map(message, "response")["canHandle"], false)) NegativeDiscoveryReplies++; return; }
                if (type == "response")
                { if (Json.Str(message, "error") == "no-handler-for-request") NegativeRequestReplies++; return; }
                Requests.Add(message);
                Queue(new { type = "broadcast", method = "synthetic-unrelated" });
                Queue(new { type = "client-discovery-request", requestId = Guid.NewGuid().ToString("D") });
                Queue(new { type = "request", requestId = Guid.NewGuid().ToString("D"), method = "synthetic-unrelated" });
                Queue(new { type = "response", requestId = Json.Str(message, "requestId"), resultType = "success", handledByClientId = assigned,
                    result = new { clientId = assigned } });
            }
            public override Task FlushAsync(CancellationToken cancellation) { Flush(); return Task.FromResult(true); }
            public override int Read(byte[] buffer, int offset, int count)
            { int size = Math.Min(Math.Min(count, read.Count), 3); for (int index = 0; index < size; index++) buffer[offset + index] = read.Dequeue(); return size; }
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellation)
            { cancellation.ThrowIfCancellationRequested(); return Task.FromResult(Read(buffer, offset, count)); }
            public override void Write(byte[] buffer, int offset, int count) { write.Write(buffer, offset, count); }
            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellation)
            { cancellation.ThrowIfCancellationRequested(); Write(buffer, offset, count); return Task.FromResult(true); }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            protected override void Dispose(bool disposing) { if (disposing) write.Dispose(); base.Dispose(disposing); }
        }
    }
}
