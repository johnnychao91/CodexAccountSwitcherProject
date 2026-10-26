using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CodexAccountSwitcher
{
    public static class ContinuationAdmissionTests
    {
        private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        private static readonly string Policy = "{\"approvalPolicy\":\"on-request\",\"approvalsReviewer\":\"user\",\"sandbox\":{\"type\":\"readOnly\",\"networkAccess\":false},\"permissions\":{\"id\":\":read-only\",\"extends\":null}}";
        private static Dictionary<string, object> Request(string operation)
        {
            return new Dictionary<string, object> { { "threadId", "synthetic-thread" }, { "clientUserMessageId", operation }, { "model", "synthetic-model" },
                { "effort", "medium" }, { "input", new object[] { new Dictionary<string, object> { { "type", "text" }, { "text", "繼續執行" }, { "text_elements", new object[0] } } } },
                { "approvalPolicy", "on-request" }, { "approvalsReviewer", "user" }, { "permissions", ":read-only" } };
        }
        private static ContinuationAdmission Guard(string operation)
        { return new ContinuationAdmission("synthetic-thread", operation, "synthetic-model", "medium", Policy, Path.GetTempPath()); }

        private static Dictionary<string, object> FrontendResume()
        {
            var pages = new Dictionary<string, object>();
            foreach (string name in new[] { "create_presentation", "create_spreadsheet", "create_canvas", "execute_artifact_code", "inspect_artifact" })
                pages.Add("chatgpt_space." + name, new Dictionary<string, object> { { "enabled", false } });
            return new Dictionary<string, object> { { "threadId", "synthetic-thread" }, { "developerInstructions", "Synthetic application guidance; never retain this raw text in a ticket." },
                { "config", new Dictionary<string, object> {
                    { "mcp_servers.cua_repl", new Dictionary<string, object> { { "command", "synthetic-managed-app.exe" }, { "enabled", false } } },
                    { "apps.connector_openai_pages.tools", pages },
                    { "plugins.codex-app-tools@openai-bundled.mcp_servers.codex_app.enabled_tools", new[] { "read_thread", "list_threads", "send_message_to_thread" } } } } };
        }
        private static object[] LocalEnvironment()
        { return new object[] { new Dictionary<string, object> { { "environmentId", "local" }, { "cwd", Workspace }, { "runtimeWorkspaceRoots", new[] { Workspace } } } }; }
        private static bool ManagedCommand(string command) { return command == "synthetic-managed-app.exe"; }
        private static readonly string[] AppFeatureKeys = { "features.unified_image_budget", "features.code_mode_tool_description_first", "features.compaction_image_budget",
            "features.guardianv2_decisions_comparison", "features.realtime_conversation" };
        private static Dictionary<string, object> ObservedFrontendResume(bool featureValue)
        {
            // Actual pinned App shape observed through a successful native
            // frontend resume and its public broker tool inventory. No private
            // paths, instructions, endpoints or credentials are fixture data.
            var request = ColdResume(); var context = FrontendResume();
            request["config"] = context["config"];
            request["developerInstructions"] = new string('g', 6581);
            var config = (Dictionary<string, object>)request["config"];
            config["plugins.codex-app-tools@openai-bundled.mcp_servers.codex_app.enabled_tools"] = new[] {
                "create_worktree", "attach_worktree", "get_worktree_creation_status", "archive_worktree", "restore_worktree",
                "read_pull_request_check_diagnostics", "list_artifacts", "automation_update", "open_in_codex", "compile_latex_document",
                "create_latex_project", "navigate_to_codex_page", "read_thread_terminal", "check_app_update", "get_usage_limits",
                "load_workspace_dependencies", "fork_thread", "handoff_thread", "get_handoff_status", "list_projects", "create_thread",
                "list_threads", "list_archived_threads", "read_thread", "wait_threads", "send_message_to_thread", "set_thread_archived",
                "set_thread_title", "set_thread_read_state", "update_sidebar_preferences", "move_thread_to_project", "create_sidebar_section",
                "rename_sidebar_section", "delete_sidebar_section", "move_project_to_sidebar_section", "move_thread_to_sidebar_section",
                "reorder_section", "reorder_sidebar_projects", "reorder_sidebar_sections" };
            // Both bool values are intentional test cases, not guessed values
            // from the source App's initially type-only diagnostic projection.
            foreach (string key in AppFeatureKeys) config[key] = featureValue;
            // The native create tool's thinking=low is mapped directly to this
            // raw config key by the pinned builder. It is verified as CP.Effort,
            // separately from the persisted tool/instruction capability summary.
            config["model_reasoning_effort"] = "low";
            return request;
        }

        private static string Workspace { get { return Path.Combine(Path.GetTempPath(), "continuation-synthetic-workspace"); } }
        private static string WorkspacePolicy(bool named)
        { return new JavaScriptSerializer().Serialize(new Dictionary<string, object> { { "approvalPolicy", "on-request" }, { "approvalsReviewer", "user" },
            { "sandbox", new Dictionary<string, object> { { "type", "workspaceWrite" }, { "writableRoots", new[] { Workspace } }, { "networkAccess", false } } },
            { "permissions", named ? new Dictionary<string, object> { { "id", ":workspace" }, { "extends", null } } : null } }); }
        private static Dictionary<string, object> NormalizedRequest(bool named)
        {
            // Extracted and executed from pinned App main.js pDe then qm and its
            // runtime oMe/fE wrappers. Main SHA256:
            // 2327d031e32c8d80c0dbf9bd81144466a90f8e49d3bc64e6604c23fbab900d7f.
            // Paths and identifiers below are synthetic; no private App records.
            const string wire = "{\"threadId\":\"synthetic-thread\",\"clientUserMessageId\":\"synthetic-operation\",\"input\":[{\"type\":\"text\",\"text\":\"繼續執行\"}],\"cwd\":null,\"approvalPolicy\":\"on-request\",\"approvalsReviewer\":\"user\",\"sandboxPolicy\":null,\"permissions\":\":workspace\",\"runtimeWorkspaceRoots\":[],\"model\":\"synthetic-model\",\"serviceTier\":null,\"effort\":\"medium\",\"multiAgentMode\":\"explicitRequestOnly\",\"summary\":null,\"personality\":null,\"responsesapiClientMetadata\":{\"workspace_kind\":\"project\"},\"outputSchema\":null,\"collaborationMode\":null}";
            var result = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(wire);
            result["cwd"] = Workspace; result["runtimeWorkspaceRoots"] = new[] { Workspace };
            if (!named)
            { result["permissions"] = null; result["sandboxPolicy"] = new Dictionary<string, object> { { "type", "workspaceWrite" }, { "writableRoots", new[] { Workspace } }, { "networkAccess", false } }; }
            result["additionalContext"] = new Dictionary<string, object> {
                { "codex_apps_client_time_context", new Dictionary<string, object> { { "kind", "application" }, { "value", "<timezone>Etc/UTC</timezone>\n<current_date>2026-10-26</current_date>\nUse this client time context for user-facing dates, times, and schedules instead of the execution host's timezone and current date." } } },
                { "codex_git_state", new Dictionary<string, object> { { "kind", "untrusted" }, { "value", GitContext("synthetic-operation", false) } } } };
            return result;
        }
        private static string GitContext(string operation, bool unsafeField)
        {
            var git = new Dictionary<string, object> { { "schemaVersion", 1 }, { "phase", "before_turn_request" }, { "hostId", "local" },
                { "sessionId", "synthetic-session" }, { "threadId", "synthetic-thread" }, { "clientUserMessageId", operation }, { "appVersion", "0.0.0" },
                { "appServerVersion", "0.0.0" }, { "status", "complete" }, { "capturedAt", "2026-10-26T00:00:00.000Z" }, { "worktreeId", "synthetic-worktree" },
                { "headCommitSha", new string('a', 40) }, { "branch", "synthetic-branch" }, { "detached", false }, { "upstream", null }, { "remotes", new object[0] }, { "remotesOmitted", 0 } };
            if (unsafeField) git["developerInstructions"] = "unexpected";
            string json = new JavaScriptSerializer().Serialize(git).Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#39;");
            return "<environment_context>\n  <git source=\"app\" trust=\"untrusted\">" + json + "</git>\n</environment_context>";
        }
        private static Dictionary<string, object> ColdResume()
        {
            // Actual pinned STe builder fragment, extracted and executed rather
            // than replaced by the previous minimal threadId-only fake request.
            // Builder SHA256: 0de63a643ec0f3b068d9153226cfdf8ba8638bd8b05b6270af6545c20dca6925.
            const string wire = "{\"threadId\":\"synthetic-thread\",\"history\":null,\"path\":null,\"model\":null,\"modelProvider\":\"synthetic-provider\",\"cwd\":null,\"personality\":\"friendly\",\"excludeTurns\":true,\"initialTurnsPage\":{\"limit\":5,\"itemsView\":\"full\",\"sortDirection\":\"desc\"},\"config\":{\"features.request_permissions_tool\":true}}";
            var request = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(wire);
            request["path"] = Path.Combine(Workspace, "sessions", "synthetic-rollout.jsonl"); request["cwd"] = Workspace; return request;
        }

        public static void Register(Action<string, Action> test)
        {
            test("Continuation rollout binds only equivalent ordinary and verbatim local drive files", delegate
            {
                string ordinary = Path.Combine(Path.GetTempPath(), "continuation-path-fixture", "rollout.jsonl");
                string prefix = new string('\\', 2) + "?\\", canonical = ContinuationAdmission.CanonicalRolloutPath(ordinary);
                Check(ContinuationAdmission.CanonicalRolloutPath(prefix + ordinary) == canonical &&
                    ContinuationAdmission.CanonicalRolloutPath(ordinary.Replace('\\', '/')) == canonical,
                    "Equivalent local-drive rollout spellings did not share the original binding.");
                string longPath = Path.GetPathRoot(ordinary) + String.Join("\\", Enumerable.Repeat(new string('a', 60), 6)) + "\\rollout.jsonl";
                Check(longPath.Length > 260 && ContinuationAdmission.CanonicalRolloutPath(prefix + longPath) == longPath,
                    "Canonicalizing an already bounded absolute rollout used legacy MAX_PATH parsing.");
                string drive = ordinary.Substring(0, 2), separator = "\\";
                foreach (string invalid in new[] { drive + "relative.jsonl", separator + "root-relative.jsonl", new string('\\', 2) + String.Join(separator, new[] { "server", "share", "rollout.jsonl" }),
                    prefix + String.Join(separator, new[] { "UNC", "server", "share", "rollout.jsonl" }), new string('\\', 2) + "." + separator + drive + separator + "rollout.jsonl", separator + "??" + separator + drive + separator + "rollout.jsonl",
                    prefix + drive + "relative.jsonl", prefix + drive + "/fixture/rollout.jsonl", prefix + drive + "?fixture" + separator + "rollout.jsonl",
                    drive + separator + String.Join(separator, new[] { "fixture", ".", "rollout.jsonl" }), drive + separator + String.Join(separator, new[] { "fixture", "..", "rollout.jsonl" }), drive + separator + "fixture" + separator + separator + "rollout.jsonl", drive + separator + "fixture" + separator,
                    drive + separator + "fixture." + separator + "rollout.jsonl", drive + separator + "fixture " + separator + "rollout.jsonl", drive + separator + "fixture" + separator + "rollout.jsonl:stream",
                    drive + separator + "CON" + separator + "rollout.jsonl", drive + separator + "fixture" + separator + "NUL.jsonl", drive + separator + "COM1.txt" + separator + "rollout.jsonl", drive + separator + "LPT9" + separator + "rollout.jsonl",
                    drive + separator + "CONIN$" + separator + "rollout.jsonl", drive + separator + "COM\u00b9" + separator + "rollout.jsonl", drive + separator + "fixture" + separator + "rollout?.jsonl" })
                {
                    bool rejected = false; try { ContinuationAdmission.CanonicalRolloutPath(invalid); } catch (IOException) { rejected = true; }
                    Check(rejected, "An ambiguous, device, remote or relative rollout spelling was accepted.");
                }
                var request = ColdResume(); request["path"] = prefix + ordinary;
                var guard = new ContinuationAdmission("synthetic-thread", "synthetic-operation", "synthetic-model", "medium", Policy,
                    Workspace, "synthetic-provider", new string[0], ordinary);
                Check(guard.ShouldAllow("thread/resume", request), "The actual frontend could not cold-resume its captured verbatim local rollout.");
                request["path"] = prefix + Path.Combine(Path.GetDirectoryName(ordinary), "different-rollout.jsonl");
                Check(!guard.ShouldAllow("thread/resume", request), "An equivalent namespace admitted a different physical rollout.");
            });
            test("Actual native frontend 39-tool five-boolean and effort-override shape preserves every setting through one local continuation", delegate
            {
                foreach (bool featureValue in new[] { false, true })
                {
                    var source = ObservedFrontendResume(featureValue);
                    string summary = ContinuationAdmission.MergeFrontendEnvironment(ContinuationAdmission.ProjectFrontendContext(source, ManagedCommand), LocalEnvironment(), Workspace);
                    Check(!summary.Contains("model_reasoning_effort"), "A duplicated model setting was persisted as an App capability.");
                    var fullRaw = ObservedFrontendResume(featureValue); ((Dictionary<string, object>)fullRaw["config"])["features.request_permissions_tool"] = true;
                    Check(ContinuationAdmission.ProjectFrontendContext(fullRaw, ManagedCommand) == ContinuationAdmission.FrontendBaseContextJson(summary),
                        "The exact ten-key pinned raw configuration was rejected or changed capability semantics.");
                    foreach (string change in AppFeatureKeys.Concat(new[] { "wrong-type", "unknown-feature", "missing-feature", "additional-tool", "instructions", "effort-change", "effort-type", "effort-null", "effort-unknown" }))
                    {
                        var guard = new ContinuationAdmission("synthetic-thread", "synthetic-operation", "synthetic-model", "low", Policy, Workspace,
                            "synthetic-provider", new[] { Workspace }, (string)source["path"], summary, ManagedCommand);
                        var candidate = ObservedFrontendResume(featureValue); var config = (Dictionary<string, object>)candidate["config"];
                        if (AppFeatureKeys.Contains(change)) config[change] = !featureValue;
                        if (change == "wrong-type") config[AppFeatureKeys[0]] = featureValue ? "true" : "false";
                        if (change == "unknown-feature") config["features.unknown_capability"] = true;
                        if (change == "missing-feature") config.Remove(AppFeatureKeys[0]);
                        if (change == "additional-tool") config["plugins.codex-app-tools@openai-bundled.mcp_servers.codex_app.enabled_tools"] =
                            ((string[])config["plugins.codex-app-tools@openai-bundled.mcp_servers.codex_app.enabled_tools"]).Concat(new[] { "additional_tool" }).ToArray();
                        if (change == "instructions") candidate["developerInstructions"] = new string('h', 6581);
                        if (change == "effort-change") config["model_reasoning_effort"] = "high";
                        if (change == "effort-type") config["model_reasoning_effort"] = true;
                        if (change == "effort-null") config["model_reasoning_effort"] = null;
                        if (change == "effort-unknown") config["model_reasoning_effort"] = "unrecognized";
                        Check(!guard.ShouldAllow("thread/resume", candidate) && !guard.WasClaimed && guard.ShouldAllow("thread/resume", ObservedFrontendResume(featureValue)),
                            "The real frontend shape changed " + change + " or consumed the reserved send.");
                        var inherited = ObservedFrontendResume(featureValue); ((Dictionary<string, object>)inherited["config"]).Remove("model_reasoning_effort");
                        Check(guard.ShouldAllow("thread/resume", inherited) && !guard.WasClaimed, "Cold hydration could not inherit the separately verified captured effort.");
                        var turn = Request("synthetic-operation"); turn["effort"] = "low"; turn["environments"] = LocalEnvironment(); turn["runtimeWorkspaceRoots"] = new[] { Workspace };
                        Check(guard.ShouldAllow("turn/start", turn) && !guard.ShouldAllow("turn/start", turn), "The exact observed capability context rejected or replayed its continuation.");
                    }
                    foreach (object invalid in new object[] { null, 0, "true", new Dictionary<string, object>(), new object[0] })
                    {
                        var request = ObservedFrontendResume(featureValue); ((Dictionary<string, object>)request["config"])[AppFeatureKeys[0]] = invalid;
                        bool rejected = false; try { ContinuationAdmission.ProjectFrontendContext(request, ManagedCommand); } catch { rejected = true; }
                        Check(rejected, "A non-boolean pinned feature value was accepted into the source baseline.");
                    }
                    foreach (object invalid in new object[] { null, true, 0, "unrecognized", new Dictionary<string, object>(), new object[0] })
                    {
                        var request = ObservedFrontendResume(featureValue); ((Dictionary<string, object>)request["config"])["model_reasoning_effort"] = invalid;
                        bool rejected = false; try { ContinuationAdmission.ProjectFrontendContext(request, ManagedCommand); } catch { rejected = true; }
                        Check(rejected, "An unbounded or ambiguous effort override was accepted into the source baseline.");
                    }
                    var projectedInjection = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(ContinuationAdmission.EmptyFrontendContextJson);
                    ((Dictionary<string, object>)projectedInjection["config"])["model_reasoning_effort"] = "low";
                    bool injected = false; try { ContinuationAdmission.ValidateFrontendContextJson(new JavaScriptSerializer().Serialize(projectedInjection)); } catch { injected = true; }
                    Check(injected, "A model effort override was injected into the durable capability schema.");
                }
            });
            test("Pinned legacy auth hydration permits token reads without refresh state changes or unknown parameters", delegate
            {
                var guard = Guard("synthetic-operation");
                foreach (var request in new[] { new Dictionary<string, object>(), new Dictionary<string, object> { { "includeToken", false }, { "refreshToken", false } },
                    new Dictionary<string, object> { { "includeToken", true }, { "refreshToken", false } }, new Dictionary<string, object> { { "includeToken", true } } })
                    Check(guard.ShouldAllow("getAuthStatus", request) && !guard.WasClaimed, "The pinned App's readonly auth hydration was blocked.");
                foreach (var request in new[] { new Dictionary<string, object> { { "refreshToken", true } }, new Dictionary<string, object> { { "includeToken", "true" } },
                    new Dictionary<string, object> { { "includeToken", null } }, new Dictionary<string, object> { { "refreshToken", 0 } },
                    new Dictionary<string, object> { { "includeToken", true }, { "accountId", "different-account" } } })
                    Check(!guard.ShouldAllow("getAuthStatus", request) && !guard.WasClaimed, "Auth refresh or an unsupported legacy auth override was admitted.");
                Check(!guard.ShouldAllow("getAuthStatus/write", new Dictionary<string, object>()), "A legacy auth mutation inherited readonly permission.");
            });
            test("Frontend capabilities and instruction digest retain semantics without raw instructions commands or credentials", delegate
            {
                var request = FrontendResume();
                string summary = ContinuationAdmission.ProjectFrontendContext(request, ManagedCommand);
                Check(!summary.Contains("Synthetic application guidance") && !summary.Contains("synthetic-managed-app.exe") && summary.Contains("verifiedManagedApp") && summary.Contains("sha256"),
                    "The frontend summary retained raw instructions or command paths.");
                Check(summary == ContinuationAdmission.ValidateFrontendContextJson(summary), "A frontend projection did not satisfy its strict durable schema.");
                string withEnvironment = ContinuationAdmission.MergeFrontendEnvironment(summary, LocalEnvironment(), Workspace);
                Check(ContinuationAdmission.FrontendBaseContextJson(withEnvironment) == summary && withEnvironment.Contains("local"), "Local environment binding changed the source capability baseline.");
                ((Dictionary<string, object>)request["config"])["features.request_permissions_tool"] = true;
                Check(ContinuationAdmission.ProjectFrontendContext(request, ManagedCommand) == summary, "A safe permission-request hydration default changed effective capability semantics.");
            });
            test("Restored frontend must preserve every tool flag and instruction digest and send the exact captured local environment", delegate
            {
                string summary = ContinuationAdmission.MergeFrontendEnvironment(ContinuationAdmission.ProjectFrontendContext(FrontendResume(), ManagedCommand), LocalEnvironment(), Workspace);
                foreach (string change in new[] { "instruction", "enabled-tool", "pages-flag", "active-cua", "command", "unknown-config", "remote-environment" })
                {
                    var guard = new ContinuationAdmission("synthetic-thread", "synthetic-operation", "synthetic-model", "medium", Policy, Workspace, "synthetic-provider", new[] { Workspace }, null, summary, ManagedCommand);
                    var request = FrontendResume(); var config = (Dictionary<string, object>)request["config"];
                    if (change == "instruction") request["developerInstructions"] = "A different application instruction.";
                    if (change == "enabled-tool") config["plugins.codex-app-tools@openai-bundled.mcp_servers.codex_app.enabled_tools"] = new[] { "read_thread", "list_threads", "send_message_to_thread", "additional_tool" };
                    if (change == "pages-flag") ((Dictionary<string, object>)((Dictionary<string, object>)config["apps.connector_openai_pages.tools"])["chatgpt_space.inspect_artifact"])["enabled"] = true;
                    if (change == "active-cua") ((Dictionary<string, object>)config["mcp_servers.cua_repl"])["enabled"] = true;
                    if (change == "command") ((Dictionary<string, object>)config["mcp_servers.cua_repl"])["command"] = "another-command.exe";
                    if (change == "unknown-config") config["mcp_servers.remote"] = new Dictionary<string, object> { { "url", "https://example.invalid/mcp" } };
                    if (change == "remote-environment") request["environments"] = new object[] { new Dictionary<string, object> { { "environmentId", "remote" }, { "cwd", Workspace } } };
                    Check(!guard.ShouldAllow("thread/resume", request) && !guard.WasClaimed && guard.ShouldAllow("thread/resume", FrontendResume()), "A changed " + change + " frontend was admitted or consumed the send permit.");
                    var valid = Request("synthetic-operation"); valid["environments"] = LocalEnvironment();
                    Check(guard.ShouldAllow("turn/start", valid) && !guard.ShouldAllow("turn/start", valid), "The captured local continuation was rejected or replayed.");
                }
                foreach (object environments in new object[] { null, new object[0],
                    new object[] { new Dictionary<string, object> { { "environmentId", "local" }, { "cwd", Workspace + "-other" }, { "runtimeWorkspaceRoots", new[] { Workspace } } } },
                    new object[] { new Dictionary<string, object> { { "environmentId", "local" }, { "cwd", Workspace } } },
                    new object[] { new Dictionary<string, object> { { "environmentId", "local" }, { "cwd", Workspace }, { "runtimeWorkspaceRoots", null } } } })
                {
                    var guard = new ContinuationAdmission("synthetic-thread", "synthetic-operation", "synthetic-model", "medium", Policy, Workspace, "synthetic-provider", new[] { Workspace }, null, summary, ManagedCommand);
                    var request = Request("synthetic-operation"); request["environments"] = environments;
                    Check(!guard.ShouldAllow("turn/start", request) && !guard.WasClaimed, "A missing or changed captured local environment was silently normalized away.");
                }
            });
            test("Frontend projection refuses unknown configuration active MCP credentials and unbound environment fields", delegate
            {
                foreach (string change in new[] { "active", "args", "env", "url", "unknown", "approval", "duplicate-tools", "instruction-type" })
                {
                    var request = FrontendResume(); var config = (Dictionary<string, object>)request["config"];
                    var cua = (Dictionary<string, object>)config["mcp_servers.cua_repl"];
                    if (change == "active") cua["enabled"] = true;
                    if (change == "args") cua["args"] = new[] { "unexpected" };
                    if (change == "env") cua["env"] = new Dictionary<string, object> { { "UNEXPECTED", "value" } };
                    if (change == "url") cua["url"] = "http://127.0.0.1:12345/mcp";
                    if (change == "unknown") config["instructions"] = "unexpected";
                    if (change == "approval") ((Dictionary<string, object>)((Dictionary<string, object>)config["apps.connector_openai_pages.tools"])["chatgpt_space.inspect_artifact"])["approval"] = "never";
                    if (change == "duplicate-tools") config["plugins.codex-app-tools@openai-bundled.mcp_servers.codex_app.enabled_tools"] = new[] { "read_thread", "read_thread" };
                    if (change == "instruction-type") request["developerInstructions"] = new Dictionary<string, object>();
                    bool rejected = false; try { ContinuationAdmission.ProjectFrontendContext(request, ManagedCommand); } catch { rejected = true; }
                    Check(rejected, "Unsupported frontend " + change + " configuration was projected as safe.");
                }
                foreach (string change in new[] { "command", "missing-roots", "null-roots" })
                {
                    var environment = LocalEnvironment(); var local = (Dictionary<string, object>)environment[0];
                    if (change == "command") local["command"] = "unexpected";
                    if (change == "missing-roots") local.Remove("runtimeWorkspaceRoots");
                    if (change == "null-roots") local["runtimeWorkspaceRoots"] = null;
                    bool invalid = false; try { ContinuationAdmission.MergeFrontendEnvironment(ContinuationAdmission.EmptyFrontendContextJson, environment, Workspace); } catch { invalid = true; }
                    Check(invalid, "Unknown or ambiguous " + change + " environment output was accepted.");
                }
            });
            test("Actual pinned cold-resume history path personality and page metadata hydrate only the captured rollout", delegate
            {
                foreach (string changed in new[] { "path", "history", "config", "config-extra", "config-false", "config-object", "page-type", "page-override", "personality" })
                {
                    var request = ColdResume(); string rollout = (string)request["path"];
                    var guard = new ContinuationAdmission("synthetic-thread", "synthetic-operation", "synthetic-model", "medium", Policy,
                        Workspace, "synthetic-provider", new string[0], rollout);
                    if (changed == "path") request["path"] = Path.Combine(Workspace, "another-rollout.jsonl");
                    if (changed == "history") request["history"] = new object[0];
                    if (changed == "config") request["config"] = new Dictionary<string, object> { { "sandbox_mode", "danger-full-access" } };
                    if (changed == "config-extra") ((Dictionary<string, object>)request["config"])["developer_instructions"] = "unexpected";
                    if (changed == "config-false") ((Dictionary<string, object>)request["config"])["features.request_permissions_tool"] = false;
                    if (changed == "config-object") ((Dictionary<string, object>)request["config"])["features.request_permissions_tool"] = new Dictionary<string, object>();
                    if (changed == "page-type") request["initialTurnsPage"] = 5;
                    if (changed == "page-override") ((Dictionary<string, object>)request["initialTurnsPage"])["history"] = new object[0];
                    if (changed == "personality") request["personality"] = "unexpected instructions";
                    Check(!guard.ShouldAllow("thread/resume", request) && !guard.WasClaimed && guard.ShouldAllow("thread/resume", ColdResume()),
                        "Cold resume " + changed + " passed or blocked the captured request.");
                }
            });
            test("Read-only continuation accepts only its captured runtime and visualization roots without changing sandbox policy", delegate
            {
                string visualization = Path.Combine(Path.GetTempPath(), "continuation-synthetic-visualizations", "synthetic-thread");
                var hydration = new ContinuationAdmission("synthetic-thread", "synthetic-operation", "synthetic-model", "medium", Policy,
                    Path.GetTempPath(), "synthetic-provider", new[] { visualization });
                Check(hydration.ShouldAllow("thread/resume", new Dictionary<string, object> { { "threadId", "synthetic-thread" }, { "runtimeWorkspaceRoots", new[] { visualization } } }) &&
                    !hydration.ShouldAllow("thread/resume", new Dictionary<string, object> { { "threadId", "synthetic-thread" }, { "runtimeWorkspaceRoots", new[] { visualization + "-another" } } }),
                    "Hydration changed the captured runtime root set.");
                foreach (string changed in new[] { "new-root", "new-sandbox", "new-profile", "relative-root" })
                {
                    var guard = new ContinuationAdmission("synthetic-thread", "synthetic-operation", "synthetic-model", "medium", Policy,
                        Path.GetTempPath(), "synthetic-provider", new[] { visualization });
                    var request = Request("synthetic-operation");
                    request["runtimeWorkspaceRoots"] = new[] { Path.GetTempPath(), visualization };
                    if (changed == "new-root") request["runtimeWorkspaceRoots"] = new[] { visualization + "-another" };
                    if (changed == "relative-root") request["runtimeWorkspaceRoots"] = new[] { "relative-root" };
                    if (changed == "new-sandbox") { request["permissions"] = null; request["sandboxPolicy"] = new Dictionary<string, object> { { "type", "dangerFullAccess" } }; }
                    if (changed == "new-profile") request["permissions"] = ":workspace";
                    Check(!guard.ShouldAllow("turn/start", request) && !guard.WasClaimed, "A captured read-only " + changed + " override passed admission.");
                    var valid = Request("synthetic-operation"); valid["runtimeWorkspaceRoots"] = new[] { visualization, Path.GetTempPath() };
                    Check(guard.ShouldAllow("turn/start", valid) && !guard.ShouldAllow("turn/start", valid), "The original read-only visualization context was rejected or replayed.");
                }
            });
            test("Pinned desktop normalization preserves captured explicit policy and one continuation through safe time and Git metadata", delegate
            {
                foreach (bool named in new[] { false, true })
                {
                    var guard = new ContinuationAdmission("synthetic-thread", "synthetic-operation", "synthetic-model", "medium", WorkspacePolicy(named), Workspace);
                    var request = NormalizedRequest(named);
                    Check(guard.ShouldAllow("turn/start", request) && guard.WasClaimed && !guard.ShouldAllow("turn/start", request),
                        "The exact pinned normalized desktop wire was rejected or replayed.");
                }
            });
            test("Normalized desktop defaults never permit missing policy extra roots arbitrary context or privilege overrides", delegate
            {
                foreach (string changed in new[] { "null-policy", "missing-profile", "runtime-root", "unknown-metadata", "unknown-context", "git-binding", "git-instructions", "time-context", "collaboration", "tier", "environment", "unknown-field", "multi-agent" })
                {
                    var guard = new ContinuationAdmission("synthetic-thread", "synthetic-operation", "synthetic-model", "medium", WorkspacePolicy(true), Workspace);
                    var request = NormalizedRequest(true);
                    var context = (Dictionary<string, object>)request["additionalContext"];
                    if (changed == "null-policy") request["approvalPolicy"] = null;
                    if (changed == "missing-profile") request["permissions"] = null;
                    if (changed == "runtime-root") request["runtimeWorkspaceRoots"] = new[] { Path.Combine(Workspace, "new-root") };
                    if (changed == "unknown-metadata") ((Dictionary<string, object>)request["responsesapiClientMetadata"])["instructions"] = "unexpected";
                    if (changed == "unknown-context") context["unexpected_context"] = new Dictionary<string, object> { { "kind", "application" }, { "value", "unexpected" } };
                    if (changed == "git-binding") ((Dictionary<string, object>)context["codex_git_state"])["value"] = GitContext("different-operation", false);
                    if (changed == "git-instructions") ((Dictionary<string, object>)context["codex_git_state"])["value"] = GitContext("synthetic-operation", true);
                    if (changed == "time-context") ((Dictionary<string, object>)context["codex_apps_client_time_context"])["value"] = "unexpected instructions";
                    if (changed == "collaboration") request["collaborationMode"] = new Dictionary<string, object> { { "mode", "default" } };
                    if (changed == "tier") request["serviceTier"] = "priority";
                    if (changed == "environment") request["environments"] = new object[] { new Dictionary<string, object> { { "environmentId", "remote" } } };
                    if (changed == "unknown-field") request["developerInstructions"] = "unexpected";
                    if (changed == "multi-agent") request["multiAgentMode"] = "automatic";
                    Check(!guard.ShouldAllow("turn/start", request) && !guard.WasClaimed && guard.ShouldAllow("turn/start", NormalizedRequest(true)),
                        "The normalized " + changed + " override passed or consumed the valid send permit.");
                }
            });
            test("Continuation admission allows read-only initialization and hydration while blocking unrelated new turns", delegate
            {
                var guard = Guard("synthetic-operation");
                foreach (string method in new[] { "initialize", "initialized", "account/read", "thread/read", "thread/loaded/list", "model/list" })
                    Check(guard.ShouldAllow(method, new Dictionary<string, object>()), "Read-only startup or hydration was blocked.");
                Check(guard.ShouldAllow("thread/resume", new Dictionary<string, object> { { "threadId", "synthetic-thread" }, { "excludeTurns", true } }), "Plain target-thread hydration was blocked.");
                Check(!guard.ShouldAllow("turn/start", Request("manual-message")) && !guard.ShouldAllow("turn/steer", Request("manual-message")) && !guard.WasClaimed,
                    "An unrelated frontend turn raced the reserved continuation.");
            });
            test("Actual desktop hydration may list presets installed plugins and credential-free readiness without claiming a send", delegate
            {
                // Bundled CLI 0.164.0-alpha.11.1 generated ClientRequest schema:
                // collaborationMode/list has an empty object; gateway OAuth,
                // remote-control status and Windows readiness have null params.
                // PluginInstalledParams only discovers installed marketplaces.
                var guard = Guard("synthetic-operation");
                foreach (string method in new[] { "collaborationMode/list", "account/gatewayOAuth/read", "remoteControl/status/read", "windowsSandbox/readiness", "plugin/installed" })
                {
                    Check(guard.ShouldAllow(method, null) && guard.ShouldAllow(method, new Dictionary<string, object>()), "A parameterless " + method + " startup read was rejected.");
                    Check(!guard.ShouldAllow(method, new Dictionary<string, object> { { "refreshToken", true } }) &&
                        !guard.ShouldAllow(method, new Dictionary<string, object> { { "command", "unexpected" } }), "A startup read accepted mutation options.");
                }
                Check(guard.ShouldAllow("plugin/installed", new Dictionary<string, object> { { "cwds", new[] { Workspace } },
                    { "installSuggestionPluginNames", new[] { "synthetic-plugin" } } }), "A schema-valid local installed-plugin listing was rejected.");
                Check(guard.ShouldAllow("plugin/installed", new Dictionary<string, object> { { "cwds", null }, { "installSuggestionPluginNames", null } }), "Null optional list fields were rejected.");
                Check(!guard.WasClaimed && guard.ShouldAllow("turn/start", Request("synthetic-operation")) &&
                    !guard.ShouldAllow("turn/start", Request("synthetic-operation")), "Readiness checks consumed or reset the one continuation permit.");
            });
            test("Desktop startup reads reject unknown options malformed discovery and neighboring mutating methods", delegate
            {
                var guard = Guard("synthetic-operation");
                foreach (string method in new[] { "account/gatewayOAuth/login", "account/gatewayOAuth/cancel", "account/login/start", "account/logout",
                    "remoteControl/enable", "remoteControl/disable", "windowsSandbox/setupStart", "plugin/install", "plugin/uninstall" })
                    Check(!guard.ShouldAllow(method, new Dictionary<string, object>()), "A neighboring " + method + " mutation was admitted.");
                var invalid = new[] {
                    new Dictionary<string, object> { { "cwds", "not-an-array" } },
                    new Dictionary<string, object> { { "cwds", new object[] { 1 } } },
                    new Dictionary<string, object> { { "cwds", new[] { "relative-root" } } },
                    new Dictionary<string, object> { { "cwds", new[] { "https://unexpected.invalid/" } } },
                    new Dictionary<string, object> { { "cwds", new[] { new string('\\', 2) + String.Join(Path.DirectorySeparatorChar.ToString(), new[] { "unexpected", "share" }) } } },
                    new Dictionary<string, object> { { "cwds", Enumerable.Range(0, 65).Select(i => Workspace + i).ToArray() } },
                    new Dictionary<string, object> { { "installSuggestionPluginNames", true } },
                    new Dictionary<string, object> { { "installSuggestionPluginNames", new object[] { null } } },
                    new Dictionary<string, object> { { "installSuggestionPluginNames", new[] { "unexpected\nname" } } },
                    new Dictionary<string, object> { { "installSuggestionPluginNames", Enumerable.Repeat("synthetic", 257).ToArray() } },
                    new Dictionary<string, object> { { "installSuggestionPluginNames", new[] { new string('x', 257) } } },
                    new Dictionary<string, object> { { "config", new Dictionary<string, object> { { "enabled", true } } } }
                };
                foreach (var request in invalid) Check(!guard.ShouldAllow("plugin/installed", request), "Malformed installed-plugin discovery passed the barrier.");
                Check(!guard.WasClaimed && guard.ShouldAllow("turn/start", Request("synthetic-operation")), "Rejected startup mutations consumed the valid send.");
            });
            test("Concurrent identical continuation requests have exactly one backend admission", delegate
            {
                var guard = Guard("synthetic-operation"); int admitted = 0;
                using (var start = new ManualResetEvent(false))
                {
                    var requests = Enumerable.Range(0, 8).Select(index => Task.Run(delegate
                    { start.WaitOne(); if (guard.ShouldAllow("turn/start", Request("synthetic-operation"))) Interlocked.Increment(ref admitted); })).ToArray();
                    start.Set(); Check(Task.WaitAll(requests, 10000), "Concurrent admission did not finish.");
                }
                Check(admitted == 1 && guard.WasClaimed, "A continuation was admitted more than once.");
            });
            test("Invalid continuation thread text permissions and model do not consume the correct send permit", delegate
            {
                foreach (string changed in new[] { "thread", "text", "permissions", "approval", "model", "cwd", "toolOutput", "dual-policy" })
                {
                    var guard = Guard("synthetic-operation"); var request = Request("synthetic-operation");
                    if (changed == "thread") request["threadId"] = "different-thread";
                    if (changed == "text") request["input"] = new object[] { new Dictionary<string, object> { { "type", "text" }, { "text", "different text" } } };
                    if (changed == "permissions") request["permissions"] = ":full-access";
                    if (changed == "approval") request["approvalPolicy"] = "never";
                    if (changed == "model") request["model"] = "different-model";
                    if (changed == "cwd") request["cwd"] = Path.Combine(Path.GetTempPath(), "different-work");
                    if (changed == "toolOutput") request["toolOutput"] = new { name = "unexpected" };
                    if (changed == "dual-policy") request["sandboxPolicy"] = new Dictionary<string, object> { { "type", "readOnly" }, { "networkAccess", false } };
                    Check(!guard.ShouldAllow("turn/start", request) && !guard.WasClaimed, "An invalid " + changed + " request consumed or passed admission.");
                    Check(guard.ShouldAllow("turn/start", Request("synthetic-operation")), "A rejected request consumed the valid continuation permit.");
                }
            });
            test("Released or cancelled continuation admission allows manual work but permanently rejects the reserved operation", delegate
            {
                foreach (bool claimed in new[] { false, true })
                {
                    var guard = Guard("synthetic-operation");
                    if (claimed) Check(guard.ShouldAllow("turn/start", Request("synthetic-operation")), "Original admission failed.");
                    guard.Cancel("synthetic-operation");
                    Check(!guard.IsArmed && !guard.ShouldAllow("turn/start", Request("synthetic-operation")) && guard.ShouldAllow("turn/start", Request("manual-after-release")),
                        "Releasing a guard enabled a replay or blocked ordinary manual work.");
                }
            });
            test("Continuation acknowledgement must match the reserved admitted operation and remain stable", delegate
            {
                var guard = Guard("synthetic-operation"); bool rejected = false;
                try { guard.RecordAcceptedTurn("synthetic-operation", "new-turn"); } catch (IOException) { rejected = true; }
                Check(rejected && guard.ObservedTurnId == null, "An unadmitted turn became the receipt.");
                guard.ShouldAllow("turn/start", Request("synthetic-operation")); guard.RecordAcceptedTurn("synthetic-operation", "new-turn");
                guard.RecordAcceptedTurn("synthetic-operation", "new-turn"); rejected = false;
                try { guard.RecordAcceptedTurn("synthetic-operation", "different-turn"); } catch (IOException) { rejected = true; }
                Check(rejected && guard.ObservedTurnId == "new-turn", "A different turn replaced the continuation receipt.");
            });
            test("Armed continuation blocks auth goal history and configuration mutations including unknown methods", delegate
            {
                var guard = Guard("synthetic-operation");
                foreach (string method in new[] { "account/login/start", "account/logout", "thread/goal/set", "thread/goal/clear", "thread/start", "thread/fork",
                    "thread/inject_items", "thread/revert", "thread/rollback", "config/value/write", "config/batchWrite", "command/exec", "process/spawn", "unknown/futureMutation" })
                    Check(!guard.ShouldAllow(method, new Dictionary<string, object>()), "An armed " + method + " mutation was permitted.");
                Check(guard.ShouldAllow("turn/start", Request("synthetic-operation")), "Rejected mutations consumed the reserved send.");
                guard.Release(); Check(guard.ShouldAllow("account/login/start", new Dictionary<string, object>()), "Manual account operations did not recover after release.");
            });
            test("Armed target hydration accepts captured policy but rejects history instructions and privilege overrides", delegate
            {
                foreach (string changed in new[] { "thread", "model", "policy", "history", "path", "instructions", "configuration", "provider" })
                {
                    var guard = Guard("synthetic-operation"); var request = new Dictionary<string, object> { { "threadId", "synthetic-thread" } };
                    if (changed == "thread") request["threadId"] = "another-thread";
                    if (changed == "model") request["model"] = "different-model";
                    if (changed == "policy") request["sandbox"] = "danger-full-access";
                    if (changed == "history") request["history"] = new object[0];
                    if (changed == "path") request["path"] = Path.Combine(Path.GetTempPath(), "other-rollout");
                    if (changed == "instructions") request["developerInstructions"] = "unexpected instructions";
                    if (changed == "configuration") request["config"] = new Dictionary<string, object> { { "unrecognized_override", true } };
                    if (changed == "provider") request["modelProvider"] = "unknown-provider";
                    Check(!guard.ShouldAllow("thread/resume", request) && !guard.WasClaimed, "A hydration " + changed + " override was permitted.");
                }
                var same = new ContinuationAdmission("synthetic-thread", "synthetic-operation", "synthetic-model", "medium", Policy, Path.GetTempPath(), "synthetic-provider");
                Check(same.ShouldAllow("thread/resume", new Dictionary<string, object> { { "threadId", "synthetic-thread" }, { "model", "synthetic-model" },
                    { "modelProvider", "synthetic-provider" }, { "sandbox", "read-only" }, { "approvalPolicy", "on-request" }, { "approvalsReviewer", "user" } }),
                    "Hydration with explicitly matching captured settings was rejected.");
            });
            test("A frontend Stop retires automatic continuation and returns manual control", delegate
            {
                var guard = Guard("synthetic-operation");
                Check(guard.ShouldAllow("turn/interrupt", new Dictionary<string, object> { { "threadId", "synthetic-thread" }, { "turnId", "original-turn" } }) &&
                    !guard.IsArmed && !guard.ShouldAllow("turn/start", Request("synthetic-operation")) && guard.ShouldAllow("turn/start", Request("manual-next")),
                    "A user Stop left the automatic send armed or blocked future manual work.");
            });
        }
    }
}
