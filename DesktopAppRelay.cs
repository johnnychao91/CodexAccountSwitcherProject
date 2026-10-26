using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CodexAccountSwitcher
{
    // The App uses its normal CLI transport, so its own thread manager remains
    // enabled. Only its stdio bytes are relayed to the Host-owned guarded backend.
    internal static class DesktopAppRelayProgram
    {
        public static int Main(string[] args)
        {
            try
            {
                var launch = DesktopAppRelay.ValidateLaunch();
                if (!DesktopAppRelay.IsRpcServer(args)) return DesktopAppRelay.PassThrough(launch.Item1.CliExecutable, args);
                var endpoint = DesktopAppRelay.ReadEndpoint();
                using (var socket = new ClientWebSocket())
                {
                    socket.Options.Proxy = null;
                    socket.Options.SetRequestHeader("Authorization", endpoint.Authorization);
                    socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                    DesktopAppRelay.VerifyListener(launch.Item2, endpoint.Address.Port);
                    using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                        socket.ConnectAsync(endpoint.Address, timeout.Token).GetAwaiter().GetResult();
                    DesktopAppRelay.VerifyListener(launch.Item2, endpoint.Address.Port);
                    using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                        DesktopAppRelay.SendGreetingAsync(socket, Environment.GetEnvironmentVariable("CODEX_APP_TOOLS_PIPE_PATH"), timeout.Token).GetAwaiter().GetResult();
                    using (var input = Console.OpenStandardInput())
                    using (var output = Console.OpenStandardOutput())
                        return DesktopAppRelay.RunAsync(socket, input, output, CancellationToken.None).GetAwaiter().GetResult();
                }
            }
            catch
            {
                // Never include endpoint credentials, RPC text, paths, or raw
                // child exceptions in stderr or logs visible to the App.
                Console.Error.WriteLine("The managed desktop relay could not start or its connection ended unexpectedly.");
                return 1;
            }
        }
    }

    internal sealed class DesktopAppRelayEndpoint
    {
        internal readonly Uri Address;
        internal readonly string Authorization;
        internal DesktopAppRelayEndpoint(Uri address, string authorization) { Address = address; Authorization = authorization; }
    }

    internal static class DesktopAppRelay
    {
        internal const int MaximumMessageBytes = 32 * 1024 * 1024;
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
        [StructLayout(LayoutKind.Sequential)]
        private struct TcpRow { public uint State, LocalAddress, LocalPort, RemoteAddress, RemotePort, Pid; }
        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(IntPtr table, ref int length, bool order, int family, int kind, uint reserved);
        private static int Port(uint value) { return (int)(((value & 255) << 8) | ((value >> 8) & 255)); }

        internal static byte[] CreateGreeting(string appToolsPipePath)
        {
            if (appToolsPipePath != null)
            {
                const string prefix = @"\\.\pipe\";
                if (!appToolsPipePath.StartsWith(prefix, StringComparison.Ordinal) || appToolsPipePath.Length <= prefix.Length ||
                    appToolsPipePath.Length - prefix.Length > 128 || appToolsPipePath.Substring(prefix.Length).Any(c => c < 33 || c > 126 || c == '\\' || c == '/'))
                    throw new IOException("Invalid managed desktop tool broker pipe.");
            }
            byte[] greeting = Utf8.GetBytes(Json.Encode(new { type = "switcherDesktopRelay", version = 1, appToolsPipePath = appToolsPipePath }));
            if (greeting.Length > 2048) throw new IOException("Managed desktop relay context is too large.");
            return greeting;
        }

        internal static Task SendGreetingAsync(WebSocket socket, string appToolsPipePath, CancellationToken cancellation)
        {
            byte[] greeting = CreateGreeting(appToolsPipePath);
            return socket.SendAsync(new ArraySegment<byte>(greeting), WebSocketMessageType.Text, true, cancellation);
        }

        internal static bool IsRpcServer(string[] args)
        {
            // The pinned App invokes `app-server --print-startup-config` before
            // starting JSON-RPC. That one-shot command must reach the real CLI.
            return args != null && Array.IndexOf(args, "app-server") >= 0 &&
                !args.Any(arg => arg == "--print-startup-config" || arg == "--help" || arg == "--version" || arg == "-h" || arg == "-V");
        }
        internal static void ValidatePassThroughArgs(string[] args)
        {
            if (args != null && args.Length == 1 && (args[0] == "--version" || args[0] == "-V")) return;
            int server = 0, policy = 0, client = 0, analytics = 0;
            if (args == null || args.Length > 24) throw new IOException("Unsupported managed relay one-shot command.");
            for (int index = 0; index < args.Length; index++)
            {
                string arg = args[index];
                if (arg == "app-server") server++;
                else if (arg == "--print-startup-config") policy++;
                else if (arg == "--analytics-default-enabled") analytics++;
                else if (arg == "-c" && ++index < args.Length)
                {
                    string config = args[index];
                    if (config != "features.code_mode_host=true" && config != "features.windows_sandbox_service=true" &&
                        !MatchesUrlOverride(config, "chatgpt_base_url", "CODEX_APP_SERVER_CHATGPT_BASE_URL") &&
                        !MatchesUrlOverride(config, "openai_base_url", "CODEX_APP_SERVER_OPENAI_BASE_URL"))
                        throw new IOException("Unsupported managed relay startup configuration.");
                }
                else if (arg == "--startup-client-info" && ++index < args.Length)
                {
                    string text = args[index]; if (text == null || text.Length > 512) throw new IOException("Invalid managed relay startup identity.");
                    var info = Json.Object(text);
                    if (info == null || info.Count != 3 || Json.Str(info, "name") != "codex_desktop" || Json.Str(info, "title") != "Codex Desktop" ||
                        !System.Text.RegularExpressions.Regex.IsMatch(Json.Str(info, "version"), "^[A-Za-z0-9.+-]{1,64}$"))
                        throw new IOException("Invalid managed relay startup identity.");
                    client++;
                }
                else throw new IOException("Unsupported managed relay one-shot command.");
            }
            if (server != 1 || policy != 1 || client != 1 || analytics > 1) throw new IOException("Unsupported managed relay startup command.");
        }
        private static bool MatchesUrlOverride(string value, string key, string environment)
        {
            string configured = Environment.GetEnvironmentVariable(environment);
            return !String.IsNullOrWhiteSpace(configured) && value == key + "=" + Json.Encode(configured.Trim());
        }

        internal static DesktopAppRelayEndpoint ParseEndpoint(string raw)
        {
            Uri endpoint;
            if (String.IsNullOrEmpty(raw) || raw.Length > 2048 || !Uri.TryCreate(raw, UriKind.Absolute, out endpoint) ||
                endpoint.Scheme != "ws" || endpoint.Host != "127.0.0.1" || endpoint.Port < 1 || endpoint.Port > 65535 ||
                endpoint.AbsolutePath != "/rpc" || endpoint.Query != "" || endpoint.Fragment != "")
                throw new IOException("Invalid managed relay endpoint.");
            string[] info = endpoint.UserInfo.Split(new[] { ':' }, 2);
            if (info.Length != 2 || Uri.UnescapeDataString(info[0]) != "switcher") throw new IOException("Invalid managed relay endpoint.");
            string password = Uri.UnescapeDataString(info[1]);
            if (password.Length < 24 || password.Length > 256 || password.Any(c => !((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-')))
                throw new IOException("Invalid managed relay endpoint.");
            return new DesktopAppRelayEndpoint(new UriBuilder(endpoint) { UserName = "", Password = "" }.Uri,
                "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("switcher:" + password)));
        }

        internal static DesktopAppRelayEndpoint ReadEndpoint()
        {
            return ParseEndpoint(Environment.GetEnvironmentVariable("CAS_DESKTOP_RELAY_ENDPOINT"));
        }

        internal static bool ValidParentBinding(DesktopProcessIdentity relay, DesktopProcessIdentity app, DesktopProcessIdentity host,
            string relayExecutable, string appExecutable, string hostExecutable,
            Func<DesktopProcessIdentity, bool> same, Func<DesktopProcessIdentity, DesktopProcessIdentity, bool> descendant)
        {
            return relay != null && app != null && host != null && same != null && descendant != null &&
                String.Equals(relay.ExecutablePath, relayExecutable, StringComparison.OrdinalIgnoreCase) &&
                String.Equals(app.ExecutablePath, appExecutable, StringComparison.OrdinalIgnoreCase) &&
                String.Equals(host.ExecutablePath, hostExecutable, StringComparison.OrdinalIgnoreCase) &&
                same(relay) && same(app) && same(host) && descendant(relay, app) && descendant(app, host);
        }

        internal static Tuple<DesktopPackageTarget, DesktopProcessIdentity> ValidateLaunch()
        {
            string appPath = Environment.GetEnvironmentVariable("CAS_DESKTOP_RELAY_APP_EXE");
            string hostPath = Environment.GetEnvironmentVariable("CAS_DESKTOP_RELAY_HOST_EXE");
            string born = Environment.GetEnvironmentVariable("CAS_DESKTOP_RELAY_HOST_STARTED_UTC"); int pid;
            if (String.IsNullOrWhiteSpace(appPath) || !Path.IsPathRooted(appPath) || String.IsNullOrWhiteSpace(hostPath) || !Path.IsPathRooted(hostPath) ||
                !Int32.TryParse(Environment.GetEnvironmentVariable("CAS_DESKTOP_RELAY_HOST_PID"), NumberStyles.None, CultureInfo.InvariantCulture, out pid) || pid <= 0)
                throw new IOException("Missing managed relay launch proof.");
            var host = DesktopNative.Identity(pid);
            if (host == null || host.StartedUtc != born) throw new IOException("Managed relay host identity changed.");
            string selfPath = Path.GetFullPath(Assembly.GetExecutingAssembly().Location);
            string expectedRelay = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(hostPath)), "DesktopAppRelay.exe");
            var self = DesktopNative.Identity(Process.GetCurrentProcess().Id); var ancestor = self; DesktopProcessIdentity app = null;
            var seen = new HashSet<int>();
            for (int depth = 0; depth < 12 && ancestor != null && seen.Add(ancestor.Pid); depth++)
            {
                if (String.Equals(ancestor.ExecutablePath, Path.GetFullPath(appPath), StringComparison.OrdinalIgnoreCase)) { app = ancestor; break; }
                ancestor = DesktopNative.Identity(ancestor.ParentPid);
            }
            if (!String.Equals(selfPath, expectedRelay, StringComparison.OrdinalIgnoreCase) ||
                !ValidParentBinding(self, app, host, expectedRelay, Path.GetFullPath(appPath), Path.GetFullPath(hostPath), DesktopNative.Same, DesktopNative.IsDescendant))
                throw new IOException("Untrusted managed relay parent.");
            string sessionDirectory = Environment.GetEnvironmentVariable("CAS_DESKTOP_RELAY_SESSION_DIRECTORY");
            if (String.IsNullOrWhiteSpace(sessionDirectory) || !Path.IsPathRooted(sessionDirectory)) throw new IOException("Missing private managed relay catalog.");
            DesktopSessionFiles.VerifyPrivate(sessionDirectory, false);
            DesktopSessionState snapshot = null; var clock = Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(5))
            {
                try
                {
                    snapshot = DesktopSessionFiles.ReadSnapshot(Path.Combine(sessionDirectory, "session.json"));
                    if (snapshot != null && DesktopNative.Equal(snapshot.Host, host) && snapshot.App != null &&
                        String.Equals(snapshot.App.ExecutablePath, Path.GetFullPath(appPath), StringComparison.OrdinalIgnoreCase) &&
                        DesktopNative.Same(snapshot.App) && DesktopNative.IsDescendant(self, snapshot.App)) break;
                }
                catch (IOException) { }
                Thread.Sleep(40);
            }
            Guid session;
            if (snapshot != null) app = snapshot.App; // Use primary App, not a same-exe utility parent.
            if (snapshot == null || !Guid.TryParseExact(snapshot.SessionId, "N", out session) || Path.GetFileName(sessionDirectory.TrimEnd(Path.DirectorySeparatorChar)) != snapshot.SessionId ||
                !DesktopNative.Equal(snapshot.Host, host) ||
                !ValidParentBinding(self, app, host, expectedRelay, Path.GetFullPath(appPath), Path.GetFullPath(hostPath), DesktopNative.Same, DesktopNative.IsDescendant))
                throw new IOException("Managed relay private catalog identity changed.");
            var target = DesktopPackageTarget.Resolve(snapshot.App.ExecutablePath);
            DesktopLocalWorkPolicy.ValidateTarget(target);
            if (String.Equals(Path.GetFullPath(target.CliExecutable), selfPath, StringComparison.OrdinalIgnoreCase)) throw new IOException("Recursive relay target.");
            return Tuple.Create(target, host);
        }

        internal static void VerifyListener(DesktopProcessIdentity host, int port)
        {
            if (!DesktopNative.Same(host)) throw new IOException("Managed relay host changed.");
            int size = 0;
            if (GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 5, 0) != 122 || size < 4 || size > 16 * 1024 * 1024)
                throw new IOException("Managed relay listener identity unavailable.");
            IntPtr memory = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(memory, ref size, false, 2, 5, 0) != 0) throw new IOException("Managed relay listener identity unavailable.");
                int count = Marshal.ReadInt32(memory), rowSize = Marshal.SizeOf(typeof(TcpRow));
                if (count < 0 || (long)count * rowSize + 4 > size) throw new IOException("Invalid managed relay listener table.");
                for (int index = 0; index < count; index++)
                {
                    var row = (TcpRow)Marshal.PtrToStructure(IntPtr.Add(memory, 4 + index * rowSize), typeof(TcpRow));
                    if (row.State == 2 && Port(row.LocalPort) == port && row.Pid == host.Pid && row.LocalAddress == 0x0100007f && DesktopNative.Same(host)) return;
                }
                throw new IOException("The relay endpoint is not owned by its managed host.");
            }
            finally { Marshal.FreeHGlobal(memory); }
        }

        internal static int PassThrough(string executable, string[] args)
        {
            ValidatePassThroughArgs(args);
            var start = new ProcessStartInfo(executable, String.Join(" ", args.Select(ManagedBridge.QuoteArgument)))
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardInput = true, RedirectStandardOutput = true };
            foreach (string key in start.EnvironmentVariables.Keys.Cast<string>().ToArray())
                if (key.StartsWith("CAS_DESKTOP_", StringComparison.OrdinalIgnoreCase) || key == "CODEX_APP_SERVER_WS_URL" || key == "CODEX_APP_SERVER_FORCE_CLI" || key == "CODEX_CLI_PATH")
                    start.EnvironmentVariables.Remove(key);
            using (var child = Process.Start(start))
            using (var input = Console.OpenStandardInput())
            using (var output = Console.OpenStandardOutput())
            {
                var discarded = child.StandardError.BaseStream.CopyToAsync(Stream.Null);
                // Explicit byte pumps are required: the App launches this relay
                // with redirected stdio, which is not a Windows console handle.
                var toChild = Task.Run(async delegate
                {
                    try { await input.CopyToAsync(child.StandardInput.BaseStream).ConfigureAwait(false); child.StandardInput.Close(); }
                    catch (IOException) { }
                    catch (ObjectDisposedException) { }
                });
                child.StandardOutput.BaseStream.CopyToAsync(output).GetAwaiter().GetResult(); output.Flush();
                child.WaitForExit(); discarded.GetAwaiter().GetResult(); ObserveFailure(toChild); return child.ExitCode;
            }
        }

        internal static void ValidateMessage(byte[] bytes)
        {
            if (bytes.Length == 0 || bytes.Length > MaximumMessageBytes) throw new IOException("Managed relay message exceeds its bound.");
            string text = Utf8.GetString(bytes);
            if (text.IndexOf('\n') >= 0 || text.IndexOf('\r') >= 0 || text.TrimStart().Length == 0 || text.TrimStart()[0] != '{' || text.TrimEnd()[text.TrimEnd().Length - 1] != '}')
                throw new IOException("Invalid managed relay NDJSON frame.");
            try
            {
                if (!(new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = MaximumMessageBytes, RecursionLimit = 128 }.DeserializeObject(text) is Dictionary<string, object>))
                    throw new IOException("Invalid managed relay JSON object.");
            }
            catch (ArgumentException) { throw new IOException("Invalid managed relay JSON object."); }
            catch (InvalidOperationException) { throw new IOException("Invalid managed relay JSON object."); }
        }

        internal static async Task<byte[]> ReadLineAsync(Stream input, CancellationToken cancellation)
        {
            return await new LineReader(input).ReadAsync(cancellation).ConfigureAwait(false);
        }
        private sealed class LineReader
        {
            private readonly Stream input;
            private readonly byte[] buffer = new byte[16384];
            private int offset, available;
            internal LineReader(Stream input) { this.input = input; }
            internal async Task<byte[]> ReadAsync(CancellationToken cancellation)
            {
                using (var line = new MemoryStream())
                {
                    while (true)
                    {
                        if (offset == available)
                        {
                            available = await input.ReadAsync(buffer, 0, buffer.Length, cancellation).ConfigureAwait(false); offset = 0;
                            if (available == 0)
                            {
                                if (line.Length != 0) throw new IOException("Incomplete managed relay NDJSON frame.");
                                return null;
                            }
                        }
                        int end = offset; while (end < available && buffer[end] != 10) end++;
                        if (line.Length + end - offset > MaximumMessageBytes) throw new IOException("Managed relay message exceeds its bound.");
                        line.Write(buffer, offset, end - offset); offset = end;
                        if (offset < available)
                        {
                            offset++;
                            byte[] bytes = line.ToArray();
                            if (bytes.Length > 0 && bytes[bytes.Length - 1] == 13) Array.Resize(ref bytes, bytes.Length - 1);
                            ValidateMessage(bytes); return bytes;
                        }
                    }
                }
            }
        }

        private static async Task InputAsync(WebSocket socket, Stream input, CancellationToken cancellation)
        {
            var reader = new LineReader(input);
            while (true)
            {
                var bytes = await reader.ReadAsync(cancellation).ConfigureAwait(false);
                if (bytes == null) return;
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellation).ConfigureAwait(false);
            }
        }
        private static async Task OutputAsync(WebSocket socket, Stream output, CancellationToken cancellation)
        {
            var buffer = new byte[16384];
            while (true)
            {
                using (var message = new MemoryStream())
                {
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellation).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            if (message.Length != 0 || result.CloseStatus != WebSocketCloseStatus.NormalClosure) throw new IOException("Managed relay closed unexpectedly.");
                            return;
                        }
                        if (result.MessageType != WebSocketMessageType.Text || message.Length + result.Count > MaximumMessageBytes)
                            throw new IOException("Invalid managed relay WebSocket frame.");
                        message.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);
                    byte[] bytes = message.ToArray(); ValidateMessage(bytes);
                    await output.WriteAsync(bytes, 0, bytes.Length, cancellation).ConfigureAwait(false);
                    await output.WriteAsync(new byte[] { 10 }, 0, 1, cancellation).ConfigureAwait(false);
                    await output.FlushAsync(cancellation).ConfigureAwait(false);
                }
            }
        }
        internal static async Task<int> RunAsync(WebSocket socket, Stream input, Stream output, CancellationToken cancellation)
        {
            using (var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                // Some Windows console streams block before returning a Task.
                // Keep stdin on a background pool thread so Host close can finish.
                var send = Task.Run(() => InputAsync(socket, input, stopping.Token));
                var receive = OutputAsync(socket, output, stopping.Token);
                try
                {
                    var first = await Task.WhenAny(send, receive).ConfigureAwait(false);
                    await first.ConfigureAwait(false);
                    // EOF and Host-confirmed normal close are both graceful. The
                    // Host, not this relay, owns and commits backend shutdown.
                    using (var close = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
                        if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
                            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", close.Token).ConfigureAwait(false);
                    if (first == send)
                    {
                        var completed = await Task.WhenAny(receive, Task.Delay(3000, cancellation)).ConfigureAwait(false);
                        if (completed == receive) await receive.ConfigureAwait(false);
                    }
                    return 0;
                }
                finally
                {
                    stopping.Cancel(); socket.Abort();
                    // Observe asynchronous failures after cancellation without
                    // waiting forever for a native stdin pipe still held open.
                    ObserveFailure(send); ObserveFailure(receive);
                }
            }
        }
        private static void ObserveFailure(Task task)
        { task.ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted); }
    }
}
