using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using CodexAccountSwitcher;

class FakeUsageCodex
{
    private static void Require(bool condition)
    { if (!condition) throw new Exception("Invalid synthetic usage fixture request."); }

    private static Dictionary<string, object> Read(string method)
    {
        string line = Console.ReadLine();
        Require(line != null && !line.Contains("refresh_token") && !line.Contains("refreshToken") && !line.Contains("fake-refresh-do-not-send"));
        var request = Json.Object(line);
        Require(Json.Str(request, "method") == method);
        return request;
    }

    private static void Reply(Dictionary<string, object> request, object result)
    { Console.WriteLine(Json.Encode(new { id = request["id"], result = result })); }

    private static void CheckIsolation()
    {
        string home = Environment.GetEnvironmentVariable("CODEX_HOME");
        Require(!String.IsNullOrEmpty(home) && Directory.Exists(home));
        Require(Path.GetFileName(home).StartsWith("usage-", StringComparison.Ordinal));
        Require(String.Equals(Path.GetFullPath(home).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(Environment.CurrentDirectory).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase));
        Require(!File.Exists(Path.Combine(home, "auth.json")));
        string config = File.ReadAllText(Path.Combine(home, "config.toml")).Replace(" ", "").Replace("\t", "");
        Require(config.Contains("cli_auth_credentials_store=\"ephemeral\""));
        var featureSettings = new HashSet<string>(StringComparer.Ordinal);
        bool inFeatures = false;
        foreach (string line in config.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("[", StringComparison.Ordinal)) inFeatures = line == "[features]";
            else if (inFeatures) featureSettings.Add(line);
        }
        foreach (string feature in new[] { "plugins", "remote_plugin", "apps", "daemon_auto_start" })
            Require(featureSettings.Contains(feature + "=false"));
        foreach (string key in new[] { "OPENAI_API_KEY", "CODEX_API_KEY", "CODEX_ACCESS_TOKEN", "OPENAI_BASE_URL", "OPENAI_IDENTITY_TOKEN_FILE", "OPENAI_FEDERATION_RULE_ID" })
            Require(String.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)));
    }

    static void Main()
    {
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = new UTF8Encoding(false);
        try
        {
            CheckIsolation();
            var initialize = Read("initialize");
            var capabilities = Json.Map(Json.Map(initialize, "params"), "capabilities");
            Require(capabilities != null && capabilities.ContainsKey("experimentalApi") && Object.Equals(capabilities["experimentalApi"], true));
            Reply(initialize, new { });
            Read("initialized");
            var login = Read("account/login/start");
            var loginParams = Json.Map(login, "params");
            Require(Json.Str(loginParams, "type") == "chatgptAuthTokens");
            Require(Json.Str(loginParams, "accessToken") == "fake-access");
            Require(Json.Str(loginParams, "chatgptPlanType") == "plus");
            string scenario = Json.Str(loginParams, "chatgptAccountId");
            Require(!String.IsNullOrEmpty(scenario));
            string storage = Directory.GetParent(Environment.GetEnvironmentVariable("CODEX_HOME")).FullName;
            if (scenario == "login-error-with-secret")
            {
                File.WriteAllText(Path.Combine(storage, "fake-usage.pid"), Process.GetCurrentProcess().Id.ToString());
                Console.WriteLine(Json.Encode(new { id = login["id"], error = new { code = -32000, message = "rawserversecret fake-access" } }));
                Thread.Sleep(30000);
                return;
            }
            Reply(login, new { type = "chatgptAuthTokens" });
            var request = Read("account/rateLimits/read");
            CheckIsolation();
            File.WriteAllText(Path.Combine(storage, "fake-usage.pid"), Process.GetCurrentProcess().Id.ToString());
            if (scenario == "wait") { Thread.Sleep(30000); return; }
            if (scenario == "eof") return;
            if (scenario == "malformed")
            {
                Console.WriteLine("{ malformed rawserversecret");
                Console.Error.WriteLine("rawserversecret fake-access");
            }
            else if (scenario == "rpc-error-with-secret")
                Console.WriteLine(Json.Encode(new { id = request["id"], error = new { code = -32001, message = "rawserversecret fake-access", data = "fake-refresh-do-not-send" } }));
            else if (scenario == "refresh-request")
            {
                Console.WriteLine(Json.Encode(new { id = 41, method = "account/chatgptAuthTokens/refresh", @params = new { reason = "unauthorized", previousAccountId = scenario } }));
                string response = Console.ReadLine();
                if (response != null)
                {
                    Require(!response.Contains("fake-refresh-do-not-send") && !response.Contains("fake-access"));
                    Require(Json.Map(Json.Object(response), "error") != null);
                }
            }
            else
            {
                if (scenario == "unknown-server-request")
                {
                    Console.WriteLine(Json.Encode(new { id = 42, method = "server/unknown", @params = new { } }));
                    string response = Console.ReadLine();
                    Require(response != null && !response.Contains("fake-refresh-do-not-send") && !response.Contains("fake-access"));
                    var errorResponse = Json.Object(response);
                    Require(Convert.ToInt32(errorResponse["id"]) == 42);
                    Require(Convert.ToInt32(Json.Map(errorResponse, "error")["code"]) == -32601);
                }
                Reply(request, new
                {
                    accountId = scenario == "invalid-account-id" ? (object)123 : scenario == "wrong-account" ? "another-workspace" : scenario,
                    rateLimitsByLimitId = new
                    {
                        codex = new
                        {
                            primary = new { windowDurationMins = 300, usedPercent = 40, resetsAt = 1704067200 },
                            secondary = new { windowDurationMins = 10080, usedPercent = 75, resetsAt = 1704067200 }
                        }
                    }
                });
            }
            // The client must terminate the helper once it has a result or error.
            Thread.Sleep(30000);
        }
        catch { Environment.ExitCode = 91; }
    }
}
