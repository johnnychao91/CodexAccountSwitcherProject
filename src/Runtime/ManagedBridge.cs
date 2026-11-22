using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CodexAccountSwitcher
{
    public sealed class ManagedBridgeOptions
    {
        public string BackendExecutable { get; set; }
        public string[] BackendArguments { get; set; }
        public string CatalogDirectory { get; set; }
        public string ControllerExecutable { get; set; }
        public string HelperExecutable { get; set; }
        public string AuthorizationFile { get; set; }
        public string AuthorizationGeneration { get; set; }
        public int OwnerProcessId { get; set; }
        public bool EnableHostControl { get; set; }
        public bool DisableControlPipe { get; set; }
        public bool RequireInitializedNotification { get; set; }
        internal ContinuationAdmission Admission { get; set; }
        internal Func<bool> IsContinuationOwner { get; set; }
        internal Func<string, bool> VerifyAppCommand { get; set; }
        internal IDictionary<string, string> BackendEnvironment { get; set; }
        public ManagedBridgeOptions() { RequireInitializedNotification = true; }
        public static string DefaultCatalogDirectory
        { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexAccountSwitcher", "integrations", "vscode", "runtimes"); } }
    }

    // This process owns the only stdin writer for its child. It never attaches to another process.
    // Full protocol lines exist only while forwarding; catalogs and controller replies contain metadata.
    public sealed class ManagedBridge : IDisposable
    {
        private const int MaxLineChars = 32 * 1024 * 1024;
        private readonly ManagedBridgeOptions options;
        private readonly TextReader clientInput;
        private readonly TextWriter clientOutput;
        private readonly object stateGate = new object(), backendWriteGate = new object(), outputGate = new object(), controlGate = new object();
        private readonly Dictionary<string, TaskCompletionSource<Dictionary<string, object>>> internalRequests = new Dictionary<string, TaskCompletionSource<Dictionary<string, object>>>();
        private readonly Dictionary<string, string> clientRequests = new Dictionary<string, string>();
        private readonly HashSet<string> readOnlyClientRequests = new HashSet<string>();
        private readonly Dictionary<string, string> activeTurns = new Dictionary<string, string>();
        private readonly HashSet<string> pendingServerRequests = new HashSet<string>();
        private readonly HashSet<string> spawnedProcesses = new HashSet<string>();
        private readonly Dictionary<string, string> processRequests = new Dictionary<string, string>();
        private readonly Dictionary<string, string> continuationRequests = new Dictionary<string, string>();
        private readonly Dictionary<string, string> frontendResumeRequests = new Dictionary<string, string>();
        private readonly Dictionary<string, string> frontendContextRequests = new Dictionary<string, string>();
        private readonly Dictionary<string, string> frontendContexts = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly HashSet<string> frontendResumedThreads = new HashSet<string>(StringComparer.Ordinal);
        private readonly string internalPrefix, wrapperStartedUtc, ownerStartedUtc;
        private readonly int ownerPid;
        private Process backend;
        private StreamWriter backendInput;
        private Task backendReaderTask;
        private NamedPipeServerStream listeningPipe;
        private string backendStartedUtc, catalogPath, state = "starting", reason = "initializing";
        private int backendPid;
        private int inFlightForwards;
        private long requestSequence, activitySequence;
        private bool initialized, initializeReplyForwarded, clientInitialized, protocolHealthy = true, frozen, expectedExit, resumeRequested, disposed;
        private bool eofAttempted, backendInputClosed;
        private volatile bool backendExitObserved;
        private Snapshot preparedSnapshot;
        private readonly ManualResetEventSlim backendReady = new ManualResetEventSlim(false);
        public string InstanceId { get; private set; }
        public string PipeName { get; private set; }
        public int ExitCode { get; private set; }
        internal bool BackendExitObserved { get { return backendExitObserved; } }
        internal ContinuationAdmission ContinuationAdmission { get { return options.Admission; } }
        internal bool HasFrontendResumedThread(string threadId)
        { lock (stateGate) return threadId != null && frontendResumedThreads.Contains(threadId); }
        internal bool HasPendingFrontendResumeThread(string threadId)
        { lock (stateGate) return threadId != null && frontendResumeRequests.Values.Contains(threadId); }
        internal bool HasOnlyReadOnlyPendingRequests
        {
            get
            {
                lock (stateGate) return clientRequests.Count > 0 && pendingServerRequests.Count == 0 && clientRequests.Keys.All(key => readOnlyClientRequests.Contains(key));
            }
        }
        internal static bool IsReadOnlyFrontendRequest(string method, Dictionary<string, object> parameters)
        {
            if (ContinuationAdmission.IsReadOnlyDesktopStartupRequest(method, parameters)) return true;
            if (method == "getAuthStatus") return parameters == null || parameters.Keys.All(key =>
                key == "includeToken" && parameters[key] is bool || key == "refreshToken" && Object.Equals(parameters[key], false));
            return new[] { "account/read", "account/rateLimits/read", "model/list", "models/list", "config/read", "configRequirements/read",
                "experimentalFeature/list", "skills/list", "plugin/list", "app/list", "mcpServerStatus/list", "permissionProfile/list",
                "thread/list", "thread/read", "thread/loaded/list", "thread/turns/list", "thread/items/list", "thread/backgroundTerminals/list",
                "thread/goal/get", "environment/list", "environment/read", "fs/readFile", "fs/listDirectory", "fs/getMetadata", "fs/read", "fs/list", "fs/stat" }.Contains(method);
        }
        internal bool TransportReady
        {
            get { lock (stateGate) return !disposed && BackendAlive() && initialized && initializeReplyForwarded &&
                (!options.RequireInitializedNotification || clientInitialized); }
        }

        public ManagedBridge(ManagedBridgeOptions options, TextReader clientInput, TextWriter clientOutput)
        {
            this.options = options; this.clientInput = clientInput; this.clientOutput = clientOutput;
            InstanceId = Guid.NewGuid().ToString("N");
            PipeName = "CodexAccountSwitcher.vscode." + InstanceId;
            internalPrefix = "__cas_control_" + InstanceId + ":";
            using (var current = Process.GetCurrentProcess()) wrapperStartedUtc = Utc(current.StartTime);
            ownerPid = options.OwnerProcessId > 0 ? options.OwnerProcessId : ParentProcessId();
            ownerStartedUtc = StartTime(ownerPid);
        }

        public static Dictionary<string, object> Parse(string value)
        { return new JavaScriptSerializer { MaxJsonLength = MaxLineChars, RecursionLimit = 256 }.DeserializeObject(value) as Dictionary<string, object>; }
        public static string Encode(object value)
        { return new JavaScriptSerializer { MaxJsonLength = MaxLineChars, RecursionLimit = 256 }.Serialize(value); }
        private static Dictionary<string, object> Map(Dictionary<string, object> value, string name)
        { object result; return value != null && value.TryGetValue(name, out result) ? result as Dictionary<string, object> : null; }
        private static string Str(Dictionary<string, object> value, string name)
        { object result; return value != null && value.TryGetValue(name, out result) && result != null ? Convert.ToString(result, CultureInfo.InvariantCulture) : null; }
        private static object[] ArrayOf(Dictionary<string, object> value, string name)
        { object result; return value != null && value.TryGetValue(name, out result) ? result as object[] : null; }
        private static string Key(object id) { return id is string ? "s:" + (string)id : "n:" + Convert.ToString(id, CultureInfo.InvariantCulture); }
        private static string Utc(DateTime value) { return value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture); }
        private static string StartTime(int pid)
        { try { using (var process = Process.GetProcessById(pid)) return Utc(process.StartTime); } catch { return null; } }
        private bool OwnerAlive() { return ownerPid > 0 && ownerStartedUtc != null && StartTime(ownerPid) == ownerStartedUtc; }
        private bool BackendAlive() { try { return backend != null && !backend.HasExited; } catch { return false; } }

        public static void PrivateDirectory(string path)
        {
            EnsureNoReparsePoint(path);
            var security = new DirectorySecurity();
            var current = WindowsIdentity.GetCurrent().User;
            security.SetOwner(current);
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(current, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            // Supply the owner and DACL when Windows creates the directory. An
            // elevated token can otherwise default to Administrators as owner.
            // This overload does not replace an existing directory's descriptor.
            Directory.CreateDirectory(path, security);
            EnsureNoReparsePoint(path);
            VerifyPrivateDirectorySecurity(Directory.GetAccessControl(path), current);
        }
        internal static void VerifyPrivateDirectorySecurity(DirectorySecurity security, SecurityIdentifier current)
        {
            if (!security.GetOwner(typeof(SecurityIdentifier)).Equals(current))
                throw new IOException("Integration directory owner changed.");
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                if (rule.AccessControlType == AccessControlType.Allow && !rule.IdentityReference.Equals(current) && !rule.IdentityReference.Equals(system))
                    throw new IOException("Integration directory is not private to this Windows user.");
        }
        internal static FileStream CreatePrivateFile(string path)
        {
            EnsureNoReparsePoint(Path.GetDirectoryName(Path.GetFullPath(path)));
            var current = WindowsIdentity.GetCurrent().User;
            var security = new FileSecurity();
            security.SetOwner(current);
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(current, FileSystemRights.FullControl, AccessControlType.Allow));
            // CreateNew prevents replacing or taking ownership of an existing leaf.
            return new FileStream(path, FileMode.CreateNew, FileSystemRights.Read | FileSystemRights.Write,
                FileShare.None, 4096, FileOptions.WriteThrough, security);
        }
        public static void EnsureNoReparsePoint(string path)
        {
            for (var current = new DirectoryInfo(Path.GetFullPath(path)); current != null; current = current.Parent)
                if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("A reparse point is not supported for integration files.");
        }
        public static string QuoteArgument(string value)
        {
            var result = new StringBuilder("\""); int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') { result.Append('\\', slashes * 2 + 1); result.Append(c); slashes = 0; continue; }
                result.Append('\\', slashes); slashes = 0; result.Append(c);
            }
            result.Append('\\', slashes * 2); return result.Append('"').ToString();
        }

        public void Run()
        {
            string directory = options.CatalogDirectory ?? ManagedBridgeOptions.DefaultCatalogDirectory;
            PrivateDirectory(directory);
            catalogPath = Path.Combine(directory, InstanceId + ".json");
            var start = new ProcessStartInfo(options.BackendExecutable, String.Join(" ", (options.BackendArguments ?? new string[0]).Select(QuoteArgument)))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false) };
            if (options.BackendEnvironment != null)
                foreach (var entry in options.BackendEnvironment)
                {
                    if (!new[] { "CODEX_APP_TOOLS_PIPE_PATH", "CODEX_MCP_NODE_PATH", "CODEX_BROWSER_USE_NODE_PATH", "CODEX_ELECTRON_RESOURCES_PATH", "CODEX_CLI_PATH", "CODEX_INTERNAL_ORIGINATOR_OVERRIDE" }.Contains(entry.Key) ||
                        entry.Key == "CODEX_INTERNAL_ORIGINATOR_OVERRIDE" && entry.Value != "Codex Desktop")
                        throw new IOException("An unsupported managed backend environment key was supplied.");
                    if (entry.Value == null) start.EnvironmentVariables.Remove(entry.Key);
                    else start.EnvironmentVariables[entry.Key] = entry.Value;
                }
            backend = new Process { StartInfo = start };
            if (!backend.Start()) throw new IOException("The managed backend could not start.");
            try
            {
                // Framework Process.StandardInput inherits the host console code page.
                // JSON-RPC is always UTF-8, including GUI launches with a legacy code page.
                // Keep the framework writer unused so it cannot encode or emit a preamble.
                backendInput = new StreamWriter(backend.StandardInput.BaseStream, new UTF8Encoding(false, true), 4096) { NewLine = "\n" };
                backendPid = backend.Id; backendStartedUtc = Utc(backend.StartTime);
                backendReady.Set();
                backendReaderTask = Task.Factory.StartNew(ReadBackend, TaskCreationOptions.LongRunning);
                Task.Factory.StartNew(DrainStderr, TaskCreationOptions.LongRunning);
                lock (stateGate) { state = "running"; WriteCatalog(); }
                if (!options.DisableControlPipe) Task.Factory.StartNew(ServeControl, TaskCreationOptions.LongRunning);
                var clientPump = Task.Factory.StartNew(() =>
                {
                    try { string line; while ((line = ReadLine(clientInput, MaxLineChars)) != null) ForwardClient(line); }
                    catch { MarkUnknown("clientStreamFailed"); }
                }, TaskCreationOptions.LongRunning);
                while (!clientPump.Wait(100))
                {
                    lock (stateGate) if (!expectedExit && !BackendAlive()) break;
                }
                if (!BackendAlive()) backendReaderTask.Wait(1000);
            }
            catch { MarkUnknown("clientStreamFailed"); }
            finally
            {
                lock (stateGate) frozen = true;
                CloseBackendInput();
                // Keep ownership and catalog discoverability if EOF does not finish the child promptly.
                // Neither a timeout nor losing the editor authorizes a force kill.
                while (BackendAlive()) Thread.Sleep(100);
                // A failed lookup is not evidence that the owned process exited. Capture
                // positive evidence from our retained Process before it is disposed.
                try
                {
                    if (backend.HasExited) { ExitCode = backend.ExitCode; backendExitObserved = true; }
                    else ExitCode = 1;
                }
                catch { ExitCode = 1; }
                Dispose();
            }
        }

        private static string ReadLine(TextReader input, int limit)
        {
            var line = new StringBuilder(); int next;
            while ((next = input.Read()) >= 0)
            {
                if (next == '\n') return line.ToString().TrimEnd('\r');
                if (line.Length >= limit) throw new IOException("Protocol line exceeded the size limit.");
                line.Append((char)next);
            }
            return line.Length == 0 ? null : line.ToString();
        }
        private void WriteClient(string line)
        { lock (outputGate) { clientOutput.WriteLine(line); clientOutput.Flush(); } }
        private void WriteBackend(string line)
        {
            lock (backendWriteGate)
            {
                if (!BackendAlive() || backendInput == null) throw new IOException("Backend unavailable.");
                backendInput.WriteLine(line); backendInput.Flush();
            }
        }
        private void WriteControllerMessage(string line, string method, Stopwatch clock, int timeout)
        {
            int remaining = timeout - (int)clock.ElapsedMilliseconds;
            if (remaining <= 0 || !Monitor.TryEnter(backendWriteGate, remaining)) throw new IOException("Controller write deadline expired.");
            try
            {
                if (clock.ElapsedMilliseconds >= timeout) throw new IOException("Controller write deadline expired.");
                if (method == "turn/interrupt" && !ControlEnabled()) throw new IOException("Integration control authorization was revoked.");
                if (!BackendAlive()) throw new IOException("Backend unavailable.");
                backendInput.WriteLine(line); backendInput.Flush();
            }
            finally { Monitor.Exit(backendWriteGate); }
        }
        private void RejectClient(object id, string message)
        { WriteClient(Encode(new { id = id, error = new { code = -32070, message = message } })); }

        internal void HostForward(string line)
        {
            if (!options.EnableHostControl || line == null || line.Length > MaxLineChars || disposed)
                throw new IOException("Desktop forwarding is unavailable.");
            if (!backendReady.Wait(5000)) throw new IOException("Desktop backend startup deadline expired.");
            // Admission accounting happens synchronously, before the supervisor can begin
            // prepare. A reader queue is not evidence that its pending requests were counted.
            ForwardClient(line, true);
        }

        private void ForwardClient(string line, bool bounded = false)
        {
            Dictionary<string, object> message;
            try { message = Parse(line); }
            catch { if (bounded) { MarkUnknown("unrecognizedClientProtocol"); throw new IOException("Invalid desktop protocol."); } ForwardUnrecognized(line); return; }
            if (message == null) { if (bounded) { MarkUnknown("unrecognizedClientProtocol"); throw new IOException("Invalid desktop protocol."); } ForwardUnrecognized(line); return; }
            object id; bool hasId = message.TryGetValue("id", out id);
            string method = Str(message, "method"); bool request = method != null && hasId;
            string rejection = null;
            lock (stateGate)
            {
                if (request)
                {
                    if (id is string && ((string)id).StartsWith(internalPrefix, StringComparison.Ordinal)) rejection = "Reserved bridge request namespace.";
                    else if (!BackendAlive()) rejection = "Managed Codex backend is unavailable.";
                    else if (frozen) rejection = "Account switching is in progress. Retry after the Codex integration reconnects.";
                    else if (options.Admission != null && options.Admission.IsArmed && (method == "turn/start" || method == "turn/steer") &&
                        options.IsContinuationOwner != null && !options.IsContinuationOwner())
                        rejection = "This owned backend has not been verified as the restored conversation owner.";
                    else if (options.Admission != null && !options.Admission.ShouldAllow(method, Map(message, "params")))
                        rejection = "The restored conversation is reserved for a single account-switch continuation.";
                    else
                    {
                        string key = Key(id);
                        if (clientRequests.ContainsKey(key)) { protocolHealthy = false; reason = "duplicateClientRequestId"; }
                        if (method == "initialize") { initialized = false; initializeReplyForwarded = false; clientInitialized = false; }
                        clientRequests[key] = method; activitySequence++;
                        if (IsReadOnlyFrontendRequest(method, Map(message, "params"))) readOnlyClientRequests.Add(key);
                        else readOnlyClientRequests.Remove(key);
                        if (method == "thread/start" || method == "thread/resume")
                        {
                            // Keep only a bounded, non-secret semantic projection.
                            // Unsupported contexts still work normally in the App,
                            // but cannot authorize an automatic continuation.
                            string summary = null;
                            try { summary = ContinuationAdmission.ProjectFrontendContext(Map(message, "params"), options.VerifyAppCommand); }
                            catch { }
                            frontendContextRequests[key] = summary;
                        }
                        if (method == "thread/resume")
                        {
                            string resumedThread = Str(Map(message, "params"), "threadId");
                            if (!String.IsNullOrEmpty(resumedThread)) frontendResumeRequests[key] = resumedThread;
                        }
                        if (method == "turn/start" && options.Admission != null)
                        {
                            string messageId = Str(Map(message, "params"), "clientUserMessageId");
                            if (messageId == options.Admission.ReservedOperationId) continuationRequests[key] = messageId;
                        }
                        if (method == "process/spawn")
                        {
                            string handle = Str(Map(message, "params"), "processHandle");
                            if (handle == null) { protocolHealthy = false; reason = "unknownProcessHandle"; }
                            else if (spawnedProcesses.Add(handle)) processRequests[key] = handle;
                            else { protocolHealthy = false; reason = "duplicateProcessHandle"; }
                        }
                    }
                }
                else if (method != null && !hasId && options.Admission != null && options.Admission.IsArmed && method != "initialized")
                {
                    // JSON-RPC notifications have no reply channel. During the
                    // restored-chat barrier, only the handshake notification is
                    // valid; never let a method-only mutation bypass admission.
                    return;
                }
                else if (method == null && hasId) { pendingServerRequests.Remove(Key(id)); activitySequence++; }
                else if (frozen && method != "initialized") { protocolHealthy = false; reason = "notificationDuringQuiesce"; return; }
                if (rejection == null) inFlightForwards++;
            }
            if (rejection != null) { RejectClient(id, rejection); return; }
            try
            {
                if (!bounded) WriteBackend(line);
                else
                {
                    var write = Task.Factory.StartNew(() => WriteBackend(line));
                    if (!write.Wait(5000)) throw new IOException("Desktop backend write deadline expired.");
                    write.GetAwaiter().GetResult();
                }
                if (method == "initialized" && !hasId) lock (stateGate) { clientInitialized = true; activitySequence++; }
            }
            catch
            {
                MarkUnknown("backendWriteFailed");
                if (request) { lock (stateGate) { clientRequests.Remove(Key(id)); readOnlyClientRequests.Remove(Key(id)); frontendResumeRequests.Remove(Key(id)); frontendContextRequests.Remove(Key(id)); } RejectClient(id, "Managed Codex backend is unavailable."); }
                if (bounded) throw new IOException("Desktop forwarding did not complete safely.");
            }
            finally { lock (stateGate) { inFlightForwards--; activitySequence++; } }
        }
        private void ForwardUnrecognized(string line)
        {
            lock (stateGate) { protocolHealthy = false; reason = "unrecognizedClientProtocol"; if (frozen) return; inFlightForwards++; }
            try { WriteBackend(line); }
            finally { lock (stateGate) { inFlightForwards--; activitySequence++; } }
        }

        private void ReadBackend()
        {
            try
            {
                string line;
                while ((line = ReadLine(backend.StandardOutput, MaxLineChars)) != null)
                {
                    Dictionary<string, object> message = null;
                    try { message = Parse(line); } catch { MarkUnknown("unrecognizedBackendProtocol"); }
                    bool suppress = false, forwardSuccessfulInitialize = false;
                    if (message != null)
                    {
                        object id; bool hasId = message.TryGetValue("id", out id); string method = Str(message, "method");
                        if (method == null && hasId && id is string && ((string)id).StartsWith(internalPrefix, StringComparison.Ordinal))
                        {
                            TaskCompletionSource<Dictionary<string, object>> pending = null;
                            lock (stateGate) { internalRequests.TryGetValue((string)id, out pending); internalRequests.Remove((string)id); }
                            if (pending != null) pending.TrySetResult(message);
                            suppress = true; // Includes timed-out controller replies. Client IDs cannot enter this namespace.
                        }
                        else
                        {
                            if (method == null && hasId && !message.ContainsKey("error") && Map(message, "result") != null)
                                lock (stateGate)
                                {
                                    string requestMethod;
                                    forwardSuccessfulInitialize = clientRequests.TryGetValue(Key(id), out requestMethod) && requestMethod == "initialize";
                                }
                            ObserveBackend(message, id, hasId, method);
                        }
                    }
                    else MarkUnknown("unrecognizedBackendProtocol");
                    if (!suppress)
                    {
                        WriteClient(line);
                        // Observing the reply is not enough: a blocked/failed frontend
                        // writer must not grant readiness before delivery completes.
                        if (forwardSuccessfulInitialize) lock (stateGate) { initializeReplyForwarded = true; activitySequence++; }
                    }
                }
            }
            catch { MarkUnknown("backendStreamFailed"); }
            finally
            {
                try { backend.WaitForExit(); } catch { }
                lock (stateGate)
                {
                    bool normal = expectedExit && eofAttempted && backendInputClosed && !BackendAlive() && backend.ExitCode == 0;
                    state = normal ? "quiesced" : "unknown";
                    reason = normal ? null : "backendExitedUnexpectedly";
                    foreach (var pending in internalRequests.Values) pending.TrySetException(new IOException("Backend disconnected."));
                    internalRequests.Clear();
                    try { WriteCatalog(); } catch { protocolHealthy = false; reason = "catalogUnavailable"; }
                }
            }
        }
        private void ObserveBackend(Dictionary<string, object> message, object id, bool hasId, string method)
        {
            lock (stateGate)
            {
                if (method == null && hasId)
                {
                    string requestMethod; string key = Key(id);
                    if (clientRequests.TryGetValue(key, out requestMethod))
                    {
                        clientRequests.Remove(key); readOnlyClientRequests.Remove(key); activitySequence++;
                        if (requestMethod == "initialize") { initialized = !message.ContainsKey("error"); reason = initialized ? null : "initializeFailed"; }
                        string frontendSummary;
                        if (frontendContextRequests.TryGetValue(key, out frontendSummary))
                        {
                            frontendContextRequests.Remove(key);
                            string actualThread = Str(Map(Map(message, "result"), "thread"), "id");
                            string requestedThread;
                            bool sameTarget = requestMethod == "thread/start" || frontendResumeRequests.TryGetValue(key, out requestedThread) && requestedThread == actualThread;
                            if (!message.ContainsKey("error") && !String.IsNullOrEmpty(actualThread) && sameTarget)
                            {
                                if (frontendSummary == null) frontendContexts.Remove(actualThread);
                                else frontendContexts[actualThread] = frontendSummary;
                            }
                        }
                        string processHandle;
                        if (processRequests.TryGetValue(key, out processHandle))
                        { processRequests.Remove(key); if (message.ContainsKey("error")) spawnedProcesses.Remove(processHandle); }
                        string continuationId;
                        string resumedThread;
                        if (frontendResumeRequests.TryGetValue(key, out resumedThread))
                        {
                            frontendResumeRequests.Remove(key);
                            if (!message.ContainsKey("error") && Str(Map(Map(message, "result"), "thread"), "id") == resumedThread)
                                frontendResumedThreads.Add(resumedThread);
                        }
                        if (continuationRequests.TryGetValue(key, out continuationId))
                        {
                            continuationRequests.Remove(key);
                            string acceptedTurn = Str(Map(Map(message, "result"), "turn"), "id");
                            if (!message.ContainsKey("error") && !String.IsNullOrEmpty(acceptedTurn) && options.Admission != null)
                                options.Admission.RecordAcceptedTurn(continuationId, acceptedTurn);
                        }
                    }
                    return;
                }
                var parameters = Map(message, "params");
                if (hasId) { pendingServerRequests.Add(Key(id)); activitySequence++; }
                if (method == "turn/started")
                {
                    string thread = Str(parameters, "threadId"), turn = Str(Map(parameters, "turn"), "id");
                    if (thread == null || turn == null) { protocolHealthy = false; reason = "unknownTurn"; }
                    else activeTurns[thread] = turn;
                    activitySequence++;
                }
                else if (method == "turn/completed")
                {
                    string thread = Str(parameters, "threadId"), turn = Str(Map(parameters, "turn"), "id"), active;
                    if (thread != null && activeTurns.TryGetValue(thread, out active) && active == turn) activeTurns.Remove(thread);
                    activitySequence++;
                }
                else if (method == "serverRequest/resolved")
                { object requestId; if (parameters != null && parameters.TryGetValue("requestId", out requestId)) pendingServerRequests.Remove(Key(requestId)); activitySequence++; }
                else if (method == "process/exited")
                { string handle = Str(parameters, "processHandle"); if (handle != null) spawnedProcesses.Remove(handle); activitySequence++; }
                else if (method == "thread/status/changed" || method == "thread/started" || method == "thread/closed") activitySequence++;
            }
        }
        private void DrainStderr()
        {
            try { var buffer = new char[4096]; while (backend.StandardError.Read(buffer, 0, buffer.Length) > 0) { } }
            catch { }
            // Backend diagnostics can contain auth URLs or user content, so never persist or echo them.
        }
        private void MarkUnknown(string code) { lock (stateGate) { protocolHealthy = false; reason = code; } }

        private Dictionary<string, object> Rpc(string method, object parameters, Stopwatch clock, int timeout)
        {
            string id = internalPrefix + Interlocked.Increment(ref requestSequence).ToString(CultureInfo.InvariantCulture);
            var completion = new TaskCompletionSource<Dictionary<string, object>>();
            lock (stateGate) internalRequests.Add(id, completion);
            try
            {
                string line = Encode(new { id = id, method = method, @params = parameters });
                var write = Task.Factory.StartNew(() => WriteControllerMessage(line, method, clock, timeout));
                int writeRemaining = timeout - (int)clock.ElapsedMilliseconds;
                if (writeRemaining <= 0 || !write.Wait(writeRemaining)) { MarkUnknown("backendWriteTimeout"); throw new IOException("runtimeQueryTimeout"); }
                int remaining = timeout - (int)clock.ElapsedMilliseconds;
                if (remaining <= 0 || !completion.Task.Wait(remaining)) throw new IOException("runtimeQueryTimeout");
                var response = completion.Task.Result;
                if (response.ContainsKey("error")) throw new IOException("runtimeQueryRejected");
                var result = Map(response, "result");
                if (result == null) throw new IOException("runtimeQueryInvalid");
                return result;
            }
            finally { lock (stateGate) internalRequests.Remove(id); }
        }
        private sealed class Snapshot
        {
            public bool Known, Idle;
            public int Active, Pending, Background;
            public long Sequence;
            public string Reason;
            public Dictionary<string, string> Turns = new Dictionary<string, string>();
        }
        private Snapshot ReadSnapshot(Stopwatch clock, int timeout)
        {
            var snapshot = new Snapshot();
            lock (stateGate)
            {
                snapshot.Sequence = activitySequence;
                if (state == "quiesced" && !BackendAlive()) { snapshot.Known = true; snapshot.Idle = true; return snapshot; }
                if (!BackendAlive()) { snapshot.Reason = reason ?? "backendUnavailable"; return snapshot; }
                if (!initialized || !initializeReplyForwarded || options.RequireInitializedNotification && !clientInitialized)
                { snapshot.Reason = reason == "initializeFailed" ? reason : "initializing"; return snapshot; }
                if (!protocolHealthy) { snapshot.Reason = reason ?? "backendUnavailable"; return snapshot; }
            }
            try
            {
                var threads = new HashSet<string>(); string cursor = null; var cursors = new HashSet<string>();
                for (int page = 0; ; page++)
                {
                    if (page >= 100) throw new IOException("paginationLimit");
                    var result = Rpc("thread/loaded/list", new { cursor = cursor, limit = 100 }, clock, timeout);
                    var ids = ArrayOf(result, "data"); if (ids == null) throw new IOException("runtimeQueryInvalid");
                    foreach (var id in ids) { if (!(id is string) || !threads.Add((string)id)) throw new IOException("runtimeQueryInvalid"); }
                    cursor = Str(result, "nextCursor"); if (cursor == null) break;
                    if (!cursors.Add(cursor)) throw new IOException("paginationLoop");
                }
                foreach (string threadId in threads)
                {
                    var thread = Map(Rpc("thread/read", new { threadId = threadId, includeTurns = false }, clock, timeout), "thread");
                    string type = Str(Map(thread, "status"), "type");
                    if (type == "active")
                    {
                        snapshot.Active++;
                        lock (stateGate) { string turn; if (activeTurns.TryGetValue(threadId, out turn)) snapshot.Turns[threadId] = turn; }
                    }
                    else if (type != "idle" && type != "notLoaded") throw new IOException("unknownThreadState");
                    cursor = null; cursors.Clear();
                    for (int page = 0; ; page++)
                    {
                        if (page >= 100) throw new IOException("paginationLimit");
                        var terminals = Rpc("thread/backgroundTerminals/list", new { threadId = threadId, cursor = cursor, limit = 100 }, clock, timeout);
                        var items = ArrayOf(terminals, "data"); if (items == null) throw new IOException("runtimeQueryInvalid");
                        snapshot.Background += items.Length; // Never inspect command, cwd, or output fields.
                        cursor = Str(terminals, "nextCursor"); if (cursor == null) break;
                        if (!cursors.Add(cursor)) throw new IOException("paginationLoop");
                    }
                }
                lock (stateGate)
                {
                    snapshot.Pending = clientRequests.Count + pendingServerRequests.Count + inFlightForwards;
                    snapshot.Background += spawnedProcesses.Count;
                    snapshot.Known = protocolHealthy && snapshot.Sequence == activitySequence;
                    snapshot.Reason = snapshot.Known ? null : "runtimeChangedDuringQuery";
                    snapshot.Idle = snapshot.Known && snapshot.Active == 0 && snapshot.Pending == 0 && snapshot.Background == 0;
                }
            }
            catch { snapshot.Known = false; snapshot.Idle = false; snapshot.Reason = "runtimeQueryUnavailable"; }
            return snapshot;
        }

        private object Result(Snapshot snapshot)
        {
            lock (stateGate) return new { protocolVersion = 1, instanceId = InstanceId, generation = InstanceId, state = state,
                known = snapshot.Known, idle = snapshot.Idle, transportReady = TransportReady, activeTurns = snapshot.Active, pendingRequests = snapshot.Pending,
                backgroundTerminals = snapshot.Background, backendPid = backendPid, backendStartedUtc = backendStartedUtc,
                ownerAlive = OwnerAlive(), controlEnabled = ControlEnabled(), requiresReload = state == "quiesced", resumeRequested = resumeRequested,
                reason = snapshot.Reason ?? reason };
        }

        private Dictionary<string, object> ContinuationThread(string threadId, Stopwatch clock, int timeout)
        {
            var thread = Map(Rpc("thread/read", new { threadId = threadId, includeTurns = false }, clock, timeout), "thread");
            if (thread == null || Str(thread, "id") != threadId) throw new IOException("Continuation thread identity changed.");
            return thread;
        }
        private Dictionary<string, object> ContinuationTurn(string threadId, Stopwatch clock, int timeout)
        {
            var page = Rpc("thread/turns/list", new { threadId = threadId, sortDirection = "desc", itemsView = "notLoaded", limit = 2 }, clock, timeout);
            var turns = ArrayOf(page, "data");
            if (turns == null || turns.Length == 0) return null;
            var latest = turns[0] as Dictionary<string, object>;
            if (latest == null || String.IsNullOrEmpty(Str(latest, "id"))) throw new IOException("Continuation turn identity unavailable.");
            return latest;
        }
        private static bool QuotaFailedTurn(Dictionary<string, object> turn)
        {
            if (Str(turn, "status") != "failed") return false;
            var error = Map(turn, "error"); object code;
            return error != null && error.TryGetValue("codexErrorInfo", out code) && code is string && (string)code == "usageLimitExceeded";
        }
        private static string ContinuationPolicies(Dictionary<string, object> resumed)
        {
            object approval, sandbox, reviewer, permissions;
            if (!resumed.TryGetValue("approvalPolicy", out approval) || approval == null ||
                !resumed.TryGetValue("sandbox", out sandbox) || sandbox == null ||
                !resumed.TryGetValue("approvalsReviewer", out reviewer) || reviewer == null)
                throw new IOException("Continuation permissions unavailable.");
            resumed.TryGetValue("activePermissionProfile", out permissions);
            return Encode(new { approvalPolicy = approval, approvalsReviewer = reviewer, sandbox = sandbox, permissions = permissions });
        }
        private static bool InactiveContinuationGoal(Dictionary<string, object> response, string threadId)
        {
            object raw;
            if (response == null || !response.TryGetValue("goal", out raw)) return false;
            if (raw == null) return true;
            var goal = raw as Dictionary<string, object>;
            if (goal == null || Str(goal, "threadId") != threadId) return false;
            // Unknown states cannot prove the autonomous goal is stopped. A quota-
            // limited goal also needs an explicit pause/restore implementation.
            return new[] { "paused", "blocked", "budgetLimited", "complete" }.Contains(Str(goal, "status"));
        }
        private Dictionary<string, object> ContinuationContext(string threadId, Stopwatch clock, int timeout, string expectedFrontendContext = null)
        {
            // No overrides: the backend restores the thread's effective permission and
            // model settings. Never copy instructions, prompts or tool output into a ticket.
            var resumed = Rpc("thread/resume", new { threadId = threadId, excludeTurns = true }, clock, timeout);
            var thread = Map(resumed, "thread");
            if (thread == null || Str(thread, "id") != threadId) throw new IOException("Continuation resume changed thread identity.");
            string cwd = Str(resumed, "cwd"), model = Str(resumed, "model"), provider = Str(resumed, "modelProvider");
            if (String.IsNullOrWhiteSpace(cwd) || !Path.IsPathRooted(cwd) || String.IsNullOrWhiteSpace(model) || String.IsNullOrWhiteSpace(provider))
                throw new IOException("Continuation settings unavailable.");
            var disabledPlugins = ArrayOf(resumed, "disabledPluginIds");
            if (disabledPlugins == null || disabledPlugins.Length != 0)
                throw new IOException("Continuation cannot replace saved disabled-plugin settings.");
            object collaboration;
            if (!resumed.TryGetValue("collaborationMode", out collaboration) || collaboration != null &&
                (!(collaboration is Dictionary<string, object>) || Str((Dictionary<string, object>)collaboration, "mode") != "default"))
                throw new IOException("Continuation requires the default collaboration mode.");
            var roots = ContinuationRuntimeRoots(resumed);
            string frontendSummary;
            lock (stateGate)
                if (!frontendContexts.TryGetValue(threadId, out frontendSummary))
                    throw new IOException("The actual frontend context has not been verified for continuation.");
            object environments;
            if (!thread.TryGetValue("environments", out environments))
                throw new IOException("The actual local environment is unavailable for continuation.");
            bool deferredEnvironment = false;
            if (expectedFrontendContext != null)
            {
                string expected = ContinuationAdmission.ValidateFrontendContextJson(expectedFrontendContext);
                if (ContinuationAdmission.FrontendBaseContextJson(frontendSummary) != ContinuationAdmission.FrontendBaseContextJson(expected))
                    throw new IOException("The actual frontend tools or instructions changed after restoration.");
                if (environments == null)
                {
                    // Environment selection is not persisted by the installed CLI.
                    // Its exact source selection must be bound by the one reserved
                    // turn/start; null here is not evidence that it already matches.
                    deferredEnvironment = true; frontendSummary = expected;
                }
                else frontendSummary = ContinuationAdmission.MergeFrontendEnvironment(frontendSummary, environments, cwd);
            }
            else
            {
                if (environments == null) throw new IOException("The actual local environment is unavailable for continuation.");
                frontendSummary = ContinuationAdmission.MergeFrontendEnvironment(frontendSummary, environments, cwd);
            }
            return new Dictionary<string, object> { { "threadId", threadId }, { "cwd", Path.GetFullPath(cwd) },
                { "model", model }, { "modelProvider", provider }, { "effort", Str(resumed, "reasoningEffort") },
                { "permissionsJson", ContinuationPolicies(resumed) }, { "runtimeWorkspaceRoots", roots },
                { "frontendContextJson", frontendSummary },
                { "deferredEnvironmentBinding", deferredEnvironment },
                { "rolloutPath", CanonicalContinuationRollout(Str(thread, "path"), true) } };
        }
        private static string CanonicalContinuationRollout(string value, bool allowMissing)
        {
            if (allowMissing && String.IsNullOrEmpty(value)) return null;
            return ContinuationAdmission.CanonicalRolloutPath(value);
        }
        private static string BoundContinuationRollout(Dictionary<string, object> metadata, Dictionary<string, object> context)
        {
            string read = CanonicalContinuationRollout(Str(metadata, "path"), true), resumed = Str(context, "rolloutPath");
            if (read == null && resumed == null || read != null && resumed != null && !String.Equals(read, resumed, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Continuation rollout identity is unavailable or changed.");
            return read ?? resumed;
        }
        private static string[] ContinuationRuntimeRoots(Dictionary<string, object> value)
        {
            object raw;
            if (value == null || !value.TryGetValue("runtimeWorkspaceRoots", out raw) || !(raw is object[]))
                throw new IOException("Continuation workspace roots unavailable.");
            var entries = (object[])raw;
            if (entries.Length > 64) throw new IOException("Continuation workspace roots exceed their bound.");
            var roots = new List<string>();
            foreach (object entry in entries)
            {
                string root = entry as string;
                if (String.IsNullOrWhiteSpace(root) || root.Length > 4096 || root.Any(Char.IsControl) || !Path.IsPathRooted(root))
                    throw new IOException("Continuation workspace root is invalid.");
                string prefix = Path.GetPathRoot(root);
                if (String.IsNullOrEmpty(prefix) || prefix.Length < 3 || prefix.EndsWith(":", StringComparison.Ordinal))
                    throw new IOException("Continuation workspace root is not fully qualified.");
                string full = Path.GetFullPath(root), volume = Path.GetPathRoot(full);
                roots.Add(full.Length == volume.Length ? full : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }
            return roots.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(root => root, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        private static void CheckContinuationLocal(Dictionary<string, object> thread, bool allowUnboundEnvironment = false)
        {
            object ephemeral, accepts;
            if (!thread.TryGetValue("ephemeral", out ephemeral) || !(ephemeral is bool) || (bool)ephemeral ||
                !String.IsNullOrEmpty(Str(thread, "parentThreadId"))) throw new IOException("Continuation requires a persistent root conversation.");
            if (thread.TryGetValue("canAcceptDirectInput", out accepts) && accepts is bool && !(bool)accepts)
                throw new IOException("Continuation input is unavailable.");
            string source = Str(thread, "source") ?? "";
            if (source.IndexOf("cloud", StringComparison.OrdinalIgnoreCase) >= 0) throw new IOException("Cloud continuation is unsupported.");
            object environments;
            if (!thread.TryGetValue("environments", out environments))
                throw new IOException("Continuation local environment is unavailable.");
            if (environments == null)
            {
                if (allowUnboundEnvironment) return;
                throw new IOException("Continuation local environment is unavailable.");
            }
            // Ordinary desktop chats select a single literal local environment.
            // This pure validator rejects remote/custom/multiple selections.
            ContinuationAdmission.MergeFrontendEnvironment(
                ContinuationAdmission.ProjectFrontendContext(new Dictionary<string, object>(), null), environments, Str(thread, "cwd"));
        }
        // Fixed, metadata-only operations for the same owned desktop backend. This
        // surface intentionally has no arbitrary RPC or user-message operation.
        internal Dictionary<string, object> HostContinuation(string method, Dictionary<string, object> parameters, int timeoutMs)
        {
            if (!options.EnableHostControl || !ControlEnabled()) throw new IOException("Continuation control is unavailable.");
            int timeout = Math.Max(100, Math.Min(15000, timeoutMs));
            lock (controlGate)
            {
                var clock = Stopwatch.StartNew();
                if (!TransportReady || state != "running" || frozen || eofAttempted) throw new IOException("Continuation backend is not running.");
                if (method == "capture")
                {
                    var snapshot = ReadSnapshot(clock, timeout);
                    if (!snapshot.Known) throw new IOException("Continuation work state is unknown.");
                    var threads = new Dictionary<string, Dictionary<string, object>>(); string cursor = null;
                    for (int page = 0; ; page++)
                    {
                        if (page >= 20) throw new IOException("Continuation inventory exceeds its bound.");
                        var loaded = Rpc("thread/loaded/list", new { cursor = cursor, limit = 100 }, clock, timeout);
                        var ids = ArrayOf(loaded, "data"); if (ids == null) throw new IOException("Continuation inventory unavailable.");
                        foreach (object id in ids)
                        {
                            if (!(id is string) || threads.ContainsKey((string)id)) throw new IOException("Continuation inventory changed.");
                            threads.Add((string)id, ContinuationThread((string)id, clock, timeout));
                        }
                        cursor = Str(loaded, "nextCursor"); if (cursor == null) break;
                    }
                    var active = threads.Where(t => Str(Map(t.Value, "status"), "type") == "active").ToArray();
                    var roots = active.Where(t => String.IsNullOrEmpty(Str(t.Value, "parentThreadId"))).ToArray();
                    string selected = null; Dictionary<string, object> latest = null;
                    if (roots.Length > 1) throw new IOException("More than one desktop conversation is active.");
                    if (roots.Length == 1) { selected = roots[0].Key; latest = ContinuationTurn(selected, clock, timeout); }
                    else
                    {
                        foreach (var entry in threads.Where(t => String.IsNullOrEmpty(Str(t.Value, "parentThreadId"))))
                        {
                            var turn = ContinuationTurn(entry.Key, clock, timeout);
                            if (!QuotaFailedTurn(turn)) continue;
                            if (selected != null) throw new IOException("More than one quota-failed conversation is loaded.");
                            selected = entry.Key; latest = turn;
                        }
                        if (selected == null)
                        {
                            if (active.Length != 0) throw new IOException("Active conversation root is unavailable.");
                            return new Dictionary<string, object> { { "captured", false } };
                        }
                    }
                    foreach (var running in active)
                    {
                        string at = running.Key; var seen = new HashSet<string>();
                        while (at != selected)
                        {
                            Dictionary<string, object> ancestor;
                            if (!seen.Add(at) || !threads.TryGetValue(at, out ancestor)) throw new IOException("Unrelated active conversation blocks continuation.");
                            at = Str(ancestor, "parentThreadId"); if (String.IsNullOrEmpty(at)) throw new IOException("Unrelated active conversation blocks continuation.");
                        }
                    }
                    CheckContinuationLocal(threads[selected]);
                    if (latest == null || Str(latest, "status") != "inProgress" && !QuotaFailedTurn(latest))
                        throw new IOException("Continuation target no longer needs interruption.");
                    var sourceAccount = Map(Rpc("account/read", new { refreshToken = false }, clock, timeout), "account");
                    if (sourceAccount == null || Str(sourceAccount, "type") != "chatgpt" ||
                        !String.Equals(Str(sourceAccount, "email"), Str(parameters, "expectedEmail"), StringComparison.OrdinalIgnoreCase))
                        throw new IOException("The source backend login differs from its credential file.");
                    var goal = Rpc("thread/goal/get", new { threadId = selected }, clock, timeout);
                    if (!InactiveContinuationGoal(goal, selected))
                        throw new IOException("Active goals must be paused before automatic continuation.");
                    var context = ContinuationContext(selected, clock, timeout);
                    context["rolloutPath"] = BoundContinuationRollout(threads[selected], context);
                    var confirmed = ContinuationTurn(selected, clock, timeout);
                    if (Str(confirmed, "id") != Str(latest, "id") || Str(confirmed, "status") != Str(latest, "status"))
                        throw new IOException("Continuation target changed while capturing.");
                    context["captured"] = true; context["turnId"] = Str(latest, "id"); context["originalTurnStatus"] = Str(latest, "status");
                    return context;
                }
                string threadId = Str(parameters, "threadId"), turnId = Str(parameters, "turnId");
                if (String.IsNullOrWhiteSpace(threadId) || String.IsNullOrWhiteSpace(turnId)) throw new IOException("Continuation target missing.");
                var metadata = ContinuationThread(threadId, clock, timeout); CheckContinuationLocal(metadata, true);
                var tail = ContinuationTurn(threadId, clock, timeout);
                if (tail == null || Str(tail, "id") != turnId) throw new IOException("Another turn intervened in the conversation.");
                string turnStatus = Str(tail, "status");
                bool terminal = turnStatus == "interrupted" || QuotaFailedTurn(tail);
                if (method == "stopped")
                {
                    var snapshot = ReadSnapshot(clock, timeout);
                    return new Dictionary<string, object> { { "stopped", terminal && snapshot.Known && snapshot.Idle },
                        { "turnStatus", turnStatus }, { "known", snapshot.Known }, { "activeTurns", snapshot.Active },
                        { "pendingRequests", snapshot.Pending }, { "backgroundTerminals", snapshot.Background } };
                }
                if (method != "prepare" || !terminal || Str(Map(metadata, "status"), "type") == "active")
                    throw new IOException("Continuation target has not stopped.");
                var policies = ContinuationContext(threadId, clock, timeout, Str(parameters, "frontendContextJson"));
                policies["rolloutPath"] = BoundContinuationRollout(metadata, policies);
                if (!String.Equals(Str(policies, "rolloutPath"), CanonicalContinuationRollout(Str(parameters, "rolloutPath"), false), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Continuation rollout path changed.");
                foreach (string key in new[] { "cwd", "model", "modelProvider", "effort", "permissionsJson", "frontendContextJson" })
                    if (!String.Equals(Str(policies, key) ?? "", Str(parameters, key) ?? "", StringComparison.Ordinal))
                        throw new IOException("Continuation model or permissions changed.");
                if (!ContinuationRuntimeRoots(policies).SequenceEqual(ContinuationRuntimeRoots(parameters), StringComparer.OrdinalIgnoreCase))
                    throw new IOException("Continuation workspace roots changed.");
                var account = Map(Rpc("account/read", new { refreshToken = false }, clock, timeout), "account");
                if (account == null || Str(account, "type") != "chatgpt" ||
                    !String.Equals(Str(account, "email"), Str(parameters, "expectedEmail"), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The restored backend account differs from the target.");
                var restoredGoal = Rpc("thread/goal/get", new { threadId = threadId }, clock, timeout);
                if (!InactiveContinuationGoal(restoredGoal, threadId))
                    throw new IOException("An automatic goal resumed before continuation.");
                bool modelAvailable = false; string modelCursor = null; var modelCursors = new HashSet<string>();
                for (int page = 0; ; page++)
                {
                    if (page >= 20) throw new IOException("Model inventory exceeds its bound.");
                    var list = Rpc("model/list", new { cursor = modelCursor, limit = 100, includeHidden = false }, clock, timeout);
                    var entries = ArrayOf(list, "data"); if (entries == null) throw new IOException("Target account model inventory unavailable.");
                    foreach (object entry in entries)
                    {
                        var model = entry as Dictionary<string, object>;
                        if (model != null && (Str(model, "id") == Str(policies, "model") || Str(model, "model") == Str(policies, "model"))) modelAvailable = true;
                    }
                    modelCursor = Str(list, "nextCursor"); if (modelCursor == null) break;
                    if (!modelCursors.Add(modelCursor)) throw new IOException("Model inventory cursor repeated.");
                }
                if (!modelAvailable) throw new IOException("The target account cannot use the original model.");
                var quiet = ReadSnapshot(clock, timeout);
                if (!quiet.Known || !quiet.Idle) throw new IOException("The restored backend is no longer idle.");
                policies["ready"] = true; policies["turnId"] = turnId; policies["originalTurnStatus"] = turnStatus;
                return policies;
            }
        }

        // The desktop supervisor owns this object and its input stream. These stages are never
        // exposed by the ordinary VS Code control pipe, and cannot authorize a foreign backend.
        internal Dictionary<string, object> HostControl(string method, int timeoutMs)
        {
            if (!options.EnableHostControl) throw new IOException("In-process control is unavailable.");
            int timeout = Math.Max(100, Math.Min(15000, timeoutMs));
            lock (controlGate)
            {
                var clock = Stopwatch.StartNew();
                Snapshot snapshot;
                if (method == "status") snapshot = ReadSnapshot(clock, timeout);
                else if (method == "confirmCommit") snapshot = ConfirmCommittedExit(clock, timeout);
                else if (method == "unprepare")
                {
                    lock (stateGate)
                    {
                        if (!expectedExit && !eofAttempted && state == "running") { frozen = false; preparedSnapshot = null; }
                    }
                    snapshot = ReadSnapshot(clock, timeout);
                }
                else if (method == "prepare")
                {
                    if (!ControlEnabled()) throw new IOException("Integration control authorization was revoked.");
                    lock (stateGate)
                    {
                        if (expectedExit || eofAttempted || state != "running") throw new IOException("Runtime cannot prepare new work.");
                        frozen = true; preparedSnapshot = null;
                    }
                    snapshot = ReadSnapshot(clock, timeout);
                    lock (stateGate)
                    {
                        if (snapshot.Known && snapshot.Idle && snapshot.Sequence == activitySequence && ControlEnabled()) preparedSnapshot = snapshot;
                        else { frozen = false; snapshot.Idle = false; }
                    }
                }
                else if (method == "commit" || method == "beginCommit")
                {
                    Snapshot prepared; bool alreadyCommitted;
                    lock (stateGate)
                    {
                        alreadyCommitted = eofAttempted;
                        prepared = preparedSnapshot;
                        if (!alreadyCommitted && (prepared == null || !frozen || !ControlEnabled())) throw new IOException("No valid prepared runtime.");
                    }
                    if (alreadyCommitted) return HostResult(ConfirmCommittedExit(clock, timeout));
                    // Read again under the barrier: background work can change without a new
                    // frontend request. The original prepare sequence must still match.
                    snapshot = ReadSnapshot(clock, timeout);
                    lock (stateGate)
                    {
                        if (!snapshot.Known || !snapshot.Idle || snapshot.Sequence != prepared.Sequence || activitySequence != prepared.Sequence || !ControlEnabled())
                        { snapshot.Known = false; snapshot.Idle = false; snapshot.Reason = "preparedRuntimeChanged"; return HostResult(snapshot); }
                    }
                    snapshot = CommitQuiet(snapshot, clock, timeout, true, method != "beginCommit");
                }
                else throw new IOException("Unsupported in-process operation.");
                return HostResult(snapshot);
            }
        }

        private Dictionary<string, object> HostResult(Snapshot snapshot)
        {
            var result = Parse(Encode(Result(snapshot)));
            lock (stateGate)
            {
                result["prepared"] = preparedSnapshot != null && frozen && !eofAttempted;
                result["preparedSequence"] = preparedSnapshot == null ? -1L : preparedSnapshot.Sequence;
                result["eofCommitted"] = eofAttempted;
            }
            return result;
        }

        private Snapshot CommitQuiet(Snapshot quiet, Stopwatch clock, int timeout, bool preserveBarrier, bool waitForExit = true)
        {
            lock (stateGate)
            {
                if (quiet.Sequence != activitySequence || !protocolHealthy || !ControlEnabled())
                {
                    if (!preserveBarrier) frozen = false;
                    quiet.Known = false; quiet.Idle = false; quiet.Reason = "runtimeChangedDuringQuery"; return quiet;
                }
                expectedExit = true; state = "quiescing";
                try { WriteCatalog(); }
                catch { expectedExit = false; if (!preserveBarrier) frozen = false; state = "running"; throw; }
            }
            bool closeCommitted = false;
            var close = Task.Factory.StartNew(() =>
            {
                int available = timeout - (int)clock.ElapsedMilliseconds;
                if (available <= 0 || !Monitor.TryEnter(backendWriteGate, available)) throw new IOException("Backend close deadline expired.");
                try
                {
                    lock (stateGate)
                    {
                        if (clock.ElapsedMilliseconds >= timeout || quiet.Sequence != activitySequence || !protocolHealthy || !ControlEnabled()) throw new IOException("Quiesce was cancelled before EOF.");
                        closeCommitted = true; eofAttempted = true;
                    }
                    backendInput.Close();
                    lock (stateGate) backendInputClosed = true;
                }
                finally { Monitor.Exit(backendWriteGate); }
            });
            int closeRemaining = Math.Max(0, timeout - (int)clock.ElapsedMilliseconds);
            bool closed = false;
            try { closed = close.Wait(closeRemaining); } catch { }
            if (!closed)
            {
                lock (stateGate)
                {
                    if (!closeCommitted) { expectedExit = false; if (!preserveBarrier) frozen = false; }
                    state = "unknown"; reason = "backendCloseUnavailable"; WriteCatalog();
                }
                return new Snapshot { Reason = "backendCloseUnavailable" };
            }
            return waitForExit ? ConfirmCommittedExit(clock, timeout) : new Snapshot { Reason = "backendExitPending" };
        }

        // Observe only the already committed EOF on this retained Process. This
        // cannot close stdin, send an RPC, unfreeze work or adopt a missing PID.
        private Snapshot ConfirmCommittedExit(Stopwatch clock, int timeout)
        {
            lock (stateGate)
                if (!expectedExit || !eofAttempted || !backendInputClosed || !frozen || !ControlEnabled())
                    return new Snapshot { Reason = "backendExitUnconfirmed" };
            int? observed = null;
            try
            {
                if (backendExitObserved) observed = ExitCode;
                else if (backend != null && (backend.HasExited || backend.WaitForExit(Math.Max(0, timeout - (int)clock.ElapsedMilliseconds))) && backend.HasExited)
                    observed = backend.ExitCode;
            }
            catch { if (backendExitObserved) observed = ExitCode; }
            lock (stateGate)
            {
                if (!ControlEnabled()) return new Snapshot { Reason = "backendExitUnconfirmed" };
                if (!observed.HasValue || observed.Value != 0)
                {
                    state = "unknown"; reason = observed.HasValue ? "backendExitFailed" : "backendExitTimeout"; WriteCatalog();
                    return new Snapshot { Reason = reason };
                }
                state = "quiesced"; reason = null; preparedSnapshot = null; WriteCatalog();
                return new Snapshot { Known = true, Idle = true };
            }
        }

        private object Control(string method, Dictionary<string, object> parameters)
        {
            int timeout = 5000; object rawTimeout;
            if (parameters != null && parameters.TryGetValue("timeoutMs", out rawTimeout))
            { int parsed; if (!Int32.TryParse(Convert.ToString(rawTimeout, CultureInfo.InvariantCulture), out parsed)) throw new IOException("Invalid timeout."); timeout = Math.Max(100, Math.Min(15000, parsed)); }
            if (method != "status" && Str(parameters, "expectedGeneration") != InstanceId) throw new IOException("Runtime generation does not match.");
            if ((method == "interrupt" || method == "quiesce") && !ControlEnabled()) throw new IOException("Integration control authorization was revoked.");
            lock (controlGate)
            {
                var clock = Stopwatch.StartNew();
                if (method == "status") return Result(ReadSnapshot(clock, timeout));
                if (method == "resume")
                {
                    lock (stateGate)
                    {
                        if (state != "quiesced" || BackendAlive()) throw new IOException("The managed backend has not quiesced.");
                        resumeRequested = true; WriteCatalog();
                    }
                    return Result(new Snapshot { Known = true, Idle = true });
                }
                if (method == "interrupt")
                {
                    var snapshot = ReadSnapshot(clock, timeout);
                    if (!snapshot.Known || snapshot.Turns.Count != snapshot.Active) { snapshot.Known = false; snapshot.Reason = "activeTurnIdentityUnavailable"; return Result(snapshot); }
                    foreach (var turn in snapshot.Turns)
                    { if (!ControlEnabled()) throw new IOException("Integration control authorization was revoked."); Rpc("turn/interrupt", new { threadId = turn.Key, turnId = turn.Value }, clock, timeout); }
                    do { snapshot = ReadSnapshot(clock, timeout); if (snapshot.Known && snapshot.Active == 0) break; Thread.Sleep(50); } while (clock.ElapsedMilliseconds < timeout);
                    return Result(snapshot);
                }
                if (method != "quiesce") throw new IOException("Unsupported controller method.");
                lock (stateGate) { if (state == "quiesced" && !BackendAlive()) return Result(new Snapshot { Known = true, Idle = true }); frozen = true; }
                var quiet = ReadSnapshot(clock, timeout);
                if (!quiet.Known || !quiet.Idle)
                { lock (stateGate) frozen = false; return Result(quiet); }
                return Result(CommitQuiet(quiet, clock, timeout, false));
            }
        }

        private void ServeControl()
        {
            while (!disposed)
            {
                try
                {
                    var security = new PipeSecurity();
                    security.SetAccessRuleProtection(true, false);
                    security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl, AccessControlType.Allow));
                    using (var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security))
                    {
                        lock (stateGate) { if (disposed) return; listeningPipe = pipe; }
                        pipe.WaitForConnection();
                        using (var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true))
                        using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true })
                        {
                            // One request per connection bounds ownership and avoids a client monopolizing the pipe.
                            var read = Task.Factory.StartNew(() => ReadLine(reader, 16384));
                            if (!read.Wait(3000)) continue;
                            object id = null; object response;
                            try
                            {
                                var request = Parse(read.Result); if (request == null || !request.TryGetValue("id", out id)) throw new IOException("Invalid controller request.");
                                string method = Str(request, "method");
                                if (!AuthorizedClient(pipe, method)) throw new IOException("Controller identity could not be verified.");
                                response = new { id = id, result = Control(method, Map(request, "params")) };
                            }
                            catch { response = new { id = id, error = new { code = -32071, message = "The managed runtime request could not be completed safely." } }; }
                            writer.WriteLine(Encode(response));
                        }
                    }
                }
                catch { if (!disposed) Thread.Sleep(100); }
            }
        }
        private void CloseBackendInput()
        { lock (backendWriteGate) { try { if (backendInput != null) backendInput.Close(); else if (backend != null) backend.StandardInput.Close(); } catch { } } }
        private bool ControlEnabled()
        {
            if (options.AuthorizationFile == null) return true; // Only in-process tests omit the authorization file.
            try
            {
                EnsureNoReparsePoint(Path.GetDirectoryName(options.AuthorizationFile));
                var info = new FileInfo(options.AuthorizationFile);
                if (!info.Exists || info.Length > 65536 || (info.Attributes & FileAttributes.ReparsePoint) != 0) return false;
                var config = Parse(File.ReadAllText(options.AuthorizationFile, Encoding.UTF8)); object enabled;
                return config != null && config.TryGetValue("Enabled", out enabled) && Object.Equals(enabled, true) &&
                    options.AuthorizationGeneration != null && Str(config, "AuthorizationGeneration") == options.AuthorizationGeneration;
            }
            catch { return false; }
        }
        private void WriteCatalog()
        {
            if (catalogPath == null || disposed) return;
            EnsureNoReparsePoint(Path.GetDirectoryName(catalogPath));
            var document = new { ProtocolVersion = 1, Kind = "vscode-stdio", InstanceId = InstanceId, Generation = InstanceId,
                PipeName = PipeName, WrapperPid = Process.GetCurrentProcess().Id, WrapperStartedUtc = wrapperStartedUtc,
                OwnerPid = ownerPid, OwnerStartedUtc = ownerStartedUtc, BackendPid = backendPid, BackendStartedUtc = backendStartedUtc,
                State = state, ResumeRequested = resumeRequested, UpdatedUtc = Utc(DateTime.UtcNow),
                CodexHome = Path.GetFullPath(Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")) };
            string temporary = catalogPath + ".tmp";
            File.WriteAllText(temporary, Encode(document), new UTF8Encoding(false));
            if (File.Exists(catalogPath)) File.Replace(temporary, catalogPath, null); else File.Move(temporary, catalogPath);
        }
        public void Dispose()
        {
            lock (stateGate)
            {
                if (disposed) return;
                disposed = true;
                try { if (listeningPipe != null) listeningPipe.Dispose(); } catch { }
                if (catalogPath != null && File.Exists(catalogPath)) File.Delete(catalogPath);
            }
            if (backend != null && !BackendAlive() && (backendReaderTask == null || backendReaderTask.IsCompleted)) backend.Dispose();
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint processId);
        private bool AuthorizedClient(NamedPipeServerStream pipe, string method)
        {
            try
            {
                uint id;
                if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out id) || id == 0) return false;
                using (var process = Process.GetProcessById(checked((int)id)))
                {
                    string started = Utc(process.StartTime);
                    if (method == "status" && id == ownerPid && started == ownerStartedUtc && OwnerAlive()) return true;
                    string executable = Path.GetFullPath(process.MainModule.FileName);
                    bool controller = options.ControllerExecutable != null && String.Equals(executable, Path.GetFullPath(options.ControllerExecutable), StringComparison.OrdinalIgnoreCase);
                    bool helper = method == "status" && options.HelperExecutable != null && String.Equals(executable, Path.GetFullPath(options.HelperExecutable), StringComparison.OrdinalIgnoreCase);
                    return (controller || helper) && StartTime(checked((int)id)) == started;
                }
            }
            catch { return false; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessBasicInformation
        { public IntPtr Reserved1, PebBaseAddress, Reserved2, Reserved3, UniqueProcessId, InheritedFromUniqueProcessId; }
        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(IntPtr process, int informationClass, ref ProcessBasicInformation info, int length, out int returned);
        private static int ParentProcessId()
        {
            try
            {
                var info = new ProcessBasicInformation(); int returned;
                using (var current = Process.GetCurrentProcess())
                    if (NtQueryInformationProcess(current.Handle, 0, ref info, Marshal.SizeOf(info), out returned) == 0)
                        return checked((int)info.InheritedFromUniqueProcessId.ToInt64());
            }
            catch { }
            return 0;
        }
    }
}
