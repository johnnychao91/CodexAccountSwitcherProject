using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

internal static class FakeManagedBackend
{
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static string mode;
    private static bool active, spawned;
    private static int loadedQueries;
    private const string ContinuationThread = "11111111-1111-4111-8111-111111111111", ContinuationTurn = "22222222-2222-4222-8222-222222222222";
    private static string continuationTail = ContinuationTurn, continuationStatus, continuationChange;
    private static object ContinuationEnvironments()
    {
        if (continuationChange == "env-unbound" || mode == "continuation-quota-unbound") return null;
        if (continuationChange == null || !continuationChange.StartsWith("env-")) return new object[0];
        string cwd = Path.GetTempPath();
        string[] roots = new[] { cwd, Path.Combine(cwd, continuationChange == "env-roots" ? "other-visualization" : "fixture-visualization") };
        var environment = new Dictionary<string, object> { { "environmentId", continuationChange == "env-remote" ? "remote-fixture" : "local" },
            { "cwd", continuationChange == "env-cwd" ? Path.Combine(cwd, "other-workspace") : cwd }, { "runtimeWorkspaceRoots", roots } };
        if (continuationChange == "env-unknown-key") environment["unverified"] = true;
        return continuationChange == "env-multiple" ? new object[] { environment, environment } : new object[] { environment };
    }
    private static string ContinuationRollout(bool resumed)
    {
        if (continuationChange == "rollout-missing" || continuationChange == "rollout-read-missing" && !resumed) return null;
        if (continuationChange == "rollout-relative") return "relative-rollout.jsonl";
        if (continuationChange == "rollout-drive-relative") return "C:relative-rollout.jsonl";
        string rollout = Path.Combine(Path.GetTempPath(), continuationChange == "rollout" || continuationChange == "rollout-mismatch" && resumed ? "other-fixture-rollout.jsonl" : "original-fixture-rollout.jsonl");
        if (continuationChange == "rollout-verbatim" || continuationChange == "rollout-verbatim-resume" && resumed)
            rollout = new string('\\', 2) + "?\\" + rollout;
        return rollout;
    }
    private static readonly object outputGate = new object();
    private static void Send(object value) { lock (outputGate) { Console.WriteLine(Json.Serialize(value)); Console.Out.Flush(); } }
    private static string Str(Dictionary<string, object> value, string key)
    { object result; return value != null && value.TryGetValue(key, out result) ? Convert.ToString(result) : null; }
    public static int Main(string[] args)
    {
        Console.InputEncoding = new UTF8Encoding(false, true);
        Console.OutputEncoding = new UTF8Encoding(false, true);
        mode = args.Length > 0 ? args[0] : "idle"; active = mode == "active" || mode == "continuation-active" || mode == "continuation-goal" || mode == "continuation-multi";
        continuationStatus = active ? "inProgress" : mode == "continuation-quota" || mode == "continuation-quota-unbound" || mode == "continuation-policy" ? "failed" : "completed";
        string line;
        while ((line = Console.ReadLine()) != null)
        {
            var request = Json.DeserializeObject(line) as Dictionary<string, object>;
            object id; request.TryGetValue("id", out id);
            string method = Str(request, "method");
            object raw; var parameters = request.TryGetValue("params", out raw) ? raw as Dictionary<string, object> : null;
            if (method == null)
            { Send(new { method = "test/callback-accepted", @params = new { id = id } }); continue; }
            if (!request.ContainsKey("id") && method != "initialized")
            { Send(new { method = "test/notification-forwarded", @params = new { notificationMethod = method } }); continue; }
            if (method == "initialize")
            {
                if (mode == "initialize-error") { Send(new { id = id, error = new { code = -32602, message = "Fixture initialization rejected" } }); continue; }
                Send(new { id = id, result = new { userAgent = "fake-managed-backend/1", platformFamily = "windows", platformOs = "windows" } });
                if (active) Send(new { method = "turn/started", @params = new { threadId = mode.StartsWith("continuation") ? ContinuationThread : "fake-thread-a", turn = new { id = mode.StartsWith("continuation") ? ContinuationTurn : "fake-turn-a", status = "inProgress" } } });
                continue;
            }
            if (method == "initialized") continue;
            if (method == "test/backend-environment")
            {
                Send(new { id = id, result = new { pipeMatches = Environment.GetEnvironmentVariable("CODEX_APP_TOOLS_PIPE_PATH") == Str(parameters, "expectedPipe"),
                    nodeMatches = Environment.GetEnvironmentVariable("CODEX_MCP_NODE_PATH") == Str(parameters, "expectedNode"),
                    cliMatches = Environment.GetEnvironmentVariable("CODEX_CLI_PATH") == Str(parameters, "expectedCli") } });
                continue;
            }
            if ((method == "account/read" || method == "test/hold-request") && Str(parameters, "testHoldMarker") != null)
            {
                string marker = Str(parameters, "testHoldMarker"); object heldId = id;
                ThreadPool.QueueUserWorkItem(delegate
                {
                    for (int attempt = 0; attempt < 500; attempt++)
                    {
                        if (File.Exists(marker)) { Send(new { id = heldId, result = new { } }); return; }
                        Thread.Sleep(10);
                    }
                });
                continue;
            }
            if (mode.StartsWith("continuation"))
            {
                if (method == "test/continuation-change")
                {
                    continuationChange = Str(parameters, "kind");
                    if (continuationChange == "tail") continuationTail = "33333333-3333-4333-8333-333333333333";
                    Send(new { id = id, result = new { } }); continue;
                }
                if (method == "thread/loaded/list")
                { Send(new { id = id, result = new { data = mode == "continuation-multi" ? new[] { ContinuationThread, "44444444-4444-4444-8444-444444444444" } : new[] { ContinuationThread }, nextCursor = (string)null } }); continue; }
                if (method == "thread/read")
                { Send(new { id = id, result = new { thread = new { id = Str(parameters, "threadId"), path = ContinuationRollout(false), ephemeral = false, parentThreadId = (string)null,
                    canAcceptDirectInput = true, source = "appServer", cwd = Path.GetTempPath(), environments = ContinuationEnvironments(), status = new { type = active ? "active" : "idle" } } } }); continue; }
                if (method == "thread/turns/list")
                {
                    if (Str(parameters, "itemsView") != "notLoaded")
                    { Send(new { id = id, error = new { code = -32602, message = "TurnItemsView must use the installed schema." } }); continue; }
                    Send(new { id = id, result = new { data = new[] { new { id = continuationTail, status = continuationStatus,
                    error = continuationStatus == "failed" ? new { codexErrorInfo = mode.StartsWith("continuation-quota") ? "usageLimitExceeded" : "contextWindowExceeded" } : null } }, nextCursor = (string)null } }); continue; }
                if (method == "thread/goal/get")
                {
                    object goal = null;
                    if (mode == "continuation-goal" || continuationChange != null && continuationChange.StartsWith("goal-"))
                        goal = new { threadId = continuationChange == "goal-wrong-thread" ? "unrelated-thread" : Str(parameters, "threadId"),
                            status = mode == "continuation-goal" ? "active" : continuationChange == "goal-usage" ? "usageLimited" : continuationChange == "goal-wrong-thread" ? "paused" : "unknown-state" };
                    Send(new { id = id, result = new { goal = goal } }); continue;
                }
                if (method == "thread/start")
                { Send(new { id = id, result = new { thread = new { id = ContinuationThread, environments = ContinuationEnvironments() } } }); continue; }
                if (method == "thread/resume")
                {
                    object excludeTurns;
                    if (parameters == null || parameters.Count != 2 || !parameters.TryGetValue("excludeTurns", out excludeTurns) || !Object.Equals(excludeTurns, true))
                    { Send(new { id = id, error = new { code = -32602, message = "Continuation metadata resume must exclude turns without overrides." } }); continue; }
                    object[] roots = new object[] { Path.GetTempPath(), Path.Combine(Path.GetTempPath(), "fixture-visualization") };
                    if (continuationChange == "roots") roots = new object[] { Path.GetTempPath(), Path.Combine(Path.GetTempPath(), "other-visualization") };
                    if (continuationChange == "roots-relative") roots = new object[] { "relative-fixture-root" };
                    if (continuationChange == "roots-drive-relative") roots = new object[] { "C:relative-fixture-root" };
                    if (continuationChange == "roots-root-relative") roots = new object[] { "\\relative-fixture-root" };
                    if (continuationChange == "roots-order") roots = new object[] { Path.Combine(Path.GetTempPath(), "fixture-visualization"), Path.GetTempPath() };
                    if (continuationChange == "roots-many") { roots = new object[65]; for (int index = 0; index < roots.Length; index++) roots[index] = Path.Combine(Path.GetTempPath(), "fixture-root-" + index); }
                    Send(new { id = id, result = new { thread = new { id = Str(parameters, "threadId"), path = ContinuationRollout(true), environments = ContinuationEnvironments() }, cwd = Path.GetTempPath(),
                    model = continuationChange == "model" ? "different-model" : "fake-model", modelProvider = "fake-provider", reasoningEffort = "medium",
                    disabledPluginIds = continuationChange == "disabled-plugins" ? new[] { "fixture-plugin@synthetic" } : new string[0], runtimeWorkspaceRoots = roots,
                    collaborationMode = continuationChange == "collab-plan" || continuationChange == "collab-unknown" || continuationChange == "collab-default" ?
                        new { mode = continuationChange == "collab-plan" ? "plan" : continuationChange == "collab-default" ? "default" : "unknown" } : null,
                    approvalPolicy = "never", approvalsReviewer = "user", sandbox = new { type = continuationChange == "permissions" ? "dangerFullAccess" : "readOnly" }, activePermissionProfile = (object)null } }); continue;
                }
                if (method == "account/read")
                { Send(new { id = id, result = new { account = new { type = "chatgpt", email = continuationChange == "account" ? "other@example.com" : "fake@example.com", planType = "plus" } } }); continue; }
                if (method == "model/list")
                { Send(new { id = id, result = new { data = new[] { new { id = continuationChange == "unavailable-model" ? "other-model" : "fake-model" } }, nextCursor = (string)null } }); continue; }
                if (method == "thread/backgroundTerminals/list")
                { Send(new { id = id, result = new { data = continuationChange == "background" ? new[] { new { processId = "fake-running-process" } } : new object[0], nextCursor = (string)null } }); continue; }
                if (method == "turn/interrupt")
                { active = false; continuationStatus = "interrupted"; Send(new { id = id, result = new { } }); Send(new { method = "turn/completed", @params = new { threadId = ContinuationThread, turn = new { id = ContinuationTurn, status = "interrupted" } } }); continue; }
                if (method == "turn/start")
                {
                    continuationTail = "fixture-continued-turn"; continuationStatus = "completed"; active = false;
                    Send(new { id = id, result = new { turn = new { id = continuationTail, status = "inProgress" } } });
                    Send(new { method = "test/continuation-accepted", @params = new { count = 1 } });
                    Send(new { method = "turn/completed", @params = new { threadId = ContinuationThread, turn = new { id = continuationTail, status = "completed" } } });
                    continue;
                }
            }
            if (method == "test/unicode-thread")
            { mode = "unicode-thread"; Send(new { id = id, result = new { } }); continue; }
            if (method == "test/query-count")
            { Send(new { id = id, result = new { count = loadedQueries } }); continue; }
            if (method == "thread/loaded/list")
            {
                loadedQueries++;
                if (mode == "query-error") { Send(new { id = id, error = new { code = -32601, message = "Test query unavailable" } }); continue; }
                bool second = Str(parameters, "cursor") == "second";
                if (mode == "unicode-thread")
                {
                    Send(new { id = id, result = new { data = new[] { "fake-thread-繁體中文-😀-café" }, nextCursor = (string)null } }); continue;
                }
                Send(new { id = id, result = new { data = second ? new[] { "fake-thread-b" } : new[] { "fake-thread-a" }, nextCursor = second ? null : "second" } }); continue;
            }
            if (method == "thread/read")
            {
                if (mode == "unicode-thread" && Str(parameters, "threadId") != "fake-thread-繁體中文-😀-café")
                { Send(new { id = id, error = new { code = -32001, message = "Synthetic non-ASCII thread identifier was changed." } }); continue; }
                Send(new { id = id, result = new { thread = new { id = Str(parameters, "threadId"), status = new { type = active && Str(parameters, "threadId") == "fake-thread-a" ? "active" : "idle", activeFlags = new string[0] } } } }); continue;
            }
            if (method == "thread/backgroundTerminals/list")
            {
                bool blocked = mode == "background" && Str(parameters, "threadId") == "fake-thread-b";
                bool second = Str(parameters, "cursor") == "terminal-second";
                Send(new { id = id, result = new { data = blocked && second ? new object[] { new { processId = "fake-background", command = "never inspect this fake command" } } : new object[0], nextCursor = blocked && !second ? "terminal-second" : null } }); continue;
            }
            if (method == "turn/interrupt")
            {
                active = false; Send(new { id = id, result = new { } });
                Send(new { method = "turn/completed", @params = new { threadId = "fake-thread-a", turn = new { id = "fake-turn-a", status = "interrupted" } } }); continue;
            }
            if (method == "test/callback")
            {
                Send(new { id = id, result = new { } });
                Send(new { id = "callback-1", method = "item/tool/requestUserInput", @params = new { threadId = "fake-thread-a" } }); continue;
            }
            if (method == "test/watch-activity" || method == "test/watch-corruption")
            {
                string marker = Str(parameters, "marker"); bool corruption = method == "test/watch-corruption"; Send(new { id = id, result = new { } });
                ThreadPool.QueueUserWorkItem(delegate
                {
                    for (int attempt = 0; attempt < 500; attempt++)
                    {
                        if (File.Exists(marker))
                        {
                            if (corruption) { lock (outputGate) { Console.WriteLine("synthetic-invalid-backend"); Console.Out.Flush(); } }
                            else Send(new { method = "thread/status/changed", @params = new { threadId = "fake-thread-a", status = new { type = "idle" } } });
                            return;
                        }
                        Thread.Sleep(10);
                    }
                });
                continue;
            }
            if (method == "process/spawn")
            {
                if (spawned) Send(new { id = id, error = new { code = -32001, message = "duplicate fake handle" } });
                else { spawned = true; Send(new { id = id, result = new { } }); }
                continue;
            }
            if (method == "test/crash") return 7;
            Send(new { id = id, result = parameters ?? new Dictionary<string, object>() });
        }
        if (mode == "slow-exit") Thread.Sleep(1000);
        return mode == "bad-exit" ? 7 : 0;
    }
}
