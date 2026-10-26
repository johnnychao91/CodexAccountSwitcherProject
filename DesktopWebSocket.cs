using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CodexAccountSwitcher
{
    internal sealed class DesktopWebSocketPeer
    {
        public int Pid { get; set; }
        public string StartedUtc { get; set; }
        public string ExecutablePath { get; set; }
        public int ParentPid { get; set; }
    }

    internal sealed class DesktopWebSocketServer : IDisposable
    {
        private const int HeaderLimit = 16384, HandshakeTimeout = 5000, MaxConnections = 8;
        private readonly string authorization;
        private readonly Func<DesktopWebSocketPeer, bool> authorize;
        private readonly Action<DesktopWebSocketConnection> connected;
        private readonly object gate = new object();
        private readonly HashSet<DesktopWebSocketConnection> connections = new HashSet<DesktopWebSocketConnection>();
        private readonly HashSet<TcpClient> pending = new HashSet<TcpClient>();
        private readonly SemaphoreSlim handshakeSlots = new SemaphoreSlim(4, 4);
        private TcpListener listener;
        private bool frozen, disposed;
        private readonly string credential;
        public Uri Endpoint { get; private set; }

        public DesktopWebSocketServer(string credential, Func<DesktopWebSocketPeer, bool> authorizePeer, Action<DesktopWebSocketConnection> connected)
        {
            if (String.IsNullOrEmpty(credential) || credential.Length < 24 || credential.Length > 256 || credential.Any(c => c < 33 || c > 126 || c == ':' || c == '\r' || c == '\n'))
                throw new ArgumentException("A bounded random integration credential is required.", "credential");
            if (authorizePeer == null || connected == null) throw new ArgumentNullException();
            this.credential = credential;
            authorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("switcher:" + credential));
            authorize = authorizePeer; this.connected = connected;
        }
        public void Start()
        {
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException("DesktopWebSocketServer");
                if (listener != null) throw new InvalidOperationException("Integration listener already started.");
                listener = new TcpListener(IPAddress.Loopback, 0); listener.Server.ExclusiveAddressUse = true; listener.Start(8);
                Endpoint = new UriBuilder("ws", "127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, "/rpc") { UserName = "switcher", Password = credential }.Uri;
                Task.Factory.StartNew(Accept, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }
        }
        public void Freeze() { lock (gate) { if (!disposed) frozen = true; } }
        public void Unfreeze() { lock (gate) { if (!disposed) frozen = false; } }
        private void Accept()
        {
            while (true)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); }
                catch { lock (gate) { if (disposed) return; } continue; }
                bool taken = handshakeSlots.Wait(0);
                lock (gate)
                {
                    if (disposed) { client.Close(); if (taken) handshakeSlots.Release(); return; }
                    pending.Add(client);
                }
                if (!taken) { Reply(client, 503); lock (gate) pending.Remove(client); client.Close(); continue; }
                Task.Factory.StartNew(() => Handshake(client), CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
            }
        }
        private static bool EqualSecret(string left, string right)
        {
            int difference = left.Length ^ right.Length;
            for (int i = 0; i < right.Length; i++) difference |= (i < left.Length ? left[i] : 0) ^ right[i];
            return difference == 0;
        }
        private static bool Token(string value, string wanted)
        { return value != null && value.Split(',').Any(part => String.Equals(part.Trim(), wanted, StringComparison.OrdinalIgnoreCase)); }
        private static string Header(Dictionary<string, string> headers, string name)
        { string value; return headers.TryGetValue(name, out value) ? value : null; }
        private static bool ValidKey(string value)
        {
            try { var bytes = Convert.FromBase64String(value ?? ""); return bytes.Length == 16 && Convert.ToBase64String(bytes) == value; }
            catch (FormatException) { return false; }
        }
        private static Dictionary<string, string> ReadUpgrade(TcpClient client, out string request)
        {
            var bytes = new List<byte>(); var clock = Stopwatch.StartNew();
            while (bytes.Count < HeaderLimit)
            {
                if (clock.ElapsedMilliseconds >= HandshakeTimeout) throw new IOException("Integration handshake timed out.");
                if (!client.Client.Poll(100000, SelectMode.SelectRead)) continue;
                var one = new byte[1]; int count = client.Client.Receive(one, 0, 1, SocketFlags.None);
                if (count == 0) throw new IOException("Incomplete integration handshake.");
                if (one[0] == 0 || one[0] > 127) throw new IOException("Invalid integration header encoding.");
                bytes.Add(one[0]); int n = bytes.Count;
                if (n >= 4 && bytes[n - 4] == 13 && bytes[n - 3] == 10 && bytes[n - 2] == 13 && bytes[n - 1] == 10) break;
            }
            if (bytes.Count >= HeaderLimit) throw new IOException("Integration headers exceeded their limit.");
            string text = Encoding.ASCII.GetString(bytes.ToArray()); string[] lines = text.Split(new[] { "\r\n" }, StringSplitOptions.None);
            request = lines[0]; var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (lines.Length > 66) throw new IOException("Too many integration headers.");
            foreach (string line in lines.Skip(1))
            {
                if (line.Length == 0) continue;
                int colon = line.IndexOf(':');
                if (colon <= 0 || line.Length > 4096 || Char.IsWhiteSpace(line[0])) throw new IOException("Malformed integration header.");
                string name = line.Substring(0, colon);
                if (name.Any(c => !(Char.IsLetterOrDigit(c) || "!#$%&'*+-.^_`|~".IndexOf(c) >= 0)) || headers.ContainsKey(name))
                    throw new IOException("Duplicate or malformed integration header.");
                string value = line.Substring(colon + 1).Trim();
                if (value.Any(c => c < 32 && c != '\t' || c == 127)) throw new IOException("Malformed integration header value.");
                headers.Add(name, value);
            }
            return headers;
        }
        private void Handshake(TcpClient client)
        {
            DesktopWebSocketConnection result = null; IntPtr handle = IntPtr.Zero;
            try
            {
                client.NoDelay = true; client.SendTimeout = HandshakeTimeout;
                lock (gate) { if (disposed) return; }
                string request; var headers = ReadUpgrade(client, out request);
                lock (gate) { if (disposed || frozen || connections.Count >= MaxConnections) { Reply(client, 503); return; } }
                if (headers.ContainsKey("Origin")) { Reply(client, 403); return; }
                if (!EqualSecret(Header(headers, "Authorization") ?? "", authorization)) { Reply(client, 401); return; }
                string host = "127.0.0.1:" + ((IPEndPoint)client.Client.LocalEndPoint).Port;
                if (request != "GET /rpc HTTP/1.1" || !String.Equals(Header(headers, "Host"), host, StringComparison.OrdinalIgnoreCase) ||
                    !Token(Header(headers, "Upgrade"), "websocket") || !Token(Header(headers, "Connection"), "Upgrade") ||
                    Header(headers, "Sec-WebSocket-Version") != "13" || !ValidKey(Header(headers, "Sec-WebSocket-Key")) || headers.ContainsKey("Transfer-Encoding") ||
                    headers.ContainsKey("Content-Length") && Header(headers, "Content-Length") != "0")
                { Reply(client, 400); return; }
                string protocol = Header(headers, "Sec-WebSocket-Protocol");
                if (protocol != null && protocol.Trim() != "codex-app-server") { Reply(client, 400); return; }
                var identity = TcpIdentity(client);
                if (identity == null) { Reply(client, 403); return; }
                var peer = new DesktopWebSocketPeer { Pid = identity.Pid, StartedUtc = identity.StartedUtc, ExecutablePath = identity.ExecutablePath, ParentPid = identity.ParentPid };
                if (!authorize(peer) || !DesktopNative.Same(identity)) { Reply(client, 403); return; }
                DesktopWebSocketNative.Check(DesktopWebSocketNative.CreateServer(IntPtr.Zero, 0, out handle));
                string response = DesktopWebSocketNative.Handshake(handle, headers, protocol);
                lock (gate)
                {
                    if (disposed || frozen || connections.Count >= MaxConnections) { Reply(client, 503); return; }
                    var bytes = Encoding.ASCII.GetBytes(response); client.GetStream().Write(bytes, 0, bytes.Length);
                    DesktopWebSocketNative.Check(DesktopWebSocketNative.EndHandshake(handle));
                    result = new DesktopWebSocketConnection(client, handle, peer, connection => { lock (gate) connections.Remove(connection); });
                    handle = IntPtr.Zero; connections.Add(result); pending.Remove(client);
                }
                var accepted = result;
                Task.Factory.StartNew(() => { try { connected(accepted); } catch { accepted.Dispose(); } }, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
            }
            catch { if (result == null) Reply(client, 400); }
            finally
            {
                if (handle != IntPtr.Zero) DesktopWebSocketNative.Delete(handle);
                lock (gate) pending.Remove(client);
                if (result == null) client.Close(); handshakeSlots.Release();
            }
        }
        private static void Reply(TcpClient client, int status)
        {
            try
            {
                client.SendTimeout = 1000;
                string extra = status == 401 ? "WWW-Authenticate: Basic realm=\"CodexAccountSwitcher\"\r\n" : "";
                byte[] reply = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + " Rejected\r\nConnection: close\r\nContent-Length: 0\r\n" + extra + "\r\n");
                client.GetStream().Write(reply, 0, reply.Length);
                client.Client.Shutdown(SocketShutdown.Send);
                // Drain only already-arrived rejected request bytes. Closing a
                // socket with unread input can turn the HTTP rejection into RST.
                var drain = new byte[1024]; int total = 0;
                while (client.Available > 0 && total < 32768)
                { int read = client.Client.Receive(drain, 0, Math.Min(drain.Length, client.Available), SocketFlags.None); if (read <= 0) break; total += read; }
            }
            catch { }
        }
        [StructLayout(LayoutKind.Sequential)] private struct TcpRow { public uint State, LocalAddress, LocalPort, RemoteAddress, RemotePort, Pid; }
        [DllImport("iphlpapi.dll", SetLastError = true)] private static extern uint GetExtendedTcpTable(IntPtr table, ref int length, bool order, int family, int kind, uint reserved);
        private static int Port(uint value) { return (int)(((value & 255) << 8) | ((value >> 8) & 255)); }
        private static DesktopProcessIdentity TcpIdentity(TcpClient client)
        {
            var remote = client.Client.RemoteEndPoint as IPEndPoint; var local = client.Client.LocalEndPoint as IPEndPoint;
            if (remote == null || local == null || !IPAddress.IsLoopback(remote.Address) || !IPAddress.IsLoopback(local.Address)) return null;
            for (int retry = 0; retry < 5; retry++)
            {
                int size = 0; uint initial = GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 5, 0);
                if (initial != 122 || size < 4 || size > 16 * 1024 * 1024) return null;
                IntPtr memory = Marshal.AllocHGlobal(size);
                try
                {
                    if (GetExtendedTcpTable(memory, ref size, false, 2, 5, 0) != 0) continue;
                    int count = Marshal.ReadInt32(memory), rowSize = Marshal.SizeOf(typeof(TcpRow));
                    if (count < 0 || (long)count * rowSize + 4 > size) return null;
                    var matches = new HashSet<int>();
                    for (int i = 0; i < count; i++)
                    {
                        var row = (TcpRow)Marshal.PtrToStructure(IntPtr.Add(memory, 4 + i * rowSize), typeof(TcpRow));
                        if (row.State == 5 && Port(row.LocalPort) == remote.Port && Port(row.RemotePort) == local.Port &&
                            BitConverter.GetBytes(row.LocalAddress).SequenceEqual(remote.Address.GetAddressBytes()) &&
                            BitConverter.GetBytes(row.RemoteAddress).SequenceEqual(local.Address.GetAddressBytes())) matches.Add((int)row.Pid);
                    }
                    if (matches.Count == 1) return DesktopNative.Identity(matches.Single());
                    if (matches.Count > 1) return null;
                }
                finally { Marshal.FreeHGlobal(memory); }
                Thread.Sleep(10);
            }
            return null;
        }
        public void Dispose()
        {
            DesktopWebSocketConnection[] active; TcpClient[] accepting;
            lock (gate)
            {
                if (disposed) return; disposed = true; frozen = true;
                if (listener != null) listener.Stop(); active = connections.ToArray(); accepting = pending.ToArray(); pending.Clear();
            }
            foreach (var client in accepting) client.Close();
            foreach (var connection in active) connection.Dispose();
        }
    }

    // Track only wire framing progress. The Windows component remains responsible
    // for protocol and UTF8 validation. It can handle a ping entirely internally,
    // without a receive-complete action, so network activity alone is not evidence
    // that an application message is incomplete.
    internal sealed class DesktopWebSocketReceiveProgress
    {
        private readonly byte[] header = new byte[14];
        private int headerBytes, headerRequired = 2, opcode;
        private ulong payloadRemaining;
        private bool final, fragmented;
        private long incompleteSince = -1;
        internal void CheckTimeout(long nowMilliseconds)
        {
            if (incompleteSince >= 0 && nowMilliseconds - incompleteSince > 30000)
                throw new IOException("Incomplete desktop integration message timed out.");
        }
        private void FrameComplete()
        {
            if (opcode == 0 && final || (opcode == 1 || opcode == 2) && final) fragmented = false;
            if (!fragmented) incompleteSince = -1;
            headerBytes = 0; headerRequired = 2;
        }
        internal void Observe(byte[] bytes, int count, long nowMilliseconds)
        {
            int offset = 0;
            while (offset < count)
            {
                if (headerBytes < headerRequired)
                {
                    if (headerBytes == 0 && incompleteSince < 0) incompleteSince = nowMilliseconds;
                    header[headerBytes++] = bytes[offset++];
                    if (headerBytes == 2)
                    {
                        int form = header[1] & 127;
                        headerRequired = 2 + (form == 126 ? 2 : form == 127 ? 8 : 0) + ((header[1] & 128) != 0 ? 4 : 0);
                    }
                    if (headerBytes != headerRequired) continue;
                    opcode = header[0] & 15; final = (header[0] & 128) != 0;
                    int lengthForm = header[1] & 127;
                    payloadRemaining = (ulong)lengthForm;
                    if (lengthForm >= 126)
                    {
                        payloadRemaining = 0; int lengthBytes = lengthForm == 126 ? 2 : 8;
                        for (int i = 0; i < lengthBytes; i++) payloadRemaining = (payloadRemaining << 8) | header[2 + i];
                    }
                    if ((opcode == 1 || opcode == 2) && !final) fragmented = true;
                    if (payloadRemaining == 0) FrameComplete();
                    continue;
                }
                int take = (int)Math.Min((ulong)(count - offset), payloadRemaining);
                offset += take; payloadRemaining -= (ulong)take;
                if (payloadRemaining == 0) FrameComplete();
            }
        }
    }

    internal sealed class DesktopWebSocketConnection : IDisposable
    {
        internal const int MaxMessageBytes = 32 * 1024 * 1024;
        private readonly TcpClient client;
        private readonly object nativeGate = new object(), readGate = new object(), sendGate = new object(), wireGate = new object();
        private readonly AutoResetEvent nativeChanged = new AutoResetEvent(false);
        private readonly DesktopWebSocketReceiveProgress receiveProgress = new DesktopWebSocketReceiveProgress();
        private readonly Stopwatch receiveClock = Stopwatch.StartNew();
        private readonly Action<DesktopWebSocketConnection> removed;
        private IntPtr handle;
        private bool disposed, closing, eventDisposed;
        private int operations;
        public DesktopWebSocketPeer Peer { get; private set; }
        internal DesktopWebSocketConnection(TcpClient client, IntPtr handle, DesktopWebSocketPeer peer, Action<DesktopWebSocketConnection> removed)
        { this.client = client; this.handle = handle; Peer = peer; this.removed = removed; }
        private void Enter()
        { lock (nativeGate) { if (disposed) throw new ObjectDisposedException("DesktopWebSocketConnection"); operations++; } }
        private void Leave()
        { lock (nativeGate) { operations--; Release(); } }
        private void Release()
        {
            if (!disposed || operations != 0) return;
            if (handle != IntPtr.Zero) { DesktopWebSocketNative.Delete(handle); handle = IntPtr.Zero; }
            if (!eventDisposed) { eventDisposed = true; nativeChanged.Dispose(); }
        }
        private void Signal() { try { nativeChanged.Set(); } catch (ObjectDisposedException) { } }
        private void CheckAlive(CancellationToken token)
        { token.ThrowIfCancellationRequested(); lock (nativeGate) if (disposed) throw new IOException("Desktop integration connection closed."); }
        private void Complete(IntPtr context, uint count)
        { lock (nativeGate) { if (handle != IntPtr.Zero) DesktopWebSocketNative.Complete(handle, context, count); } Signal(); }
        private uint Action(uint queue, DesktopWebSocketNative.Buffer[] buffers, out uint count, out uint type, out IntPtr context)
        {
            uint action; IntPtr application; count = (uint)buffers.Length;
            lock (nativeGate)
            {
                if (disposed) throw new IOException("Desktop integration connection closed.");
                DesktopWebSocketNative.Check(DesktopWebSocketNative.GetAction(handle, queue, buffers, ref count, out action, out type, out application, out context));
            }
            return action;
        }
        private void WriteBuffers(DesktopWebSocketNative.Buffer[] buffers, uint count, CancellationToken token)
        {
            lock (wireGate)
            {
                var clock = Stopwatch.StartNew();
                for (int i = 0; i < count; i++)
                {
                    if (buffers[i].Length > MaxMessageBytes + 1024U) throw new IOException("Native send buffer exceeded its bound.");
                    var bytes = new byte[Math.Min((int)buffers[i].Length, 65536)]; int offset = 0;
                    while (offset < buffers[i].Length)
                    {
                        CheckAlive(token); if (clock.ElapsedMilliseconds > 10000) throw new IOException("Desktop integration send timed out.");
                        int n = Math.Min(bytes.Length, (int)buffers[i].Length - offset); Marshal.Copy(IntPtr.Add(buffers[i].Data, offset), bytes, 0, n);
                        int sent = 0;
                        while (sent < n)
                        {
                            CheckAlive(token); if (clock.ElapsedMilliseconds > 10000) throw new IOException("Desktop integration send timed out.");
                            if (!client.Client.Poll(100000, SelectMode.SelectWrite)) continue;
                            int written = client.Client.Send(bytes, sent, n - sent, SocketFlags.None);
                            if (written == 0) throw new IOException("Desktop integration send closed."); sent += written;
                        }
                        offset += n;
                    }
                }
            }
        }
        public string ReadText(CancellationToken cancellation)
        {
            lock (readGate)
            {
                Enter();
                try
                {
                    using (cancellation.Register(Dispose))
                    using (var message = new MemoryStream())
                    {
                        while (true)
                        {
                            CheckAlive(cancellation);
                            lock (nativeGate) DesktopWebSocketNative.Check(DesktopWebSocketNative.Receive(handle, IntPtr.Zero, IntPtr.Zero));
                            bool completed = false;
                            while (!completed)
                            {
                                CheckAlive(cancellation); receiveProgress.CheckTimeout(receiveClock.ElapsedMilliseconds);
                                var buffers = new DesktopWebSocketNative.Buffer[8]; uint count, type; IntPtr actionContext;
                                uint action = Action(2, buffers, out count, out type, out actionContext);
                                if (action == 0) { nativeChanged.WaitOne(25); continue; }
                                uint transferred = 0;
                                try
                                {
                                    if (action == 3)
                                    {
                                        if (count == 0 || buffers[0].Length == 0 || buffers[0].Length > 1024 * 1024) throw new IOException("Invalid native receive buffer.");
                                        while (!client.Client.Poll(100000, SelectMode.SelectRead))
                                        { CheckAlive(cancellation); receiveProgress.CheckTimeout(receiveClock.ElapsedMilliseconds); }
                                        var bytes = new byte[Math.Min((int)buffers[0].Length, 65536)];
                                        int read = client.Client.Receive(bytes, 0, bytes.Length, SocketFlags.None);
                                        if (read == 0) throw new IOException("Desktop integration transport ended without a close frame.");
                                        receiveProgress.Observe(bytes, read, receiveClock.ElapsedMilliseconds);
                                        Marshal.Copy(bytes, 0, buffers[0].Data, read); transferred = (uint)read;
                                    }
                                    else if (action == 1)
                                    { WriteBuffers(buffers, count, cancellation); for (int i = 0; i < count; i++) transferred += buffers[i].Length; }
                                    else if (action == 4)
                                    {
                                        if (count != 1) throw new IOException("Invalid native message result.");
                                        completed = true;
                                        if (type == DesktopWebSocketNative.CloseType)
                                        {
                                            Complete(actionContext, 0); actionContext = IntPtr.Zero;
                                            Close(1000, ""); return null;
                                        }
                                        if (type == DesktopWebSocketNative.BinaryType || type == DesktopWebSocketNative.BinaryFragment)
                                            throw new IOException("Binary desktop integration messages are not supported.");
                                        if (type == 0x80000005 || type == 0x80000006)
                                        { continue; }
                                        if (type != DesktopWebSocketNative.TextType && type != DesktopWebSocketNative.TextFragment)
                                            throw new IOException("Unsupported desktop integration frame type.");
                                        if (message.Length + buffers[0].Length > MaxMessageBytes) throw new IOException("Desktop integration message exceeded its limit.");
                                        var bytes = new byte[(int)buffers[0].Length]; if (bytes.Length > 0) Marshal.Copy(buffers[0].Data, bytes, 0, bytes.Length);
                                        message.Write(bytes, 0, bytes.Length);
                                        if (type == DesktopWebSocketNative.TextType) return new UTF8Encoding(false, true).GetString(message.ToArray());
                                    }
                                    else if (action != 2) throw new IOException("Invalid native integration action.");
                                }
                                finally { if (actionContext != IntPtr.Zero) Complete(actionContext, transferred); }
                            }
                        }
                    }
                }
                catch { Dispose(); cancellation.ThrowIfCancellationRequested(); throw; }
                finally { Leave(); }
            }
        }
        public void SendText(string text, CancellationToken cancellation)
        {
            if (text == null) throw new ArgumentNullException("text");
            var utf8 = new UTF8Encoding(false, true);
            if (text.Length > MaxMessageBytes || utf8.GetByteCount(text) > MaxMessageBytes) throw new IOException("Desktop integration message exceeded its limit.");
            byte[] bytes = utf8.GetBytes(text);
            Send(bytes, DesktopWebSocketNative.TextType, 0, cancellation);
        }
        private void Send(byte[] bytes, uint type, ushort code, CancellationToken cancellation)
        {
            lock (sendGate)
            {
                Enter(); GCHandle pinned = default(GCHandle);
                try
                {
                    using (cancellation.Register(Dispose))
                    {
                        CheckAlive(cancellation); pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                        var buffer = new DesktopWebSocketNative.Buffer { Data = pinned.AddrOfPinnedObject(), Length = (uint)bytes.Length, Status = code };
                        lock (nativeGate) DesktopWebSocketNative.Check(DesktopWebSocketNative.Send(handle, type, new[] { buffer }, IntPtr.Zero));
                        var clock = Stopwatch.StartNew();
                        while (true)
                        {
                            CheckAlive(cancellation); if (clock.ElapsedMilliseconds > 10000) throw new IOException("Desktop integration send timed out.");
                            var buffers = new DesktopWebSocketNative.Buffer[8]; uint count, receivedType; IntPtr actionContext;
                            uint action = Action(1, buffers, out count, out receivedType, out actionContext);
                            if (action == 0) { nativeChanged.WaitOne(25); continue; }
                            uint transferred = 0;
                            try
                            {
                                if (action == 1) { WriteBuffers(buffers, count, cancellation); for (int i = 0; i < count; i++) transferred += buffers[i].Length; }
                                else if (action == 2) return;
                                else throw new IOException("Invalid native send action.");
                            }
                            finally { Complete(actionContext, transferred); }
                        }
                    }
                }
                catch { Dispose(); cancellation.ThrowIfCancellationRequested(); throw; }
                finally { if (pinned.IsAllocated) pinned.Free(); Leave(); }
            }
        }
        public void Close(int code, string reason)
        {
            if (!(code >= 3000 && code < 5000 || code >= 1000 && code <= 1014 && code != 1004 && code != 1005 && code != 1006)) throw new ArgumentOutOfRangeException("code");
            byte[] bytes = new UTF8Encoding(false, true).GetBytes(reason ?? "");
            if (bytes.Length > 123) throw new ArgumentException("Close reason exceeded its limit.", "reason");
            lock (nativeGate) { if (disposed || closing) return; closing = true; }
            try { Send(bytes, DesktopWebSocketNative.CloseType, (ushort)code, CancellationToken.None); }
            catch { }
            finally { Dispose(); }
        }
        public void Dispose()
        {
            bool notify;
            lock (nativeGate)
            {
                notify = !disposed; if (!notify) return; disposed = true;
                if (handle != IntPtr.Zero) DesktopWebSocketNative.Abort(handle); Release();
            }
            try { client.Close(); } catch { } Signal(); if (removed != null) removed(this);
        }
    }

    internal static class DesktopWebSocketNative
    {
        internal const uint TextType = 0x80000000, TextFragment = 0x80000001, BinaryType = 0x80000002, BinaryFragment = 0x80000003, CloseType = 0x80000004;
        [StructLayout(LayoutKind.Explicit, Size = 16)] internal struct Buffer
        { [FieldOffset(0)] public IntPtr Data; [FieldOffset(8)] public uint Length; [FieldOffset(12)] public ushort Status; }
        [StructLayout(LayoutKind.Sequential)] private struct Header
        { public IntPtr Name; public uint NameLength; public IntPtr Value; public uint ValueLength; }
        [DllImport("websocket.dll", EntryPoint = "WebSocketCreateServerHandle")] internal static extern int CreateServer(IntPtr properties, uint count, out IntPtr handle);
        [DllImport("websocket.dll", EntryPoint = "WebSocketBeginServerHandshake", CharSet = CharSet.Ansi)] private static extern int BeginHandshake(IntPtr handle, string protocol, IntPtr extensions, uint extensionCount, [In] Header[] headers, uint headerCount, out IntPtr responseHeaders, out uint responseCount);
        [DllImport("websocket.dll", EntryPoint = "WebSocketEndServerHandshake")] internal static extern int EndHandshake(IntPtr handle);
        [DllImport("websocket.dll", EntryPoint = "WebSocketGetAction")] internal static extern int GetAction(IntPtr handle, uint queue, [In, Out] Buffer[] buffers, ref uint count, out uint action, out uint type, out IntPtr applicationContext, out IntPtr actionContext);
        [DllImport("websocket.dll", EntryPoint = "WebSocketReceive")] internal static extern int Receive(IntPtr handle, IntPtr buffer, IntPtr applicationContext);
        [DllImport("websocket.dll", EntryPoint = "WebSocketSend")] internal static extern int Send(IntPtr handle, uint type, [In] Buffer[] buffer, IntPtr applicationContext);
        [DllImport("websocket.dll", EntryPoint = "WebSocketCompleteAction")] internal static extern void Complete(IntPtr handle, IntPtr context, uint count);
        [DllImport("websocket.dll", EntryPoint = "WebSocketAbortHandle")] internal static extern void Abort(IntPtr handle);
        [DllImport("websocket.dll", EntryPoint = "WebSocketDeleteHandle")] internal static extern void Delete(IntPtr handle);
        internal static void Check(int status) { if (status < 0) throw new IOException("The native desktop integration protocol rejected the operation."); }
        internal static string Handshake(IntPtr handle, Dictionary<string, string> values, string protocol)
        {
            var allocated = new List<IntPtr>();
            try
            {
                var request = values.Select(pair =>
                {
                    IntPtr name = Marshal.StringToHGlobalAnsi(pair.Key), value = Marshal.StringToHGlobalAnsi(pair.Value); allocated.Add(name); allocated.Add(value);
                    return new Header { Name = name, NameLength = (uint)pair.Key.Length, Value = value, ValueLength = (uint)pair.Value.Length };
                }).ToArray();
                IntPtr response; uint count; Check(BeginHandshake(handle, protocol, IntPtr.Zero, 0, request, (uint)request.Length, out response, out count));
                if (count > 32) throw new IOException("Native handshake headers exceeded their limit.");
                var text = new StringBuilder("HTTP/1.1 101 Switching Protocols\r\n"); int size = Marshal.SizeOf(typeof(Header));
                for (int i = 0; i < count; i++)
                {
                    var header = (Header)Marshal.PtrToStructure(IntPtr.Add(response, i * size), typeof(Header));
                    if (header.NameLength > 256 || header.ValueLength > 4096) throw new IOException("Native handshake header exceeded its limit.");
                    text.Append(Marshal.PtrToStringAnsi(header.Name, (int)header.NameLength)).Append(": ").Append(Marshal.PtrToStringAnsi(header.Value, (int)header.ValueLength)).Append("\r\n");
                }
                return text.Append("\r\n").ToString();
            }
            finally { foreach (IntPtr memory in allocated) Marshal.FreeHGlobal(memory); }
        }
    }
}
