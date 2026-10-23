using System;
using System.IO;
using System.Text;
using System.Threading;
using CodexAccountSwitcher;
class FakeCodex
{
    static void Main()
    {
        Console.InputEncoding = Encoding.UTF8; Console.OutputEncoding = new UTF8Encoding(false);
        var init = Json.Object(Console.ReadLine());
        if (Json.Str(init, "method") != "initialize") Environment.Exit(2);
        Console.WriteLine("{\"id\":1,\"result\":{}}");
        if (Json.Str(Json.Object(Console.ReadLine()), "method") != "initialized") Environment.Exit(3);
        if (Json.Str(Json.Object(Console.ReadLine()), "method") != "account/login/start") Environment.Exit(4);
        Console.WriteLine("{\"id\":2,\"result\":{\"loginId\":\"test\",\"authUrl\":\"https://auth.openai.com/test-only\"}}");
        Thread.Sleep(300);
        File.WriteAllText(Path.Combine(Environment.GetEnvironmentVariable("CODEX_HOME"), "auth.json"), SelfTests.Auth("test", "workspace", "test@example.com", "plus", "test-refresh"));
        Console.WriteLine("{\"method\":\"account/login/completed\",\"params\":{\"loginId\":\"test\",\"success\":true}}");
        Thread.Sleep(30000);
    }
}
