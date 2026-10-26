using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CodexAccountSwitcher
{
    public static class ManagedBridgeTests
    {
        private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        private static bool Flag(Dictionary<string, object> value, string key) { object item; return value.TryGetValue(key, out item) && Object.Equals(item, true); }
        private static string Text(Dictionary<string, object> value, string key) { object item; return value.TryGetValue(key, out item) ? Convert.ToString(item) : null; }
        private sealed class Feed : TextReader
        {
            private readonly BlockingCollection<string> lines = new BlockingCollection<string>();
            private string line; private int index;
            public void Send(object value) { lines.Add(ManagedBridge.Encode(value) + "\n"); }
            public void End() { lines.CompleteAdding(); }
            public override int Read()
            {
                while (line == null || index >= line.Length)
                { if (!lines.TryTake(out line, Timeout.Infinite)) return -1; index = 0; }
                return line[index++];
            }
        }
        private sealed class Capture : TextWriter
        {
            public readonly ConcurrentQueue<string> Lines = new ConcurrentQueue<string>();
            internal Action<string> BeforeWrite;
            public override Encoding Encoding { get { return Encoding.UTF8; } }
            public override void WriteLine(string value) { if (BeforeWrite != null) BeforeWrite(value); Lines.Enqueue(value); }
        }
        private sealed class Fixture : IDisposable
        {
            public readonly string Root, Authorization;
            public readonly Feed Input = new Feed();
            public readonly Capture Output = new Capture();
            public readonly ManagedBridge Bridge;
            private readonly Task running;
            private readonly bool host;
            public Fixture(string executable, string mode, bool hostControl = false, bool completeInitialization = true, bool requireInitializedNotification = true, ContinuationAdmission admission = null, Func<bool> continuationOwner = null, bool observeFrontendContext = true, IDictionary<string, string> backendEnvironment = null)
            {
                host = hostControl;
                Root = Path.Combine(Path.GetTempPath(), "CodexAccountSwitcher-bridge-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Root); Authorization = Path.Combine(Root, "authorization.json");
                File.WriteAllText(Authorization, ManagedBridge.Encode(new { Enabled = true, AuthorizationGeneration = "fake-authorization" }));
                string controller; using (var process = Process.GetCurrentProcess()) controller = process.MainModule.FileName;
                Bridge = new ManagedBridge(new ManagedBridgeOptions { BackendExecutable = executable, BackendArguments = new[] { mode }, CatalogDirectory = Path.Combine(Root, "runtimes"),
                    ControllerExecutable = controller, AuthorizationFile = Authorization, AuthorizationGeneration = "fake-authorization",
                    EnableHostControl = host, DisableControlPipe = host, RequireInitializedNotification = requireInitializedNotification,
                    OwnerProcessId = host ? Process.GetCurrentProcess().Id : 0, Admission = admission, IsContinuationOwner = continuationOwner,
                    BackendEnvironment = backendEnvironment }, Input, Output);
                running = Task.Factory.StartNew(Bridge.Run, TaskCreationOptions.LongRunning);
                Input.Send(new { id = "client:initialize", method = "initialize", @params = new { capabilities = new { experimentalApi = true } } });
                Wait(() => Output.Lines.Any(line => line.Contains("client:initialize")), "Mock initialization did not arrive.");
                if (completeInitialization)
                {
                    Input.Send(new { method = "initialized" });
                    Wait(() => { try { return Flag(Status(), "known"); } catch { return false; } }, "Managed status did not initialize.", mode == "query-error");
                    if (mode.StartsWith("continuation") && admission == null && observeFrontendContext)
                    {
                        Input.Send(new { id = "fixture:initial-frontend-resume", method = "thread/resume", @params = new
                            { threadId = "11111111-1111-4111-8111-111111111111", excludeTurns = true } });
                        Wait(() => Output.Lines.Any(line => line.Contains("fixture:initial-frontend-resume")), "The actual fixture frontend context did not arrive.");
                    }
                }
            }
            public Dictionary<string, object> Request(string method, string generation = null, int timeout = 3000)
            {
                using (var pipe = new NamedPipeClientStream(".", Bridge.PipeName, PipeDirection.InOut))
                {
                    pipe.Connect(3000);
                    using (var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true))
                    using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true })
                    {
                        writer.WriteLine(ManagedBridge.Encode(new { id = "controller-test", method = method, @params = new { expectedGeneration = generation ?? Bridge.InstanceId, timeoutMs = timeout } }));
                        var read = Task.Factory.StartNew(() => reader.ReadLine());
                        Check(read.Wait(6000), "Controller request did not return within its bound.");
                        return ManagedBridge.Parse(read.Result);
                    }
                }
            }
            public Dictionary<string, object> Result(string method, int timeout = 3000)
            { var response = Request(method, null, timeout); object result; Check(response.TryGetValue("result", out result), "Controller operation failed."); return result as Dictionary<string, object>; }
            public Dictionary<string, object> Status() { return host ? Bridge.HostControl("status", 3000) : Result("status"); }
            public void Dispose()
            {
                Input.End(); Check(running.Wait(7000), "Owned mock backend did not exit naturally.");
                string parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
                Check(String.Equals(Path.GetDirectoryName(Path.GetFullPath(Root)), parent, StringComparison.OrdinalIgnoreCase), "Unexpected test cleanup path.");
                if (Directory.Exists(Root)) Directory.Delete(Root, true);
            }
        }
        private static void Wait(Func<bool> condition, string message, bool skip = false)
        { if (skip) return; var clock = Stopwatch.StartNew(); while (clock.ElapsedMilliseconds < 6000) { if (condition()) return; Thread.Sleep(30); } throw new Exception(message); }
        private static bool TrySetConsoleInputEncoding(Encoding encoding)
        {
            try { Console.InputEncoding = encoding; return true; }
            // The GUI self-test entry has no console handle. Its Unicode roundtrip
            // still runs; the separate console counterexample forces ASCII.
            catch (IOException) { return false; }
        }

        public static void Register(Action<string, Action> test)
        {
            string fake = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FakeManagedBackend.exe");
            if (!File.Exists(fake)) return;
            RegisterContinuationTests(test, fake);
            test("Desktop initialization succeeds after the forwarded reply without an optional initialized notification", delegate
            {
                using (var fixture = new Fixture(fake, "idle", true, false, false))
                {
                    Wait(() => fixture.Bridge.TransportReady, "The Desktop reply-only handshake never became transport ready.");
                    var status = fixture.Status();
                    Check(Flag(status, "transportReady") && Flag(status, "known") && Flag(status, "idle"), "The actual Desktop handshake pattern remained initializing.");
                    Check(!fixture.Output.Lines.Any(line => line.Contains("fixture:initialized")), "The fixture unexpectedly supplied the optional notification.");
                }
            });
            test("Desktop transport readiness does not claim a failed work-status query is known or idle", delegate
            {
                using (var fixture = new Fixture(fake, "query-error", true, false, false))
                {
                    Wait(() => fixture.Bridge.TransportReady, "The valid Desktop handshake was not transport ready.");
                    var status = fixture.Status();
                    Check(Flag(status, "transportReady") && !Flag(status, "known") && !Flag(status, "idle"), "Transport readiness fabricated work-state proof.");
                    var prepared = fixture.Bridge.HostControl("prepare", 3000);
                    Check(!Flag(prepared, "prepared") && !Flag(prepared, "idle") && !Flag(prepared, "eofCommitted"), "Unknown work was allowed to prepare/commit an account switch.");
                }
            });
            test("Desktop rejected initialization never grants transport or work-state readiness", delegate
            {
                using (var fixture = new Fixture(fake, "initialize-error", true, false, false))
                {
                    var status = fixture.Status();
                    Check(!fixture.Bridge.TransportReady && !Flag(status, "transportReady") && !Flag(status, "known") && !Flag(status, "idle") && Text(status, "reason") == "initializeFailed",
                        "An initialization error was accepted as a completed handshake.");
                }
            });
            test("Desktop transport readiness waits for initialization reply forwarding to finish", delegate
            {
                string root = Path.Combine(Path.GetTempPath(), "CodexAccountSwitcher-reply-barrier-" + Guid.NewGuid().ToString("N"));
                ManagedBridge.PrivateDirectory(root);
                var input = new Feed(); var output = new Capture();
                using (var writing = new ManualResetEventSlim(false))
                using (var deliver = new ManualResetEventSlim(false))
                {
                    output.BeforeWrite = line => { if (line.Contains("fixture:initialize")) { writing.Set(); Check(deliver.Wait(5000), "Reply forwarding fixture expired."); } };
                    var bridge = new ManagedBridge(new ManagedBridgeOptions { BackendExecutable = fake, BackendArguments = new[] { "idle" }, CatalogDirectory = root,
                        EnableHostControl = true, DisableControlPipe = true, RequireInitializedNotification = false, OwnerProcessId = Process.GetCurrentProcess().Id }, input, output);
                    var running = Task.Factory.StartNew(bridge.Run, TaskCreationOptions.LongRunning);
                    try
                    {
                        input.Send(new { id = "fixture:initialize", method = "initialize", @params = new { } });
                        Check(writing.Wait(3000), "The initialization reply did not reach the frontend writer.");
                        var pending = bridge.HostControl("status", 1000);
                        Check(!bridge.TransportReady && !Flag(pending, "transportReady") && !Flag(pending, "known"), "Observing an undelivered reply granted readiness.");
                        deliver.Set(); Wait(() => bridge.TransportReady, "A delivered reply did not grant transport readiness.");
                        Check(Flag(bridge.HostControl("status", 3000), "known"), "The forwarded reply did not permit read-only work queries.");
                    }
                    finally
                    {
                        deliver.Set(); input.End(); Check(running.Wait(7000), "Reply-barrier backend did not exit after normal EOF.");
                        Check(Path.GetDirectoryName(Path.GetFullPath(root)).TrimEnd(Path.DirectorySeparatorChar) == Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), "Unexpected reply fixture cleanup path.");
                        Directory.Delete(root, true);
                    }
                }
            });
            test("Managed status waits for the frontend initialized notification before injecting queries", delegate
            {
                using (var fixture = new Fixture(fake, "idle", true, false))
                {
                    var status = fixture.Status();
                    Check(new ManagedBridgeOptions().RequireInitializedNotification && !fixture.Bridge.TransportReady && !Flag(status, "transportReady"), "The editor default no longer requires its initialized notification.");
                    Check(!Flag(status, "known") && !Flag(status, "idle") && Text(status, "reason") == "initializing", "The initialize reply alone granted query readiness.");
                    fixture.Input.Send(new { id = "fixture:query-count", method = "test/query-count", @params = new { } });
                    Wait(() => fixture.Output.Lines.Any(line => line.Contains("fixture:query-count") && line.Contains("result")), "The query-count fixture did not answer.");
                    var response = ManagedBridge.Parse(fixture.Output.Lines.First(line => line.Contains("fixture:query-count") && line.Contains("result")));
                    var result = response["result"] as Dictionary<string, object>;
                    Check(Convert.ToInt32(result["count"]) == 0, "A controller query was injected before the frontend completed its handshake.");
                    fixture.Input.Send(new { method = "initialized" });
                    Wait(() => Flag(fixture.Status(), "known"), "A completed frontend handshake did not become queryable.");
                }
            });
            foreach (bool desktop in new[] { false, true })
            {
                bool capturedDesktop = desktop;
                test("Managed bridge preserves UTF8 non-ASCII JSON through " + (desktop ? "Desktop host forwarding" : "editor stdio forwarding"), delegate
                {
                    Encoding previous = Console.InputEncoding;
                    bool changedEncoding = false;
                    try
                    {
                    changedEncoding = TrySetConsoleInputEncoding(Encoding.ASCII);
                    using (var fixture = new Fixture(fake, "idle", capturedDesktop))
                    {
                        string marker = "合成測試 😀 café e\u0301 日本語";
                        var request = new { id = "fixture:utf8", method = "test/echo", @params = new { marker = marker } };
                        if (capturedDesktop) fixture.Bridge.HostForward(ManagedBridge.Encode(request)); else fixture.Input.Send(request);
                        Wait(() => fixture.Output.Lines.Any(line => line.Contains("fixture:utf8") && line.Contains("result")), "Non-ASCII request did not return.");
                        var response = ManagedBridge.Parse(fixture.Output.Lines.First(line => line.Contains("fixture:utf8") && line.Contains("result")));
                        object result; Check(response.TryGetValue("result", out result) && Text(result as Dictionary<string, object>, "marker") == marker,
                            "UTF8 request text was replaced, truncated, or encoded using the host code page.");
                        Check(!File.ReadAllText(Path.Combine(fixture.Root, "runtimes", fixture.Bridge.InstanceId + ".json")).Contains(marker), "The Unicode protocol content was persisted.");
                    }
                    }
                    finally { if (changedEncoding) Console.InputEncoding = previous; }
                });
            }
            test("Managed bridge controller preserves non-ASCII thread identifiers through status and EOF", delegate
            {
                Encoding previous = Console.InputEncoding;
                bool changedEncoding = false;
                try
                {
                changedEncoding = TrySetConsoleInputEncoding(Encoding.ASCII);
                using (var fixture = new Fixture(fake, "idle", true))
                {
                    fixture.Input.Send(new { id = "fixture:unicode-thread", method = "test/unicode-thread", @params = new { } });
                    Wait(() => fixture.Output.Lines.Any(line => line.Contains("fixture:unicode-thread") && line.Contains("result")), "The Unicode control-query fixture did not initialize.");
                    Check(Flag(fixture.Status(), "known") && Flag(fixture.Status(), "idle"), "A UTF8 controller query changed the loaded thread identifier.");
                    Check(Flag(fixture.Bridge.HostControl("prepare", 3000), "prepared"), "A UTF8 thread could not prepare.");
                    Check(Text(fixture.Bridge.HostControl("commit", 3000), "state") == "quiesced", "A UTF8 thread did not exit normally after verified idle.");
                }
                }
                finally { if (changedEncoding) Console.InputEncoding = previous; }
            });
            test("Desktop bridge prepare can be released without EOF and without exposing a control pipe", delegate
            {
                using (var f = new Fixture(fake, "idle", true))
                {
                    var prepared = f.Bridge.HostControl("prepare", 3000);
                    Check(Flag(prepared, "prepared") && Flag(prepared, "known") && Flag(prepared, "idle") && !Flag(prepared, "eofCommitted"), "Idle backend did not prepare.");
                    using (var pipe = new NamedPipeClientStream(".", f.Bridge.PipeName, PipeDirection.InOut))
                    {
                        bool rejected = false; try { pipe.Connect(100); } catch (TimeoutException) { rejected = true; }
                        Check(rejected, "Desktop member exposed an unnecessary external control pipe.");
                    }
                    f.Input.Send(new { id = "blocked-before-unprepare", method = "test/echo", @params = new { } });
                    Wait(() => f.Output.Lines.Any(line => line.Contains("blocked-before-unprepare") && line.Contains("error")), "Prepare did not freeze new requests.");
                    var released = f.Bridge.HostControl("unprepare", 3000);
                    Check(!Flag(released, "prepared") && !Flag(released, "eofCommitted"), "Prepare release committed EOF.");
                    f.Input.Send(new { id = "after-unprepare", method = "test/echo", @params = new { } });
                    Wait(() => f.Output.Lines.Any(line => line.Contains("after-unprepare") && line.Contains("result")), "Prepare release did not restore ordinary forwarding.");
                }
            });
            test("Desktop bridge refuses busy prepare and commits a prepared idle backend exactly once", delegate
            {
                using (var f = new Fixture(fake, "active", true))
                {
                    var busy = f.Bridge.HostControl("prepare", 3000);
                    Check(Flag(busy, "known") && !Flag(busy, "idle") && !Flag(busy, "prepared") && !Flag(busy, "eofCommitted"), "Busy prepare changed the backend.");
                    bool rejected = false; try { f.Bridge.HostControl("commit", 3000); } catch (IOException) { rejected = true; }
                    Check(rejected, "Commit accepted a backend which was never prepared.");
                }
                using (var f = new Fixture(fake, "idle", true))
                {
                    f.Bridge.HostControl("prepare", 3000);
                    var result = f.Bridge.HostControl("commit", 3000);
                    Check(Flag(result, "known") && Flag(result, "idle") && Flag(result, "eofCommitted") && Text(result, "state") == "quiesced", "Prepared backend did not exit normally.");
                    Check(Text(f.Bridge.HostControl("commit", 3000), "state") == "quiesced", "Repeated commit was not idempotent.");
                }
            });
            test("Desktop bridge invalidates prepare when a real backend notification arrives", delegate
            {
                using (var f = new Fixture(fake, "idle", true))
                {
                    string marker = Path.Combine(f.Root, "activity.marker");
                    f.Input.Send(new { id = "watch-activity", method = "test/watch-activity", @params = new { marker = marker } });
                    Wait(() => f.Output.Lines.Any(line => line.Contains("watch-activity") && line.Contains("result")), "Activity watcher did not initialize.");
                    Check(Flag(f.Bridge.HostControl("prepare", 3000), "prepared"), "The initial idle prepare failed.");
                    File.WriteAllText(marker, "synthetic activity");
                    Wait(() => f.Output.Lines.Any(line => line.Contains("thread/status/changed")), "Delayed backend activity did not arrive.");
                    var result = f.Bridge.HostControl("commit", 3000);
                    Check(!Flag(result, "known") && !Flag(result, "eofCommitted"), "A stale prepare committed EOF after new backend activity.");
                    f.Bridge.HostControl("unprepare", 3000);
                    f.Input.Send(new { id = "after-stale-prepare", method = "test/echo", @params = new { } });
                    Wait(() => f.Output.Lines.Any(line => line.Contains("after-stale-prepare") && line.Contains("result")), "Stale prepare damaged the live backend.");
                }
            });
            test("Desktop bridge revocation between prepare and commit preserves ordinary forwarding", delegate
            {
                using (var f = new Fixture(fake, "idle", true))
                {
                    Check(Flag(f.Bridge.HostControl("prepare", 3000), "prepared"), "Initial prepare failed.");
                    File.WriteAllText(f.Authorization, ManagedBridge.Encode(new { Enabled = false, AuthorizationGeneration = "fake-authorization" }));
                    bool rejected = false; try { f.Bridge.HostControl("commit", 3000); } catch (IOException) { rejected = true; }
                    Check(rejected, "Revocation did not stop EOF commitment.");
                    Check(!Flag(f.Bridge.HostControl("unprepare", 3000), "eofCommitted"), "Revocation recovery reported EOF.");
                    f.Input.Send(new { id = "ordinary-after-host-revoke", method = "test/echo", @params = new { } });
                    Wait(() => f.Output.Lines.Any(line => line.Contains("ordinary-after-host-revoke") && line.Contains("result")), "Revoked host control blocked ordinary traffic.");
                }
            });
            test("Managed bridge forwards client IDs and server callbacks without exposing controller replies", delegate
            {
                using (var fixture = new Fixture(fake, "idle"))
                {
                    fixture.Input.Send(new { id = 17, method = "test/echo", @params = new { marker = "fake-prompt-marker" } });
                    Wait(() => fixture.Output.Lines.Any(line => line.Contains("fake-prompt-marker") && line.Contains("17")), "Client response changed.");
                    fixture.Input.Send(new { id = "client:callback", method = "test/callback", @params = new { } });
                    Wait(() => fixture.Output.Lines.Any(line => line.Contains("callback-1")), "Server callback was swallowed.");
                    Check(!Flag(fixture.Status(), "idle"), "Pending server callback was treated as idle.");
                    fixture.Input.Send(new { id = "callback-1", result = new { answers = new { } } });
                    Wait(() => fixture.Output.Lines.Any(line => line.Contains("test/callback-accepted")), "Client callback reply was swallowed.");
                    Check(!fixture.Output.Lines.Any(line => line.Contains("__cas_control_")), "Internal controller response leaked to the editor.");
                    string catalog = File.ReadAllText(Path.Combine(fixture.Root, "runtimes", fixture.Bridge.InstanceId + ".json"));
                    Check(!catalog.Contains("fake-prompt-marker"), "Protocol content was persisted in the catalog.");
                }
            });
            test("Managed bridge interrupts the same active runtime then quiesces by EOF and requests a reload", delegate
            {
                using (var fixture = new Fixture(fake, "active"))
                {
                    Check(!Flag(fixture.Result("quiesce"), "idle"), "An active runtime was quiesced.");
                    Check(Flag(fixture.Result("interrupt"), "idle"), "Normal interruption did not finish.");
                    var quiet = fixture.Result("quiesce");
                    Check(Flag(quiet, "known") && Flag(quiet, "idle") && Text(quiet, "state") == "quiesced", "EOF quiesce failed.");
                    var resumed = fixture.Result("resume");
                    Check(Flag(resumed, "resumeRequested") && Flag(resumed, "requiresReload"), "Resume did not request the supported reload path.");
                    fixture.Input.Send(new { id = "client:blocked", method = "turn/start", @params = new { } });
                    Wait(() => fixture.Output.Lines.Any(line => line.Contains("client:blocked") && line.Contains("error")), "New work was not rejected while quiesced.");
                }
            });
            test("Managed bridge paginates loaded threads and background terminals before permitting quiesce", delegate
            {
                using (var fixture = new Fixture(fake, "background"))
                {
                    var status = fixture.Status(); Check(Flag(status, "known") && !Flag(status, "idle") && Text(status, "backgroundTerminals") == "1", "A later background page was ignored.");
                    Check(Text(fixture.Result("quiesce"), "state") == "running", "Background work was stopped.");
                }
            });
            test("Managed bridge rejects unknown runtime state, stale generations, and duplicate process handles", delegate
            {
                using (var fixture = new Fixture(fake, "query-error"))
                { Check(!Flag(fixture.Result("quiesce"), "known"), "Unsupported runtime queries permitted quiesce."); }
                using (var fixture = new Fixture(fake, "idle"))
                {
                    Check(fixture.Request("quiesce", "stale-generation").ContainsKey("error"), "A stale generation controlled the runtime.");
                    fixture.Input.Send(new { id = "spawn1", method = "process/spawn", @params = new { processHandle = "same-fake-handle" } });
                    Wait(() => fixture.Output.Lines.Any(line => line.Contains("spawn1")), "First spawn response missing.");
                    fixture.Input.Send(new { id = "spawn2", method = "process/spawn", @params = new { processHandle = "same-fake-handle" } });
                    Wait(() => fixture.Output.Lines.Any(line => line.Contains("spawn2")), "Duplicate spawn response missing.");
                    Check(!Flag(fixture.Result("quiesce"), "known"), "Duplicate handles erased an active process.");
                }
            });
            test("Managed bridge never reports a timed-out or nonzero backend exit as quiesced", delegate
            {
                using (var fixture = new Fixture(fake, "slow-exit"))
                { var result = fixture.Result("quiesce", 100); Check(!Flag(result, "known") && Text(result, "state") == "unknown", "A timed-out backend exit was treated as success."); }
                using (var fixture = new Fixture(fake, "bad-exit"))
                { Check(!Flag(fixture.Result("quiesce"), "known"), "A nonzero backend exit was treated as success."); }
            });
            test("Managed bridge revocation immediately blocks control while allowing ordinary editor requests", delegate
            {
                using (var fixture = new Fixture(fake, "idle"))
                {
                    File.WriteAllText(fixture.Authorization, ManagedBridge.Encode(new { Enabled = false, AuthorizationGeneration = "fake-authorization" }));
                    Check(!Flag(fixture.Status(), "controlEnabled"), "Revocation was not visible.");
                    Check(fixture.Request("quiesce").ContainsKey("error"), "Revoked control still quiesced the runtime.");
                    fixture.Input.Send(new { id = "client:after-revoke", method = "test/echo", @params = new { } });
                    Wait(() => fixture.Output.Lines.Any(line => line.Contains("client:after-revoke") && line.Contains("result")), "Revocation broke ordinary editor forwarding.");
                }
            });
        }
        private static bool ContinuationRejected(Action action) { try { action(); return false; } catch { return true; } }
        private static Dictionary<string, object> CaptureContinuation(Fixture fixture)
        { return fixture.Bridge.HostContinuation("capture", new Dictionary<string, object> { { "expectedEmail", "fake@example.com" } }, 7000); }
        private static void ContinuationChange(Fixture fixture, string kind)
        {
            string request = "fixture:change:" + Guid.NewGuid().ToString("N");
            fixture.Input.Send(new { id = request, method = "test/continuation-change", @params = new { kind = kind } });
            Wait(() => fixture.Output.Lines.Any(line => line.Contains(request)), "Continuation fixture change was not delivered.");
        }
        private static void StopContinuation(Fixture fixture)
        {
            fixture.Input.Send(new { id = "fixture:interrupt", method = "turn/interrupt", @params = new
                { threadId = "11111111-1111-4111-8111-111111111111", turnId = "22222222-2222-4222-8222-222222222222" } });
            Wait(() => Flag(fixture.Status(), "idle"), "The continuation target did not finish normally.");
        }
        private static void RegisterContinuationTests(Action<string, Action> test, string fake)
        {
            test("Desktop continuation carries broker environment only in memory and strictly classifies legacy auth reads", delegate
            {
                Check(ManagedBridge.IsReadOnlyFrontendRequest("getAuthStatus", null) && ManagedBridge.IsReadOnlyFrontendRequest("getAuthStatus", new Dictionary<string, object> { { "includeToken", true }, { "refreshToken", false } }),
                    "The pinned non-refreshing legacy auth read was not classified as read-only.");
                foreach (var invalid in new[] { new Dictionary<string, object> { { "refreshToken", true } }, new Dictionary<string, object> { { "includeToken", "true" } }, new Dictionary<string, object> { { "unknown", false } } })
                    Check(!ManagedBridge.IsReadOnlyFrontendRequest("getAuthStatus", invalid), "A refreshing, malformed or unknown legacy auth request was classified as read-only.");
                foreach (string method in new[] { "collaborationMode/list", "account/gatewayOAuth/read", "remoteControl/status/read", "windowsSandbox/readiness" })
                {
                    Check(ManagedBridge.IsReadOnlyFrontendRequest(method, null) && ManagedBridge.IsReadOnlyFrontendRequest(method, new Dictionary<string, object>()), "A pinned startup read was not eligible for bounded read-only pending waits.");
                    Check(!ManagedBridge.IsReadOnlyFrontendRequest(method, new Dictionary<string, object> { { "refresh", true } }), "An unsupported startup parameter was classified as read-only.");
                }
                Check(ManagedBridge.IsReadOnlyFrontendRequest("plugin/installed", new Dictionary<string, object> { { "cwds", new[] { Path.GetTempPath() } } }) &&
                    !ManagedBridge.IsReadOnlyFrontendRequest("plugin/installed", new Dictionary<string, object> { { "cwds", new[] { "relative-workspace" } } }) &&
                    !ManagedBridge.IsReadOnlyFrontendRequest("account/gatewayOAuth/write", null), "Startup pending classification diverged from the strict admission read schema.");
                string pipe = @"\\.\pipe\codex-app-tools-" + Guid.NewGuid().ToString("D"), node = Path.Combine(Path.GetTempPath(), "synthetic-bundled-node.exe"), cli = Path.Combine(Path.GetTempPath(), "synthetic-bundled-cli.exe");
                var environment = new Dictionary<string, string> { { "CODEX_APP_TOOLS_PIPE_PATH", pipe }, { "CODEX_MCP_NODE_PATH", node }, { "CODEX_CLI_PATH", cli } };
                using (var fixture = new Fixture(fake, "idle", true, true, true, null, null, true, environment))
                {
                    fixture.Input.Send(new { id = "fixture:environment-proof", method = "test/backend-environment", @params = new { expectedPipe = pipe, expectedNode = node, expectedCli = cli } });
                    Wait(() => fixture.Output.Lines.Any(line => line.Contains("fixture:environment-proof")), "The synthetic backend environment proof did not arrive.");
                    var reply = ManagedBridge.Parse(fixture.Output.Lines.Single(line => line.Contains("fixture:environment-proof")));
                    var result = (Dictionary<string, object>)reply["result"];
                    Check(Flag(result, "pipeMatches") && Flag(result, "nodeMatches") && Flag(result, "cliMatches"), "The backend did not receive its strictly selected in-memory runtime context.");
                    string metadata = ManagedBridge.Encode(fixture.Status()) + String.Join("", Directory.GetFiles(Path.Combine(fixture.Root, "runtimes"), "*.json").Select(File.ReadAllText));
                    Check(!metadata.Contains(pipe) && !metadata.Contains(node) && !metadata.Contains(cli), "Runtime broker or executable environment was persisted into a status/catalog snapshot.");
                }
            });
            test("Desktop continuation records only acknowledged frontend instruction hashes and exact capability semantics", delegate
            {
                using (var source = new Fixture(fake, "continuation-active", true, true, true, null, null, false))
                {
                    Check(ContinuationRejected(() => CaptureContinuation(source)), "A private metadata read created missing frontend evidence.");
                    string instructions = "Synthetic private instructions must never be persisted in a ticket.";
                    var config = new Dictionary<string, object> { { "plugins.codex-app-tools@openai-bundled.mcp_servers.codex_app.enabled_tools", new[] { "read_thread", "send_message_to_thread" } } };
                    source.Input.Send(new { id = "fixture:context-thread-start", method = "thread/start", @params = new { config = config, developerInstructions = instructions } });
                    Wait(() => source.Output.Lines.Any(line => line.Contains("fixture:context-thread-start")), "The frontend start acknowledgement did not arrive.");
                    var capture = CaptureContinuation(source); string summary = Text(capture, "frontendContextJson");
                    Check(Flag(capture, "captured") && summary.Contains("sha256") && summary.Contains("read_thread") && !summary.Contains(instructions) && !ManagedBridge.Encode(capture).Contains(instructions),
                        "The checkpoint omitted capability semantics or retained raw frontend instructions.");
                    StopContinuation(source);
                    source.Input.Send(new { id = "fixture:changed-context", method = "thread/start", @params = new { config = config, developerInstructions = "Different synthetic instructions" } });
                    Wait(() => source.Output.Lines.Any(line => line.Contains("fixture:changed-context")), "The changed context acknowledgement did not arrive.");
                    capture["expectedEmail"] = "fake@example.com";
                    Check(ContinuationRejected(() => source.Bridge.HostContinuation("prepare", capture, 7000)), "Changed frontend instructions preserved continuation authority.");
                    source.Input.Send(new { id = "fixture:unsupported-context", method = "thread/start", @params = new { config = new { unsupported = true }, developerInstructions = instructions } });
                    Wait(() => source.Output.Lines.Any(line => line.Contains("fixture:unsupported-context")), "Ordinary unsupported frontend context did not continue normally.");
                    Check(ContinuationRejected(() => source.Bridge.HostContinuation("prepare", capture, 7000)), "An unsupported later frontend context reused older cached authority.");
                }
            });
            test("Desktop continuation preserves nonpersistent local environments and binds only its exact reserved start", delegate
            {
                Dictionary<string, object> capture;
                using (var source = new Fixture(fake, "continuation-active", true))
                {
                    ContinuationChange(source, "env-local"); capture = CaptureContinuation(source);
                    Check(Flag(capture, "captured") && Text(capture, "frontendContextJson").Contains("local"), "The source local environment was not captured from authenticated metadata.");
                    StopContinuation(source);
                    Check(Flag(source.Bridge.HostContinuation("stopped", capture, 7000), "stopped"), "Source work did not stop normally.");
                }
                string operation = Guid.NewGuid().ToString("N"), thread = Text(capture, "threadId");
                var admission = new ContinuationAdmission(thread, operation, Text(capture, "model"), Text(capture, "effort"), Text(capture, "permissionsJson"),
                    Text(capture, "cwd"), Text(capture, "modelProvider"), ((string[])capture["runtimeWorkspaceRoots"]), Text(capture, "rolloutPath"), Text(capture, "frontendContextJson"), null);
                using (var restored = new Fixture(fake, "continuation-quota-unbound", true, true, true, admission, () => true))
                {
                    restored.Input.Send(new { id = "fixture:restored-local-resume", method = "thread/resume", @params = new { threadId = thread, excludeTurns = true } });
                    Wait(() => restored.Bridge.HasFrontendResumedThread(thread), "The actual restored frontend did not acknowledge its context.");
                    capture["expectedEmail"] = "fake@example.com";
                    var prepared = restored.Bridge.HostContinuation("prepare", capture, 7000);
                    Check(Flag(prepared, "ready") && Flag(prepared, "deferredEnvironmentBinding") && Text(prepared, "frontendContextJson") == Text(capture, "frontendContextJson"),
                        "Missing persisted environment was silently treated as equal or erased from the source authority.");
                    object exactEnvironments = ManagedBridge.Parse(Text(capture, "frontendContextJson"))["environments"];
                    var parameters = ManagedBridge.Parse(ManagedBridge.Encode(new { threadId = thread, clientUserMessageId = operation,
                        input = new[] { new { type = "text", text = "繼續執行" } }, model = "fake-model", effort = "medium", cwd = Path.GetTempPath(),
                        approvalPolicy = "never", approvalsReviewer = "user", sandboxPolicy = new { type = "readOnly" }, environments = exactEnvironments }));
                    foreach (string invalid in new[] { "null", "empty", "remote", "roots" })
                    {
                        var rejected = ManagedBridge.Parse(ManagedBridge.Encode(parameters));
                        if (invalid == "null") rejected["environments"] = null;
                        else if (invalid == "empty") rejected["environments"] = new object[0];
                        else
                        {
                            var environment = (Dictionary<string, object>)((object[])rejected["environments"])[0];
                            if (invalid == "remote") environment["environmentId"] = "remote-fixture";
                            else environment["runtimeWorkspaceRoots"] = new[] { Path.Combine(Path.GetTempPath(), "unapproved-root") };
                        }
                        string requestId = "fixture:bad-environment:" + invalid;
                        restored.Input.Send(new { id = requestId, method = "turn/start", @params = rejected });
                        Wait(() => restored.Output.Lines.Any(line => line.Contains(requestId)), "The altered environment packet did not finish.");
                        Check(!admission.WasClaimed, "An altered or absent local environment consumed the reserved message.");
                    }
                    restored.Input.Send(new { id = "fixture:exact-environment", method = "turn/start", @params = parameters });
                    Wait(() => admission.ObservedTurnId == "fixture-continued-turn", "The exact captured local environment could not be bound by the reserved start.");
                    Check(admission.WasClaimed && restored.Output.Lines.Count(line => line.Contains("test/continuation-accepted")) == 1,
                        "The deferred environment start was duplicated.");
                }
                foreach (string invalid in new[] { "env-unbound", "env-remote", "env-cwd", "env-multiple", "env-unknown-key" })
                    using (var source = new Fixture(fake, "continuation-active", true))
                    { ContinuationChange(source, invalid); Check(ContinuationRejected(() => CaptureContinuation(source)), "An unverified source environment authorized continuation."); }
            });
            test("Desktop continuation transient read-only queries remain distinguishable from pending writes", delegate
            {
                using (var fixture = new Fixture(fake, "idle", true))
                {
                    string readMarker = Path.Combine(fixture.Root, "release-read"), writeMarker = Path.Combine(fixture.Root, "release-write");
                    Check(!fixture.Bridge.HasOnlyReadOnlyPendingRequests, "No pending work was represented as a read-only query.");
                    fixture.Input.Send(new { id = "hold-read", method = "account/read", @params = new { testHoldMarker = readMarker } });
                    Wait(() => fixture.Bridge.HasOnlyReadOnlyPendingRequests, "The fixed account/read method was not recognized while pending.");
                    Wait(() => { var status = fixture.Status(); return Flag(status, "known") && Convert.ToInt32(status["pendingRequests"]) == 1; },
                        "The pending read was not retained in a stable fresh work status.");
                    fixture.Input.Send(new { id = "hold-write", method = "test/hold-request", @params = new { testHoldMarker = writeMarker } });
                    Wait(() => !fixture.Bridge.HasOnlyReadOnlyPendingRequests, "An unknown pending method was treated as read-only.");
                    File.WriteAllText(writeMarker, "release");
                    Wait(() => fixture.Bridge.HasOnlyReadOnlyPendingRequests, "Completing a non-read-only request did not restore the pending read classification.");
                    File.WriteAllText(readMarker, "release");
                    Wait(() => fixture.Output.Lines.Any(line => line.Contains("hold-read")), "The held read did not complete.");
                    Check(!fixture.Bridge.HasOnlyReadOnlyPendingRequests, "A completed read remained a pending-work exemption.");
                }
            });
            test("Desktop continuation frontend proof excludes private metadata and two owned bridges share one send receipt", delegate
            {
                string operation = Guid.NewGuid().ToString("N"), thread = "11111111-1111-4111-8111-111111111111";
                string policy = "{\"approvalPolicy\":\"never\",\"approvalsReviewer\":\"user\",\"sandbox\":{\"type\":\"readOnly\"},\"permissions\":null}";
                var admission = new ContinuationAdmission(thread, operation, "fake-model", "medium", policy, Path.GetTempPath(), "fake-provider");
                bool firstOwner = false, secondOwner = false;
                using (var first = new Fixture(fake, "continuation-quota", true, true, true, admission, () => firstOwner))
                using (var second = new Fixture(fake, "continuation-quota", true, true, true, admission, () => secondOwner))
                {
                    Check(ContinuationRejected(() => CaptureContinuation(first)) && !first.Bridge.HasFrontendResumedThread(thread), "A private metadata resume manufactured frontend context or ownership.");
                    second.Input.Send(new { id = "fixture:failed-resume", method = "thread/resume", @params = new { threadId = thread } });
                    Wait(() => second.Output.Lines.Any(line => line.Contains("fixture:failed-resume")), "The rejected frontend resume did not return.");
                    Check(!second.Bridge.HasFrontendResumedThread(thread) && !second.Bridge.HasPendingFrontendResumeThread(thread), "A failed frontend resume became owner proof.");
                    first.Input.Send(new { id = "fixture:real-resume", method = "thread/resume", @params = new { threadId = thread, excludeTurns = true } });
                    Wait(() => first.Bridge.HasFrontendResumedThread(thread), "A successful actual frontend resume did not prove ownership.");
                    var parameters = new { threadId = thread, clientUserMessageId = operation, input = new[] { new { type = "text", text = "繼續執行" } },
                        model = "fake-model", effort = "medium", cwd = Path.GetTempPath(), approvalPolicy = "never", approvalsReviewer = "user", sandboxPolicy = new { type = "readOnly" } };
                    second.Input.Send(new { id = "fixture:non-owner-send", method = "turn/start", @params = parameters });
                    Wait(() => second.Output.Lines.Any(line => line.Contains("fixture:non-owner-send")), "A non-owner send was not rejected.");
                    Check(!admission.WasClaimed && admission.ObservedTurnId == null, "An account/helper backend consumed the reserved send.");
                    firstOwner = true;
                    first.Input.Send(new { id = "fixture:one-send", method = "turn/start", @params = parameters });
                    Wait(() => admission.ObservedTurnId == "fixture-continued-turn", "The actual backend acknowledgement did not reach the shared receipt.");
                    Wait(() => first.Output.Lines.Any(line => line.Contains("test/continuation-accepted")), "The synthetic backend did not observe the first turn.");
                    secondOwner = true;
                    second.Input.Send(new { id = "fixture:duplicate-family-send", method = "turn/start", @params = parameters });
                    Wait(() => second.Output.Lines.Any(line => line.Contains("fixture:duplicate-family-send")), "The duplicate family packet did not finish.");
                    Check(first.Output.Lines.Count(line => line.Contains("test/continuation-accepted")) == 1 &&
                        !second.Output.Lines.Any(line => line.Contains("test/continuation-accepted")), "Two independent owned backends accepted the same reserved send.");
                    admission.Cancel(operation);
                    second.Input.Send(new { id = "fixture:duplicate-after-release", method = "turn/start", @params = parameters });
                    Wait(() => second.Output.Lines.Any(line => line.Contains("fixture:duplicate-after-release")), "The late duplicate did not finish.");
                    Check(admission.WasClaimed && admission.ObservedTurnId == "fixture-continued-turn" && !second.Output.Lines.Any(line => line.Contains("test/continuation-accepted")),
                        "Releasing a shared admission reset its exactly-once claim.");
                }
            });
            test("Desktop continuation drops method-only mutations before the backend without consuming its reserved send", delegate
            {
                string operation = Guid.NewGuid().ToString("N");
                var admission = new ContinuationAdmission("11111111-1111-4111-8111-111111111111", operation,
                    "fake-model", "medium", "{\"approvalPolicy\":\"never\",\"approvalsReviewer\":\"user\",\"sandbox\":{\"type\":\"readOnly\"},\"permissions\":null}", Path.GetTempPath(), "fake-provider");
                using (var fixture = new Fixture(fake, "continuation-quota", true, true, true, admission))
                {
                    foreach (string method in new[] { "turn/start", "turn/interrupt", "account/logout", "thread/resume" })
                        fixture.Input.Send(new { method = method, @params = new { threadId = "11111111-1111-4111-8111-111111111111", clientUserMessageId = operation,
                            input = new[] { new { type = "text", text = "繼續執行" } } } });
                    fixture.Input.Send(new { id = "fixture:notification-fence", method = "account/read", @params = new { refreshToken = false } });
                    Wait(() => fixture.Output.Lines.Any(line => line.Contains("fixture:notification-fence")), "The allowed frontend fence did not return.");
                    Check(admission.IsArmed && !admission.WasClaimed && admission.ObservedTurnId == null, "A method-only packet consumed or released the reserved operation.");
                    Check(!fixture.Output.Lines.Any(line => line.Contains("test/notification-forwarded")), "An unguarded notification reached the backend.");
                    Check(fixture.Bridge.TransportReady && Flag(fixture.Status(), "known"), "Dropping invalid notifications broke the initialized backend.");
                }
            });
            test("Desktop continuation captures actual latest turn and safe settings then confirms normal stop and unchanged restoration", delegate
            {
                using (var fixture = new Fixture(fake, "continuation-active", true))
                {
                    ContinuationChange(fixture, "collab-default");
                    var capture = CaptureContinuation(fixture);
                    Check(Flag(capture, "captured") && Text(capture, "threadId") == "11111111-1111-4111-8111-111111111111" &&
                        Text(capture, "turnId") == "22222222-2222-4222-8222-222222222222" && Text(capture, "model") == "fake-model" &&
                        ((string[])capture["runtimeWorkspaceRoots"]).Length == 2 &&
                        Path.IsPathRooted(Text(capture, "rolloutPath")) && Text(capture, "rolloutPath").EndsWith("original-fixture-rollout.jsonl", StringComparison.Ordinal) &&
                        Text(capture, "permissionsJson").Contains("readOnly") && !ManagedBridge.Encode(capture).Contains("fake@example.com"),
                        "The checkpoint lost its bound identity, policies or projected private account data.");
                    Check(!Flag(fixture.Bridge.HostContinuation("stopped", capture, 7000), "stopped"), "A running turn was declared stopped.");
                    StopContinuation(fixture);
                    Check(Flag(fixture.Bridge.HostContinuation("stopped", capture, 7000), "stopped"), "A confirmed interrupted idle turn remained pending.");
                    capture["expectedEmail"] = "fake@example.com";
                    ContinuationChange(fixture, "roots-order");
                    Check(Flag(fixture.Bridge.HostContinuation("prepare", capture, 7000), "ready"), "The unchanged account/model/policy context was not ready.");
                }
            });
            test("Desktop continuation confirms late retained EOF0 without closing stdin again", delegate
            {
                using (var fixture = new Fixture(fake, "slow-exit", true))
                {
                    var before = fixture.Bridge.HostControl("confirmCommit", 100);
                    Check(!Flag(before, "known") && !Flag(before, "eofCommitted") && Flag(fixture.Status(), "idle"),
                        "An exit-only confirmation sent EOF or invented a prepared runtime.");
                    Check(Flag(fixture.Bridge.HostControl("prepare", 3000), "prepared"), "The delayed backend was not prepared.");
                    var pending = fixture.Bridge.HostControl("commit", 100);
                    Check(Flag(pending, "eofCommitted") && !Flag(pending, "known"), "A still-live delayed backend was declared exited.");
                    var confirmed = fixture.Bridge.HostControl("confirmCommit", 3000);
                    Check(Flag(confirmed, "known") && Flag(confirmed, "idle") && Text(confirmed, "state") == "quiesced",
                        "A normal retained process exit arriving after the first deadline could not be confirmed.");
                    Check(Flag(fixture.Bridge.HostControl("commit", 100), "known"), "Repeating commit did not observe the already closed input safely.");
                }
            });
            test("Desktop continuation begins EOF once and confirms its runtime tail separately", delegate
            {
                using (var fixture = new Fixture(fake, "slow-exit", true))
                {
                    Check(Flag(fixture.Bridge.HostControl("prepare", 3000), "prepared"), "The delayed backend was not prepared.");
                    var pending = fixture.Bridge.HostControl("beginCommit", 3000);
                    Check(Flag(pending, "eofCommitted") && !Flag(pending, "known") && Text(pending, "reason") == "backendExitPending",
                        "Beginning EOF waited for or invented runtime completion.");
                    var confirmed = fixture.Bridge.HostControl("confirmCommit", 3000);
                    Check(Flag(confirmed, "known") && Flag(confirmed, "idle") && Text(confirmed, "state") == "quiesced",
                        "A separately observed retained exit0 was not confirmed.");
                    Check(Flag(fixture.Bridge.HostControl("confirmCommit", 100), "eofCommitted"), "Read-only confirmation lost its single original EOF binding.");
                }
            });
            test("Desktop continuation refuses EOF if protocol health changes while its close is queued", delegate
            {
                using (var fixture = new Fixture(fake, "idle", true))
                {
                    string marker = Path.Combine(fixture.Root, "corruption-marker");
                    fixture.Input.Send(new { id = "fixture:watch-corruption", method = "test/watch-corruption", @params = new { marker = marker } });
                    Wait(() => fixture.Output.Lines.Any(line => line.Contains("fixture:watch-corruption")), "The synthetic protocol watcher was not armed.");
                    Check(Flag(fixture.Bridge.HostControl("prepare", 3000), "prepared"), "The healthy backend was not prepared.");
                    var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                    var gate = typeof(ManagedBridge).GetField("backendWriteGate", flags).GetValue(fixture.Bridge);
                    var stateGate = typeof(ManagedBridge).GetField("stateGate", flags).GetValue(fixture.Bridge);
                    var prepared = typeof(ManagedBridge).GetField("preparedSnapshot", flags).GetValue(fixture.Bridge);
                    var state = typeof(ManagedBridge).GetField("state", flags);
                    Task<Dictionary<string, object>> commit = null;
                    Monitor.Enter(gate);
                    try
                    {
                        // Isolate the last pre-EOF interval after the existing
                        // authenticated prepare, without a product test hook.
                        commit = Task.Run(() =>
                        {
                            var snapshot = typeof(ManagedBridge).GetMethod("CommitQuiet", flags).Invoke(fixture.Bridge, new[] { prepared, Stopwatch.StartNew(), (object)4000, true, true });
                            return (Dictionary<string, object>)typeof(ManagedBridge).GetMethod("HostResult", flags).Invoke(fixture.Bridge, new[] { snapshot });
                        });
                        Wait(() => { lock (stateGate) return Object.Equals(state.GetValue(fixture.Bridge), "quiescing"); },
                            "The real close did not queue behind the existing stdin writer.");
                        File.WriteAllText(marker, "synthetic");
                        Wait(() => fixture.Output.Lines.Contains("synthetic-invalid-backend"), "The synthetic protocol failure did not reach the real reader.");
                    }
                    finally { Monitor.Exit(gate); }
                    Check(commit != null && commit.Wait(5000) && !Flag(commit.Result, "eofCommitted") && !Flag(commit.Result, "known"),
                        "A newly unknown backend received EOF after the initial quiet check.");
                }
            });
            test("Desktop continuation never turns a nonzero exit or revoked confirmation into EOF0 proof", delegate
            {
                using (var failed = new Fixture(fake, "bad-exit", true))
                {
                    Check(Flag(failed.Bridge.HostControl("prepare", 3000), "prepared"), "The synthetic failed-exit backend was not prepared.");
                    Check(!Flag(failed.Bridge.HostControl("commit", 3000), "known") &&
                        !Flag(failed.Bridge.HostControl("confirmCommit", 100), "known"), "A retained nonzero exit was declared normal.");
                }
                using (var revoked = new Fixture(fake, "slow-exit", true))
                {
                    Check(Flag(revoked.Bridge.HostControl("prepare", 3000), "prepared"), "The synthetic delayed backend was not prepared.");
                    revoked.Bridge.HostControl("commit", 100);
                    File.WriteAllText(revoked.Authorization, ManagedBridge.Encode(new { Enabled = false, AuthorizationGeneration = "fake-authorization" }));
                    Check(!Flag(revoked.Bridge.HostControl("confirmCommit", 2000), "known"), "Revoked control authorized a fresh successful commit proof.");
                    File.WriteAllText(revoked.Authorization, ManagedBridge.Encode(new { Enabled = true, AuthorizationGeneration = "fake-authorization" }));
                    Check(Flag(revoked.Bridge.HostControl("confirmCommit", 3000), "known"), "Reauthorization could not observe the same already committed normal exit.");
                }
            });
            test("Desktop continuation canonicalizes real backend verbatim rollout metadata before capture and restoration", delegate
            {
                foreach (string change in new[] { "rollout-verbatim", "rollout-verbatim-resume" })
                    using (var fixture = new Fixture(fake, "continuation-active", true))
                    {
                        ContinuationChange(fixture, change);
                        var capture = CaptureContinuation(fixture);
                        string expected = ContinuationAdmission.CanonicalRolloutPath(Path.Combine(Path.GetTempPath(), "original-fixture-rollout.jsonl"));
                        Check(Text(capture, "rolloutPath") == expected, "Actual backend verbatim metadata did not bind the same local rollout.");
                        StopContinuation(fixture); capture["expectedEmail"] = "fake@example.com";
                        ContinuationChange(fixture, "rollout-verbatim-resume");
                        Check(Flag(fixture.Bridge.HostContinuation("prepare", capture, 7000), "ready"),
                            "An equivalent ordinary read path and verbatim resumed path changed the captured file identity.");
                    }
            });
            test("Desktop continuation does not interrupt multiple roots active goals or a mismatched source login", delegate
            {
                foreach (string mode in new[] { "continuation-multi", "continuation-goal" })
                    using (var fixture = new Fixture(fake, mode, true))
                    {
                        Check(ContinuationRejected(() => CaptureContinuation(fixture)), "An ambiguous target or autonomous goal was captured.");
                        Check(!Flag(fixture.Status(), "idle"), "Capture ended an active turn.");
                    }
                using (var fixture = new Fixture(fake, "continuation-active", true))
                    Check(ContinuationRejected(() => fixture.Bridge.HostContinuation("capture", new Dictionary<string, object> { { "expectedEmail", "other@example.com" } }, 7000)),
                        "A source credential/backend mismatch was accepted.");
            });
            test("Desktop continuation accepts a quota-failed turn but never treats unrelated errors or completed work as resumable", delegate
            {
                using (var fixture = new Fixture(fake, "continuation-quota", true))
                {
                    var capture = CaptureContinuation(fixture);
                    Check(Flag(capture, "captured") && Text(capture, "originalTurnStatus") == "failed" &&
                        Flag(fixture.Bridge.HostContinuation("stopped", capture, 7000), "stopped"), "A terminal usage-limit failure could not be recovered.");
                }
                foreach (string mode in new[] { "continuation-policy", "continuation-idle" })
                    using (var fixture = new Fixture(fake, mode, true)) Check(!Flag(CaptureContinuation(fixture), "captured"), "A non-quota or already completed turn was selected.");
            });
            test("Desktop continuation rejects intervening turns changed policies wrong account unavailable model and live background work", delegate
            {
                foreach (string change in new[] { "tail", "permissions", "account", "model", "unavailable-model", "background", "goal-unknown", "goal-usage", "goal-wrong-thread", "roots", "roots-relative", "roots-many", "disabled-plugins", "collab-plan", "collab-unknown", "rollout", "rollout-missing", "rollout-relative", "rollout-drive-relative", "rollout-mismatch" })
                    using (var fixture = new Fixture(fake, "continuation-active", true))
                    {
                        var capture = CaptureContinuation(fixture); StopContinuation(fixture);
                        capture["expectedEmail"] = "fake@example.com"; ContinuationChange(fixture, change);
                        Check(ContinuationRejected(() => fixture.Bridge.HostContinuation("prepare", capture, 7000)), "Unsafe continuation was accepted after " + change + ".");
                    }
            });
            test("Desktop continuation refuses malformed workspace roots or saved disabled plugins before interruption", delegate
            {
                foreach (string change in new[] { "roots-relative", "roots-drive-relative", "roots-root-relative", "roots-many", "disabled-plugins", "collab-plan", "collab-unknown", "rollout-missing", "rollout-relative", "rollout-drive-relative", "rollout-mismatch" })
                    using (var fixture = new Fixture(fake, "continuation-active", true))
                    {
                        ContinuationChange(fixture, change);
                        Check(ContinuationRejected(() => CaptureContinuation(fixture)), "Unsupported original continuation context was captured.");
                        Check(!Flag(fixture.Status(), "idle"), "Rejecting unsupported settings interrupted the source turn.");
                    }
                using (var fixture = new Fixture(fake, "continuation-active", true))
                {
                    ContinuationChange(fixture, "rollout-read-missing");
                    Check(Path.IsPathRooted(Text(CaptureContinuation(fixture), "rolloutPath")), "Authenticated resume metadata could not bind a missing optional read path.");
                }
            });
        }
    }
}
