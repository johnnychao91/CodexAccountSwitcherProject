using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CodexAccountSwitcher
{
    public static class DesktopSessionTests
    {
        private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        private static bool Rejected(Action operation) { try { operation(); return false; } catch { return true; } }
        private sealed class NavigationBrokerFixture : IDisposable
        {
            private readonly NamedPipeServerStream pipe;
            private readonly Task serving;
            internal readonly string Path;
            internal readonly ConcurrentQueue<Dictionary<string, object>> Requests = new ConcurrentQueue<Dictionary<string, object>>();
            internal int Calls;
            internal NavigationBrokerFixture(bool ready, bool loseAcknowledgement, object acknowledgement = null)
            {
                string name = "CodexAccountSwitcher-navigation-" + Guid.NewGuid().ToString("N"); Path = @"\\.\pipe\" + name;
                pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                serving = Task.Run(() =>
                {
                    pipe.WaitForConnection(); var list = DesktopNative.ReadAppBrokerFrame(pipe); Requests.Enqueue(list);
                    Check(Json.Str(list, "method") == "tools/list" && Json.Str(Json.Map(list, "params"), "threadStartKind") == "default", "The readiness probe invoked an unexpected broker operation.");
                    DesktopNative.WriteAppBrokerFrame(pipe, new { jsonrpc = "2.0", id = 1, result = new { tools = ready ? new[] { new { name = "navigate_to_codex_page", @namespace = "codex_app" } } : new object[0] } });
                    if (!ready) return;
                    Dictionary<string, object> request;
                    try { request = DesktopNative.ReadAppBrokerFrame(pipe); } catch (IOException) { return; }
                    Requests.Enqueue(request); Interlocked.Increment(ref Calls);
                    if (loseAcknowledgement) { pipe.ReadByte(); return; }
                    DesktopNative.WriteAppBrokerFrame(pipe, acknowledgement ?? new { jsonrpc = "2.0", id = 2, result = new {
                        success = true, contentItems = new[] { new { type = "inputText", text = "{\"navigated\":true}" } } } });
                });
            }
            public void Dispose()
            {
                Check(serving.Wait(5000), "The synthetic navigation broker did not finish within its bound."); pipe.Dispose();
            }
        }
        private static void RegisterMemberGroupTests(Action<string, Action> test)
        {
            test("Desktop continuation group rejects frozen requests without blocking later callbacks on admission", delegate
            {
                var transaction = new DesktopSessionTransaction(); int replies = 0;
                using (var held = new ManualResetEventSlim(false))
                using (var release = new ManualResetEventSlim(false))
                {
                    var quiesce = Task.Run(() => { lock (transaction.Admission) { transaction.Frozen = true; held.Set(); release.Wait(); } });
                    Check(held.Wait(2000), "The synthetic quiesce did not retain its admission barrier.");
                    try
                    {
                        var request = new Dictionary<string, object> { { "id", "ordinary-read" }, { "method", "config/read" } };
                        var rejected = Task.Run(() => !DesktopRuntimeHost.EnterFrontendAdmission(transaction, request,
                            id => { Check(Object.Equals(id, "ordinary-read"), "The frozen reply changed request identity."); Interlocked.Increment(ref replies); }));
                        Check(rejected.Wait(1000) && rejected.Result && replies == 1, "A frozen request waited for the long transaction lock before rejection.");
                        Check(!DesktopRuntimeHost.RejectFrozenFrontend(transaction, new Dictionary<string, object> { { "id", "backend-callback" }, { "result", new { } } }, id => replies++),
                            "An existing backend callback was treated as new frozen work.");
                        Check(DesktopRuntimeHost.RejectFrozenFrontend(transaction, new Dictionary<string, object> { { "method", "initialized" } }, id => replies++) && replies == 1,
                            "A method-only notification invented a reply or passed the frozen barrier.");
                    }
                    finally { release.Set(); Check(quiesce.Wait(2000), "The synthetic admission owner did not finish."); }
                    transaction.Frozen = false;
                    Check(!DesktopRuntimeHost.RejectFrozenFrontend(transaction, new Dictionary<string, object> { { "id", 1 }, { "method", "config/read" } }, id => replies++),
                        "Unfrozen work was rejected before its normal locked admission check.");
                }
            });
            test("Desktop continuation group observes freezing while waiting for admission without entering the long lock", delegate
            {
                var transaction = new DesktopSessionTransaction(); int replies = 0;
                using (var held = new ManualResetEventSlim(false))
                using (var release = new ManualResetEventSlim(false))
                {
                    var owner = Task.Run(() => { lock (transaction.Admission) { held.Set(); release.Wait(); } });
                    Check(held.Wait(2000), "The synthetic owner did not hold admission.");
                    try
                    {
                        var request = new Dictionary<string, object> { { "id", "queued-read" }, { "method", "config/read" } };
                        var waiting = Task.Run(() => {
                            bool admitted = DesktopRuntimeHost.EnterFrontendAdmission(transaction, request, id => Interlocked.Increment(ref replies));
                            if (admitted) Monitor.Exit(transaction.Admission);
                            return admitted;
                        });
                        Check(!waiting.Wait(100), "The queued request entered another owner's admission lock.");
                        transaction.Frozen = true;
                        Check(waiting.Wait(1000) && !waiting.Result && replies == 1, "A request already waiting for admission blocked the callback reader after freezing.");
                    }
                    finally { release.Set(); Check(owner.Wait(2000), "The synthetic admission owner did not finish."); }
                }
            });
            test("Desktop continuation group keeps the same rollout binding across local DOS and verbatim metadata", delegate
            {
                var original = new DesktopContinuationCheckpoint { SessionId = Guid.NewGuid().ToString("N"), Generation = 1,
                    ThreadId = Guid.NewGuid().ToString("D"), TurnId = Guid.NewGuid().ToString("D"), Home = Path.GetTempPath(), Cwd = Path.GetTempPath(),
                    RuntimeWorkspaceRoots = new string[0], RolloutPath = Path.Combine(Path.GetTempPath(), "same-fixture-rollout.jsonl") };
                var fresh = Json.Decode<DesktopContinuationCheckpoint>(Json.Encode(original));
                fresh.RolloutPath = new string('\\', 2) + "?\\" + fresh.RolloutPath;
                Check(DesktopRuntimeHost.SameContinuation(original, fresh), "A restored equivalent namespace changed the original rollout binding.");
                fresh.RolloutPath = Path.Combine(Path.GetTempPath(), "different-fixture-rollout.jsonl");
                Check(!DesktopRuntimeHost.SameContinuation(original, fresh), "Another rollout file inherited a retired continuation binding.");
            });
            test("Desktop continuation group navigates only the bound original chat after positive broker readiness", delegate
            {
                long now = 0;
                var app = DesktopNative.Identity(Process.GetCurrentProcess().Id); var claims = new DesktopContinuationNavigationClaims(() => now);
                string operation = Guid.NewGuid().ToString("N"), thread = Guid.NewGuid().ToString("D"), turn = Guid.NewGuid().ToString("D");
                int readinessClaims = 0;
                using (var unready = new NavigationBrokerFixture(false, false))
                    Check(!DesktopNative.NavigateOriginalConversation(unready.Path, app, app.ExecutablePath, thread, turn,
                        token => { readinessClaims++; return claims.TryClaim(operation, app); }) && readinessClaims == 0 && unready.Calls == 0,
                        "An unready broker consumed the navigation attempt or invoked a tool.");
                using (var ready = new NavigationBrokerFixture(true, false))
                {
                    Check(DesktopNative.NavigateOriginalConversation(ready.Path, app, app.ExecutablePath, thread, turn,
                        token => claims.TryClaim(operation, app)) && ready.Calls == 1, "The verified broker did not acknowledge the bound navigation.");
                    var request = ready.Requests.Last(); var parameters = Json.Map(request, "params"); var arguments = Json.Map(parameters, "arguments");
                    Check(Json.Str(request, "method") == "tools/call" && Json.Str(parameters, "tool") == "navigate_to_codex_page" && Json.Str(parameters, "namespace") == "codex_app" &&
                        Json.Str(parameters, "threadId") == thread && Json.Str(parameters, "turnId") == turn && Json.Str(parameters, "hostId") == "local" && Json.Str(parameters, "callerSource") == "codex" &&
                        arguments.Count == 1 && Json.Str(arguments, "threadId") == thread && !Json.Encode(request).Contains("繼續執行"), "Navigation changed the original binding or sent a conversation message.");
                }
                Check(claims.WasAttempted(operation, app) && !claims.CanAttempt(operation, app) && !claims.TryClaim(operation, app), "A group generation change bypassed the route retry interval.");
                now = 1999; Check(!claims.TryClaim(operation, app), "A route retry was admitted before the monotonic interval.");
                now = 2000;
                using (var late = new NavigationBrokerFixture(true, false))
                    Check(DesktopNative.NavigateOriginalConversation(late.Path, app, app.ExecutablePath, thread, turn,
                        token => claims.TryClaim(operation, app)) && late.Calls == 1, "The same bound route could not be redelivered after its early listener was absent.");
                var target = new Account { Id = "synthetic-target", Email = "target@example.com" };
                DesktopRuntimeHost.RequireContinuationTarget(target, target.Id, target.Email);
                Check(Rejected(() => DesktopRuntimeHost.RequireContinuationTarget(target, "different-target", target.Email)) &&
                    Rejected(() => DesktopRuntimeHost.RequireContinuationTarget(target, target.Id, "different@example.com")) &&
                    Rejected(() => DesktopRuntimeHost.RequireContinuationTarget(target, target.Id, target.Email.ToUpperInvariant())), "A different target identity authorized original-chat navigation.");
            });
            test("Desktop continuation group bounds navigation retries by monotonic interval operation and App birth", delegate
            {
                long now = 1000; var claims = new DesktopContinuationNavigationClaims(() => now);
                var app = DesktopNative.Identity(Process.GetCurrentProcess().Id); string operation = Guid.NewGuid().ToString("N");
                for (int count = 0; count < 8; count++)
                {
                    Check(claims.CanAttempt(operation, app) && claims.TryClaim(operation, app), "An allowed bounded route attempt was rejected.");
                    Check(!claims.TryClaim(operation, app), "Two route attempts shared one monotonic interval.");
                    now += 2000;
                }
                now += 60000;
                Check(!claims.CanAttempt(operation, app) && !claims.TryClaim(operation, app), "A route retry exceeded the eight-attempt cap.");
                Check(claims.TryClaim(Guid.NewGuid().ToString("N"), app), "A separate operation inherited another operation's retry budget.");
                var laterApp = new DesktopProcessIdentity { Pid = app.Pid, ExecutablePath = app.ExecutablePath,
                    StartedUtc = DateTimeOffset.Parse(app.StartedUtc).AddTicks(1).ToString("o") };
                Check(claims.TryClaim(operation, laterApp), "A new App birth inherited an old process's retry budget.");
                now -= 10000;
                Check(!claims.CanAttempt(operation, laterApp), "A backwards test clock bypassed the monotonic interval.");
            });
            test("Desktop continuation group never retries navigation after its write acknowledgement is lost", delegate
            {
                long now = 0;
                var app = DesktopNative.Identity(Process.GetCurrentProcess().Id); var claims = new DesktopContinuationNavigationClaims(() => now);
                string operation = Guid.NewGuid().ToString("N"), thread = Guid.NewGuid().ToString("D"), turn = Guid.NewGuid().ToString("D");
                using (var lost = new NavigationBrokerFixture(true, true))
                    Check(Rejected(() => { try { DesktopNative.NavigateOriginalConversation(lost.Path, app, app.ExecutablePath, thread, turn,
                        token => claims.TryClaim(operation, app), 1000); } catch { claims.Retire(operation, app); throw; } }) && lost.Calls == 1 && claims.WasAttempted(operation, app), "A lost acknowledgement did not retire the written navigation.");
                now = 10000;
                Check(Rejected(() => claims.CanAttempt(operation, app)) && Rejected(() => claims.TryClaim(operation, app)), "A failed navigation was retried after its interval elapsed.");
            });
            test("Desktop continuation group requires the exact inner navigation acknowledgement", delegate
            {
                Func<object, Dictionary<string, object>> response = content => ManagedBridge.Parse(Json.Encode(new { id = 2, result = new { success = true, contentItems = content } }));
                Check(DesktopNative.AcknowledgedOriginalNavigation(response(new[] { new { type = "inputText", text = "{\"navigated\":true}" } })), "The pinned successful route acknowledgement was rejected.");
                foreach (object invalid in new object[] { null, new object[0], new[] { new { type = "text", text = "{\"navigated\":true}" } },
                    new[] { new { type = "inputText", text = "{\"navigated\":false}" } }, new[] { new { type = "inputText", text = "{\"navigated\":true,\"extra\":true}" } },
                    new[] { new { type = "inputText", text = "not JSON" } }, new[] { new { type = "inputText", text = "{\"navigated\":true}" }, new { type = "inputText", text = "extra" } } })
                    Check(!DesktopNative.AcknowledgedOriginalNavigation(response(invalid)), "A malformed or unrelated inner result was represented as navigation success.");
                var app = DesktopNative.Identity(Process.GetCurrentProcess().Id);
                using (var falseAck = new NavigationBrokerFixture(true, false, new { id = 2, result = new { success = true } }))
                    Check(Rejected(() => DesktopNative.NavigateOriginalConversation(falseAck.Path, app, app.ExecutablePath,
                        Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), token => true)) && falseAck.Calls == 1,
                        "The native route path accepted a wrapper success without the fixed tool acknowledgement.");
            });
            test("Desktop continuation group keeps its relay endpoint private and preserves ordinary App transport selection", delegate
            {
                var start = new ProcessStartInfo();
                start.EnvironmentVariables["CODEX_APP_SERVER_WS_URL"] = "ws://unrelated.invalid/rpc";
                start.EnvironmentVariables["CODEX_APP_SERVER_FORCE_CLI"] = "1";
                start.EnvironmentVariables["CAS_DESKTOP_RELAY_ENDPOINT"] = "ws://old-private.invalid/rpc";
                start.EnvironmentVariables["CAS_TEST_UNRELATED"] = "preserved";
                string relay = Path.Combine(Path.GetTempPath(), "fixture-owned-DesktopAppRelay.exe");
                var endpoint = new Uri("ws://switcher:synthetic-private-credential@127.0.0.1:12345/rpc");
                DesktopRuntimeHost.ConfigureRelayTransport(start, endpoint, relay);
                Check(start.EnvironmentVariables["CODEX_APP_SERVER_WS_URL"] == null && start.EnvironmentVariables["CODEX_APP_SERVER_FORCE_CLI"] == null &&
                    start.EnvironmentVariables["CAS_DESKTOP_RELAY_ENDPOINT"] == endpoint.AbsoluteUri && start.EnvironmentVariables["CODEX_CLI_PATH"] == relay &&
                    start.EnvironmentVariables["CAS_TEST_UNRELATED"] == "preserved", "The managed relay globally overrode hosted/SSH App routing or inherited a stale public endpoint.");
                Check(DesktopSessionFiles.EphemeralEnvironment("CAS_DESKTOP_RELAY_ENDPOINT"), "The private relay endpoint escaped foreign-session environment cleanup.");
            });
            test("Desktop continuation group requires a verified chat broker only for captured App-tool capabilities", delegate
            {
                string empty = ContinuationAdmission.ProjectFrontendContext(new Dictionary<string, object>(), null);
                DesktopRuntimeHost.RequireContinuationBroker(empty, false);
                var tools = new Dictionary<string, object> { { "plugins.codex-app-tools@openai-bundled.mcp_servers.codex_app.enabled_tools", new[] { "read_thread", "send_message_to_thread" } } };
                string captured = ContinuationAdmission.ProjectFrontendContext(new Dictionary<string, object> { { "config", tools } }, null);
                Check(Rejected(() => DesktopRuntimeHost.RequireContinuationBroker(captured, false)), "A null or unverified chat broker was represented as ready for captured App tools.");
                DesktopRuntimeHost.RequireContinuationBroker(captured, true);
                tools["plugins.codex-app-tools@openai-bundled.mcp_servers.codex_app.enabled_tools"] = new string[0];
                DesktopRuntimeHost.RequireContinuationBroker(ContinuationAdmission.ProjectFrontendContext(new Dictionary<string, object> { { "config", tools } }, null), false);
                Check(Rejected(() => DesktopRuntimeHost.RequireContinuationBroker("{}", true)), "A verified broker allowed malformed capability authority.");
            });
            test("Desktop continuation group accepts only an exact bounded relay greeting before any RPC", delegate
            {
                string path = @"\\.\pipe\codex-app-tools-" + Guid.NewGuid().ToString("D");
                Check(DesktopRuntimeHost.ParseRelayGreeting(ManagedBridge.Encode(new { type = "switcherDesktopRelay", version = 1, appToolsPipePath = path })) == path &&
                    DesktopRuntimeHost.ParseRelayGreeting(ManagedBridge.Encode(new { type = "switcherDesktopRelay", version = 1, appToolsPipePath = (string)null })) == null,
                    "The exact normal relay greeting was rejected.");
                foreach (object malformed in new object[] { new { id = 1, method = "initialize", @params = new { } }, new { type = "switcherDesktopRelay", version = 2, appToolsPipePath = path },
                    new { type = "switcherDesktopRelay", version = 1, appToolsPipePath = path, extra = "forbidden" }, new { type = "switcherDesktopRelay", version = 1 },
                    new { type = "switcherDesktopRelay", version = 1, appToolsPipePath = 123 } })
                    Check(Rejected(() => DesktopRuntimeHost.ParseRelayGreeting(ManagedBridge.Encode(malformed))), "An old RPC or malformed greeting created a backend.");
                foreach (string invalid in new[] { new string('\\', 2) + @"server\pipe\remote", @"\\.\pipe\nested\broker", @"\\.\pipe\nested/broker", @"relative", @"\\.\pipe\", @"\\.\pipe\..", @"\\.\pipe\" + new string('a', 129), @"\\.\pipe\" + "非ASCII", @"\\.\pipe\bad" + '\0' })
                    Check(Rejected(() => DesktopRuntimeHost.ParseRelayGreeting(ManagedBridge.Encode(new { type = "switcherDesktopRelay", version = 1, appToolsPipePath = invalid }))), "A non-local or unbounded pipe name was accepted.");
                Check(Rejected(() => DesktopRuntimeHost.ParseRelayGreeting(new string(' ', 2049))), "An oversized greeting reached JSON decoding.");
            });
            test("Desktop continuation group proves native broker ownership without sending a tool RPC", delegate
            {
                var own = DesktopNative.Identity(Process.GetCurrentProcess().Id); Check(own != null, "Synthetic App identity was unavailable.");
                string name = "codex-app-tools-test-" + Guid.NewGuid().ToString("N"), path = @"\\.\pipe\" + name;
                using (var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
                {
                    var connected = Task.Run(() => pipe.WaitForConnection());
                    var verified = DesktopNative.VerifyAppToolsPipe(path, own, own.ExecutablePath);
                    Check(DesktopNative.Equal(verified, own) && connected.Wait(2000), "The native server did not belong to the exact live synthetic App.");
                    var eof = Task.Run(() => pipe.ReadByte());
                    Check(eof.Wait(2000) && eof.Result == -1, "The ownership probe sent protocol or tool bytes to the App broker.");
                }
                var stale = Json.Decode<DesktopProcessIdentity>(Json.Encode(own)); stale.StartedUtc = DateTime.UtcNow.AddDays(-5).ToString("o");
                Check(Rejected(() => DesktopNative.VerifyAppToolsPipe(path, stale, own.ExecutablePath, 100)), "A stale App creation identity authorized a broker.");
                var parent = DesktopNative.Identity(own.ParentPid);
                Check(parent != null && !DesktopNative.Equal(parent, own), "The synthetic unowned server fixture lacked a separate live owner.");
                string foreignName = "codex-app-tools-unowned-" + Guid.NewGuid().ToString("N");
                using (var pipe = new NamedPipeServerStream(foreignName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
                {
                    var connected = Task.Run(() => pipe.WaitForConnection());
                    Check(Rejected(() => DesktopNative.VerifyAppToolsPipe(@"\\.\pipe\" + foreignName, parent, parent.ExecutablePath)) && connected.Wait(2000),
                        "An unrelated process's pipe was represented as the managed App broker.");
                }
            });
            test("Desktop continuation group derives backend runtime environment only from its verified package", delegate
            {
                string root = Path.Combine(Path.GetTempPath(), "CodexAccountSwitcher-runtime-env-" + Guid.NewGuid().ToString("N"));
                string resources = Path.Combine(root, "resources"), node = Path.Combine(resources, "cua_node", "bin", "node.exe"), cli = Path.Combine(resources, "codex.exe");
                Directory.CreateDirectory(Path.GetDirectoryName(node)); File.WriteAllBytes(node, new byte[] { 0 }); File.WriteAllBytes(cli, new byte[] { 0 });
                try
                {
                    string path = @"\\.\pipe\codex-app-tools-" + Guid.NewGuid().ToString("D");
                    var environment = DesktopRuntimeHost.ManagedBackendEnvironment(new DesktopPackageTarget { CliExecutable = cli }, path);
                    Check(environment.Count == 6 && environment["CODEX_APP_TOOLS_PIPE_PATH"] == path && environment["CODEX_CLI_PATH"] == cli &&
                        environment["CODEX_MCP_NODE_PATH"] == node && environment["CODEX_BROWSER_USE_NODE_PATH"] == node && environment["CODEX_ELECTRON_RESOURCES_PATH"] == resources,
                        "Arbitrary caller environment replaced a verified bundled runtime.");
                    Check(environment["CODEX_INTERNAL_ORIGINATOR_OVERRIDE"] == "Codex Desktop", "The backend did not preserve the pinned App's fixed desktop originator.");
                    var early = DesktopRuntimeHost.ManagedBackendEnvironment(new DesktopPackageTarget { CliExecutable = cli }, null);
                    Check(early.ContainsKey("CODEX_APP_TOOLS_PIPE_PATH") && early["CODEX_APP_TOOLS_PIPE_PATH"] == null, "An early member could inherit an old App broker.");
                }
                finally
                {
                    Check(String.Equals(Path.GetDirectoryName(Path.GetFullPath(root)), Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase),
                        "The synthetic runtime cleanup escaped its intended temporary directory.");
                    Directory.Delete(root, true);
                }
            });
            test("Desktop continuation group preserves only pinned App backend startup choices", delegate
            {
                var expected = new[] { "-c", "features.code_mode_host=true", "app-server", "--stdio", "--analytics-default-enabled", "-c",
                    "plugins.codex-app-tools@openai-bundled.mcp_servers.codex_app.enabled=true" };
                var arguments = DesktopRuntimeHost.ManagedBackendArguments();
                Check(arguments.SequenceEqual(expected), "The managed CLI omitted a pinned App runtime choice or accepted an unverified option.");
                arguments[0] = "changed-caller-array";
                Check(DesktopRuntimeHost.ManagedBackendArguments().SequenceEqual(expected), "A caller mutated future backend startup choices.");
            });
            test("Desktop continuation group verifies disabled App commands against the exact live primary identity", delegate
            {
                var primary = DesktopNative.Identity(Process.GetCurrentProcess().Id);
                Check(primary != null && DesktopRuntimeHost.VerifyDisabledAppCommand(primary.ExecutablePath.Replace('\\', '/'), primary, primary.ExecutablePath),
                    "The exact live App executable was not recognized for a disabled built-in command.");
                Check(!DesktopRuntimeHost.VerifyDisabledAppCommand("relative-app.exe", primary, primary.ExecutablePath) &&
                    !DesktopRuntimeHost.VerifyDisabledAppCommand(Path.Combine(Path.GetTempPath(), "different-app.exe"), primary, primary.ExecutablePath) &&
                    !DesktopRuntimeHost.VerifyDisabledAppCommand(primary.ExecutablePath, null, primary.ExecutablePath) &&
                    !DesktopRuntimeHost.VerifyDisabledAppCommand(primary.ExecutablePath, primary, Path.Combine(Path.GetTempPath(), "different-app.exe")),
                    "A relative, different, missing or unverified App identity authorized its command.");
                var changed = Json.Decode<DesktopProcessIdentity>(Json.Encode(primary)); changed.StartedUtc = DateTime.UtcNow.AddDays(-10).ToString("o");
                Check(!DesktopRuntimeHost.VerifyDisabledAppCommand(primary.ExecutablePath, changed, primary.ExecutablePath), "A stale process creation identity authorized its command.");
            });
            test("Desktop continuation group waits only for positively read-only transient peer requests", delegate
            {
                var status = new Dictionary<string, object> { { "known", true }, { "activeTurns", 0 }, { "backgroundTerminals", 0 }, { "pendingRequests", 1 } };
                Check(DesktopRuntimeHost.MayWaitForReadOnlyPeer(status, true), "A known idle helper's ordinary UI query could not be waited for.");
                Check(!DesktopRuntimeHost.MayWaitForReadOnlyPeer(status, false), "An unknown pending method received a read-only wait exemption.");
                foreach (string unsafeKey in new[] { "activeTurns", "backgroundTerminals" })
                {
                    status[unsafeKey] = 1;
                    Check(!DesktopRuntimeHost.MayWaitForReadOnlyPeer(status, true), "Busy peer work received a read-only wait exemption.");
                    status[unsafeKey] = 0;
                }
                status["known"] = false;
                Check(!DesktopRuntimeHost.MayWaitForReadOnlyPeer(status, true), "Unknown work received a read-only wait exemption.");
                status["known"] = true; status["pendingRequests"] = 0;
                Check(!DesktopRuntimeHost.MayWaitForReadOnlyPeer(status, true), "An idle peer was mistaken for a pending query.");
            });
            test("Desktop member group prepares every backend before EOF and closes all before its App", delegate
            {
                var first = new Member(); var account = new Member(); var h = new Harness(first, account);
                Check(h.Session.Generation == 2 && h.Session.Members.Count == 2 && h.Close(), "Independent owned connections did not form a quiescible group.");
                Check(first.PrepareCalls == 1 && account.PrepareCalls == 1 && first.CommitCalls == 1 && account.CommitCalls == 1 && h.Shutdowns == 1 && h.Session.Stage == "quiesced",
                    "A member was omitted from the account-switch shutdown proof.");
            });
            test("Desktop member group shares one bounded normal runtime shutdown budget", delegate
            {
                long now = 1000;
                var first = new BoundedMember(); var second = new BoundedMember(); var h = new Harness(first, second);
                h.Session.MonotonicMilliseconds = () => now;
                first.OnBoundedCommit = (allowed, budget) => {
                    Check(first.PrepareCalls == 1 && second.PrepareCalls == 1 && budget == DesktopSessionTransaction.GracefulShutdownBudgetMs,
                        "A runtime was committed before the entire group prepared or without its initial shared budget.");
                    now += 60000; return first.Commit();
                };
                second.OnBoundedCommit = (allowed, budget) => {
                    Check(budget == 30000 && allowed(), "A later member received a new rather than remaining shutdown budget.");
                    second.CommitCalls++; now += 30001;
                    Check(!allowed(), "The group deadline was not observed during confirmation.");
                    return new Dictionary<string, object> { { "known", false }, { "idle", false }, { "eofCommitted", true } };
                };
                Check(!h.Close() && first.CommitCalls == 1 && second.CommitCalls == 1 && !second.Gone && h.Shutdowns == 0 &&
                    h.Session.Stage == "recoveryRequired" && h.Session.Frozen, "Unconfirmed runtime cleanup exceeded the shared deadline or authorized App/auth shutdown.");
            });
            test("Desktop member group deadline before EOF unprepares without closing anything", delegate
            {
                long now = 0; var member = new Member(); var h = new Harness(member);
                h.Session.MonotonicMilliseconds = () => now;
                member.OnPrepare = () => now = DesktopSessionTransaction.GracefulShutdownBudgetMs;
                Check(!h.Close() && member.CommitCalls == 0 && member.UnprepareCalls == 1 && h.Shutdowns == 0 && !h.Session.Frozen && !h.Session.EofCommitted,
                    "An expired preparation budget sent EOF or retained an unnecessary shutdown barrier.");
            });
            test("Desktop member group does not close its App after cancellation during runtime confirmation", delegate
            {
                var member = new BoundedMember(); var h = new Harness(member);
                member.OnBoundedCommit = (allowed, budget) => {
                    Check(allowed(), "The initial confirmed caller was rejected.");
                    member.Commit(); h.Connected = false;
                    Check(!allowed(), "Caller cancellation was ignored by runtime confirmation.");
                    return member.Status();
                };
                Check(!h.Close() && member.Gone && member.CommitCalls == 1 && h.Shutdowns == 0 && h.Session.Frozen && h.Session.Stage == "recoveryRequired",
                    "Positive backend exit0 overrode cancellation and closed its App.");
            });
            foreach (string unsafePeer in new[] { "unknown", "pending", "background", "active" })
            {
                string captured = unsafePeer;
                test("Desktop member group blocks " + captured + " account helper before any backend EOF", delegate
                {
                    var first = new Member(); var helper = new Member();
                    if (captured == "unknown") helper.Known = false; else if (captured == "pending") helper.Pending = 1;
                    else if (captured == "background") helper.Background = 1; else helper.Turns = 1;
                    var h = new Harness(first, helper);
                    Check(!h.Close() && first.CommitCalls == 0 && helper.CommitCalls == 0 && h.Shutdowns == 0 && !first.Gone && !helper.Gone,
                        "The first idle connection concealed unsafe work in another owned backend.");
                });
            }
            test("Desktop continuation group requires a unique positive frontend chat owner", delegate
            {
                var first = new Member(); var account = new Member(); var group = new[] { first, account };
                Check(DesktopRuntimeHost.UniqueContinuationOwner(group, member => false) == null, "A first or last backend was guessed to own the chat.");
                Check(DesktopRuntimeHost.UniqueContinuationOwner(group, member => member == first) == first, "A unique positive chat owner was not selected.");
                Check(Rejected(() => DesktopRuntimeHost.UniqueContinuationOwner(group, member => true)), "Two chat-bearing members were silently disambiguated.");
            });
            test("Desktop group live connection bound and frozen admission preserve existing members", delegate
            {
                var h = new Harness(); var admitted = new List<Member>();
                for (int i = 0; i < 16; i++) { var member = new Member(); Check(h.Session.Admit(member), "A bounded owned group member was rejected."); admitted.Add(member); }
                Check(!h.Session.Admit(new Member()) && h.Session.Members.Count == 16, "An unbounded family of backends was admitted.");
                admitted[0].Gone = true; Check(h.Session.Admit(new Member()) && h.Session.Generation == 17, "A positive retired member did not release a bounded slot.");
                h.Session.Frozen = true; Check(!h.Session.Admit(new Member()), "Frozen group admission accepted a late connection.");
            });
            test("Desktop Local Work uses only its verified independent CLI and preserves the explicit block policy", delegate
            {
                string verified = Path.Combine(Path.GetTempPath(), "fixture-verified-codex.exe");
                var start = new ProcessStartInfo(); start.EnvironmentVariables.Remove("CODEX_TPP_LOCAL_EXECUTOR_CLI_PATH");
                DesktopRuntimeHost.ConfigureIndependentLocalWorkCli(start, verified, false);
                Check(start.EnvironmentVariables["CODEX_TPP_LOCAL_EXECUTOR_CLI_PATH"] == verified, "Local Work inherited the App JSON-RPC relay.");
                string same = verified.Replace('\\', '/'); start.EnvironmentVariables["CODEX_TPP_LOCAL_EXECUTOR_CLI_PATH"] = same;
                DesktopRuntimeHost.ConfigureIndependentLocalWorkCli(start, verified, false);
                Check(start.EnvironmentVariables["CODEX_TPP_LOCAL_EXECUTOR_CLI_PATH"] == same, "A matching explicit user override was rewritten.");
                string custom = Path.Combine(Path.GetTempPath(), "fixture-external-runtime.exe"); start.EnvironmentVariables["CODEX_TPP_LOCAL_EXECUTOR_CLI_PATH"] = custom;
                Check(Rejected(() => DesktopRuntimeHost.ConfigureIndependentLocalWorkCli(start, verified, false)) &&
                    start.EnvironmentVariables["CODEX_TPP_LOCAL_EXECUTOR_CLI_PATH"] == custom, "An unsupported external runtime was overwritten or represented as controlled.");
                DesktopRuntimeHost.ConfigureIndependentLocalWorkCli(start, verified, true);
                Check(start.EnvironmentVariables["CODEX_TPP_LOCAL_EXECUTOR_CLI_PATH"] == custom, "The helper changed the dedicated block policy's refusal path.");
            });
        }
        private static void RegisterContinuationRetirementTests(Action<string, Action> test)
        {
            test("Desktop continuation release retries are immutable idempotent and never clear a newer reservation", delegate
            {
                string policy = "{\"approvalPolicy\":\"never\",\"approvalsReviewer\":\"user\",\"sandbox\":{\"type\":\"readOnly\"},\"permissions\":null}";
                string operation = Guid.NewGuid().ToString("N"), nextOperation = Guid.NewGuid().ToString("N");
                var checkpoint = new DesktopContinuationCheckpoint { SessionId = Guid.NewGuid().ToString("N"), Generation = 1,
                    ThreadId = Guid.NewGuid().ToString("D"), TurnId = "original-turn", Home = Path.GetTempPath(), Cwd = Path.GetTempPath(),
                    Model = "fixture-model", ModelProvider = "fixture", Effort = "medium", PermissionsJson = policy, OriginalTurnStatus = "inProgress" };
                checkpoint.RuntimeWorkspaceRoots = new[] { Path.GetTempPath(), Path.Combine(Path.GetTempPath(), "fixture-visualization") };
                checkpoint.RolloutPath = Path.Combine(Path.GetTempPath(), "fixture-original-rollout.jsonl");
                var exact = Json.Decode<DesktopContinuationCheckpoint>(Json.Encode(checkpoint));
                exact.RuntimeWorkspaceRoots = exact.RuntimeWorkspaceRoots.Reverse().ToArray();
                Check(DesktopRuntimeHost.SameContinuation(exact, checkpoint) && !Object.ReferenceEquals(exact.RuntimeWorkspaceRoots, checkpoint.RuntimeWorkspaceRoots),
                    "The checkpoint clone shared mutable roots or ignored canonical set ordering.");
                var changedRoots = Json.Decode<DesktopContinuationCheckpoint>(Json.Encode(checkpoint));
                changedRoots.RuntimeWorkspaceRoots = new[] { Path.Combine(Path.GetTempPath(), "unapproved-runtime-root") };
                Check(!DesktopRuntimeHost.SameContinuation(changedRoots, checkpoint), "Changed runtime roots preserved checkpoint authority.");
                var changedRollout = Json.Decode<DesktopContinuationCheckpoint>(Json.Encode(checkpoint));
                changedRollout.RolloutPath = Path.Combine(Path.GetTempPath(), "other-fixture-rollout.jsonl");
                Check(!DesktopRuntimeHost.SameContinuation(changedRollout, checkpoint), "A different rollout file preserved checkpoint authority.");
                var changedFrontend = Json.Decode<DesktopContinuationCheckpoint>(Json.Encode(checkpoint));
                changedFrontend.FrontendContextJson = ContinuationAdmission.ProjectFrontendContext(new Dictionary<string, object> { { "developerInstructions", "Different synthetic frontend context" } }, null);
                Check(!DesktopRuntimeHost.SameContinuation(changedFrontend, checkpoint), "Changed frontend tools or instruction identity preserved checkpoint authority.");
                var old = new ContinuationAdmission(checkpoint.ThreadId, operation, checkpoint.Model, checkpoint.Effort, policy, checkpoint.Cwd, checkpoint.ModelProvider);
                var retired = new DesktopContinuationRetirements(2);
                Check(DesktopRuntimeHost.ReleaseContinuationBinding(checkpoint, operation, checkpoint, operation, new[] { old }, retired) && !old.IsArmed,
                    "The matching reservation was not released.");
                Check(!DesktopRuntimeHost.ReleaseContinuationBinding(exact, operation, null, null, new[] { old }, retired), "A lost-release-ACK retry was not idempotent.");
                checkpoint.Model = "mutated-caller-object";
                Check(Rejected(() => DesktopRuntimeHost.ReleaseContinuationBinding(checkpoint, operation, null, null, new[] { old }, retired)),
                    "A changed checkpoint reused an old release acknowledgement.");
                var next = Json.Decode<DesktopContinuationCheckpoint>(Json.Encode(exact)); next.Generation = 2; next.TurnId = "next-original-turn";
                var newer = new ContinuationAdmission(next.ThreadId, nextOperation, next.Model, next.Effort, policy, next.Cwd, next.ModelProvider);
                Check(!DesktopRuntimeHost.ReleaseContinuationBinding(exact, operation, next, nextOperation, new[] { old, newer }, retired) && newer.IsArmed,
                    "Retrying an old release cleared the newer operation.");
                Check(Rejected(() => DesktopRuntimeHost.ReleaseContinuationBinding(next, operation, next, nextOperation, new[] { newer }, retired)) && newer.IsArmed,
                    "A retired operation identifier was rebound to another checkpoint.");
                Check(DesktopRuntimeHost.ReleaseContinuationBinding(next, nextOperation, next, nextOperation, new[] { old, newer }, retired) && !newer.IsArmed,
                    "The new exact reservation could not be released.");
                var third = Json.Decode<DesktopContinuationCheckpoint>(Json.Encode(next)); third.Generation = 3;
                string thirdOperation = Guid.NewGuid().ToString("N"); retired.Remember(third, thirdOperation);
                Check(!retired.IsReleased(exact, operation) && retired.IsReleased(next, nextOperation) && retired.IsReleased(third, thirdOperation),
                    "The released-operation ledger did not enforce its fixed retention bound.");
            });
        }
        private static void RegisterSharedStatusTests(Action<string, Action> test)
        {
            test("Desktop simultaneous status readers share only the in-flight query and the next request reads afresh", delegate
            {
                var query = new DesktopStatusQuery<int>(); int reads = 0;
                using (var entered = new ManualResetEventSlim(false))
                using (var release = new ManualResetEventSlim(false))
                {
                    var first = query.GetStatus(() => { Interlocked.Increment(ref reads); entered.Set(); Check(release.Wait(3000), "The first status reader was not released."); return 41; });
                    try
                    {
                        Check(entered.Wait(1000), "The first status query did not begin.");
                        var second = query.GetStatus(() => { Interlocked.Increment(ref reads); return -1; });
                        Check(Object.ReferenceEquals(first, second) && !second.IsCompleted && reads == 1,
                            "Concurrent background and foreground readers queued two backend queries.");
                        release.Set();
                        Check(first.GetAwaiter().GetResult() == 41 && second.GetAwaiter().GetResult() == 41 && reads == 1,
                            "Readers did not receive the same completed observation.");
                        var fresh = query.GetStatus(() => { Interlocked.Increment(ref reads); return 42; });
                        Check(!Object.ReferenceEquals(first, fresh) && fresh.GetAwaiter().GetResult() == 42 && reads == 2,
                            "A completed status observation was reused as fresh state.");
                    }
                    finally { release.Set(); first.Wait(3000); }
                }
            });
            test("Desktop simultaneous status failures reach every reader and do not poison the next query", delegate
            {
                var query = new DesktopStatusQuery<int>(); int reads = 0;
                using (var entered = new ManualResetEventSlim(false))
                using (var release = new ManualResetEventSlim(false))
                {
                    var first = query.GetStatus(() => { Interlocked.Increment(ref reads); entered.Set(); Check(release.Wait(3000), "The failing status reader was not released."); throw new IOException("Synthetic status unavailable."); });
                    try
                    {
                        Check(entered.Wait(1000), "The failing status query did not begin.");
                        var second = query.GetStatus(() => -1);
                        Check(Object.ReferenceEquals(first, second), "Concurrent readers did not share the failing observation.");
                        release.Set();
                        Check(Rejected(() => first.GetAwaiter().GetResult()) && Rejected(() => second.GetAwaiter().GetResult()) &&
                            first.IsFaulted && second.IsFaulted && reads == 1, "A failed status query returned success to a reader.");
                        var retry = query.GetStatus(() => { Interlocked.Increment(ref reads); return 43; });
                        Check(!Object.ReferenceEquals(first, retry) && retry.GetAwaiter().GetResult() == 43 && reads == 2,
                            "A failed observation prevented a fresh status retry.");
                    }
                    finally { release.Set(); try { first.Wait(3000); } catch { } }
                }
            });
            test("Desktop prepare commit and unprepare never share a read-only status result", delegate
            {
                var query = new DesktopStatusQuery<int>(); var controlGate = new object(); int reads = 0, mutations = 0;
                using (var entered = new ManualResetEventSlim(false))
                using (var release = new ManualResetEventSlim(false))
                {
                    Func<string, int> control = method =>
                    {
                        lock (controlGate)
                        {
                            if (method == "status")
                            { Interlocked.Increment(ref reads); entered.Set(); Check(release.Wait(3000), "The overlapping status query was not released."); return 44; }
                            Interlocked.Increment(ref mutations); return 45;
                        }
                    };
                    var status = query.GetStatus(() => control("status"));
                    try
                    {
                        Check(entered.Wait(1000), "The overlapping status query did not begin.");
                        var changes = new[] { "prepare", "commit", "unprepare" }.Select(method => Task.Run(() => query.Execute(method, control))).ToArray();
                        release.Set();
                        Check(Task.WaitAll(changes, 3000) && status.GetAwaiter().GetResult() == 44 &&
                            changes.All(change => change.Result == 45) && reads == 1 && mutations == 3,
                            "A control mutation reused shared status or skipped its own backend operation.");
                    }
                    finally { release.Set(); status.Wait(3000); }
                }
            });
        }
        private static void RegisterControlPreferenceTests(Action<string, Action> test)
        {
            test("Desktop control defaults on without fabricating authorization or starting a session", delegate
            {
                var dead = DeadCatalogIdentity(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DesktopRuntimeHost.exe"));
                using (var fixture = new RecordFixture(dead, dead))
                {
                    File.Delete(fixture.Configuration);
                    var client = new DesktopSessionClient(fixture.Root, fixture.Root, () => { throw new Exception("A preference queried live processes."); });
                    Check(client.ControlRequested && !client.Enabled && !File.Exists(fixture.Configuration), "The default changed authorization or eagerly wrote configuration.");
                    client.InitializeControlPreference();
                    var config = DesktopSessionFiles.ReadConfiguration(fixture.Configuration);
                    Check(config.ControlRequested == true && !config.Enabled && String.IsNullOrEmpty(config.AuthorizationGeneration), "Initialization granted runtime authority.");
                    Check(config.Home == fixture.Root && !config.BlockLocalWork, "Initialization changed unrelated preferences.");
                    string before = DesktopSessionFiles.ReadPrivateText(fixture.Configuration);
                    client.InitializeControlPreference();
                    Check(before == DesktopSessionFiles.ReadPrivateText(fixture.Configuration), "A repeat initialization rewrote the preference.");
                }
            });
            test("Desktop control off and on choices survive new clients and initialization without granting authority", delegate
            {
                var dead = DeadCatalogIdentity(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DesktopRuntimeHost.exe"));
                using (var fixture = new RecordFixture(dead, dead))
                {
                    fixture.Client.SetBlockLocalWork(true); fixture.Client.SetControlRequested(false);
                    var reopened = new DesktopSessionClient(fixture.Root, fixture.Root, () => new List<ProcessBlocker>());
                    reopened.InitializeControlPreference();
                    var config = DesktopSessionFiles.ReadConfiguration(fixture.Configuration);
                    Check(!reopened.ControlRequested && !reopened.Enabled && config.ControlRequested == false && !config.Enabled,
                        "Reopening replaced an explicit off choice with the default.");
                    Check(config.BlockLocalWork && config.AuthorizationGeneration == "private-fixture" && config.Home == fixture.Root,
                        "Saving the control choice erased existing preferences or identity.");
                    reopened.SetControlRequested(true);
                    var again = new DesktopSessionClient(fixture.Root, fixture.Root, () => new List<ProcessBlocker>());
                    again.InitializeControlPreference(); config = DesktopSessionFiles.ReadConfiguration(fixture.Configuration);
                    Check(again.ControlRequested && !again.Enabled && config.ControlRequested == true && !config.Enabled &&
                        config.AuthorizationGeneration == "private-fixture", "Turning the preference back on fabricated live authorization.");
                }
            });
            test("Desktop control never treats a directory or an invalid parent as a missing default-on configuration", delegate
            {
                var dead = DeadCatalogIdentity(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DesktopRuntimeHost.exe"));
                using (var fixture = new RecordFixture(dead, dead))
                {
                    File.Delete(fixture.Configuration); Directory.CreateDirectory(fixture.Configuration);
                    Check(Rejected(() => { bool requested = fixture.Client.ControlRequested; }) &&
                        Rejected(() => fixture.Client.InitializeControlPreference()) && Rejected(() => fixture.Client.SetControlRequested(true)),
                        "A configuration directory was mistaken for a new default-on installation.");
                    Check(Directory.Exists(fixture.Configuration) && Directory.GetFileSystemEntries(fixture.Configuration).Length == 0 && !fixture.Client.Enabled,
                        "Rejected initialization changed a configuration directory or authorized control.");
                    string nested = Path.Combine(fixture.Root, "invalid-parent-storage"); Directory.CreateDirectory(nested);
                    string parent = Path.Combine(nested, "integrations"); File.WriteAllText(parent, "synthetic non-directory ancestor");
                    var client = new DesktopSessionClient(nested, fixture.Root, () => new List<ProcessBlocker>());
                    Check(Rejected(() => { bool requested = client.ControlRequested; }) && Rejected(() => client.InitializeControlPreference()) &&
                        File.ReadAllText(parent) == "synthetic non-directory ancestor", "A non-directory ancestor silently became a default-on installation.");
                }
            });
            test("Desktop control legacy migration preserves explicit disabling and defaults unauthorised preferences on", delegate
            {
                var dead = DeadCatalogIdentity(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DesktopRuntimeHost.exe"));
                using (var fixture = new RecordFixture(dead, dead))
                {
                    var raw = ManagedBridge.Parse(DesktopSessionFiles.ReadPrivateText(fixture.Configuration));
                    raw.Remove("ControlRequested"); raw["Enabled"] = false;
                    DesktopSessionFiles.Write(fixture.Configuration, raw);
                    Check(!fixture.Client.ControlRequested, "A legacy explicit disabled state defaulted back on.");
                    fixture.Client.InitializeControlPreference();
                    Check(DesktopSessionFiles.ReadConfiguration(fixture.Configuration).ControlRequested == false, "Migration did not persist the legacy disabled choice.");
                    raw.Remove("AuthorizationGeneration"); raw["BlockLocalWork"] = true;
                    DesktopSessionFiles.Write(fixture.Configuration, raw);
                    Check(fixture.Client.ControlRequested && !fixture.Client.Enabled, "A never-authorized preference did not use the on default.");
                    fixture.Client.InitializeControlPreference();
                    var config = DesktopSessionFiles.ReadConfiguration(fixture.Configuration);
                    Check(config.ControlRequested == true && config.BlockLocalWork && !config.Enabled && String.IsNullOrEmpty(config.AuthorizationGeneration),
                        "The legacy preference migration changed unrelated policy or granted authorization.");
                }
            });
            test("Desktop control malformed preferences fail closed without rewriting configuration", delegate
            {
                var dead = DeadCatalogIdentity(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DesktopRuntimeHost.exe"));
                using (var fixture = new RecordFixture(dead, dead))
                {
                    var raw = ManagedBridge.Parse(DesktopSessionFiles.ReadPrivateText(fixture.Configuration));
                    foreach (object invalid in new object[] { null, "false", 0, new object[0], new Dictionary<string, object>() })
                    {
                        raw["ControlRequested"] = invalid; DesktopSessionFiles.Write(fixture.Configuration, raw);
                        string before = DesktopSessionFiles.ReadPrivateText(fixture.Configuration);
                        Check(Rejected(() => DesktopSessionFiles.ReadConfiguration(fixture.Configuration)) &&
                            Rejected(() => { bool requested = fixture.Client.ControlRequested; }) &&
                            Rejected(() => fixture.Client.InitializeControlPreference()) &&
                            Rejected(() => fixture.Client.SetControlRequested(true)), "Malformed preference was accepted or reset.");
                        Check(!fixture.Client.Enabled && before == DesktopSessionFiles.ReadPrivateText(fixture.Configuration),
                            "An invalid preference authorized control or changed private metadata.");
                    }
                }
            });
            test("Desktop control preference cannot rebind another home's private configuration", delegate
            {
                var dead = DeadCatalogIdentity(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DesktopRuntimeHost.exe"));
                using (var fixture = new RecordFixture(dead, dead))
                {
                    var raw = ManagedBridge.Parse(DesktopSessionFiles.ReadPrivateText(fixture.Configuration));
                    raw["Home"] = Path.Combine(fixture.Root, "different-home"); DesktopSessionFiles.Write(fixture.Configuration, raw);
                    string before = DesktopSessionFiles.ReadPrivateText(fixture.Configuration);
                    Check(Rejected(() => fixture.Client.InitializeControlPreference()) && Rejected(() => fixture.Client.SetControlRequested(true)) &&
                        before == DesktopSessionFiles.ReadPrivateText(fixture.Configuration), "A control preference rebound an unrelated home.");
                }
            });
            test("Desktop upgrade revocation preserves the requested on mode across a new client", delegate
            {
                var dead = DeadCatalogIdentity(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DesktopRuntimeHost.exe"));
                using (var fixture = new RecordFixture(dead, dead))
                {
                    Check(fixture.Client.ControlRequested && fixture.Client.Enabled, "A legacy enabled fixture was not recognized.");
                    fixture.Client.DisableAsync(CancellationToken.None).GetAwaiter().GetResult();
                    var reopened = new DesktopSessionClient(fixture.Root, fixture.Root, () => new List<ProcessBlocker>());
                    reopened.InitializeControlPreference();
                    var config = DesktopSessionFiles.ReadConfiguration(fixture.Configuration);
                    Check(config.ControlRequested == true && reopened.ControlRequested && !config.Enabled && !reopened.Enabled &&
                        config.AuthorizationGeneration == "private-fixture", "Temporary upgrade revocation erased the user's mode or retained authority.");
                }
            });
            test("Desktop host cannot treat the requested on default as authorization or ignore an explicit off choice", delegate
            {
                string home = Path.Combine(Path.GetTempPath(), "private-control-mode-fixture");
                var config = new DesktopIntegrationConfiguration { ProtocolVersion = 1, Home = home };
                var spec = new DesktopHostSpecification { Home = home, AuthorizationGeneration = "private-fixture" };
                Check(config.ResolveControlRequested() && !DesktopRuntimeHost.ConfigurationAuthorizes(config, spec), "The default granted host authority.");
                config.Enabled = true; config.AuthorizationGeneration = "private-fixture";
                Check(DesktopRuntimeHost.ConfigurationAuthorizes(config, spec), "A valid legacy authorization was rejected.");
                config.ControlRequested = false;
                Check(!DesktopRuntimeHost.ConfigurationAuthorizes(config, spec), "A stale enabled authorization bypassed the explicit off preference.");
                config.ControlRequested = true; config.Enabled = false;
                Check(!DesktopRuntimeHost.ConfigurationAuthorizes(config, spec), "Requested on mode restored revoked host authority.");
            });
        }
        private static void RegisterLocalWorkConfigurationTests(Action<string, Action> test)
        {
            test("Desktop local Work preference alone never authorizes control", delegate
            {
                string root = Path.Combine(Path.GetTempPath(), "CodexAccountSwitcher-localwork-preference-" + Guid.NewGuid().ToString("N"));
                DesktopSessionFiles.CreatePrivate(root);
                try
                {
                    var client = new DesktopSessionClient(root, root, () => new List<ProcessBlocker>());
                    Check(!client.Enabled && !client.BlockLocalWork, "A missing preference changed legacy authorization.");
                    client.SetBlockLocalWork(true);
                    string path = Path.Combine(root, "integrations", "desktop", "config.json");
                    var config = DesktopSessionFiles.ReadConfiguration(path);
                    Check(client.BlockLocalWork && !client.Enabled && !config.Enabled && String.IsNullOrEmpty(config.AuthorizationGeneration), "Saving a preference granted control.");
                    client.SetBlockLocalWork(false); config = DesktopSessionFiles.ReadConfiguration(path);
                    Check(!client.BlockLocalWork && !config.Enabled && String.IsNullOrEmpty(config.AuthorizationGeneration), "Clearing a preference granted control.");
                }
                finally
                {
                    Check(Path.GetDirectoryName(Path.GetFullPath(root)).TrimEnd('\\') == Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\'), "Unexpected preference cleanup target.");
                    ManagedBridge.EnsureNoReparsePoint(root); Directory.Delete(root, true);
                }
            });
            test("Desktop local Work preference preserves authorization and survives disabling", delegate
            {
                var dead = DeadCatalogIdentity(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DesktopRuntimeHost.exe"));
                using (var fixture = new RecordFixture(dead, dead))
                {
                    fixture.Client.SetBlockLocalWork(true);
                    var config = DesktopSessionFiles.ReadConfiguration(fixture.Configuration);
                    Check(config.BlockLocalWork && config.Enabled && config.AuthorizationGeneration == "private-fixture" && config.Home == fixture.Root, "A preference reset existing authorization or home.");
                    fixture.Client.DisableAsync(CancellationToken.None).GetAwaiter().GetResult();
                    config = DesktopSessionFiles.ReadConfiguration(fixture.Configuration);
                    Check(config.BlockLocalWork && !config.Enabled && config.AuthorizationGeneration == "private-fixture", "Disabling erased the explicit preference or replaced its authorization.");
                }
            });
            test("Desktop local Work configuration accepts a legacy omission and rejects malformed booleans", delegate
            {
                var dead = DeadCatalogIdentity(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DesktopRuntimeHost.exe"));
                using (var fixture = new RecordFixture(dead, dead))
                {
                    var raw = ManagedBridge.Parse(DesktopSessionFiles.ReadPrivateText(fixture.Configuration)); raw.Remove("BlockLocalWork");
                    DesktopSessionFiles.Write(fixture.Configuration, raw);
                    Check(!DesktopSessionFiles.ReadConfiguration(fixture.Configuration).BlockLocalWork && fixture.Client.Enabled, "A legacy omission did not preserve the disabled-by-default option.");
                    foreach (object invalid in new object[] { null, "true", 1, new object[0], new Dictionary<string, object>() })
                    {
                        raw["BlockLocalWork"] = invalid; DesktopSessionFiles.Write(fixture.Configuration, raw);
                        string before = DesktopSessionFiles.ReadPrivateText(fixture.Configuration);
                        Check(Rejected(() => DesktopSessionFiles.ReadConfiguration(fixture.Configuration)) && Rejected(() => fixture.Client.SetBlockLocalWork(false)), "A malformed policy was silently accepted or reset.");
                        Check(before == DesktopSessionFiles.ReadPrivateText(fixture.Configuration), "A rejected preference changed private configuration.");
                    }
                }
            });
            test("Desktop local Work preference cannot rewrite another home's configuration", delegate
            {
                var dead = DeadCatalogIdentity(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DesktopRuntimeHost.exe"));
                using (var fixture = new RecordFixture(dead, dead))
                {
                    var raw = ManagedBridge.Parse(DesktopSessionFiles.ReadPrivateText(fixture.Configuration)); raw["Home"] = Path.Combine(fixture.Root, "other-home");
                    DesktopSessionFiles.Write(fixture.Configuration, raw); string before = DesktopSessionFiles.ReadPrivateText(fixture.Configuration);
                    Check(Rejected(() => fixture.Client.SetBlockLocalWork(true)) && before == DesktopSessionFiles.ReadPrivateText(fixture.Configuration), "A preference rebound a different home.");
                }
            });
            test("Desktop launch local Work policy is legacy compatible and type checked", delegate
            {
                var dead = DeadCatalogIdentity(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DesktopRuntimeHost.exe"));
                using (var fixture = new RecordFixture(dead, dead))
                {
                    string path = Path.Combine(fixture.Directory, "launch.json");
                    var raw = new Dictionary<string, object> { { "ProtocolVersion", 1 }, { "FirstEnable", false } };
                    DesktopSessionFiles.Write(path, raw); Check(!DesktopSessionFiles.ReadSpecification(path).BlockLocalWork, "A legacy launch unexpectedly opted out.");
                    raw["BlockLocalWork"] = true; DesktopSessionFiles.Write(path, raw);
                    Check(DesktopSessionFiles.ReadSpecification(path).BlockLocalWork, "An explicit launch choice was lost.");
                    foreach (object invalid in new object[] { null, "false", 0, new object[0], new Dictionary<string, object>() })
                    { raw["BlockLocalWork"] = invalid; DesktopSessionFiles.Write(path, raw); Check(Rejected(() => DesktopSessionFiles.ReadSpecification(path)), "A malformed launch choice bypassed validation."); }
                }
            });
            test("Desktop host authorization binds its launch policy to the current explicit choice", delegate
            {
                string home = Path.Combine(Path.GetTempPath(), "private-policy-fixture");
                var config = new DesktopIntegrationConfiguration { ProtocolVersion = 1, Enabled = true, AuthorizationGeneration = "private-fixture", Home = home };
                var spec = new DesktopHostSpecification { AuthorizationGeneration = "private-fixture", Home = home };
                Check(DesktopRuntimeHost.ConfigurationAuthorizes(config, spec), "A matching legacy authorization was rejected.");
                config.BlockLocalWork = true;
                Check(!DesktopRuntimeHost.ConfigurationAuthorizes(config, spec), "A stale or tampered launch bypassed the requested block.");
                spec.BlockLocalWork = true; Check(DesktopRuntimeHost.ConfigurationAuthorizes(config, spec), "A matching explicit policy was rejected.");
                config.BlockLocalWork = false; Check(!DesktopRuntimeHost.ConfigurationAuthorizes(config, spec), "A running host retained authority after a preference change.");
                config.BlockLocalWork = true; config.Enabled = false; Check(!DesktopRuntimeHost.ConfigurationAuthorizes(config, spec), "A matching policy reauthorized disabled control.");
                var state = new DesktopSessionState { ControlEnabled = false, Reason = "localWorkPolicyChanged" };
                Check(state.Describe().Contains("選項已變更") && state.Describe().Contains("正常 Quit"), "A policy change omitted its normal-restart explanation.");
            });
        }
        private static ProcessBlocker FirstEnableDesktop(int pid)
        { return new ProcessBlocker { Pid = pid, Kind = "Desktop", Name = "ChatGPT.exe", Description = "Fixture desktop App or background process" }; }
        private static void RegisterManagedOpenTests(Action<string, Action> test)
        {
            test("Explicit managed open preserves a saved off preference before inspecting or validating launch", delegate
            {
                var dead = DeadCatalogIdentity(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DesktopRuntimeHost.exe"));
                using (var fixture = new RecordFixture(dead, dead))
                {
                    fixture.Client.SetControlRequested(false);
                    string before = DesktopSessionFiles.ReadPrivateText(fixture.Configuration); int inspections = 0, validations = 0;
                    var client = new DesktopSessionClient(fixture.Root, fixture.Root,
                        () => { inspections++; return new List<ProcessBlocker>(); },
                        exe => { validations++; return new DesktopPackageTarget(); });
                    Check(Rejected(() => client.OpenManagedAsync("synthetic-app", null, CancellationToken.None).GetAwaiter().GetResult()),
                        "An explicit open silently re-enabled saved-off desktop control.");
                    Check(!client.ControlRequested && !client.Enabled && inspections == 0 && validations == 0 &&
                        before == DesktopSessionFiles.ReadPrivateText(fixture.Configuration) && !File.Exists(Path.Combine(fixture.Directory, "launch.json")),
                        "A saved-off open probed, authorized, launched, or rewrote configuration.");
                }
            });
            test("Ordinary enabled launch still rejects an unmanaged desktop instead of changing to queued first enable", delegate
            {
                var dead = DeadCatalogIdentity(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DesktopRuntimeHost.exe"));
                using (var fixture = new RecordFixture(dead, dead))
                {
                    fixture.Client.SetControlRequested(true);
                    string before = DesktopSessionFiles.ReadPrivateText(fixture.Configuration); int validations = 0, reports = 0;
                    var client = new DesktopSessionClient(fixture.Root, fixture.Root,
                        () => new List<ProcessBlocker> { FirstEnableDesktop(48601) },
                        exe => { validations++; return new DesktopPackageTarget(); });
                    string message = null;
                    try { client.LaunchIfEnabledAsync("synthetic-app", text => reports++, CancellationToken.None).GetAwaiter().GetResult(); }
                    catch (UserError error) { message = error.Message; }
                    Check(message != null && message.Contains("未受控") && validations == 0 && reports == 0 &&
                        before == DesktopSessionFiles.ReadPrivateText(fixture.Configuration) && !File.Exists(Path.Combine(fixture.Directory, "launch.json")),
                        "The existing launch path stopped rejecting an unmanaged desktop or changed authority.");
                }
            });
            test("Explicit enabled managed open waits for ordinary desktop Quit and cancellation cannot create authority or a session", delegate
            {
                var dead = DeadCatalogIdentity(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DesktopRuntimeHost.exe"));
                using (var fixture = new RecordFixture(dead, dead))
                using (var cancellation = new CancellationTokenSource())
                {
                    fixture.Client.SetControlRequested(true);
                    string before = DesktopSessionFiles.ReadPrivateText(fixture.Configuration);
                    string auth = Path.Combine(fixture.Root, "auth.json"); File.WriteAllText(auth, "synthetic-auth-unchanged");
                    string sessions = Path.GetDirectoryName(fixture.Directory); int originalSessions = Directory.GetDirectories(sessions).Length;
                    int inspections = 0, validations = 0; var reports = new List<string>(); bool cancelled = false;
                    var client = new DesktopSessionClient(fixture.Root, fixture.Root,
                        () => { inspections++; return new List<ProcessBlocker> { FirstEnableDesktop(48602) }; },
                        exe => { validations++; Check(exe == "synthetic-app", "The explicit open changed its launch target."); return new DesktopPackageTarget(); });
                    try
                    {
                        client.OpenManagedAsync("synthetic-app", text => { reports.Add(text); cancellation.Cancel(); }, cancellation.Token).GetAwaiter().GetResult();
                    }
                    catch (OperationCanceledException) { cancelled = true; }
                    Check(cancelled && inspections >= 2 && validations == 1 && reports.Any(text => text.Contains("48602") && text.Contains("Quit")),
                        "The enabled explicit open did not reach the real cancellable first-enable wait.");
                    Check(before == DesktopSessionFiles.ReadPrivateText(fixture.Configuration) &&
                        DesktopSessionFiles.ReadConfiguration(fixture.Configuration).AuthorizationGeneration == "private-fixture" &&
                        File.ReadAllText(auth) == "synthetic-auth-unchanged" && Directory.GetDirectories(sessions).Length == originalSessions &&
                        Directory.GetFiles(sessions, "launch.json", SearchOption.AllDirectories).Length == 0,
                        "A cancelled normal-Quit wait changed authorization, account data, or created a launch/session.");
                }
            });
        }
        private static void RegisterFirstEnableTests(Action<string, Action> test)
        {
            test("Desktop first enable waits for the App and remaining CLI to exit before launch", delegate
            {
                int reads = 0, launched = 0; var reports = new List<string>();
                DesktopFirstEnableWaiter.WaitAsync(() =>
                {
                    reads++; Check(launched == 0, "Launch preceded the prerequisite exit check.");
                    if (reads == 1) return Task.FromResult(new List<ProcessBlocker> { FirstEnableDesktop(48101) });
                    if (reads == 2) return Task.FromResult(new List<ProcessBlocker> { new ProcessBlocker { Pid = 48102, Kind = "CLI", Name = "codex.exe", Description = "Fixture remaining desktop CLI" } });
                    return Task.FromResult(new List<ProcessBlocker>());
                }, reports.Add, CancellationToken.None, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2), TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
                launched++;
                Check(reads >= 4 && launched == 1 && reports.Any(s => s.Contains("48101")) && reports.Any(s => s.Contains("48102")), "The pending enable omitted the remaining process or did not continue automatically.");
            });
            test("Desktop first enable reports only actual blockers instead of inventing VS Code", delegate
            {
                int reads = 0; var reports = new List<string>();
                DesktopFirstEnableWaiter.WaitAsync(() => Task.FromResult(++reads == 1 ? new List<ProcessBlocker> { FirstEnableDesktop(48201) } : new List<ProcessBlocker>()),
                    reports.Add, CancellationToken.None, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2), TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
                Check(reports.Any(s => s.Contains("48201")), "The actual desktop PID was hidden.");
                Check(reports.All(s => s.IndexOf("VS Code", StringComparison.OrdinalIgnoreCase) < 0 && s.IndexOf("VSCode", StringComparison.OrdinalIgnoreCase) < 0), "The pending message claimed an absent VS Code blocker.");
            });
            test("Desktop first enable restarts its quiet check if a process reappears", delegate
            {
                int reads = 0, launched = 0;
                DesktopFirstEnableWaiter.WaitAsync(() =>
                {
                    reads++; Check(launched == 0, "Launch happened inside a quiet interval.");
                    return Task.FromResult(reads == 2 ? new List<ProcessBlocker> { FirstEnableDesktop(48301) } : new List<ProcessBlocker>());
                }, null, CancellationToken.None, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(3), TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
                launched++; Check(reads >= 4 && launched == 1, "A transient empty snapshot bypassed the second fresh check.");
            });
            test("Desktop first enable cancellation while blocked never reaches launch", delegate
            {
                int reads = 0, launched = 0; bool cancelled = false;
                using (var cancellation = new CancellationTokenSource())
                {
                    try
                    {
                        DesktopFirstEnableWaiter.WaitAsync(() => { reads++; return Task.FromResult(new List<ProcessBlocker> { FirstEnableDesktop(48401) }); },
                            text => cancellation.Cancel(), cancellation.Token, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2), TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
                        launched++;
                    }
                    catch (OperationCanceledException) { cancelled = true; }
                }
                Check(cancelled && reads > 0 && launched == 0, "Cancelled pending enable returned permission to launch.");
            });
            test("Desktop first enable quiet interval remains cancellable", delegate
            {
                int reads = 0, launched = 0; bool cancelled = false;
                using (var cancellation = new CancellationTokenSource())
                {
                    try
                    {
                        DesktopFirstEnableWaiter.WaitAsync(() =>
                        { reads++; cancellation.CancelAfter(5); return Task.FromResult(new List<ProcessBlocker>()); }, null, cancellation.Token,
                            TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(80), TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
                        launched++;
                    }
                    catch (OperationCanceledException) { cancelled = true; }
                }
                Check(cancelled && reads == 1 && launched == 0, "Cancellation during the quiet period reached a second check or launch.");
            });
            test("Desktop first enable timeout preserves the unopened state", delegate
            {
                int reads = 0, launched = 0;
                Check(Rejected(() =>
                {
                    DesktopFirstEnableWaiter.WaitAsync(() => { reads++; return Task.FromResult(new List<ProcessBlocker> { FirstEnableDesktop(48501) }); }, null,
                        CancellationToken.None, TimeSpan.FromMilliseconds(2), TimeSpan.FromMilliseconds(2), TimeSpan.FromMilliseconds(60)).GetAwaiter().GetResult();
                    launched++;
                }), "A permanently running App did not time out.");
                Check(reads > 0 && launched == 0, "Timeout allowed configuration/launch work to follow the waiter.");
            });
            test("Desktop first enable cannot launch when an empty inspection arrives after its deadline", delegate
            {
                int reads = 0, launched = 0;
                Check(Rejected(() =>
                {
                    DesktopFirstEnableWaiter.WaitAsync(async delegate
                    {
                        reads++; await Task.Delay(35).ConfigureAwait(false); return new List<ProcessBlocker>();
                    }, null, CancellationToken.None, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(10)).GetAwaiter().GetResult();
                    launched++;
                }), "A late empty snapshot bypassed the overall enable deadline.");
                Check(reads > 0 && launched == 0, "Readiness after timeout still permitted launch.");
            });
            test("Desktop first enable unknown inspection is never treated as an empty snapshot", delegate
            {
                int reads = 0, launched = 0;
                Check(Rejected(() =>
                {
                    DesktopFirstEnableWaiter.WaitAsync(() => { reads++; throw new UserError("Fixture process metadata unavailable."); }, null,
                        CancellationToken.None, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2), TimeSpan.FromMilliseconds(60)).GetAwaiter().GetResult();
                    launched++;
                }), "Failed process inspection became permission to launch.");
                Check(reads > 0 && launched == 0, "Unknown inspection continued into configuration/launch work.");
            });
            test("Desktop first enable initially empty still requires two fresh observations", delegate
            {
                int reads = 0, launched = 0; var elapsed = Stopwatch.StartNew();
                DesktopFirstEnableWaiter.WaitAsync(() => { reads++; return Task.FromResult(new List<ProcessBlocker>()); }, null, CancellationToken.None,
                    TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
                launched++;
                Check(reads == 2 && launched == 1 && elapsed.ElapsedMilliseconds >= 10, "Initial emptiness bypassed a real quiet interval and fresh inspection.");
            });
        }
        private static Dictionary<string, object> RawState()
        {
            string id = Guid.NewGuid().ToString("N");
            var actor = new DesktopProcessIdentity { Pid = 100, ParentPid = 1, SessionId = 1, StartedUtc = "2026-01-01T00:00:00.0000000Z", ExecutablePath = Path.Combine(Path.GetTempPath(), "private-fixture", "host.exe") };
            return ManagedBridge.Parse(ManagedBridge.Encode(new DesktopSessionState { SessionId = id, Generation = 1, Host = actor, App = actor, Home = Path.GetTempPath(), PipeName = "CodexAccountSwitcher.desktop." + id,
                OwnedActors = new List<DesktopProcessIdentity> { actor }, State = "running", Known = true, Idle = true, ControlEnabled = true }));
        }
        private sealed class RecordFixture : IDisposable
        {
            internal readonly string Root, Directory, Configuration;
            internal readonly DesktopSessionClient Client;
            internal readonly DesktopSessionState Record;
            internal RecordFixture(DesktopProcessIdentity host, params DesktopProcessIdentity[] actors)
            {
                Root = Path.Combine(Path.GetTempPath(), "CodexAccountSwitcher-previous-installation-" + Guid.NewGuid().ToString("N"));
                DesktopSessionFiles.CreatePrivate(Root);
                string integration = Path.Combine(Root, "integrations", "desktop"), sessions = Path.Combine(integration, "sessions"), id = Guid.NewGuid().ToString("N");
                DesktopSessionFiles.CreatePrivate(integration); DesktopSessionFiles.CreatePrivate(sessions);
                Directory = Path.Combine(sessions, id); DesktopSessionFiles.CreatePrivate(Directory);
                Configuration = Path.Combine(integration, "config.json");
                DesktopSessionFiles.Write(Configuration, new DesktopIntegrationConfiguration { ProtocolVersion = 1, Enabled = true, AuthorizationGeneration = "private-fixture", Home = Root });
                Record = new DesktopSessionState { SessionId = id, Generation = 1, Host = host, Home = Root, PipeName = "CodexAccountSwitcher.desktop." + id,
                    OwnedActors = actors.ToList(), State = "running", Known = true, Idle = true, ControlEnabled = true };
                Save(); Client = new DesktopSessionClient(Root, Root, () => new List<ProcessBlocker>());
            }
            internal void Save() { DesktopSessionFiles.Write(Path.Combine(Directory, "session.json"), Record); }
            public void Dispose()
            {
                Check(Path.GetDirectoryName(Path.GetFullPath(Root)).TrimEnd('\\') == Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\'), "Unexpected catalog fixture cleanup target.");
                ManagedBridge.EnsureNoReparsePoint(Root); System.IO.Directory.Delete(Root, true);
            }
        }
        private static DesktopProcessIdentity DeadCatalogIdentity(string executable)
        {
            return new DesktopProcessIdentity { Pid = Int32.MaxValue, ParentPid = 0, SessionId = Process.GetCurrentProcess().SessionId,
                StartedUtc = "2026-01-01T00:00:00.0000000Z", ExecutablePath = executable };
        }
        private static void RegisterPreviousInstallationTests(Action<string, Action> test)
        {
            string oldHost = Path.Combine(Path.GetTempPath(), "private-fixture-older-release", "DesktopRuntimeHost.exe");
            test("Desktop ignores fully exited previous-installation records without assuming successful quiescence", delegate
            {
                var dead = DeadCatalogIdentity(oldHost);
                using (var fixture = new RecordFixture(dead, dead))
                {
                    Check(fixture.Client.InspectAsync(CancellationToken.None).GetAwaiter().GetResult().Count == 0, "A dead older release prevented an upgrade.");
                    Check(!fixture.Record.AllBackendsExited && !fixture.Record.AppExitConfirmed && fixture.Client.Enabled, "Skipping an expired record changed authorization or fabricated exit success.");
                }
            });
            test("Desktop retains a live previous-installation host as uncontrolled and never connects to its pipe", delegate
            {
                using (var own = Process.GetCurrentProcess())
                {
                    var live = DesktopNative.Identity(own.Id); Check(live != null, "Fixture process identity unavailable.");
                    using (var fixture = new RecordFixture(live, live))
                    using (var pipe = new NamedPipeServerStream(fixture.Record.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
                    {
                        var states = fixture.Client.InspectAsync(CancellationToken.None).GetAwaiter().GetResult();
                        Check(states.Count == 1 && states[0].Reason == "previousInstallationStillRunning" && !states[0].Known && !states[0].Idle && !states[0].ControlEnabled && !pipe.IsConnected,
                            "A previous installation was queried or granted control/idle authority.");
                        var blocker = new ProcessBlocker { Pid = live.Pid, Kind = "Integration", Name = "DesktopRuntimeHost.exe" };
                        Check(fixture.Client.FilterOwnedProcesses(new List<ProcessBlocker> { blocker }, states).Count == 1, "A previous installation removed a real process blocker.");
                        states[0].Known = states[0].ControlEnabled = true;
                        Check(fixture.Client.FilterOwnedProcesses(new List<ProcessBlocker> { blocker }, states).Count == 1, "Caller-supplied stale flags exempted a different installation.");
                        fixture.Client.DisableAsync(CancellationToken.None).GetAwaiter().GetResult();
                        Check(!DesktopSessionFiles.Read<DesktopIntegrationConfiguration>(fixture.Configuration).Enabled && !pipe.IsConnected && DesktopNative.Same(live),
                            "Revoking old control queried its pipe, ended its process, or failed to persist.");
                    }
                    if (live.Lifetime != null) live.Lifetime.Dispose();
                }
            });
            test("Desktop retains a previous-installation live actor even after its host exits", delegate
            {
                using (var own = Process.GetCurrentProcess())
                {
                    var live = DesktopNative.Identity(own.Id); Check(live != null, "Fixture process identity unavailable.");
                    var dead = DeadCatalogIdentity(oldHost);
                    using (var fixture = new RecordFixture(dead, dead, live))
                    {
                        var states = fixture.Client.InspectAsync(CancellationToken.None).GetAwaiter().GetResult();
                        Check(states.Count == 1 && !states[0].Known && fixture.Client.CredentialProcessesRunning(), "A surviving actor was lost with its previous host.");
                    }
                    if (live.Lifetime != null) live.Lifetime.Dispose();
                }
            });
            test("Desktop current-installation expired records retain existing behavior", delegate
            {
                var dead = DeadCatalogIdentity(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DesktopRuntimeHost.exe"));
                using (var fixture = new RecordFixture(dead, dead))
                    Check(fixture.Client.InspectAsync(CancellationToken.None).GetAwaiter().GetResult().Count == 0, "A current-installation expired record became an upgrade blocker.");
            });
            test("Desktop previous-installation handling still validates catalog home binding", delegate
            {
                var dead = DeadCatalogIdentity(oldHost);
                using (var fixture = new RecordFixture(dead, dead))
                {
                    fixture.Record.Home = Path.Combine(fixture.Root, "other-home"); fixture.Save();
                    Check(Rejected(() => fixture.Client.InspectAsync(CancellationToken.None).GetAwaiter().GetResult()), "An unrelated home catalog bypassed validation as expired metadata.");
                }
            });
        }
        private sealed class SocketFixture : IDisposable
        {
            internal readonly string Root, Marker;
            internal readonly DesktopWebSocketServer Server;
            internal DesktopBridgeMember Member;
            internal readonly Process App;
            private readonly int expectedBackendExit;
            private bool cleaned;
            private readonly ManualResetEvent memberCreated = new ManualResetEvent(false);
            internal SocketFixture(string fakeApp, string fakeBackend, string mode)
            {
                Root = Path.Combine(Path.GetTempPath(), "CodexAccountSwitcher-desktop-" + Guid.NewGuid().ToString("N")); DesktopSessionFiles.CreatePrivate(Root); Marker = Path.Combine(Root, "owner-marker");
                expectedBackendExit = mode == "bad-exit" || mode == "unexpected-exit" ? 7 : 0;
                try
                {
                string config = Path.Combine(Root, "authorization.json"); DesktopSessionFiles.Write(config, new { Enabled = true, AuthorizationGeneration = "private-fixture" });
                var spec = new DesktopHostSpecification { Directory = Root, ConfigurationPath = config, AuthorizationGeneration = "private-fixture", ControllerExecutable = fakeApp };
                Server = new DesktopWebSocketServer(Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"), peer => String.Equals(peer.ExecutablePath, fakeApp, StringComparison.OrdinalIgnoreCase), connection =>
                {
                    Member = new DesktopBridgeMember(spec, new DesktopPackageTarget { CliExecutable = fakeBackend }, connection, 1, new[] { mode == "pending" ? "idle" : mode }); memberCreated.Set();
                    try { string line; while ((line = connection.ReadText(CancellationToken.None)) != null) Member.Forward(line); }
                    catch { }
                    finally { Member.MarkDisconnected(); }
                });
                Server.Start();
                var start = new ProcessStartInfo(fakeApp, ManagedBridge.QuoteArgument(Marker) + " " + mode) { UseShellExecute = false, CreateNoWindow = true };
                start.EnvironmentVariables["CODEX_APP_SERVER_WS_URL"] = Server.Endpoint.AbsoluteUri;
                App = Process.Start(start);
                Check(memberCreated.WaitOne(7000), "Fake App did not establish an authenticated owned connection.");
                var clock = Stopwatch.StartNew();
                while (!File.Exists(Marker) && clock.ElapsedMilliseconds < 8000) { if (File.Exists(Marker + ".error")) throw new IOException("Fake App failed."); Thread.Sleep(20); }
                Check(File.Exists(Marker) && !App.HasExited, "Fake owner did not remain alive after socket EOF.");
                clock.Restart();
                bool keepConnected = mode == "desktop-handshake";
                while (clock.ElapsedMilliseconds < 5000)
                { try { if (DesktopSessionFiles.Flag(Member.Status(), "known") && Member.Disconnected != keepConnected) break; } catch { } Thread.Sleep(20); }
                Check(Member.Disconnected != keepConnected && DesktopSessionFiles.Flag(Member.Last, "known"), "Fixture connection/status was not confirmed.");
                }
                catch { try { Cleanup(false); } catch { } throw; }
            }
            public void Dispose()
            { Cleanup(true); }
            private void Cleanup(bool verify)
            {
                if (cleaned) return;
                if (App != null)
                {
                    File.WriteAllText(Marker + ".stop", "normal-owner-exit");
                    Check(App.WaitForExit(5000) || App.WaitForExit(20000), "Fake App remains alive; backend input was preserved.");
                    if (verify) Check(App.ExitCode == 0, "Fake App did not exit normally.");
                }
                if (Member != null)
                {
                    Member.EndForConfirmedOwnerExit(); Check(Member.Running.Wait(7000), "Owner-exit backend did not finish naturally.");
                    if (verify) Check(Member.Bridge.ExitCode == expectedBackendExit && Member.Exited0 == (expectedBackendExit == 0), "Backend exit evidence was misclassified.");
                }
                if (Server != null) Server.Dispose(); if (App != null) App.Dispose(); memberCreated.Dispose();
                Check(Path.GetDirectoryName(Path.GetFullPath(Root)).TrimEnd('\\') == Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\'), "Unexpected fixture cleanup target.");
                ManagedBridge.EnsureNoReparsePoint(Root); Directory.Delete(Root, true);
                cleaned = true;
            }
        }
        private class Member : IDesktopSessionMember
        {
            internal bool Known = true, Prepared = true, Idle = true, Gone, Orphan, ThrowCommit;
            internal int Turns, Pending, Background, PrepareCalls, CommitCalls, UnprepareCalls;
            internal Action OnPrepare;
            internal Action OnStatus;
            public bool Exited0 { get { return Gone; } }
            public bool Disconnected { get { return Orphan; } }
            public Dictionary<string, object> Status()
            {
                if (OnStatus != null) OnStatus();
                return new Dictionary<string, object> { { "known", Known }, { "idle", Idle && Turns == 0 && Pending == 0 && Background == 0 },
                    { "activeTurns", Turns }, { "pendingRequests", Pending }, { "backgroundTerminals", Background }, { "prepared", Prepared }, { "state", Gone ? "quiesced" : "running" } };
            }
            public Dictionary<string, object> Prepare() { PrepareCalls++; if (OnPrepare != null) OnPrepare(); return Status(); }
            public Dictionary<string, object> Commit() { CommitCalls++; if (ThrowCommit) throw new IOException("Fake lost commit reply."); Gone = true; return Status(); }
            public void Unprepare() { UnprepareCalls++; }
        }
        private sealed class BoundedMember : Member, IDesktopSessionBoundedCommit
        {
            internal Func<Func<bool>, int, Dictionary<string, object>> OnBoundedCommit;
            public Dictionary<string, object> CommitForShutdown(Func<bool> mayContinue, int budgetMs)
            { return OnBoundedCommit == null ? Commit() : OnBoundedCommit(mayContinue, budgetMs); }
        }
        private sealed class Harness
        {
            internal readonly DesktopSessionTransaction Session = new DesktopSessionTransaction();
            internal bool Enabled = true, Connected = true, Gone = true, Refuse;
            internal int ExitCode, Shutdowns, Freezes, Unfreezes, Writes;
            internal Harness(params Member[] members)
            {
                Session.Enabled = () => Enabled; Session.AppGone = () => Gone;
                Session.Shutdown = () => { Shutdowns++; return new DesktopShutdownResult { Success = !Refuse, Refused = Refuse, ExitCode = Refuse ? (int?)null : ExitCode }; };
                Session.Freeze = () => Freezes++; Session.Unfreeze = () => Unfreezes++; Session.Persist = () => Writes++;
                foreach (var member in members) Check(Session.Admit(member), "Fixture member admission failed.");
            }
            internal bool Close() { return Session.Quiesce(() => Connected); }
        }
        public static void Register(Action<string, Action> test)
        {
            RegisterContinuationRetirementTests(test);
            RegisterMemberGroupTests(test);
            RegisterSharedStatusTests(test);
            RegisterControlPreferenceTests(test);
            RegisterLocalWorkConfigurationTests(test);
            test("Desktop diagnostics project only content-free state and bound unknown reasons", delegate
            {
                string secret = "private-fixture-marker";
                var state = new DesktopSessionState { SessionId = Guid.NewGuid().ToString("N"), Generation = 1, State = "running", TransportReady = true,
                    Home = secret, Host = new DesktopProcessIdentity { ExecutablePath = secret, Pid = 123 }, Reason = secret };
                string encoded = ManagedBridge.Encode(Program.CreateDesktopDiagnosticsReport(new[] { state }, null));
                Check(!encoded.Contains(secret) && !encoded.Contains("Home") && !encoded.Contains("Host") && !encoded.Contains("OwnedActors") && !encoded.Contains("Pid"),
                    "Diagnostic output exposed process paths or private fields.");
                var raw = ManagedBridge.Parse(encoded); var sessions = raw["sessions"] as object[];
                Check(Object.Equals(raw["success"], true) && sessions.Length == 1 && DesktopSessionFiles.Text(sessions[0] as Dictionary<string, object>, "Reason") == "other", "Diagnostic projection lost state or echoed a free-form reason.");
                state.Reason = "initializing";
                Check(ManagedBridge.Encode(Program.CreateDesktopDiagnosticsReport(new[] { state }, null)).Contains("initializing"), "A known content-free diagnostic code was lost.");
                state.Reason = "localWorkPolicyChanged";
                Check(ManagedBridge.Encode(Program.CreateDesktopDiagnosticsReport(new[] { state }, null)).Contains("localWorkPolicyChanged"), "The managed Local Work restart reason was lost.");
            });
            test("Desktop transport startup readiness does not authorize an unknown runtime shutdown", delegate
            {
                string id = Guid.NewGuid().ToString("N");
                var state = new DesktopSessionState { SessionId = id, Generation = 1, State = "running", ControlEnabled = true, TransportReady = true,
                    Known = false, Idle = false, Reason = "runtimeQueryUnavailable" };
                Check(DesktopSessionClient.IsLaunchTransportReady(state, id), "A live established transport still waited for full work-state certainty to finish startup.");
                Check(!RuntimeCoordinator.MayBeginShutdown(new RuntimeInspection(), new[] { state }, false), "Transport readiness was mistaken for permission to close or switch an unknown runtime.");
                using (var own = Process.GetCurrentProcess())
                {
                    var actor = DesktopNative.Identity(own.Id); Check(actor != null, "Fixture process identity unavailable.");
                    state.Host = actor; state.OwnedActors.Add(actor);
                    var blocker = new ProcessBlocker { Pid = actor.Pid, Kind = "Integration", Name = "DesktopRuntimeHost.exe" };
                    var client = new DesktopSessionClient(Path.GetTempPath(), Path.GetTempPath(), () => new List<ProcessBlocker> { blocker });
                    Check(client.FilterOwnedProcesses(new List<ProcessBlocker> { blocker }, new[] { state }).Count == 1, "A transport-ready unknown state removed a real process blocker.");
                    if (actor.Lifetime != null) actor.Lifetime.Dispose();
                }
                state.Known = state.Idle = true;
                Check(RuntimeCoordinator.MayBeginShutdown(new RuntimeInspection(), new[] { state }, false), "The original known-idle shutdown gate changed.");
            });
            test("Desktop launch transport readiness requires the current running authorized session", delegate
            {
                string id = Guid.NewGuid().ToString("N");
                Func<DesktopSessionState> create = () => new DesktopSessionState { SessionId = id, Generation = 1, State = "running", ControlEnabled = true, TransportReady = true };
                Check(!DesktopSessionClient.IsLaunchTransportReady(null, id), "Missing live state established readiness.");
                var state = create(); state.TransportReady = false; state.Known = state.Idle = true;
                Check(!DesktopSessionClient.IsLaunchTransportReady(state, id), "Known work status replaced the transport handshake.");
                state = create(); state.ControlEnabled = false; Check(!DesktopSessionClient.IsLaunchTransportReady(state, id), "Revoked transport became ready.");
                state = create(); state.BackendExitCode = 0; Check(!DesktopSessionClient.IsLaunchTransportReady(state, id), "An exited backend became ready.");
                state = create(); state.Generation = 0; Check(!DesktopSessionClient.IsLaunchTransportReady(state, id), "A session without an admitted backend became ready.");
                state = create(); state.State = "quiesced"; Check(!DesktopSessionClient.IsLaunchTransportReady(state, id), "A paused connection was reported as a running launch.");
                Check(!DesktopSessionClient.IsLaunchTransportReady(create(), Guid.NewGuid().ToString("N")), "Another session fulfilled this launch.");
            });
            test("Desktop transport readiness is optional for legacy metadata and strictly typed when supplied", delegate
            {
                var raw = RawState(); raw.Remove("TransportReady"); DesktopSessionFiles.ValidateSnapshot(raw);
                foreach (object value in new object[] { null, "true", 1 })
                { raw["TransportReady"] = value; Check(Rejected(() => DesktopSessionFiles.ValidateSnapshot(raw)), "A malformed transport-ready field was accepted."); }
                raw["TransportReady"] = true; DesktopSessionFiles.ValidateSnapshot(raw);
            });
            test("Desktop unknown work-state descriptions distinguish an established connection without echoing diagnostic content", delegate
            {
                var state = new DesktopSessionState { State = "running", ControlEnabled = true, TransportReady = true, Reason = "runtimeQueryUnavailable" };
                Check(state.Describe().Contains("後端已連線") && state.Describe().Contains("查詢未完成") && state.Describe().Contains("阻止切換") && !state.Describe().Contains("閒置"), "A connected unknown state was described as idle or an unstarted transport.");
                state.Reason = "runtimeChangedDuringQuery"; Check(state.Describe().Contains("工作狀態變動"), "A changing work state lost its explanation.");
                state.Reason = "initializing"; Check(state.Describe().Contains("取得工作狀態"), "Initialization status was indistinguishable from a finished work-state check.");
                state.Reason = "private-fixture-detail"; Check(!state.Describe().Contains(state.Reason), "An unrecognized diagnostic value was echoed into the interface.");
            });
            test("Desktop public quiescence downgrades lost proof while retaining its private recovery stage", delegate
            {
                var transaction = new DesktopSessionTransaction { Stage = "quiesced", EofCommitted = true };
                Check(DesktopRuntimeHost.PublicSnapshotStage(transaction.Stage, true) == "quiesced", "Complete proof lost successful quiescence.");
                Check(DesktopRuntimeHost.PublicSnapshotStage(transaction.Stage, false) == "recoveryRequired" && transaction.Stage == "quiesced" && transaction.EofCommitted,
                    "Lost proof either produced an invalid public state or discarded private recovery ownership.");
                Check(DesktopRuntimeHost.PublicSnapshotStage("running", false) == "running", "A normal running stage was altered.");
            });
            foreach (bool missingAppProof in new[] { false, true })
            {
                bool captured = missingAppProof;
                test("Desktop historical quiesced record with " + (captured ? "reopened App" : "late failed status") + " is downgraded without granting idle", delegate
                {
                    var actor = DeadCatalogIdentity(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DesktopRuntimeHost.exe"));
                    using (var fixture = new RecordFixture(actor, actor))
                    {
                        fixture.Record.State = "quiesced"; fixture.Record.Known = fixture.Record.Idle = false;
                        fixture.Record.AllBackendsExited = true; fixture.Record.AppExitConfirmed = !captured; fixture.Save();
                        var parsed = DesktopSessionFiles.ReadSnapshot(Path.Combine(fixture.Directory, "session.json"));
                        Check(parsed.State == "recoveryRequired" && !parsed.Known && !parsed.Idle && parsed.Reason == "desktopQuiescenceProofLost" && parsed.AppExitConfirmed == !captured,
                            "Historic lost proof became a successful paused state or fabricated App exit.");
                        Check(fixture.Client.InspectAsync(CancellationToken.None).GetAwaiter().GetResult().Count == 0, "An expired historical transition poisoned subsequent inspection.");
                        Check(File.ReadAllText(Path.Combine(fixture.Directory, "session.json"), Encoding.UTF8).Contains("quiesced"), "Historical normalization rewrote private data.");
                    }
                });
            }
            test("Desktop historical live lost-proof actors remain blocking after normalization", delegate
            {
                using (var own = Process.GetCurrentProcess())
                {
                    var live = DesktopNative.Identity(own.Id);
                    using (var fixture = new RecordFixture(live, live))
                    {
                        fixture.Record.State = "quiesced"; fixture.Record.Known = fixture.Record.Idle = false; fixture.Record.AllBackendsExited = true; fixture.Record.AppExitConfirmed = false; fixture.Save();
                        var states = fixture.Client.InspectAsync(CancellationToken.None).GetAwaiter().GetResult();
                        Check(states.Count == 1 && !states[0].Known && !states[0].Idle && fixture.Client.CredentialProcessesRunning(), "A live actor escaped the switch guard after downgrade.");
                    }
                    if (live.Lifetime != null) live.Lifetime.Dispose();
                }
            });
            test("Desktop legacy complete quiescence without diagnostic fields remains compatible", delegate
            {
                var actor = DeadCatalogIdentity(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DesktopRuntimeHost.exe"));
                using (var fixture = new RecordFixture(actor, actor))
                {
                    fixture.Record.State = "quiesced"; fixture.Record.AllBackendsExited = fixture.Record.AppExitConfirmed = true;
                    var raw = ManagedBridge.Parse(ManagedBridge.Encode(fixture.Record)); raw.Remove("BackendExitCode");
                    DesktopSessionFiles.Write(Path.Combine(fixture.Directory, "session.json"), raw);
                    var parsed = DesktopSessionFiles.ReadSnapshot(Path.Combine(fixture.Directory, "session.json"));
                    Check(parsed.State == "quiesced" && parsed.Known && parsed.Idle && parsed.BackendExitCode == null, "A complete legacy snapshot lost compatibility.");
                    raw.Remove("ActiveTurns"); DesktopSessionFiles.Write(Path.Combine(fixture.Directory, "session.json"), raw);
                    Check(Rejected(() => DesktopSessionFiles.ReadSnapshot(Path.Combine(fixture.Directory, "session.json"))), "Historical handling bypassed structural validation.");
                }
            });
            test("Desktop snapshot reads tolerate atomic replacement without blocking the writer", delegate
            {
                var actor = DeadCatalogIdentity(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DesktopRuntimeHost.exe"));
                using (var fixture = new RecordFixture(actor, actor))
                using (var start = new ManualResetEventSlim(false))
                {
                    string path = Path.Combine(fixture.Directory, "session.json");
                    var failures = new ConcurrentQueue<string>(); int reads = 0, writes = 0;
                    var tasks = new List<Task>();
                    tasks.Add(Task.Run(() =>
                    {
                        start.Wait();
                        for (int n = 0; n < 200; n++)
                        { try { fixture.Save(); Interlocked.Increment(ref writes); } catch (Exception error) { failures.Enqueue(error.GetType().Name); } Thread.Sleep(1); }
                    }));
                    for (int reader = 0; reader < 2; reader++) tasks.Add(Task.Run(() =>
                    {
                        start.Wait();
                        for (int n = 0; n < 350; n++)
                        { try { var state = DesktopSessionFiles.ReadSnapshot(path); Check(state.SessionId == fixture.Record.SessionId, "Atomic read mixed sessions."); Interlocked.Increment(ref reads); } catch (Exception error) { failures.Enqueue(error.GetType().Name); } }
                    }));
                    start.Set(); Check(Task.WaitAll(tasks.ToArray(), 20000), "Concurrent metadata fixture did not finish.");
                    Check(failures.IsEmpty && writes == 200 && reads == 700, "Atomic replacement failed a private reader/writer: " + String.Join(",", failures.Distinct()));
                }
            });
            test("Desktop retained-handle reads still reject broad ACLs oversize and malformed UTF8", delegate
            {
                var actor = DeadCatalogIdentity(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DesktopRuntimeHost.exe"));
                using (var fixture = new RecordFixture(actor, actor))
                {
                    string path = Path.Combine(fixture.Directory, "session.json");
                    var original = File.GetAccessControl(path);
                    var broadened = File.GetAccessControl(path);
                    broadened.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                        new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.WorldSid, null),
                        System.Security.AccessControl.FileSystemRights.Read, System.Security.AccessControl.AccessControlType.Allow));
                    File.SetAccessControl(path, broadened);
                    try { Check(Rejected(() => DesktopSessionFiles.ReadSnapshot(path)), "A non-private metadata ACL was accepted."); }
                    finally { File.SetAccessControl(path, original); }
                    File.WriteAllBytes(path, new byte[131073]); Check(Rejected(() => DesktopSessionFiles.ReadPrivateText(path)), "An oversized file bypassed the byte bound.");
                    File.WriteAllBytes(path, new byte[] { 255, 254, 253 }); Check(Rejected(() => DesktopSessionFiles.ReadPrivateText(path)), "Malformed UTF8 was silently replaced.");
                }
            });
            test("Desktop slow status queries do not hold frontend admission during startup", delegate
            {
                object admission = new object(); var member = new Member();
                using (var queried = new ManualResetEventSlim(false))
                using (var finish = new ManualResetEventSlim(false))
                using (var forwarded = new ManualResetEventSlim(false))
                {
                    member.OnStatus = () => { queried.Set(); Check(finish.Wait(3000), "Fixture status deadline expired."); };
                    var members = new List<IDesktopSessionMember> { member };
                    var status = Task.Run(() => DesktopRuntimeHost.RefreshMemberStatus(admission, members));
                    try
                    {
                        Check(queried.Wait(1000), "Slow status query did not begin.");
                        var frontend = Task.Run(() => { lock (admission) forwarded.Set(); });
                        Check(forwarded.Wait(1000) && !status.IsCompleted, "A status RPC blocked frontend startup admission.");
                        Check(frontend.Wait(1000), "Frontend admission did not complete.");
                    }
                    finally { finish.Set(); Check(status.Wait(3000), "Fixture status did not complete."); }
                    Check(status.Result && member.CommitCalls == 0, "Read-only status committed a backend exit.");
                }
            });
            test("Desktop failed status remains unknown without preparing or closing a backend", delegate
            {
                var member = new Member { OnStatus = () => { throw new IOException("Fixture unavailable status."); } };
                Check(!DesktopRuntimeHost.RefreshMemberStatus(new object(), new List<IDesktopSessionMember> { member }) && member.PrepareCalls == 0 && member.CommitCalls == 0,
                    "Failed read-only status was healthy or mutated the backend.");
            });
            test("Desktop backend exit diagnostics distinguish failure from successful startup", delegate
            {
                var state = new DesktopSessionState { State = "starting", BackendExitCode = 7 };
                Check(state.Describe().Contains("7") && state.Describe().Contains("後端已退出") && !state.Describe().Contains("閒置"), "Exited backend was reported as a successful or idle startup.");
            });
            test("Desktop exit diagnostics cannot contradict known idle or successful quiescence", delegate
            {
                var raw = RawState(); raw["BackendExitCode"] = 7;
                Check(Rejected(() => DesktopSessionFiles.ValidateSnapshot(raw)), "Failed backend was marked known idle.");
                raw["Known"] = raw["Idle"] = false; DesktopSessionFiles.ValidateSnapshot(raw);
                raw["BackendExitCode"] = "7";
                Check(Rejected(() => DesktopSessionFiles.ValidateSnapshot(raw)), "Invalid exit diagnostic type was accepted.");
                raw.Remove("BackendExitCode"); DesktopSessionFiles.ValidateSnapshot(raw);
            });
            RegisterFirstEnableTests(test);
            RegisterManagedOpenTests(test);
            RegisterPreviousInstallationTests(test);
            test("Desktop session idle prepare commits EOF0 before exact App close", delegate
            {
                var member = new Member(); var h = new Harness(member);
                Check(h.Close(), "Idle transaction failed.");
                Check(member.PrepareCalls == 1 && member.CommitCalls == 1 && member.Gone && h.Shutdowns == 1 && h.Session.Stage == "quiesced", "EOF/App order was not confirmed.");
                Check(h.Freezes == 1 && h.Unfreezes == 0 && h.Session.Frozen, "Successful session did not retain admission barrier.");
            });
            foreach (string kind in new[] { "turn", "pending", "background", "unknown" })
            {
                string captured = kind;
                test("Desktop session blocks " + captured + " before any EOF or App shutdown", delegate
                {
                    var member = new Member(); if (captured == "turn") member.Turns = 1; else if (captured == "pending") member.Pending = 1; else if (captured == "background") member.Background = 1; else member.Known = false;
                    var h = new Harness(member); Check(!h.Close(), "Nonquiet member was accepted.");
                    Check(member.CommitCalls == 0 && !member.Gone && h.Shutdowns == 0 && member.UnprepareCalls == 1 && !h.Session.Frozen, "Nonquiet failure closed a backend or did not reopen admission.");
                });
            }
            test("Desktop session requires a prepared lease even when snapshot says idle", delegate
            {
                var member = new Member { Prepared = false }; var h = new Harness(member);
                Check(!h.Close() && member.CommitCalls == 0 && h.Shutdowns == 0, "An unprepared lease was committed.");
            });
            test("Desktop session cancellation after prepare rolls back before EOF", delegate
            {
                var member = new Member(); var h = new Harness(member); member.OnPrepare = () => h.Connected = false;
                Check(!h.Close() && member.CommitCalls == 0 && member.UnprepareCalls == 1 && h.Shutdowns == 0 && !h.Session.Frozen, "Disconnected controller did not cancel before EOF.");
            });
            test("Desktop session revocation after prepare prevents EOF and RM", delegate
            {
                var member = new Member(); var h = new Harness(member); member.OnPrepare = () => h.Enabled = false;
                Check(!h.Close() && member.CommitCalls == 0 && h.Shutdowns == 0 && member.UnprepareCalls == 1, "Revocation was ignored.");
            });
            test("Desktop session changed membership generation cancels its prepared transaction", delegate
            {
                var member = new Member(); var h = new Harness(member); member.OnPrepare = () => h.Session.Generation++;
                Check(!h.Close() && member.CommitCalls == 0 && h.Shutdowns == 0, "Stale membership was committed.");
            });
            test("Desktop stale expected generation is rejected before freeze, prepare or EOF", delegate
            {
                var member = new Member(); var h = new Harness(member);
                Check(!h.Session.Quiesce(() => true, h.Session.Generation - 1) && member.PrepareCalls == 0 && member.CommitCalls == 0 && h.Freezes == 0 && h.Shutdowns == 0, "A stale caller controlled the replacement generation.");
            });
            test("Desktop session lost commit reply retains frozen recovery ownership", delegate
            {
                var member = new Member { ThrowCommit = true }; var h = new Harness(member);
                Check(!h.Close() && h.Session.EofCommitted && h.Session.Frozen && h.Session.Stage == "recoveryRequired", "Unknown commit was treated as a rollback.");
                Check(member.UnprepareCalls == 0 && h.Shutdowns == 0 && h.Unfreezes == 0, "A possible EOF was undone or App was closed.");
            });
            test("Desktop session RM refusal never becomes credential-commit ready", delegate
            {
                var member = new Member(); var h = new Harness(member) { Refuse = true };
                Check(!h.Close() && member.Gone && h.Shutdowns == 1 && h.Session.Stage == "recoveryRequired" && h.Session.Frozen, "RM refusal was treated as successful close.");
            });
            test("Desktop session requires App exit0 and disappearance after RM", delegate
            {
                var a = new Harness(new Member()) { ExitCode = 7 };
                var b = new Harness(new Member()) { Gone = false };
                Check(!a.Close() && !b.Close() && a.Session.Stage != "quiesced" && b.Session.Stage != "quiesced", "App nonzero or still alive passed.");
            });
            test("Desktop transaction waits for late Electron child exits before declaring quiescence", delegate
            {
                var h = new Harness(new Member()); int observations = 0;
                h.Session.AppGone = () => ++observations >= 3;
                Check(h.Close() && observations >= 3 && h.Shutdowns == 1 && h.Session.Stage == "quiesced", "A normally settling process family was misclassified as failed shutdown.");
            });
            test("Desktop committed recovery retains its host instead of racing the control reply", delegate
            {
                var h = new Harness(new Member()) { Refuse = true }; Check(!h.Close() && h.Session.EofCommitted, "Fixture did not reach committed recovery.");
                Check(!DesktopRuntimeHost.ShouldCleanupOwnerExit(h.Session.Stage, h.Session.EofCommitted, true), "Recovery would dispose its control pipe before restoration.");
                Check(!DesktopRuntimeHost.ShouldCleanupOwnerExit("quiesced", true, true) && DesktopRuntimeHost.ShouldCleanupOwnerExit("running", false, true), "Transactional close and ordinary owner exit were conflated.");
                Check(DesktopRuntimeHost.ShouldCleanupOwnerExit("recoveryRequired", true, false), "Explicitly disabled, fully gone ownership could not be released.");
            });
            test("Desktop orphan busy generation retains input and blocks replacement admission", delegate
            {
                var orphan = new Member { Orphan = true, Turns = 1 }; var h = new Harness(orphan);
                Check(!h.Session.Admit(new Member()) && !orphan.Gone && orphan.CommitCalls == 0, "A busy orphan was replaced or EOFed.");
            });
            test("Desktop separate owned connections never replace the still-live first backend", delegate
            {
                var old = new Member(); var h = new Harness(old); var account = new Member();
                Check(h.Session.Admit(account) && h.Session.Members.Contains(old) && !old.Gone && old.CommitCalls == 0 && h.Session.Generation == 2,
                    "An independently owned account connection replaced or ended live chat work.");
                old.Gone = true; Check(h.Session.Admit(new Member()) && h.Session.Generation == 3, "A retired connection prevented a distinct owned replacement.");
            });
            test("Desktop cancelled orphan close preserves its preexisting admission barrier", delegate
            {
                var orphan = new Member { Orphan = true, Turns = 1 }; var h = new Harness(orphan); h.Session.Frozen = true; h.Session.Stage = "orphaned";
                Check(!h.Close() && h.Session.Frozen && h.Session.Stage == "orphaned" && h.Unfreezes == 0, "Orphan failure reopened admission.");
            });
            test("Desktop recovery cancellation cannot unfreeze a possible committed EOF", delegate
            {
                var member = new Member { ThrowCommit = true }; var h = new Harness(member); h.Close(); h.Session.CancelBeforeEof();
                Check(h.Session.Frozen && h.Session.Stage == "recoveryRequired" && h.Unfreezes == 0, "Cancellation discarded committed ownership.");
            });
            test("Desktop session public metadata contains no endpoint credential or prompt fields", delegate
            {
                var state = new DesktopSessionState { SessionId = Guid.NewGuid().ToString("N"), Generation = 2, State = "running", Known = true, Idle = false, ControlEnabled = true, ActiveTurns = 1 };
                string encoded = new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(state);
                Check(!encoded.Contains("Endpoint") && !encoded.Contains("Credential") && !encoded.Contains("Prompt") && state.Describe().Contains("忙碌"), "Session metadata exposed a content/capability field.");
            });
            test("Desktop snapshot requires explicit protocol, counts and boolean fields", delegate
            {
                DesktopSessionFiles.ValidateSnapshot(RawState());
                foreach (string key in new[] { "ProtocolVersion", "Generation", "Known", "Idle", "ControlEnabled", "ActiveTurns", "PendingRequests", "BackgroundTerminals", "AllBackendsExited", "AppExitConfirmed", "RecoveryAccepted", "OwnerExitCleanup", "Host", "App", "OwnedActors", "Home", "PipeName", "State", "SessionId" })
                { var raw = RawState(); raw.Remove(key); Check(Rejected(() => DesktopSessionFiles.ValidateSnapshot(raw)), "Missing field silently defaulted: " + key); }
            });
            test("Desktop snapshot rejects negative, wrong-type and contradictory idle counts", delegate
            {
                foreach (object count in new object[] { -1, "0", null, 1 })
                { var raw = RawState(); raw["PendingRequests"] = count; Check(Rejected(() => DesktopSessionFiles.ValidateSnapshot(raw)), "Invalid pending count passed."); }
                var unknown = RawState(); unknown["Known"] = false; Check(Rejected(() => DesktopSessionFiles.ValidateSnapshot(unknown)), "Unknown was marked idle.");
            });
            test("Desktop snapshot refuses quiescence without complete backend and App exit evidence", delegate
            {
                var raw = RawState(); raw["State"] = "quiesced"; Check(Rejected(() => DesktopSessionFiles.ValidateSnapshot(raw)), "Incomplete quiescence was accepted.");
                raw["AllBackendsExited"] = true; raw["AppExitConfirmed"] = true; DesktopSessionFiles.ValidateSnapshot(raw);
            });
            test("Desktop startup preserves captured profile paths and removes only session context", delegate
            {
                string ui = Environment.GetEnvironmentVariable("CODEX_ELECTRON_USER_DATA_PATH"), sqlite = Environment.GetEnvironmentVariable("CODEX_SQLITE_HOME");
                try
                {
                    string expectedUi = Path.Combine(Path.GetTempPath(), "private-fixture-ui"), expectedSqlite = Path.Combine(Path.GetTempPath(), "private-fixture-sqlite");
                    Environment.SetEnvironmentVariable("CODEX_ELECTRON_USER_DATA_PATH", expectedUi); Environment.SetEnvironmentVariable("CODEX_SQLITE_HOME", expectedSqlite);
                    Check(DesktopSessionFiles.CapturePath("CODEX_ELECTRON_USER_DATA_PATH") == expectedUi && DesktopSessionFiles.CapturePath("CODEX_SQLITE_HOME") == expectedSqlite, "User data paths were not captured.");
                    Check(!DesktopSessionFiles.EphemeralEnvironment("CODEX_ELECTRON_USER_DATA_PATH") && !DesktopSessionFiles.EphemeralEnvironment("CODEX_SQLITE_HOME") && !DesktopSessionFiles.EphemeralEnvironment("CODEX_MODEL"), "A production preference was classified as ephemeral.");
                    Check(DesktopSessionFiles.EphemeralEnvironment("CODEX_THREAD_ID") && DesktopSessionFiles.EphemeralEnvironment("CODEX_APP_SERVER_WS_URL") && DesktopSessionFiles.EphemeralEnvironment("VSCODE_IPC_HOOK"), "A foreign session capability would be retained.");
                }
                finally { Environment.SetEnvironmentVariable("CODEX_ELECTRON_USER_DATA_PATH", ui); Environment.SetEnvironmentVariable("CODEX_SQLITE_HOME", sqlite); }
            });
            test("Desktop startup refuses external WebSocket, forced CLI and custom CLI overrides", delegate
            {
                string ws = Environment.GetEnvironmentVariable("CODEX_APP_SERVER_WS_URL"), force = Environment.GetEnvironmentVariable("CODEX_APP_SERVER_FORCE_CLI"), cli = Environment.GetEnvironmentVariable("CODEX_CLI_PATH");
                var target = new DesktopPackageTarget { CliExecutable = Path.Combine(Path.GetTempPath(), "fixture-bundled-cli.exe") };
                try
                {
                    Environment.SetEnvironmentVariable("CODEX_APP_SERVER_WS_URL", null); Environment.SetEnvironmentVariable("CODEX_APP_SERVER_FORCE_CLI", null); Environment.SetEnvironmentVariable("CODEX_CLI_PATH", target.CliExecutable); DesktopSessionFiles.ValidateExternalStartup(target);
                    Environment.SetEnvironmentVariable("CODEX_CLI_PATH", Path.Combine(Path.GetTempPath(), "different-fixture-cli.exe")); Check(Rejected(() => DesktopSessionFiles.ValidateExternalStartup(target)), "Custom CLI override was overwritten.");
                    Environment.SetEnvironmentVariable("CODEX_CLI_PATH", null); Environment.SetEnvironmentVariable("CODEX_APP_SERVER_FORCE_CLI", "1"); Check(Rejected(() => DesktopSessionFiles.ValidateExternalStartup(target)), "Forced CLI override was overwritten.");
                    Environment.SetEnvironmentVariable("CODEX_APP_SERVER_FORCE_CLI", null); Environment.SetEnvironmentVariable("CODEX_APP_SERVER_WS_URL", "ws://127.0.0.1:12345/private-fixture"); Check(Rejected(() => DesktopSessionFiles.ValidateExternalStartup(target)), "External WebSocket override was overwritten.");
                }
                finally { Environment.SetEnvironmentVariable("CODEX_APP_SERVER_WS_URL", ws); Environment.SetEnvironmentVariable("CODEX_APP_SERVER_FORCE_CLI", force); Environment.SetEnvironmentVariable("CODEX_CLI_PATH", cli); }
            });
            test("Desktop bootstrap exclusion is bound to host path, creation identity and Windows session", delegate
            {
                var bootstrap = new DesktopProcessIdentity { Pid = 100, StartedUtc = "2026-01-01T00:00:00.0000000Z", SessionId = 5, ExecutablePath = Path.Combine(Path.GetTempPath(), "private-fixture", "DesktopRuntimeHost.exe") };
                Check(DesktopSessionFiles.ValidBootstrap(bootstrap, bootstrap.ExecutablePath, 5), "Valid host bootstrap was rejected.");
                Check(!DesktopSessionFiles.ValidBootstrap(bootstrap, Path.Combine(Path.GetTempPath(), "private-fixture", "ChatGPT.exe"), 5) && !DesktopSessionFiles.ValidBootstrap(bootstrap, bootstrap.ExecutablePath, 6), "Unrelated App/session was excluded as a bootstrap.");
                bootstrap.StartedUtc = "invalid"; Check(!DesktopSessionFiles.ValidBootstrap(bootstrap, bootstrap.ExecutablePath, 5), "Bootstrap missing creation identity passed.");
            });
            test("Desktop recovery early ACK is separate from confirmed readiness", delegate
            {
                string id = Guid.NewGuid().ToString("N");
                var early = new DesktopSessionState { SessionId = id, Generation = 1, State = "starting", RecoveryAccepted = true, ControlEnabled = true };
                Check(!DesktopRecoveryReadiness.Ready(early, id, 1), "Accepted startup was prematurely reported ready.");
                var touched = new HashSet<string> { id }; int reads = 0;
                DesktopRecoveryReadiness.WaitAsync(touched, id, 1, () => true, token =>
                {
                    reads++;
                    return Task.FromResult(reads == 1 ? early : new DesktopSessionState { SessionId = id, Generation = 2, State = "running", Known = true, RecoveryAccepted = true, ControlEnabled = true, ActiveTurns = 1 });
                }, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1), CancellationToken.None).GetAwaiter().GetResult();
                Check(reads == 2 && !touched.Contains(id), "Fresh known running readiness was not adopted, or idle was incorrectly required.");
            });
            test("Desktop recovery readiness refreshes membership after generation races", delegate
            {
                string id = Guid.NewGuid().ToString("N"); var touched = new HashSet<string> { id }; int reads = 0;
                DesktopRecoveryReadiness.WaitAsync(touched, id, 2, () => true, token =>
                {
                    reads++;
                    if (reads == 2) throw new IOException("Fake stale-generation status.");
                    return Task.FromResult(new DesktopSessionState { SessionId = id, Generation = reads == 1 ? 1 : 3, State = "running", Known = true, RecoveryAccepted = true, ControlEnabled = true });
                }, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1), CancellationToken.None).GetAwaiter().GetResult();
                Check(reads == 3 && !touched.Contains(id), "A stale generation was accepted or fresh status was not retried.");
            });
            test("Desktop recovery timeout preserves touched ownership for a later inspection", delegate
            {
                string id = Guid.NewGuid().ToString("N"); var touched = new HashSet<string> { id }; int reads = 0;
                Check(Rejected(() => DesktopRecoveryReadiness.WaitAsync(touched, id, 1, () => true, token =>
                {
                    reads++; return Task.FromResult(new DesktopSessionState { SessionId = id, Generation = 1, State = "starting", RecoveryAccepted = true, ControlEnabled = true });
                }, TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(2), CancellationToken.None).GetAwaiter().GetResult()), "Unknown startup never timed out.");
                Check(touched.Contains(id) && reads > 0, "Timeout discarded recovery ownership.");
            });
            test("Desktop recovery readiness stops on configuration or host control revocation", delegate
            {
                foreach (bool globalRevocation in new[] { true, false })
                {
                    string id = Guid.NewGuid().ToString("N"); var touched = new HashSet<string> { id }; int reads = 0; bool enabled = true;
                    Check(Rejected(() => DesktopRecoveryReadiness.WaitAsync(touched, id, 1, () => enabled, token =>
                    {
                        reads++; if (globalRevocation) enabled = false;
                        return Task.FromResult(new DesktopSessionState { SessionId = id, Generation = 1, State = "running", Known = true, RecoveryAccepted = true, ControlEnabled = globalRevocation });
                    }, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1), CancellationToken.None).GetAwaiter().GetResult()), "Revoked recovery was reported ready.");
                    Check(touched.Contains(id) && reads == 1, "Revocation lost ownership or continued polling.");
                }
            });
            test("Desktop refresh proves ancestry only for genuinely new App identities", delegate
            {
                string executable = Path.Combine(Path.GetTempPath(), "private-fixture", "ChatGPT.exe");
                var before = new DesktopProcessIdentity { Pid = 1, StartedUtc = "2026-01-01T00:00:00.0000000Z", ExecutablePath = executable, SessionId = 1 };
                var tracked = new DesktopProcessIdentity { Pid = 2, StartedUtc = "2026-01-01T00:00:01.0000000Z", ExecutablePath = executable, SessionId = 1 };
                var baseline = new[] { before }; var owned = new[] { tracked };
                Check(!DesktopRuntimeHost.NeedsAncestryProof(before, executable, baseline, owned) && !DesktopRuntimeHost.NeedsAncestryProof(tracked, executable, baseline, owned), "Fresh unchanged identities repeated expensive ancestry walks.");
                var reused = new DesktopProcessIdentity { Pid = tracked.Pid, StartedUtc = "2026-01-01T00:00:02.0000000Z", ExecutablePath = executable, SessionId = 1 };
                var unowned = new DesktopProcessIdentity { Pid = 3, StartedUtc = reused.StartedUtc, ExecutablePath = executable, SessionId = 1 };
                Check(DesktopRuntimeHost.NeedsAncestryProof(reused, executable, baseline, owned) && DesktopRuntimeHost.NeedsAncestryProof(unowned, executable, baseline, owned), "A reused PID or unregistered App bypassed ancestry validation.");
            });
            string app = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FakeDesktopApp.exe"), backend = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FakeManagedBackend.exe");
            if (File.Exists(app) && File.Exists(backend))
            {
                test("Desktop native WebSocket matches the App handshake without initialized notification", delegate
                {
                    using (var fixture = new SocketFixture(app, backend, "desktop-handshake"))
                    {
                        Check(!fixture.Member.Disconnected && fixture.Member.Bridge.TransportReady && DesktopSessionFiles.Quiet(fixture.Member.Status()),
                            "A connected desktop using its actual handshake stayed permanently unready.");
                        Check(DesktopSessionFiles.Flag(fixture.Member.Prepare(), "prepared"), "The notification-free desktop could not prepare a fresh idle state.");
                        fixture.Member.Unprepare(); Check(fixture.Member.Bridge.TransportReady && !fixture.Member.Feed.Ended, "Checking readiness or releasing prepare closed the desktop backend.");
                    }
                });
                foreach (string mode in new[] { "active", "pending", "background" })
                {
                    string captured = mode;
                    test("Desktop native WebSocket EOF preserves owned " + captured + " backend while fake App is alive", delegate
                    {
                        using (var fixture = new SocketFixture(app, backend, captured))
                        {
                            var before = fixture.Member.Status(); Check(!DesktopSessionFiles.Flag(before, "idle") && !fixture.Member.Running.IsCompleted, "Live orphan work was discarded.");
                            var prepare = fixture.Member.Prepare(); Check(!DesktopSessionFiles.Quiet(prepare) && !fixture.Member.Running.IsCompleted && !fixture.Member.Feed.Ended && !fixture.App.HasExited, "Socket EOF or nonquiet prepare ended a busy backend.");
                            fixture.Member.Unprepare();
                        }
                    });
                }
                test("Desktop disabled control preserves live orphan work until its owner really exits", delegate
                {
                    using (var fixture = new SocketFixture(app, backend, "active"))
                    {
                        DesktopSessionFiles.Write(Path.Combine(fixture.Root, "authorization.json"), new { Enabled = false, AuthorizationGeneration = "private-fixture" });
                        Check(Rejected(() => fixture.Member.Prepare()) && !fixture.Member.Feed.Ended && !fixture.Member.Running.IsCompleted && !fixture.App.HasExited, "Revoked control closed live orphan work.");
                    }
                });
                test("Desktop native backend nonzero exit never becomes successful quiescence", delegate
                {
                    using (var fixture = new SocketFixture(app, backend, "bad-exit"))
                    {
                        Check(DesktopSessionFiles.Quiet(fixture.Member.Prepare()), "Idle fake backend did not prepare.");
                        var result = fixture.Member.Commit();
                        Check(!fixture.Member.Exited0 && !DesktopSessionFiles.Quiet(result) && !fixture.Member.CommitConfirmed, "Nonzero backend exit was accepted as EOF0.");
                    }
                });
                test("Desktop owner retirement observes nonzero backend exit without granting quiescence", delegate
                {
                    using (var fixture = new SocketFixture(app, backend, "bad-exit"))
                    using (var unrelated = Process.GetCurrentProcess())
                    {
                        Check(!DesktopRuntimeHost.OwnedAppsExited(fixture.App, new[] { fixture.App }) && !fixture.Member.BackendExited, "Live owner/backend was retired.");
                        File.WriteAllText(fixture.Marker + ".stop", "normal-owner-exit");
                        Check(fixture.App.WaitForExit(5000), "Fixture owner did not exit.");
                        Check(DesktopRuntimeHost.OwnedAppsExited(fixture.App, new[] { fixture.App }) && !unrelated.HasExited, "An unrelated live process prevented owned-App retirement.");
                        Check(!fixture.Member.Feed.Ended && !fixture.Member.BackendExited, "Merely checking owner exit ended its backend.");
                        fixture.Member.EndForConfirmedOwnerExit(); Check(fixture.Member.Running.Wait(7000), "Owned backend did not exit after EOF.");
                        Check(fixture.Member.BackendExited && fixture.Member.Bridge.BackendExitObserved && fixture.Member.Bridge.ExitCode == 7 && !fixture.Member.Exited0,
                            "Nonzero exit must release the empty host without granting normal-exit evidence.");
                        int closes = 0;
                        var transaction = new DesktopSessionTransaction { Enabled = () => true, AppGone = () => true,
                            Freeze = () => { }, Unfreeze = () => { }, Persist = () => { }, Shutdown = () => { closes++; return new DesktopShutdownResult { Success = true, ExitCode = 0 }; } };
                        transaction.Admit(fixture.Member);
                        Check(!transaction.Quiesce(() => true) && transaction.Stage != "quiesced" && closes == 0, "Retirement became permission to change accounts.");
                    }
                });
                test("Desktop owner retirement requires every retained owned App handle to report exit", delegate
                {
                    using (var fixture = new SocketFixture(app, backend, "active"))
                    using (var liveChild = Process.GetCurrentProcess())
                    {
                        File.WriteAllText(fixture.Marker + ".stop", "normal-owner-exit"); Check(fixture.App.WaitForExit(5000), "Fixture owner did not exit.");
                        Check(!DesktopRuntimeHost.OwnedAppsExited(fixture.App, new[] { fixture.App, liveChild }), "A retained live child was ignored.");
                        Check(!DesktopRuntimeHost.OwnedAppsExited(fixture.App, new Process[0]) && !DesktopRuntimeHost.OwnedAppsExited(fixture.App, new Process[] { fixture.App, null }),
                            "Missing owned-process evidence was treated as a confirmed exit.");
                        Check(!fixture.Member.Feed.Ended && !fixture.Member.Running.IsCompleted, "An exit check interrupted the active fixture backend.");
                    }
                });
                test("Desktop unexpected backend exit can retire its host only after owned App exit", delegate
                {
                    using (var fixture = new SocketFixture(app, backend, "unexpected-exit"))
                    {
                        fixture.Member.Forward(ManagedBridge.Encode(new { id = "fixture:crash", method = "test/crash", @params = new { } }));
                        Check(fixture.Member.Running.Wait(7000), "Fixture backend did not exit unexpectedly.");
                        Check(fixture.Member.BackendExited && fixture.Member.Bridge.ExitCode == 7 && !fixture.Member.Exited0 && !fixture.Member.OwnerExitCleanup,
                            "Unexpected exit was promoted to a confirmed normal exit.");
                        Check(!DesktopRuntimeHost.OwnedAppsExited(fixture.App, new[] { fixture.App }), "A surviving App was ignored during retirement.");
                        File.WriteAllText(fixture.Marker + ".stop", "normal-owner-exit"); Check(fixture.App.WaitForExit(5000), "Fixture owner did not exit.");
                        Check(DesktopRuntimeHost.OwnedAppsExited(fixture.App, new[] { fixture.App }) && fixture.Member.BackendExited && !fixture.Member.Exited0,
                            "A fully gone unexpected-exit session still required normal-exit evidence to release its supervisor.");
                    }
                });
                test("Desktop owner retirement does not infer exit from an unavailable Process handle", delegate
                {
                    using (var fixture = new SocketFixture(app, backend, "idle"))
                    {
                        File.WriteAllText(fixture.Marker + ".stop", "normal-owner-exit"); Check(fixture.App.WaitForExit(5000), "Fixture owner did not exit.");
                        var unavailable = new Process(); unavailable.Dispose();
                        Check(!DesktopRuntimeHost.OwnedAppsExited(fixture.App, new[] { fixture.App, unavailable }), "An unavailable handle was accepted as positive exit evidence.");
                        Check(!fixture.Member.BackendExited && !fixture.Member.Exited0, "An unobserved backend exit was accepted.");
                    }
                });
            }
        }
    }
}
