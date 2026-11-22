using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace CodexAccountSwitcher
{
    // A per-restored-backend admission barrier. The persistent ticket owns crash
    // protection; this barrier prevents a concurrent frontend turn from racing the
    // one reserved continuation within this exact backend generation.
    internal sealed class ContinuationAdmission
    {
        private readonly object gate = new object();
        private readonly string threadId, operationId, model, effort, cwd, modelProvider, rolloutPath;
        private readonly Dictionary<string, object> policy;
        private readonly HashSet<string> capturedRuntimeRoots;
        private readonly string frontendContextJson;
        private readonly Func<string, bool> verifyAppCommand;
        internal const string EmptyFrontendContextJson = "{\"config\":{},\"developerInstructions\":null,\"environments\":[],\"version\":1}";
        private bool armed = true, claimed;
        private string observedTurnId;

        internal ContinuationAdmission(string threadId, string operationId, string model, string effort, string permissionsJson)
            : this(threadId, operationId, model, effort, permissionsJson, null) { }

        internal ContinuationAdmission(string threadId, string operationId, string model, string effort, string permissionsJson, string cwd)
            : this(threadId, operationId, model, effort, permissionsJson, cwd, null) { }

        internal ContinuationAdmission(string threadId, string operationId, string model, string effort, string permissionsJson, string cwd, string modelProvider)
            : this(threadId, operationId, model, effort, permissionsJson, cwd, modelProvider, new string[0]) { }

        internal ContinuationAdmission(string threadId, string operationId, string model, string effort, string permissionsJson, string cwd, string modelProvider, string[] runtimeWorkspaceRoots)
            : this(threadId, operationId, model, effort, permissionsJson, cwd, modelProvider, runtimeWorkspaceRoots, null) { }

        internal ContinuationAdmission(string threadId, string operationId, string model, string effort, string permissionsJson, string cwd, string modelProvider, string[] runtimeWorkspaceRoots, string rolloutPath)
            : this(threadId, operationId, model, effort, permissionsJson, cwd, modelProvider, runtimeWorkspaceRoots, rolloutPath, EmptyFrontendContextJson, null) { }

        internal ContinuationAdmission(string threadId, string operationId, string model, string effort, string permissionsJson, string cwd, string modelProvider, string[] runtimeWorkspaceRoots, string rolloutPath,
            string frontendContextJson, Func<string, bool> verifyAppCommand)
        {
            RequireText(threadId); RequireText(operationId); RequireText(model);
            if (permissionsJson == null || permissionsJson.Length > 16384) throw new IOException("Continuation permissions are unavailable.");
            this.policy = new JavaScriptSerializer { MaxJsonLength = 16384 }.Deserialize<Dictionary<string, object>>(permissionsJson);
            if (policy == null || !policy.ContainsKey("approvalPolicy") || policy["approvalPolicy"] == null ||
                !(Value(policy, "sandbox") is Dictionary<string, object>) ||
                policy.Keys.Any(key => !new[] { "approvalPolicy", "approvalsReviewer", "sandbox", "permissions" }.Contains(key)))
                throw new IOException("Continuation permission fields are invalid.");
            this.threadId = threadId; this.operationId = operationId; this.model = model; this.effort = effort;
            this.modelProvider = modelProvider;
            if (cwd != null) this.cwd = CanonicalRoot(cwd);
            this.frontendContextJson = ValidateFrontendContextJson(frontendContextJson);
            this.verifyAppCommand = verifyAppCommand;
            var frontend = ParseFrontendContext(this.frontendContextJson);
            if (Canonical(Value(frontend, "environments")) != Canonical(NormalizeEnvironments(Value(frontend, "environments"), this.cwd)))
                throw new IOException("Continuation environment binding changed.");
            if (rolloutPath != null) this.rolloutPath = CanonicalRolloutPath(rolloutPath);
            if (runtimeWorkspaceRoots == null || runtimeWorkspaceRoots.Length > 64) throw new IOException("Continuation runtime roots are invalid.");
            capturedRuntimeRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in runtimeWorkspaceRoots)
                if (!capturedRuntimeRoots.Add(CanonicalRoot(root))) throw new IOException("Continuation runtime roots contain duplicates.");
        }

        internal string ReservedOperationId { get { return operationId; } }
        internal string ObservedTurnId { get { lock (gate) return observedTurnId; } }
        internal bool IsArmed { get { lock (gate) return armed; } }
        internal bool WasClaimed { get { lock (gate) return claimed; } }

        internal bool ShouldAllow(string method, Dictionary<string, object> parameters)
        { string rejection; return TryAdmit(method, parameters, out rejection); }

        internal bool TryAdmit(string method, Dictionary<string, object> parameters, out string rejection)
        {
            lock (gate)
            {
                rejection = null;
                string messageId = StringValue(parameters, "clientUserMessageId");
                if (messageId == operationId)
                {
                    // This operation identifier remains reserved even after release,
                    // cancellation, acknowledgement or an uncertain frontend write.
                    if (!armed || claimed || method != "turn/start") return Reject(out rejection);
                    if (!ValidStart(parameters)) return Reject(out rejection);
                    claimed = true; // Consume before the bridge writes to its backend.
                    return true;
                }
                if (!armed) return true;
                if (method == "turn/start" || method == "turn/steer") return Reject(out rejection);
                if (method == "getAuthStatus")
                {
                    // The pinned owned App needs the legacy token read while
                    // hydrating. Never refresh or rotate account state here.
                    return parameters == null || parameters.Keys.All(key => (key == "includeToken" && parameters[key] is bool) ||
                        (key == "refreshToken" && Object.Equals(parameters[key], false))) || Reject(out rejection);
                }
                if (method == "collaborationMode/list" || method == "account/gatewayOAuth/read" || method == "remoteControl/status/read" ||
                    method == "windowsSandbox/readiness" || method == "plugin/installed")
                    return IsReadOnlyDesktopStartupRequest(method, parameters) || Reject(out rejection);
                if (method == "turn/interrupt")
                {
                    // A fresh frontend Stop is user intervention. Release manual
                    // control while keeping this automatic message ID retired.
                    armed = false; return true;
                }
                if (method == "thread/resume") return ValidResume(parameters) || Reject(out rejection);
                return IsReadOnlyRequest(method) || Reject(out rejection);
            }
        }

        internal void Release() { lock (gate) armed = false; }
        internal void Cancel(string expectedOperationId)
        {
            if (expectedOperationId != operationId) throw new IOException("Continuation operation changed.");
            Release();
        }
        internal void RecordAcceptedTurn(string messageId, string turnId)
        {
            RequireText(turnId);
            lock (gate)
            {
                if (!claimed || messageId != operationId || observedTurnId != null && observedTurnId != turnId)
                    throw new IOException("Continuation acknowledgement did not match its reserved operation.");
                observedTurnId = turnId;
            }
        }

        // Observe only a successful native frontend request. The bridge stores
        // this projection, never its raw instructions, endpoints or credentials.
        internal static string ProjectFrontendContext(Dictionary<string, object> parameters, Func<string, bool> verifyAppCommand)
        {
            if (parameters == null) throw new IOException("Frontend continuation context is unavailable.");
            object instructions = Value(parameters, "developerInstructions"), digest = null;
            if (instructions != null)
            {
                var text = instructions as string;
                if (text == null || text.Length > 65536) throw new IOException("Frontend continuation instructions are invalid.");
                byte[] bytes = new UTF8Encoding(false, true).GetBytes(text);
                if (bytes.Length > 65536) throw new IOException("Frontend continuation instructions exceeded their bound.");
                using (var sha = SHA256.Create())
                    digest = new Dictionary<string, object> { { "sha256", BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "") }, { "utf8Bytes", bytes.Length } };
            }
            return ValidateFrontendContextJson(Canonical(new Dictionary<string, object> { { "version", 1 }, { "config", NormalizeFrontendConfig(Value(parameters, "config"), false, verifyAppCommand) },
                { "developerInstructions", digest }, { "environments", new object[0] } }));
        }

        internal static string MergeFrontendEnvironment(string summary, object environments, string cwd)
        {
            var context = ParseFrontendContext(ValidateFrontendContextJson(summary));
            context["environments"] = NormalizeEnvironments(environments, cwd);
            return ValidateFrontendContextJson(Canonical(context));
        }

        internal static string FrontendBaseContextJson(string summary)
        {
            var context = ParseFrontendContext(ValidateFrontendContextJson(summary));
            context["environments"] = new object[0]; return Canonical(context);
        }

        internal static string ValidateFrontendContextJson(string summary)
        {
            var context = ParseFrontendContext(summary);
            if (context.Count != 4 || !context.ContainsKey("version") || !Object.Equals(context["version"], 1) ||
                !context.ContainsKey("config") || !context.ContainsKey("developerInstructions") || !context.ContainsKey("environments"))
                throw new IOException("Frontend continuation summary fields are invalid.");
            if (!(context["config"] is Dictionary<string, object>)) throw new IOException("Frontend continuation capabilities are invalid.");
            context["config"] = NormalizeFrontendConfig(context["config"], true, null);
            object instructions = context["developerInstructions"];
            if (instructions != null)
            {
                var digest = instructions as Dictionary<string, object>;
                if (digest == null || digest.Count != 2 || !digest.ContainsKey("sha256") || !digest.ContainsKey("utf8Bytes") ||
                    !(digest["sha256"] is string) || !Regex.IsMatch((string)digest["sha256"], "^[0-9A-F]{64}$", RegexOptions.CultureInvariant) ||
                    !(digest["utf8Bytes"] is int) || (int)digest["utf8Bytes"] < 0 || (int)digest["utf8Bytes"] > 65536)
                    throw new IOException("Frontend continuation instruction digest is invalid.");
            }
            var environments = context["environments"] as IList;
            if (environments == null) throw new IOException("Frontend continuation environments are invalid.");
            foreach (object item in environments)
            {
                var environment = item as Dictionary<string, object>;
                if (environment == null || environment.Count != 3 || !environment.ContainsKey("environmentId") || !environment.ContainsKey("cwd") ||
                    !environment.ContainsKey("runtimeWorkspaceRoots") || !(environment["runtimeWorkspaceRoots"] is IList))
                    throw new IOException("Frontend continuation environment summary fields are invalid.");
            }
            context["environments"] = NormalizeEnvironments(context["environments"], null);
            return Canonical(context);
        }

        private static Dictionary<string, object> ParseFrontendContext(string summary)
        {
            if (String.IsNullOrEmpty(summary) || Encoding.UTF8.GetByteCount(summary) > 16384) throw new IOException("Frontend continuation summary is unavailable.");
            var context = new JavaScriptSerializer { MaxJsonLength = 16384, RecursionLimit = 12 }.Deserialize<Dictionary<string, object>>(summary);
            if (context == null) throw new IOException("Frontend continuation summary is invalid.");
            return context;
        }

        private static Dictionary<string, object> NormalizeFrontendConfig(object value, bool projected, Func<string, bool> verifyAppCommand)
        {
            var output = new Dictionary<string, object>();
            if (value == null && !projected) return output;
            var config = value as Dictionary<string, object>;
            if (config == null || config.Count > (projected ? 8 : 10)) throw new IOException("Frontend continuation configuration is unsupported.");
            string[] featureKeys = { "features.unified_image_budget", "features.code_mode_tool_description_first", "features.compaction_image_budget",
                "features.guardianv2_decisions_comparison", "features.realtime_conversation" };
            foreach (var entry in config)
            {
                if (entry.Key == "model_reasoning_effort" && !projected)
                {
                    // Native App thread/start expresses thinking through this
                    // config key. Effective effort is captured independently in
                    // CP.Effort and verified against the restored backend; it is
                    // not an App tool capability or instruction override.
                    if (!(entry.Value is string) || !new[] { "none", "minimal", "low", "medium", "high", "xhigh", "max", "ultra" }.Contains((string)entry.Value))
                        throw new IOException("Frontend reasoning effort override is invalid.");
                    continue;
                }
                if (entry.Key == "features.request_permissions_tool" && !projected)
                {
                    // Pinned STe exposes the permission-request tool under the
                    // unchanged captured sandbox/reviewer/approval policy. Its
                    // presence is not a grant or a different tool capability.
                    if (!Object.Equals(entry.Value, true)) throw new IOException("Frontend permission-request configuration is invalid.");
                    continue;
                }
                if (featureKeys.Contains(entry.Key))
                {
                    // These five pinned App defaults were observed in a real
                    // successful frontend request. Preserve every bool exactly;
                    // this is not permission for arbitrary feature/config keys.
                    if (!(entry.Value is bool)) throw new IOException("Frontend continuation feature values are invalid.");
                    output.Add(entry.Key, entry.Value);
                }
                else if (entry.Key == "mcp_servers.cua_repl")
                {
                    var server = entry.Value as Dictionary<string, object>;
                    string commandKey = projected ? "commandKind" : "command";
                    if (server == null || server.Count != 2 || !server.ContainsKey(commandKey) || !server.ContainsKey("enabled") ||
                        !Object.Equals(server["enabled"], false)) throw new IOException("Active frontend MCP servers are unsupported.");
                    string command = server[commandKey] as string;
                    if (projected ? command != "verifiedManagedApp" : command == null || verifyAppCommand == null || !verifyAppCommand(command))
                        throw new IOException("Frontend MCP command is not this managed App.");
                    output.Add(entry.Key, new Dictionary<string, object> { { "commandKind", "verifiedManagedApp" }, { "enabled", false } });
                }
                else if (entry.Key == "apps.connector_openai_pages.tools")
                {
                    var tools = entry.Value as Dictionary<string, object>;
                    string[] names = { "chatgpt_space.create_presentation", "chatgpt_space.create_spreadsheet", "chatgpt_space.create_canvas", "chatgpt_space.execute_artifact_code", "chatgpt_space.inspect_artifact" };
                    if (tools == null || tools.Count != names.Length || names.Any(name => !tools.ContainsKey(name))) throw new IOException("Frontend Pages capabilities are unsupported.");
                    var copied = new Dictionary<string, object>();
                    foreach (string name in names)
                    {
                        var setting = tools[name] as Dictionary<string, object>;
                        if (setting == null || setting.Count != 1 || !setting.ContainsKey("enabled") || !(setting["enabled"] is bool))
                            throw new IOException("Frontend Pages approval or tool configuration changed.");
                        copied.Add(name, new Dictionary<string, object> { { "enabled", setting["enabled"] } });
                    }
                    output.Add(entry.Key, copied);
                }
                else if (entry.Key == "plugins.codex-app-tools@openai-bundled.mcp_servers.codex_app.enabled_tools")
                {
                    var tools = entry.Value as IList;
                    if (tools == null || tools.Count > 128) throw new IOException("Frontend App tool capabilities exceeded their bound.");
                    var names = new SortedSet<string>(StringComparer.Ordinal);
                    foreach (object item in tools)
                    {
                        string name = item as string;
                        if (name == null || !Regex.IsMatch(name, "^[A-Za-z][A-Za-z0-9_.:/-]{0,255}$", RegexOptions.CultureInvariant) || !names.Add(name))
                            throw new IOException("Frontend App tool capabilities are invalid.");
                    }
                    output.Add(entry.Key, names.ToArray());
                }
                else throw new IOException("Unknown frontend continuation configuration was rejected.");
            }
            return output;
        }

        private static object[] NormalizeEnvironments(object value, string cwd)
        {
            if (value == null) return new object[0];
            var environments = value as IList;
            if (environments == null || environments.Count > 1) throw new IOException("Continuation requires one local environment.");
            if (environments.Count == 0) return new object[0];
            var local = environments[0] as Dictionary<string, object>;
            if (local == null || local.Count != 3 || !local.ContainsKey("cwd") || !local.ContainsKey("runtimeWorkspaceRoots") ||
                local.Keys.Any(key => key != "environmentId" && key != "cwd" && key != "runtimeWorkspaceRoots") ||
                StringValue(local, "environmentId") != "local") throw new IOException("Continuation requires the local default environment.");
            string directory = CanonicalRoot(StringValue(local, "cwd"));
            if (cwd != null && !String.Equals(directory, CanonicalRoot(cwd), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Continuation environment directory changed.");
            object requestedRoots = Value(local, "runtimeWorkspaceRoots");
            var roots = requestedRoots as IList;
            // Backend ThreadEnvironment output requires this array. The reserved
            // turn/start deliberately sends it explicitly as well: omitted/null
            // request roots mean default-to-cwd, not the empty set.
            if (roots == null || roots.Count > 64) throw new IOException("Continuation environment roots are invalid.");
            var canonicalRoots = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            if (roots != null) foreach (object root in roots)
                if (!(root is string) || !canonicalRoots.Add(CanonicalRoot((string)root))) throw new IOException("Continuation environment roots are invalid.");
            return new object[] { new Dictionary<string, object> { { "environmentId", "local" }, { "cwd", directory }, { "runtimeWorkspaceRoots", canonicalRoots.ToArray() } } };
        }

        private bool ValidStart(Dictionary<string, object> parameters)
        {
            try
            {
                if (parameters == null || parameters.Keys.Any(key => !new[] { "threadId", "input", "clientUserMessageId", "model", "effort", "cwd",
                    "approvalPolicy", "approvalsReviewer", "sandboxPolicy", "permissions", "disabledPluginIds", "turnTrigger", "parentTurnId", "rootTurnId",
                    "additionalContext", "toolOutput", "environments", "runtimeWorkspaceRoots", "cyberAccessProgram", "serviceTier", "multiAgentMode",
                    "summary", "personality", "responsesapiClientMetadata", "outputSchema", "collaborationMode" }.Contains(key))) return false;
                if (StringValue(parameters, "threadId") != threadId || StringValue(parameters, "model") != model) return false;
                object value;
                if (parameters.TryGetValue("effort", out value) && !Object.Equals(value, effort)) return false;
                if (parameters.TryGetValue("cwd", out value) && value != null && (cwd == null || !(value is string) ||
                    !String.Equals(CanonicalRoot((string)value), cwd, StringComparison.OrdinalIgnoreCase))) return false;
                var input = Value(parameters, "input") as IList;
                if (input == null || input.Count != 1) return false;
                var text = input[0] as Dictionary<string, object>;
                if (text == null || text.Keys.Any(key => key != "type" && key != "text" && key != "text_elements") ||
                    StringValue(text, "type") != "text" || StringValue(text, "text") != "繼續執行") return false;
                if (text.TryGetValue("text_elements", out value))
                { var elements = value as IList; if (elements == null || elements.Count != 0) return false; }
                foreach (string key in new[] { "approvalPolicy", "approvalsReviewer" })
                    if (!parameters.TryGetValue(key, out value) || Canonical(value) != Canonical(Value(policy, key))) return false;
                object permission = Value(parameters, "permissions"), sandbox = Value(parameters, "sandboxPolicy");
                if (permission != null && sandbox != null) return false;
                // A verified resumed thread is not permission to silently use a
                // different global default. The actual desktop manager must send
                // the captured profile or the complete captured sandbox policy.
                var capturedProfile = Value(policy, "permissions") as Dictionary<string, object>;
                if (permission != null)
                {
                    if (!(permission is string) || capturedProfile == null || StringValue(capturedProfile, "id") != (string)permission) return false;
                }
                else if (capturedProfile != null || sandbox == null) return false;
                if (sandbox != null && Canonical(sandbox) != Canonical(Value(policy, "sandbox"))) return false;
                foreach (string key in new[] { "turnTrigger", "parentTurnId", "rootTurnId", "toolOutput", "cyberAccessProgram",
                    "serviceTier", "outputSchema", "collaborationMode" })
                    if (Value(parameters, key) != null) return false;
                value = Value(parameters, "disabledPluginIds");
                if (value != null && (!(value is IList) || ((IList)value).Count != 0)) return false;
                value = Value(parameters, "multiAgentMode");
                if (value != null && !Object.Equals(value, "explicitRequestOnly")) return false;
                value = Value(parameters, "summary");
                if (value != null && !(value is string && new[] { "none", "auto", "concise", "detailed" }.Contains((string)value))) return false;
                value = Value(parameters, "personality");
                if (value != null && !(value is string && new[] { "none", "friendly", "pragmatic" }.Contains((string)value))) return false;
                if (!ValidRuntimeRoots(Value(parameters, "runtimeWorkspaceRoots")) || !ValidClientMetadata(Value(parameters, "responsesapiClientMetadata")) ||
                    !ValidAdditionalContext(Value(parameters, "additionalContext"))) return false;
                var frontend = ParseFrontendContext(frontendContextJson);
                if (Canonical(NormalizeEnvironments(Value(parameters, "environments"), cwd)) != Canonical(Value(frontend, "environments"))) return false;
                return true;
            }
            catch { return false; }
        }
        private bool ValidRuntimeRoots(object value)
        {
            if (value == null) return true;
            var roots = value as IList; if (roots == null || roots.Count > 64) return false;
            var sandbox = Value(policy, "sandbox") as Dictionary<string, object>;
            if (sandbox == null) return false;
            var captured = Value(sandbox, "writableRoots") as IList;
            string kind = StringValue(sandbox, "type");
            foreach (object item in roots)
            {
                string root = item as string;
                string canonical = CanonicalRoot(root);
                // Runtime roots are the authenticated thread's persisted context,
                // not permission to introduce another project or visualization
                // directory. Keep the explicit captured policy unchanged above.
                if (capturedRuntimeRoots.Contains(canonical) || cwd != null && String.Equals(CanonicalRoot(cwd), canonical, StringComparison.OrdinalIgnoreCase)) continue;
                if (kind != "workspaceWrite" || captured == null || !captured.Cast<object>().Any(allowed => allowed is string &&
                    String.Equals(CanonicalRoot((string)allowed), canonical, StringComparison.OrdinalIgnoreCase))) return false;
            }
            return true;
        }
        private static bool ValidClientMetadata(object value)
        {
            if (value == null) return true;
            var metadata = value as Dictionary<string, object>;
            return metadata != null && metadata.Count == 1 && new[] { "project", "projectless" }.Contains(StringValue(metadata, "workspace_kind"));
        }
        private bool ValidAdditionalContext(object value)
        {
            if (value == null) return true;
            var context = value as Dictionary<string, object>;
            if (context == null || context.Count > 2 || context.Keys.Any(key => key != "codex_apps_client_time_context" && key != "codex_git_state")) return false;
            foreach (var item in context)
            {
                var entry = item.Value as Dictionary<string, object>;
                if (entry == null || entry.Count != 2 || !entry.ContainsKey("kind") || !entry.ContainsKey("value")) return false;
                string text = StringValue(entry, "value");
                if (item.Key == "codex_apps_client_time_context")
                {
                    if (StringValue(entry, "kind") != "application" || text == null || text.Length > 512 ||
                        !Regex.IsMatch(text, "^<timezone>[A-Za-z0-9_+/-]{1,128}</timezone>\\n<current_date>[0-9]{4}-[0-9]{2}-[0-9]{2}</current_date>\\nUse this client time context for user-facing dates, times, and schedules instead of the execution host's timezone and current date\\.$", RegexOptions.CultureInvariant)) return false;
                }
                else if (StringValue(entry, "kind") != "untrusted" || !ValidGitContext(text)) return false;
            }
            return true;
        }
        private bool ValidGitContext(string text)
        {
            // This is the pinned App's read-only Git metadata wrapper (eMe/fE),
            // never a tool response, executable instruction, or arbitrary context.
            const string prefix = "<environment_context>\n  <git source=\"app\" trust=\"untrusted\">", suffix = "</git>\n</environment_context>";
            if (text == null || Encoding.UTF8.GetByteCount(text) > 3947 || !text.StartsWith(prefix, StringComparison.Ordinal) || !text.EndsWith(suffix, StringComparison.Ordinal)) return false;
            string encoded = text.Substring(prefix.Length, text.Length - prefix.Length - suffix.Length);
            string json = encoded.Replace("&quot;", "\"").Replace("&#39;", "'").Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&");
            if (EscapeHtml(json) != encoded) return false;
            var git = new JavaScriptSerializer { MaxJsonLength = 3947 }.Deserialize<Dictionary<string, object>>(json);
            if (git == null || git.Keys.Any(key => !new[] { "schemaVersion", "phase", "hostId", "sessionId", "threadId", "clientUserMessageId", "appVersion", "appServerVersion",
                "status", "capturedAt", "worktreeId", "headCommitSha", "branch", "detached", "upstream", "remotes", "remotesOmitted", "failureReason", "headUnavailableReason",
                "branchUnavailableReason", "upstreamUnavailableReason", "remotesUnavailableReason", "omissionReason", "omittedFields" }.Contains(key))) return false;
            if (!Object.Equals(Value(git, "schemaVersion"), 1) || StringValue(git, "phase") != "before_turn_request" || StringValue(git, "hostId") != "local" ||
                StringValue(git, "threadId") != threadId || StringValue(git, "clientUserMessageId") != operationId ||
                !new[] { "complete", "partial", "failed", "not_git" }.Contains(StringValue(git, "status"))) return false;
            DateTimeOffset captured;
            if (!DateTimeOffset.TryParse(StringValue(git, "capturedAt"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out captured)) return false;
            foreach (string key in new[] { "sessionId", "worktreeId", "appVersion", "appServerVersion" })
                if (!OptionalText(Value(git, key), 256)) return false;
            if (!OptionalText(Value(git, "branch"), 1024)) return false;
            object commit = Value(git, "headCommitSha");
            if (commit != null && (!(commit is string) || !Regex.IsMatch((string)commit, "^(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})$", RegexOptions.CultureInvariant))) return false;
            if (Value(git, "detached") != null && !(Value(git, "detached") is bool)) return false;
            object count = Value(git, "remotesOmitted");
            if (!(count is int) || (int)count < 0 || (int)count > 1000000) return false;
            foreach (string key in new[] { "failureReason", "headUnavailableReason", "branchUnavailableReason", "upstreamUnavailableReason", "remotesUnavailableReason", "omissionReason" })
                if (!ValidGitFailure(Value(git, key))) return false;
            var upstream = Value(git, "upstream") as Dictionary<string, object>;
            if (Value(git, "upstream") != null && (upstream == null || upstream.Keys.Any(key => !new[] { "remoteName", "mergeRef", "trackingRef" }.Contains(key)) ||
                upstream.Values.Any(item => !OptionalText(item, 1024)))) return false;
            var remotes = Value(git, "remotes") as IList;
            if (Value(git, "remotes") != null && (remotes == null || remotes.Count > 16)) return false;
            if (remotes != null) foreach (object item in remotes)
            {
                var remote = item as Dictionary<string, object>;
                if (remote == null || remote.Keys.Any(key => !new[] { "name", "fetchUrl", "unavailableReason" }.Contains(key)) ||
                    !ValidText(StringValue(remote, "name"), 256, false) || !OptionalText(Value(remote, "fetchUrl"), 2048) || !ValidGitFailure(Value(remote, "unavailableReason"))) return false;
            }
            var omitted = Value(git, "omittedFields") as IList;
            if (Value(git, "omittedFields") != null && (omitted == null || omitted.Count > 2 || omitted.Cast<object>().Any(item => !Object.Equals(item, "upstream") && !Object.Equals(item, "branch")))) return false;
            return true;
        }
        private static bool ValidGitFailure(object value)
        { return value == null || value is string && new[] { "timeout", "cancelled", "command_failed", "size_limit", "invalid_value", "unresolved", "unsafe_url" }.Contains((string)value); }
        private static bool OptionalText(object value, int maximum) { return value == null || value is string && ValidText((string)value, maximum, true); }
        private static string CanonicalRoot(string value)
        {
            if (!ValidText(value, 4096, false) || !Path.IsPathRooted(value)) throw new IOException("Continuation runtime root is not absolute.");
            string requestedRoot = Path.GetPathRoot(value);
            if (String.IsNullOrEmpty(requestedRoot) || requestedRoot.Length < 3 || requestedRoot.EndsWith(":", StringComparison.Ordinal))
                throw new IOException("Continuation runtime root is drive-relative.");
            string full = Path.GetFullPath(value), root = Path.GetPathRoot(full);
            return full.Length == root.Length ? full : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        internal static string CanonicalRolloutPath(string value)
        {
            if (!ValidText(value, 4096, false)) throw new IOException("Continuation rollout path is invalid.");
            // Rust may return a verbatim local-drive path. Strip only that exact
            // namespace after proving that ordinary DOS parsing is equivalent.
            // Do not use legacy NetFx Path APIs on the prefix or long paths.
            const string verbatim = @"\\?\";
            bool extended = value.StartsWith(verbatim, StringComparison.Ordinal);
            string path = extended ? value.Substring(verbatim.Length) : value;
            if (path.Length < 4 || !((path[0] >= 'A' && path[0] <= 'Z') || (path[0] >= 'a' && path[0] <= 'z')) ||
                path[1] != ':' || path[2] != '\\' && (extended || path[2] != '/') || extended && path.IndexOf('/') >= 0)
                throw new IOException("Continuation rollout path is not a local absolute drive path.");
            path = path.Replace('/', '\\');
            string[] segments = path.Substring(3).Split('\\');
            foreach (string segment in segments)
            {
                if (segment.Length == 0 || segment.Length > 255 || segment == "." || segment == ".." ||
                    segment.EndsWith(".", StringComparison.Ordinal) || segment.EndsWith(" ", StringComparison.Ordinal) ||
                    segment.Any(character => Char.IsControl(character) || "<>:\"|?*".IndexOf(character) >= 0))
                    throw new IOException("Continuation rollout path has an ambiguous component.");
                string stem = segment.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
                if (new[] { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$" }.Contains(stem) ||
                    stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
                    (stem[3] >= '0' && stem[3] <= '9' || "\u00b9\u00b2\u00b3".IndexOf(stem[3]) >= 0))
                    throw new IOException("Continuation rollout path uses a device component.");
            }
            return Char.ToUpperInvariant(path[0]) + path.Substring(1);
        }
        private static bool ValidText(string value, int maximum, bool empty) { return value != null && value.Length <= maximum && (empty || value.Length > 0) && !value.Any(Char.IsControl); }
        private static string EscapeHtml(string value)
        { return value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#39;"); }
        private bool ValidResume(Dictionary<string, object> parameters)
        {
            try
            {
                if (parameters == null || StringValue(parameters, "threadId") != threadId || parameters.Keys.Any(key => !new[] {
                    "threadId", "excludeTurns", "initialTurnsPage", "model", "modelProvider", "cwd", "approvalPolicy", "approvalsReviewer", "sandbox", "permissions", "runtimeWorkspaceRoots",
                    "history", "path", "personality", "config", "developerInstructions", "environments" }.Contains(key))) return false;
                object value;
                if (parameters.TryGetValue("excludeTurns", out value) && !(value is bool)) return false;
                if (Value(parameters, "history") != null) return false;
                if (parameters.TryGetValue("path", out value) && value != null && (!(value is string) || rolloutPath == null ||
                    !String.Equals(CanonicalRolloutPath((string)value), rolloutPath, StringComparison.OrdinalIgnoreCase))) return false;
                if (parameters.TryGetValue("initialTurnsPage", out value) && value != null)
                {
                    var page = value as Dictionary<string, object>;
                    if (page == null || page.Count != 3 || !Object.Equals(Value(page, "limit"), 5) || StringValue(page, "itemsView") != "full" ||
                        StringValue(page, "sortDirection") != "desc") return false;
                }
                value = Value(parameters, "personality");
                if (value != null && !(value is string && new[] { "none", "friendly", "pragmatic" }.Contains((string)value))) return false;
                var requestedContext = ParseFrontendContext(ProjectFrontendContext(parameters, verifyAppCommand));
                var capturedContext = ParseFrontendContext(frontendContextJson);
                var requestedConfig = Value(parameters, "config") as Dictionary<string, object>;
                if (requestedConfig != null && requestedConfig.TryGetValue("model_reasoning_effort", out value) && !Object.Equals(value, effort)) return false;
                if (Canonical(Value(requestedContext, "config")) != Canonical(Value(capturedContext, "config")) ||
                    Canonical(Value(requestedContext, "developerInstructions")) != Canonical(Value(capturedContext, "developerInstructions"))) return false;
                // The backend Prepare check compares effective environments too.
                // A cold resume may omit them to inherit; an explicit override
                // must still describe exactly the captured local environment.
                if (Value(parameters, "environments") != null && Canonical(NormalizeEnvironments(Value(parameters, "environments"), cwd)) !=
                    Canonical(Value(capturedContext, "environments"))) return false;
                if (parameters.TryGetValue("model", out value) && value != null && !Object.Equals(value, model)) return false;
                if (parameters.TryGetValue("modelProvider", out value) && value != null && !Object.Equals(value, modelProvider)) return false;
                if (parameters.TryGetValue("cwd", out value) && value != null && (cwd == null || !(value is string) ||
                    !String.Equals(CanonicalRoot((string)value), cwd, StringComparison.OrdinalIgnoreCase))) return false;
                if (parameters.TryGetValue("runtimeWorkspaceRoots", out value) && value != null)
                {
                    var roots = value as IList; if (roots == null || roots.Count > 64) return false;
                    var requested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (object root in roots) if (!(root is string) || !requested.Add(CanonicalRoot((string)root))) return false;
                    if (!requested.SetEquals(capturedRuntimeRoots)) return false;
                }
                foreach (string key in new[] { "approvalPolicy", "approvalsReviewer" })
                    if (parameters.TryGetValue(key, out value) && value != null && Canonical(value) != Canonical(Value(policy, key))) return false;
                object requestedProfile = Value(parameters, "permissions"), requestedSandbox = Value(parameters, "sandbox");
                if (requestedProfile != null && requestedSandbox != null) return false;
                if (requestedProfile != null)
                {
                    var profile = Value(policy, "permissions") as Dictionary<string, object>;
                    if (!(requestedProfile is string) || profile == null || StringValue(profile, "id") != (string)requestedProfile) return false;
                }
                if (requestedSandbox != null)
                {
                    var original = Value(policy, "sandbox") as Dictionary<string, object>;
                    string type = original == null ? null : StringValue(original, "type");
                    string mode = type == "readOnly" ? "read-only" : type == "workspaceWrite" ? "workspace-write" : type == "dangerFullAccess" ? "danger-full-access" : null;
                    if (!(requestedSandbox is string) || mode == null || (string)requestedSandbox != mode) return false;
                }
                return true;
            }
            catch { return false; }
        }
        internal static bool IsReadOnlyDesktopStartupRequest(string method, Dictionary<string, object> parameters)
        {
            if (method != "collaborationMode/list" && method != "account/gatewayOAuth/read" && method != "remoteControl/status/read" &&
                method != "windowsSandbox/readiness" && method != "plugin/installed") return false;
            // These are the pinned bundled protocol's parameterless preset and
            // readiness reads. In particular gatewayOAuth/read returns policy
            // and readiness only, never credentials or an OAuth refresh/login.
            if (method != "plugin/installed") return parameters == null || parameters.Count == 0;
            // plugin/installed discovers local installed marketplaces. It does
            // not install/enable a plugin; reject all options outside its read
            // schema and never admit a command, URL or configuration override.
            if (parameters == null) return true;
            if (parameters.Keys.Any(key => key != "cwds" && key != "installSuggestionPluginNames")) return false;
            try
            {
                object value;
                if (parameters.TryGetValue("cwds", out value) && value != null)
                {
                    var roots = value as IList; if (roots == null || roots.Count > 64) return false;
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (object entry in roots)
                    {
                        if (!(entry is string)) return false;
                        string path = CanonicalRoot((string)entry), root = Path.GetPathRoot(path);
                        if (root == null || root.Length != 3 || root[1] != ':' || !seen.Add(path)) return false;
                    }
                }
                if (parameters.TryGetValue("installSuggestionPluginNames", out value) && value != null)
                {
                    var names = value as IList; if (names == null || names.Count > 256) return false;
                    foreach (object entry in names) if (!(entry is string) || !ValidText((string)entry, 256, false)) return false;
                }
                return true;
            }
            catch { return false; }
        }
        private static bool IsReadOnlyRequest(string method)
        {
            return new[] { "initialize", "initialized", "account/read", "account/rateLimits/read", "model/list", "models/list",
                "config/read", "configRequirements/read", "experimentalFeature/list", "skills/list", "plugin/list", "app/list",
                "mcpServerStatus/list", "permissionProfile/list", "thread/list", "thread/read", "thread/loaded/list", "thread/turns/list",
                "thread/items/list", "thread/backgroundTerminals/list", "thread/goal/get", "environment/list", "environment/read",
                "fs/readFile", "fs/listDirectory", "fs/getMetadata", "fs/read", "fs/list", "fs/stat" }.Contains(method);
        }
        private static object Value(Dictionary<string, object> dictionary, string key)
        { object value; return dictionary != null && dictionary.TryGetValue(key, out value) ? value : null; }
        private static string StringValue(Dictionary<string, object> dictionary, string key)
        { return Value(dictionary, key) as string; }
        private static string Canonical(object value)
        {
            return new JavaScriptSerializer { MaxJsonLength = 32768 }.Serialize(Normalize(value, 0));
        }
        private static object Normalize(object value, int depth)
        {
            if (depth > 12) throw new IOException("Continuation metadata nesting exceeded its bound.");
            var map = value as IDictionary<string, object>;
            if (map != null)
            {
                var sorted = new SortedDictionary<string, object>(StringComparer.Ordinal);
                foreach (var pair in map) sorted.Add(pair.Key, Normalize(pair.Value, depth + 1));
                return sorted;
            }
            var list = value as IList;
            if (list != null) { var copy = new List<object>(); foreach (object item in list) copy.Add(Normalize(item, depth + 1)); return copy; }
            return value;
        }
        private static bool Reject(out string rejection)
        { rejection = "Automatic continuation is pending or this continuation was already attempted."; return false; }
        private static void RequireText(string value)
        { if (String.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.Any(Char.IsControl)) throw new IOException("Continuation identity is invalid."); }
    }
}
