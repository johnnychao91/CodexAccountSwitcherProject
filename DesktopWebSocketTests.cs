using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CodexAccountSwitcher
{
    internal static class DesktopWebSocketTests
    {
        private const string Credential = "synthetic-desktop-transport-credential-001";
        private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        private static void Finish(Task task, int milliseconds = 5000)
        { Check(Task.WhenAny(task, Task.Delay(milliseconds)).GetAwaiter().GetResult() == task, "Synthetic WebSocket operation exceeded its deadline."); task.GetAwaiter().GetResult(); }
        private static void Rejected(Task task)
        {
            bool rejected = false;
            try { Finish(task); } catch (IOException) { rejected = true; } catch (ObjectDisposedException) { rejected = true; }
            Check(rejected, "Malformed integration traffic was accepted.");
        }
        private sealed class Fixture : IDisposable
        {
            public readonly BlockingCollection<DesktopWebSocketConnection> Accepted = new BlockingCollection<DesktopWebSocketConnection>();
            public readonly DesktopWebSocketServer Server;
            public Fixture(bool allow = true)
            {
                int self; using (var process = Process.GetCurrentProcess()) self = process.Id;
                Server = new DesktopWebSocketServer(Credential, peer => allow && peer.Pid == self && peer.StartedUtc != null && peer.ExecutablePath != null, connection => Accepted.Add(connection));
                Server.Start();
            }
            public DesktopWebSocketConnection Connection()
            { DesktopWebSocketConnection value; Check(Accepted.TryTake(out value, 5000), "Authenticated synthetic connection was not accepted."); return value; }
            public ClientWebSocket Client()
            {
                var client = new ClientWebSocket();
                client.Options.SetRequestHeader("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("switcher:" + Credential)));
                var uri = new UriBuilder(Server.Endpoint) { UserName = "", Password = "" }.Uri;
                using (var cancel = new CancellationTokenSource(5000)) Finish(client.ConnectAsync(uri, cancel.Token));
                return client;
            }
            public TcpClient Raw(out int status, bool auth = true, string extra = "", string request = "GET /rpc HTTP/1.1", string password = Credential, string key = "c3ludGhldGljLXByb2JlIQ==")
            {
                var client = new TcpClient(); client.Connect(IPAddress.Loopback, Server.Endpoint.Port); client.ReceiveTimeout = 5000; client.SendTimeout = 5000;
                string text = request + "\r\nHost: 127.0.0.1:" + Server.Endpoint.Port + "\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: " + key + "\r\nSec-WebSocket-Version: 13\r\n" +
                    (auth ? "Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("switcher:" + password)) + "\r\n" : "") + extra + "\r\n";
                var bytes = Encoding.ASCII.GetBytes(text); client.GetStream().Write(bytes, 0, bytes.Length);
                var response = new StringBuilder();
                while (response.Length < 32768 && !response.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                { int next = client.GetStream().ReadByte(); if (next < 0) break; response.Append((char)next); }
                var parts = response.ToString().Split(' '); status = parts.Length > 1 ? Int32.Parse(parts[1]) : 0; return client;
            }
            public void Dispose() { Server.Dispose(); }
        }
        private static byte[] Frame(int opcode, bool final, byte[] data, bool mask = true, bool reserved = false)
        {
            using (var stream = new MemoryStream())
            {
                stream.WriteByte((byte)((final ? 128 : 0) | (reserved ? 64 : 0) | opcode));
                if (data.Length < 126) stream.WriteByte((byte)((mask ? 128 : 0) | data.Length));
                else { stream.WriteByte((byte)((mask ? 128 : 0) | 126)); stream.WriteByte((byte)(data.Length >> 8)); stream.WriteByte((byte)data.Length); }
                byte[] key = { 1, 2, 3, 4 }; if (mask) stream.Write(key, 0, key.Length);
                for (int i = 0; i < data.Length; i++) stream.WriteByte((byte)(data[i] ^ (mask ? key[i % 4] : 0)));
                return stream.ToArray();
            }
        }
        private static void Write(TcpClient client, byte[] bytes) { client.GetStream().Write(bytes, 0, bytes.Length); }
        private static byte[] ServerFrame(TcpClient client, out int opcode)
        {
            var input = client.GetStream(); int first = input.ReadByte(), next = input.ReadByte();
            Check(first >= 0 && next >= 0 && (next & 128) == 0, "Synthetic server frame was absent or incorrectly masked."); opcode = first & 15;
            int size = next & 127; if (size == 126) size = (input.ReadByte() << 8) | input.ReadByte();
            Check(size >= 0 && size <= 65535, "Unexpected synthetic frame size."); var result = new byte[size]; int read = 0;
            while (read < result.Length) { int n = input.Read(result, read, result.Length - read); Check(n > 0, "Synthetic frame was truncated."); read += n; } return result;
        }
        private static void DeadlineRejected(DesktopWebSocketReceiveProgress progress, long milliseconds)
        {
            bool rejected = false; try { progress.CheckTimeout(milliseconds); } catch (IOException) { rejected = true; }
            Check(rejected, "A real partial frame or fragmented message lost its deadline.");
        }
        public static void Register(Action<string, Action> test)
        {
            test("Desktop WebSocket authenticates a native TCP peer and exchanges UTF8 through ClientWebSocket", delegate
            {
                using (var f = new Fixture()) using (var client = f.Client())
                {
                    var connection = f.Connection(); int self; using (var process = Process.GetCurrentProcess()) self = process.Id;
                    Check(connection.Peer.Pid == self && connection.Peer.StartedUtc != null, "TCP peer process identity was not verified.");
                    var read = Task.Factory.StartNew(() => connection.ReadText(CancellationToken.None));
                    byte[] text = Encoding.UTF8.GetBytes("synthetic 中文"); Finish(client.SendAsync(new ArraySegment<byte>(text), WebSocketMessageType.Text, true, CancellationToken.None));
                    Finish(read); Check(read.Result == "synthetic 中文", "UTF8 input changed.");
                    connection.SendText("synthetic reply", CancellationToken.None); var bytes = new byte[128];
                    var response = client.ReceiveAsync(new ArraySegment<byte>(bytes), CancellationToken.None); Finish(response);
                    Check(response.Result.MessageType == WebSocketMessageType.Text && Encoding.UTF8.GetString(bytes, 0, response.Result.Count) == "synthetic reply", "UTF8 output changed.");
                }
            });
            test("Desktop WebSocket rejects missing authentication browser origins and unauthorized process ownership", delegate
            {
                int status;
                using (var f = new Fixture())
                {
                    using (f.Raw(out status, false)) Check(status == 401, "Missing credentials were accepted.");
                    using (f.Raw(out status, password: "synthetic-wrong-credential")) Check(status == 401, "Incorrect credentials were accepted.");
                    using (f.Raw(out status, true, "Origin: https://synthetic.example\r\n")) Check(status == 403, "A browser origin was accepted.");
                }
                using (var f = new Fixture(false)) using (f.Raw(out status)) Check(status == 403, "An unauthorized TCP peer was accepted.");
            });
            test("Desktop WebSocket freeze blocks upgrades and unfreeze restores authenticated acceptance", delegate
            {
                using (var f = new Fixture())
                {
                    f.Server.Freeze(); int status; using (f.Raw(out status)) Check(status == 503, "Frozen listener accepted an upgrade.");
                    f.Server.Unfreeze(); using (var client = f.Client()) Check(f.Connection() != null, "Unfreeze did not restore acceptance.");
                }
            });
            test("Desktop WebSocket bounds and validates HTTP upgrade headers", delegate
            {
                using (var f = new Fixture())
                {
                    int status;
                    using (f.Raw(out status, true, "Sec-WebSocket-Version: 13\r\n")) Check(status == 400, "Duplicate upgrade headers were accepted.");
                    using (f.Raw(out status, true, "", "POST /rpc HTTP/1.1")) Check(status == 400, "A non-GET upgrade was accepted.");
                    using (f.Raw(out status, key: "not-base64")) Check(status == 400, "An invalid handshake key was accepted.");
                    using (f.Raw(out status, true, "X-Large: " + new string('x', 17000) + "\r\n")) Check(status == 400, "Oversized headers were accepted.");
                    using (f.Raw(out status, true, "Sec-WebSocket-Extensions: permessage-deflate; client_max_window_bits\r\n"))
                        Check(status == 101, "Declining an offered optional extension blocked a compatible client.");
                }
            });
            test("Desktop WebSocket native parser preserves fragmented UTF8 messages", delegate
            {
                using (var f = new Fixture())
                {
                    int status; using (var raw = f.Raw(out status))
                    {
                        Check(status == 101, "Synthetic raw upgrade failed."); var connection = f.Connection();
                        var read = Task.Factory.StartNew(() => connection.ReadText(CancellationToken.None));
                        byte[] text = Encoding.UTF8.GetBytes("甲乙");
                        Write(raw, Frame(1, false, new[] { text[0], text[1] }));
                        Write(raw, Frame(0, true, new[] { text[2], text[3], text[4], text[5] }));
                        Finish(read); Check(read.Result == "甲乙", "Fragmented multi-byte text changed.");
                    }
                }
            });
            test("Desktop WebSocket permits an independent sender while the reader is blocked", delegate
            {
                using (var f = new Fixture()) using (var client = f.Client())
                {
                    var connection = f.Connection(); var read = Task.Factory.StartNew(() => connection.ReadText(CancellationToken.None));
                    var send = Task.Factory.StartNew(() => connection.SendText("server-first", CancellationToken.None));
                    var bytes = new byte[128]; var incoming = client.ReceiveAsync(new ArraySegment<byte>(bytes), CancellationToken.None);
                    Finish(send); Finish(incoming); Check(Encoding.UTF8.GetString(bytes, 0, incoming.Result.Count) == "server-first", "Pending receive blocked independent send.");
                    var outgoing = Encoding.UTF8.GetBytes("client-later"); Finish(client.SendAsync(new ArraySegment<byte>(outgoing), WebSocketMessageType.Text, true, CancellationToken.None));
                    Finish(read); Check(read.Result == "client-later", "Concurrent sender corrupted the receive state.");
                }
            });
            test("Desktop WebSocket native ping handling returns pong without losing the text reader", delegate
            {
                using (var f = new Fixture())
                {
                    int status; using (var raw = f.Raw(out status))
                    {
                        var connection = f.Connection(); var read = Task.Factory.StartNew(() => connection.ReadText(CancellationToken.None));
                        Write(raw, Frame(9, true, Encoding.UTF8.GetBytes("ping"))); int opcode; var pong = ServerFrame(raw, out opcode);
                        Check(opcode == 10 && Encoding.UTF8.GetString(pong) == "ping", "Native ping did not receive the matching pong.");
                        Write(raw, Frame(1, true, Encoding.UTF8.GetBytes("after-ping"))); Finish(read); Check(read.Result == "after-ping", "Control traffic consumed the text message.");
                    }
                }
            });
            test("Desktop WebSocket an idle reader survives a handled ping beyond the partial-message deadline", delegate
            {
                using (var f = new Fixture())
                {
                    int status; using (var raw = f.Raw(out status))
                    {
                        var connection = f.Connection(); var read = Task.Factory.StartNew(() => connection.ReadText(CancellationToken.None));
                        Write(raw, Frame(9, true, Encoding.UTF8.GetBytes("idle-ping"))); int opcode; var pong = ServerFrame(raw, out opcode);
                        Check(opcode == 10 && Encoding.UTF8.GetString(pong) == "idle-ping", "Native ping did not receive the matching pong.");
                        // The old network-read deadline expired even though the complete ping
                        // was handled internally and no partial application message remained.
                        Thread.Sleep(31500);
                        if (read.IsCompleted) read.GetAwaiter().GetResult();
                        Check(!read.IsCompleted, "An idle reader ended after handling a complete control frame.");
                        Write(raw, Frame(1, true, Encoding.UTF8.GetBytes("after-idle-ping"))); Finish(read);
                        Check(read.Result == "after-idle-ping", "The idle ping damaged the next text message.");
                    }
                }
            });
            test("Desktop WebSocket complete ping and pong traffic leaves no partial-message deadline", delegate
            {
                var progress = new DesktopWebSocketReceiveProgress();
                var ping = Frame(9, true, Encoding.UTF8.GetBytes("ping"));
                progress.Observe(ping, ping.Length, 100); progress.CheckTimeout(30101);
                var pong = Frame(10, true, Encoding.UTF8.GetBytes("pong"));
                progress.Observe(pong, pong.Length, 30101); progress.CheckTimeout(60102);
                var text = Frame(1, true, Encoding.UTF8.GetBytes("complete"));
                progress.Observe(text, text.Length, 60102); progress.CheckTimeout(90103);
            });
            test("Desktop WebSocket a ping followed by a partial frame in the same network read retains its bound", delegate
            {
                var ping = Frame(9, true, Encoding.UTF8.GetBytes("ping"));
                var text = Frame(1, true, Encoding.UTF8.GetBytes("partial-payload"));
                foreach (int partial in new[] { 1, 2, 5, text.Length - 1 })
                {
                    var progress = new DesktopWebSocketReceiveProgress(); var combined = new byte[ping.Length + partial];
                    Buffer.BlockCopy(ping, 0, combined, 0, ping.Length); Buffer.BlockCopy(text, 0, combined, ping.Length, partial);
                    progress.Observe(combined, combined.Length, 100); progress.CheckTimeout(30099); DeadlineRejected(progress, 30101);
                }
                // A split header/payload that does finish must release the same bound.
                var finished = new DesktopWebSocketReceiveProgress();
                finished.Observe(text, 1, 100); var rest = new byte[text.Length - 1]; Buffer.BlockCopy(text, 1, rest, 0, rest.Length);
                finished.Observe(rest, rest.Length, 200); finished.CheckTimeout(30201);
            });
            test("Desktop WebSocket control traffic cannot extend an unfinished fragmented message deadline", delegate
            {
                var first = Frame(1, false, Encoding.UTF8.GetBytes("first"));
                var ping = Frame(9, true, Encoding.UTF8.GetBytes("ping"));
                var pong = Frame(10, true, Encoding.UTF8.GetBytes("pong"));
                var progress = new DesktopWebSocketReceiveProgress();
                progress.Observe(first, first.Length, 100); progress.Observe(ping, ping.Length, 25000); progress.Observe(pong, pong.Length, 29000);
                progress.CheckTimeout(30099); DeadlineRejected(progress, 30101);
                var completed = new DesktopWebSocketReceiveProgress(); var final = Frame(0, true, Encoding.UTF8.GetBytes("last"));
                completed.Observe(first, first.Length, 100); completed.Observe(ping, ping.Length, 25000);
                completed.Observe(final, final.Length, 30000); completed.CheckTimeout(60101);
            });
            test("Desktop WebSocket native parser rejects unmasked reserved binary and invalid UTF8 traffic", delegate
            {
                foreach (var frame in new[] { Frame(1, true, new byte[] { 65 }, false), Frame(1, true, new byte[] { 65 }, true, true), Frame(2, true, new byte[] { 1 }), Frame(1, true, new byte[] { 0xc0, 0xaf }),
                    Frame(0, true, new byte[] { 65 }), Frame(9, false, new byte[] { 65 }), Frame(8, true, new byte[] { 3 }), Frame(8, true, new byte[] { 3, 237 }) })
                {
                    using (var f = new Fixture())
                    {
                        int status; using (var raw = f.Raw(out status))
                        { var connection = f.Connection(); var read = Task.Factory.StartNew(() => connection.ReadText(CancellationToken.None)); Write(raw, frame); Rejected(read); }
                    }
                }
            });
            test("Desktop WebSocket truncated frames are failures rather than a normal protocol EOF", delegate
            {
                using (var f = new Fixture())
                {
                    int status; using (var raw = f.Raw(out status))
                    {
                        var connection = f.Connection(); var read = Task.Factory.StartNew(() => connection.ReadText(CancellationToken.None));
                        Write(raw, new byte[] { 0x81, 0x85, 1, 2 }); raw.Client.Shutdown(SocketShutdown.Send); Rejected(read);
                    }
                }
            });
            test("Desktop WebSocket valid close frames return normal EOF and a close response", delegate
            {
                using (var f = new Fixture())
                {
                    int status; using (var raw = f.Raw(out status))
                    {
                        var connection = f.Connection(); var read = Task.Factory.StartNew(() => connection.ReadText(CancellationToken.None));
                        Write(raw, Frame(8, true, new byte[] { 3, 232 })); int opcode; ServerFrame(raw, out opcode); Finish(read);
                        Check(opcode == 8 && read.Result == null, "A valid close was not recognized as normal protocol EOF.");
                    }
                }
            });
            test("Desktop WebSocket blocked receive cancellation releases the connection", delegate
            {
                using (var f = new Fixture()) using (var client = f.Client()) using (var cancel = new CancellationTokenSource(150))
                {
                    var connection = f.Connection(); var read = Task.Factory.StartNew(() => connection.ReadText(cancel.Token)); bool cancelled = false;
                    try { Finish(read); } catch (OperationCanceledException) { cancelled = true; }
                    Check(cancelled, "Blocked receive ignored cancellation.");
                }
            });
            test("Desktop WebSocket send backpressure is cancellable with bounded memory", delegate
            {
                using (var f = new Fixture())
                {
                    int status; using (var raw = f.Raw(out status)) using (var cancel = new CancellationTokenSource(200))
                    {
                        raw.ReceiveBufferSize = 1024; var connection = f.Connection();
                        var send = Task.Factory.StartNew(() => connection.SendText(new string('x', 16 * 1024 * 1024), cancel.Token)); bool cancelled = false;
                        try { Finish(send, 5000); } catch (OperationCanceledException) { cancelled = true; }
                        Check(cancelled, "A blocked sender ignored cancellation.");
                    }
                }
            });
            test("Desktop WebSocket outbound UTF8 byte limits reject oversized text before poisoning the connection", delegate
            {
                using (var f = new Fixture()) using (var client = f.Client())
                {
                    var connection = f.Connection(); bool rejected = false;
                    try { connection.SendText(new string('界', DesktopWebSocketConnection.MaxMessageBytes / 3 + 1), CancellationToken.None); }
                    catch (IOException) { rejected = true; }
                    Check(rejected, "UTF8 expansion exceeded the message-byte limit.");
                    connection.SendText("still-open", CancellationToken.None); var bytes = new byte[128];
                    var reply = client.ReceiveAsync(new ArraySegment<byte>(bytes), CancellationToken.None); Finish(reply);
                    Check(Encoding.UTF8.GetString(bytes, 0, reply.Result.Count) == "still-open", "Rejecting oversized output damaged the connection.");
                }
            });
            test("Desktop WebSocket inbound fragmented messages cannot exceed the aggregate byte limit", delegate
            {
                using (var f = new Fixture())
                {
                    int status; using (var raw = f.Raw(out status))
                    {
                        var connection = f.Connection(); var read = Task.Factory.StartNew(() => connection.ReadText(CancellationToken.None));
                        var write = Task.Factory.StartNew(delegate
                        {
                            var block = new byte[32768]; for (int n = 0; n < block.Length; n++) block[n] = 65;
                            byte[] first = Frame(1, false, block), later = Frame(0, false, block);
                            try
                            {
                                Write(raw, first);
                                for (int total = block.Length; total <= DesktopWebSocketConnection.MaxMessageBytes; total += block.Length) Write(raw, later);
                            }
                            catch (IOException) { } catch (SocketException) { }
                        });
                        Rejected(read); Finish(write);
                    }
                }
            });
        }
    }
}
