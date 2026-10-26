using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal static class FakeDesktopApp
{
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static void Send(ClientWebSocket socket, object value, CancellationToken cancellation)
    { byte[] data = Encoding.UTF8.GetBytes(Json.Serialize(value)); socket.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Text, true, cancellation).GetAwaiter().GetResult(); }
    private static Dictionary<string, object> Receive(ClientWebSocket socket, CancellationToken cancellation)
    {
        using (var output = new MemoryStream())
        {
            byte[] buffer = new byte[4096]; WebSocketReceiveResult result;
            do
            {
                result = socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellation).GetAwaiter().GetResult();
                if (result.MessageType != WebSocketMessageType.Text || output.Length + result.Count > 131072) throw new IOException("Fixture protocol bound exceeded.");
                output.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);
            return Json.DeserializeObject(Encoding.UTF8.GetString(output.ToArray())) as Dictionary<string, object>;
        }
    }
    public static int Main(string[] args)
    {
        if (args.Length != 2 || !Path.IsPathRooted(args[0])) return 2;
        string marker = args[0], mode = args[1];
        try
        {
            var endpoint = new Uri(Environment.GetEnvironmentVariable("CODEX_APP_SERVER_WS_URL"));
            string credentials = Uri.UnescapeDataString(endpoint.UserInfo);
            var safeEndpoint = new UriBuilder(endpoint) { UserName = "", Password = "" }.Uri;
            using (var timeout = new CancellationTokenSource(10000))
            using (var socket = new ClientWebSocket())
            {
                socket.Options.SetRequestHeader("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials)));
                socket.ConnectAsync(safeEndpoint, timeout.Token).GetAwaiter().GetResult();
                Send(socket, new { id = "fake:init", method = "initialize", @params = new { clientInfo = new { name = "fake_desktop", version = "1" }, capabilities = new { experimentalApi = true } } }, timeout.Token);
                while (true)
                { var response = Receive(socket, timeout.Token); object id; if (response.TryGetValue("id", out id) && Object.Equals(id, "fake:init")) break; }
                if (mode == "desktop-handshake")
                {
                    File.WriteAllText(marker, "connected-without-initialized-notification", new UTF8Encoding(false));
                    var connectedClock = System.Diagnostics.Stopwatch.StartNew();
                    while (!File.Exists(marker + ".stop") && connectedClock.ElapsedMilliseconds < 20000) Thread.Sleep(20);
                    socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "fixture normal Quit", timeout.Token).GetAwaiter().GetResult();
                    return File.Exists(marker + ".stop") ? 0 : 3;
                }
                Send(socket, new { method = "initialized" }, timeout.Token);
                if (mode == "pending")
                {
                    Send(socket, new { id = "fake:callback", method = "test/callback", @params = new { } }, timeout.Token);
                    while (true)
                    { var response = Receive(socket, timeout.Token); object method; if (response.TryGetValue("method", out method) && Object.Equals(method, "item/tool/requestUserInput")) break; }
                }
                socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "fixture socket disconnected while App remains alive", timeout.Token).GetAwaiter().GetResult();
            }
            File.WriteAllText(marker, "disconnected-but-App-alive", new UTF8Encoding(false));
            // Remain alive after WebSocket EOF. Only this explicit fixture stop marker ends the owner.
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!File.Exists(marker + ".stop") && clock.ElapsedMilliseconds < 20000) Thread.Sleep(20);
            return File.Exists(marker + ".stop") ? 0 : 3;
        }
        catch { File.WriteAllText(marker + ".error", "fixture-failed", new UTF8Encoding(false)); return 1; }
    }
}
