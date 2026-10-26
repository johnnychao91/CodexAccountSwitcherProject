using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CodexAccountSwitcher
{
    public static class DesktopAppRelayTests
    {
        private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        private static bool Rejected(Action operation) { try { operation(); return false; } catch { return true; } }
        public static void Register(Action<string, Action> test)
        {
            test("Desktop App relay distinguishes startup policy probes from persistent JSON-RPC", delegate
            {
                Check(DesktopAppRelay.IsRpcServer(new[] { "app-server", "--analytics-default-enabled" }) &&
                    DesktopAppRelay.IsRpcServer(new[] { "-c", "features.code_mode_host=true", "app-server", "--stdio" }), "The actual App RPC command was not relayed.");
                Check(!DesktopAppRelay.IsRpcServer(new[] { "app-server", "--analytics-default-enabled", "--print-startup-config", "--startup-client-info", "{}" }) &&
                    !DesktopAppRelay.IsRpcServer(new[] { "--version" }) && !DesktopAppRelay.IsRpcServer(new[] { "app-server", "--help" }),
                    "A one-shot startup policy/version/help command was mistaken for a persistent RPC stream.");
            });
            test("Desktop App relay one-shot commands cannot bypass guarded execution or change accounts", delegate
            {
                string info = Json.Encode(new { name = "codex_desktop", title = "Codex Desktop", version = "26.1021.3705" });
                string[] startup = { "-c", "features.code_mode_host=true", "app-server", "--analytics-default-enabled", "--print-startup-config", "--startup-client-info", info };
                DesktopAppRelay.ValidatePassThroughArgs(startup); DesktopAppRelay.ValidatePassThroughArgs(new[] { "--version" });
                foreach (string[] command in new[] { new[] { "login" }, new[] { "logout" }, new[] { "exec", "synthetic" }, new[] { "config", "set", "synthetic" },
                    startup.Concat(new[] { "--startup-sign-out" }).ToArray(), startup.Concat(new[] { "-c", "approval_policy=never" }).ToArray(),
                    startup.Concat(new[] { "exec", "synthetic" }).ToArray() })
                    Check(Rejected(() => DesktopAppRelay.ValidatePassThroughArgs(command)), "An unguarded CLI command or account mutation was accepted.");
            });
            test("Desktop App relay rejects external endpoints and keeps credentials out of its transport URI", delegate
            {
                string token = new string('a', 43);
                var parsed = DesktopAppRelay.ParseEndpoint("ws://switcher:" + token + "@127.0.0.1:17891/rpc");
                Check(parsed.Address.UserInfo == "" && parsed.Address.AbsoluteUri.IndexOf(token, StringComparison.Ordinal) < 0 &&
                    parsed.Authorization == "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("switcher:" + token)),
                    "The loopback relay endpoint exposed its credential or changed authentication.");
                foreach (string raw in new[] {
                    "ws://switcher:" + token + "@example.invalid:17891/rpc", "wss://switcher:" + token + "@127.0.0.1:17891/rpc",
                    "ws://switcher:" + token + "@localhost:17891/rpc", "ws://switcher:" + token + "@127.0.0.1:17891/other",
                    "ws://switcher:" + token + "@127.0.0.1:17891/rpc?token=other", "ws://other:" + token + "@127.0.0.1:17891/rpc",
                    "ws://switcher:short@127.0.0.1:17891/rpc" })
                    Check(Rejected(() => DesktopAppRelay.ParseEndpoint(raw)), "An untrusted relay endpoint was accepted.");
            });
            test("Desktop App relay greeting contains only one bounded local broker pipe or null", delegate
            {
                foreach (string path in new[] { null, @"\\.\pipe\synthetic-app-tools-123" })
                {
                    byte[] bytes = DesktopAppRelay.CreateGreeting(path); var greeting = Json.Object(Encoding.UTF8.GetString(bytes));
                    Check(bytes.Length <= 2048 && greeting.Count == 3 && Json.Str(greeting, "type") == "switcherDesktopRelay" &&
                        Object.Equals(greeting["version"], 1) && Object.Equals(greeting["appToolsPipePath"], path),
                        "The greeting changed its exact schema or forwarded another environment value.");
                }
                foreach (string path in new[] { "", new string('\\', 2) + @"remote\pipe\broker", Path.GetPathRoot(Path.GetTempPath()) + "broker", @"\\.\pipe\", @"\\.\pipe\a\b",
                    @"\\.\pipe\a/b", @"\\.\pipe\with space", @"\\.\pipe\" + new string('x', 129), @"\\.\pipe\" + "\u4e00" })
                    Check(Rejected(() => DesktopAppRelay.CreateGreeting(path)), "An invalid broker pipe was forwarded.");
            });
            test("Desktop App relay uses only its private endpoint without changing desktop transport selection", delegate
            {
                string privateBefore = Environment.GetEnvironmentVariable("CAS_DESKTOP_RELAY_ENDPOINT");
                string publicBefore = Environment.GetEnvironmentVariable("CODEX_APP_SERVER_WS_URL");
                try
                {
                    string local = "ws://switcher:" + new string('c', 43) + "@127.0.0.1:17892/rpc";
                    Environment.SetEnvironmentVariable("CAS_DESKTOP_RELAY_ENDPOINT", local);
                    Environment.SetEnvironmentVariable("CODEX_APP_SERVER_WS_URL", "ws://untrusted.invalid/rpc");
                    Check(DesktopAppRelay.ReadEndpoint().Address.Port == 17892, "The relay used the public desktop transport override.");
                    Environment.SetEnvironmentVariable("CAS_DESKTOP_RELAY_ENDPOINT", null);
                    Environment.SetEnvironmentVariable("CODEX_APP_SERVER_WS_URL", local);
                    Check(Rejected(() => DesktopAppRelay.ReadEndpoint()), "The relay fell back to a public desktop endpoint.");
                }
                finally
                {
                    Environment.SetEnvironmentVariable("CAS_DESKTOP_RELAY_ENDPOINT", privateBefore);
                    Environment.SetEnvironmentVariable("CODEX_APP_SERVER_WS_URL", publicBefore);
                }
            });
            test("Desktop App relay requires its exact live App parent and Host ownership", delegate
            {
                string folder = Path.GetTempPath();
                var host = new DesktopProcessIdentity { Pid = 11, ExecutablePath = Path.Combine(folder, "DesktopRuntimeHost.exe") };
                var app = new DesktopProcessIdentity { Pid = 12, ExecutablePath = Path.Combine(folder, "synthetic-app.exe") };
                var relay = new DesktopProcessIdentity { Pid = 13, ExecutablePath = Path.Combine(folder, "DesktopAppRelay.exe") };
                var live = new HashSet<DesktopProcessIdentity> { host, app, relay };
                Func<DesktopProcessIdentity, DesktopProcessIdentity, bool> descendants = (child, parent) => child == relay && parent == app || child == app && parent == host;
                Check(DesktopAppRelay.ValidParentBinding(relay, app, host, relay.ExecutablePath, app.ExecutablePath, host.ExecutablePath, live.Contains, descendants),
                    "The known managed launch ancestry was rejected.");
                Check(!DesktopAppRelay.ValidParentBinding(relay, app, host, relay.ExecutablePath, app.ExecutablePath, host.ExecutablePath, live.Contains, (child, parent) => false),
                    "An unrelated App was allowed to invoke the relay.");
                live.Remove(host);
                Check(!DesktopAppRelay.ValidParentBinding(relay, app, host, relay.ExecutablePath, app.ExecutablePath, host.ExecutablePath, live.Contains, descendants),
                    "A stale Host authorized the relay.");
            });
            test("Desktop App relay bounds NDJSON and rejects malformed UTF8 JSON and truncated lines", delegate
            {
                byte[] expected = Encoding.UTF8.GetBytes("{\"text\":\"繼續執行\"}");
                using (var stream = new MemoryStream(expected.Concat(new byte[] { 13, 10 }).ToArray()))
                    Check(DesktopAppRelay.ReadLineAsync(stream, CancellationToken.None).GetAwaiter().GetResult().SequenceEqual(expected),
                        "The relay changed UTF8 or CRLF framing.");
                foreach (byte[] frame in new[] { new byte[] { 0xc3, 0x28, 10 }, Encoding.UTF8.GetBytes("{bad}\n"),
                    Encoding.UTF8.GetBytes("[]\n"), Encoding.UTF8.GetBytes("{}"), Encoding.UTF8.GetBytes("\n") })
                    using (var stream = new MemoryStream(frame))
                        Check(Rejected(() => DesktopAppRelay.ReadLineAsync(stream, CancellationToken.None).GetAwaiter().GetResult()),
                            "An invalid NDJSON record reached the transport.");
                Check(Rejected(() => DesktopAppRelay.ValidateMessage(new byte[DesktopAppRelay.MaximumMessageBytes + 1])), "The message bound was not enforced.");
            });
            test("Desktop App relay normal stdin EOF closes WebSocket and exits zero", delegate
            {
                using (var socket = new ClosingSocket())
                using (var input = new MemoryStream())
                using (var output = new MemoryStream())
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                {
                    Check(DesktopAppRelay.RunAsync(socket, input, output, timeout.Token).GetAwaiter().GetResult() == 0 && socket.NormalCloseOutputs == 1 && output.Length == 0,
                        "Normal App stdin EOF was reported as a backend crash.");
                }
            });
            test("Desktop App relay rejects binary WebSocket data without writing stdout", delegate
            {
                using (var socket = new ClosingSocket { BinaryFrame = true })
                using (var input = new BlockingInput(new byte[0]))
                using (var output = new MemoryStream())
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                    Check(Rejected(() => DesktopAppRelay.RunAsync(socket, input, output, timeout.Token).GetAwaiter().GetResult()) && output.Length == 0,
                        "A binary frame was written into App NDJSON.");
            });
            test("Desktop App relay actual loopback WebSocket preserves Unicode records and normal server close", delegate
            {
                string token = new string('b', 43); string greeting = null, first = null, second = null; Exception serverError = null;
                var finished = new ManualResetEventSlim(false);
                using (var server = new DesktopWebSocketServer(token, peer => true, connection =>
                {
                    Task.Run(delegate
                    {
                        try
                        {
                            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                            {
                                greeting = connection.ReadText(deadline.Token);
                                first = connection.ReadText(deadline.Token); second = connection.ReadText(deadline.Token);
                                connection.SendText("{\"reply\":\"正常結束\"}", deadline.Token);
                                connection.Close(1000, "");
                            }
                        }
                        catch (Exception error) { serverError = error; }
                        finally { finished.Set(); }
                    });
                }))
                {
                    server.Start(); var parsed = DesktopAppRelay.ParseEndpoint(server.Endpoint.AbsoluteUri);
                    using (var socket = new ClientWebSocket())
                    using (var input = new BlockingInput(Encoding.UTF8.GetBytes("{\"text\":\"繼續執行\"}\n{\"second\":true}\n")))
                    using (var output = new MemoryStream())
                    using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12)))
                    {
                        socket.Options.Proxy = null; socket.Options.SetRequestHeader("Authorization", parsed.Authorization);
                        socket.ConnectAsync(parsed.Address, timeout.Token).GetAwaiter().GetResult();
                        DesktopAppRelay.SendGreetingAsync(socket, null, timeout.Token).GetAwaiter().GetResult();
                        var clock = Stopwatch.StartNew();
                        Check(DesktopAppRelay.RunAsync(socket, input, output, timeout.Token).GetAwaiter().GetResult() == 0,
                            "A normal guarded backend close did not exit successfully.");
                        Check(finished.Wait(1000) && serverError == null && Json.Str(Json.Object(greeting), "type") == "switcherDesktopRelay" &&
                            Json.Object(greeting)["appToolsPipePath"] == null && first == "{\"text\":\"繼續執行\"}" && second == "{\"second\":true}" &&
                            Encoding.UTF8.GetString(output.ToArray()) == "{\"reply\":\"正常結束\"}\n" && clock.Elapsed < TimeSpan.FromSeconds(6),
                            "Loopback WebSocket records changed or normal server close waited on blocked App stdin.");
                    }
                }
                finished.Dispose();
            });
        }

        private sealed class BlockingInput : Stream
        {
            private readonly byte[] bytes; private int offset;
            internal BlockingInput(byte[] bytes) { this.bytes = bytes; }
            public override Task<int> ReadAsync(byte[] buffer, int start, int count, CancellationToken cancellation)
            {
                if (offset < bytes.Length) { int size = Math.Min(count, bytes.Length - offset); Buffer.BlockCopy(bytes, offset, buffer, start, size); offset += size; return Task.FromResult(size); }
                var completion = new TaskCompletionSource<int>(); cancellation.Register(() => completion.TrySetCanceled()); return completion.Task;
            }
            public override bool CanRead { get { return true; } } public override bool CanSeek { get { return false; } } public override bool CanWrite { get { return false; } }
            public override long Length { get { throw new NotSupportedException(); } } public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
            public override void Flush() { } public override int Read(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); } public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        }
        private sealed class ClosingSocket : WebSocket
        {
            private readonly TaskCompletionSource<WebSocketReceiveResult> closed = new TaskCompletionSource<WebSocketReceiveResult>();
            private WebSocketState state = WebSocketState.Open;
            internal int NormalCloseOutputs; internal bool BinaryFrame;
            public override WebSocketCloseStatus? CloseStatus { get { return WebSocketCloseStatus.NormalClosure; } }
            public override string CloseStatusDescription { get { return ""; } } public override string SubProtocol { get { return null; } }
            public override WebSocketState State { get { return state; } }
            public override void Abort() { state = WebSocketState.Aborted; closed.TrySetCanceled(); }
            public override Task CloseAsync(WebSocketCloseStatus status, string description, CancellationToken cancellation) { return CloseOutputAsync(status, description, cancellation); }
            public override Task CloseOutputAsync(WebSocketCloseStatus status, string description, CancellationToken cancellation)
            { NormalCloseOutputs++; state = WebSocketState.CloseSent; closed.TrySetResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true, status, "")); return Task.FromResult(true); }
            public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellation)
            { if (BinaryFrame) return Task.FromResult(new WebSocketReceiveResult(1, WebSocketMessageType.Binary, true)); return closed.Task; }
            public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken cancellation) { return Task.FromResult(true); }
            public override void Dispose() { Abort(); }
        }
    }
}
