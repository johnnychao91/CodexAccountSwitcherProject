using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CodexAccountSwitcher
{
    internal static class DesktopHostProgram
    {
        public static int Main(string[] args)
        {
            string validatedDirectory = null;
            try
            {
                if (args.Length != 2 || (args[0] != "--launch" && args[0] != "--inside-package")) return 2;
                string path = Path.GetFullPath(args[1]); var spec = DesktopSessionFiles.ReadSpecification(path);
                DesktopRuntimeHost.ValidateSpecification(spec, path); validatedDirectory = spec.Directory;
                if (args[0] == "--launch")
                {
                    spec.BootstrapHost = DesktopNative.Identity(Process.GetCurrentProcess().Id);
                    if (spec.BootstrapHost == null) throw new IOException("Desktop bootstrap identity unavailable.");
                    DesktopSessionFiles.Write(path, spec);
                    string script = Path.Combine(Path.GetDirectoryName(spec.HostExecutable), "desktop-start.ps1");
                    ManagedBridge.EnsureNoReparsePoint(Path.GetDirectoryName(script)); if (!File.Exists(script)) throw new IOException("Desktop bootstrap missing.");
                    string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
                    var start = new ProcessStartInfo(powershell, "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File " + ManagedBridge.QuoteArgument(script) + " -Specification " + ManagedBridge.QuoteArgument(path))
                    { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = spec.Directory };
                    using (var bootstrap = Process.Start(start))
                    {
                        // This outer bootstrap owns no App/backend. Its normal exit never ends the durable host.
                        if (!bootstrap.WaitForExit(20000)) return 3;
                        if (bootstrap.ExitCode != 0) throw new IOException("Windows package launch refused.");
                    }
                    return 0;
                }
                if (DesktopNative.CurrentPackageFullName() != spec.PackageFullName) throw new IOException("Desktop package identity mismatch.");
                using (var host = new DesktopRuntimeHost(spec)) host.Run();
                return 0;
            }
            catch (Exception error)
            {
                // Only a previously validated private directory may receive a content-free error marker.
                if (validatedDirectory != null)
                    try { DesktopSessionFiles.Write(Path.Combine(validatedDirectory, "launch-error.json"), new { failed = true, code = error.GetType().Name }); } catch { }
                return 1;
            }
        }
    }

    internal sealed class DesktopProtocolFeed : TextReader
    {
        private readonly BlockingCollection<string> lines = new BlockingCollection<string>(256);
        private string current; private int index;
        internal bool Ended;
        internal void Send(string line)
        {
            if (line == null || line.Length > 32 * 1024 * 1024 || Ended || !lines.TryAdd(line + "\n", 2000)) throw new IOException("Desktop backend input unavailable.");
        }
        internal void End() { if (!Ended) { Ended = true; lines.CompleteAdding(); } }
        public override int Read()
        {
            while (current == null || index >= current.Length)
            { if (!lines.TryTake(out current, Timeout.Infinite)) return -1; index = 0; }
            return current[index++];
        }
    }

    internal sealed class DesktopProtocolOutput : TextWriter
    {
        internal Action<string> Send;
        public override Encoding Encoding { get { return new UTF8Encoding(false); } }
        public override void WriteLine(string value) { var send = Send; if (send != null) send(value); }
    }

    internal sealed class DesktopStatusQuery<T>
    {
        private readonly object gate = new object();
        private Task<T> flight;

        internal Task<T> GetStatus(Func<T> query)
        {
            if (query == null) throw new ArgumentNullException("query");
            lock (gate)
            {
                // Only simultaneous readers share work. A completed or failed
                // observation must never substitute for a new status request.
                if (flight == null || flight.IsCompleted) flight = Task.Run(query);
                return flight;
            }
        }
        internal T Execute(string method, Func<string, T> control)
        {
            if (control == null) throw new ArgumentNullException("control");
            // Prepare/commit/unprepare retain their independent, fresh checks.
            // Sharing those operations could conceal a new turn or a mutation.
            if (method != "status") return control(method);
            return GetStatus(() => control("status")).GetAwaiter().GetResult();
        }
    }

    internal sealed class DesktopBridgeMember : IDesktopSessionMember, IDesktopSessionBoundedCommit
    {
        internal readonly ManagedBridge Bridge;
        internal readonly DesktopProtocolFeed Feed;
        internal readonly DesktopWebSocketConnection Connection;
        internal readonly Task Running;
        internal DesktopProcessIdentity Backend;
        internal Dictionary<string, object> Last;
        internal volatile bool CommitConfirmed, OwnerExitCleanup, UnexpectedExit;
        internal volatile bool ContinuationOwner;
        internal bool HasVerifiedAppBroker;
        internal readonly string AppToolsPipePath;
        private readonly DesktopStatusQuery<Dictionary<string, object>> statusQuery = new DesktopStatusQuery<Dictionary<string, object>>();
        private readonly object completionGate = new object();
        private volatile bool disconnected;
        public bool Disconnected { get { return disconnected; } }
        internal bool BackendExited { get { return Running.Status == TaskStatus.RanToCompletion && Bridge.BackendExitObserved; } }
        public bool Exited0 { get { return (CommitConfirmed || OwnerExitCleanup) && BackendExited && Bridge.ExitCode == 0 && Backend != null && !DesktopNative.Same(Backend); } }
        internal DesktopBridgeMember(DesktopHostSpecification spec, DesktopPackageTarget target, DesktopWebSocketConnection connection, int generation)
            : this(spec, target, connection, generation, null) { }
        internal DesktopBridgeMember(DesktopHostSpecification spec, DesktopPackageTarget target, DesktopWebSocketConnection connection, int generation, string[] fixtureBackendArguments)
            : this(spec, target, connection, generation, fixtureBackendArguments, null) { }
        internal DesktopBridgeMember(DesktopHostSpecification spec, DesktopPackageTarget target, DesktopWebSocketConnection connection, int generation, string[] fixtureBackendArguments, ContinuationAdmission admission)
            : this(spec, target, connection, generation, fixtureBackendArguments, admission, null) { }
        internal DesktopBridgeMember(DesktopHostSpecification spec, DesktopPackageTarget target, DesktopWebSocketConnection connection, int generation, string[] fixtureBackendArguments, ContinuationAdmission admission, Func<string, bool> verifyAppCommand)
            : this(spec, target, connection, generation, fixtureBackendArguments, admission, verifyAppCommand, null) { }
        internal DesktopBridgeMember(DesktopHostSpecification spec, DesktopPackageTarget target, DesktopWebSocketConnection connection, int generation, string[] fixtureBackendArguments, ContinuationAdmission admission, Func<string, bool> verifyAppCommand, string appToolsPipePath)
        {
            AppToolsPipePath = appToolsPipePath;
            Connection = connection; Feed = new DesktopProtocolFeed();
            var output = new DesktopProtocolOutput();
            output.Send = line =>
            {
                if (disconnected) return;
                try { using (var deadline = new CancellationTokenSource(10000)) Connection.SendText(line, deadline.Token); }
                catch { disconnected = true; }
            };
            Bridge = new ManagedBridge(new ManagedBridgeOptions { BackendExecutable = target.CliExecutable,
                BackendArguments = fixtureBackendArguments ?? DesktopRuntimeHost.ManagedBackendArguments(),
                CatalogDirectory = Path.Combine(spec.Directory, "members", generation.ToString()), ControllerExecutable = spec.ControllerExecutable,
                AuthorizationFile = spec.ConfigurationPath, AuthorizationGeneration = spec.AuthorizationGeneration,
                OwnerProcessId = Process.GetCurrentProcess().Id, EnableHostControl = true, DisableControlPipe = true,
                RequireInitializedNotification = false, Admission = admission, IsContinuationOwner = () => ContinuationOwner,
                VerifyAppCommand = verifyAppCommand,
                BackendEnvironment = fixtureBackendArguments == null ? DesktopRuntimeHost.ManagedBackendEnvironment(target, appToolsPipePath) : null }, Feed, output);
            Running = Task.Factory.StartNew(Bridge.Run, TaskCreationOptions.LongRunning);
        }
        private Dictionary<string, object> Control(string method)
        {
            return statusQuery.Execute(method, ReadControl);
        }
        private Dictionary<string, object> ReadControl(string method)
        { return ReadControl(method, method == "commit" ? 15000 : 7000); }
        private Dictionary<string, object> ReadControl(string method, int timeoutMs)
        {
            var result = Bridge.HostControl(method, timeoutMs);
            Last = result;
            if (result != null)
            {
                int pid = DesktopSessionFiles.Count(result, "backendPid"); string start = DesktopSessionFiles.Text(result, "backendStartedUtc");
                if (Backend == null && pid > 0)
                { var identity = DesktopNative.Identity(pid); if (identity != null && identity.StartedUtc == start) Backend = identity; }
            }
            // A late normal exit remains positive process evidence even after a
            // transaction deadline. Retire this member without declaring the
            // failed group transaction quiesced or applying another account.
            if (method == "status" && !CommitConfirmed && DesktopSessionFiles.Flag(result, "eofCommitted"))
                CompleteConfirmedCommit(Bridge.HostControl("confirmCommit", 100));
            if (Running.IsCompleted && !CommitConfirmed && !OwnerExitCleanup) UnexpectedExit = true;
            return result;
        }
        public Dictionary<string, object> Status() { return Control("status"); }
        internal void Forward(string line) { Bridge.HostForward(line); }
        public Dictionary<string, object> Prepare() { return Control("prepare"); }
        public void Unprepare() { Control("unprepare"); }
        public Dictionary<string, object> Commit()
        { return CommitWithDeadline(15000, 5000); }
        public Dictionary<string, object> CommitForShutdown(Func<bool> mayContinue, int budgetMs)
        {
            if (mayContinue == null || budgetMs < 100 || !mayContinue()) throw new OperationCanceledException();
            budgetMs = Math.Min(DesktopSessionTransaction.GracefulShutdownBudgetMs, budgetMs);
            var clock = Stopwatch.StartNew();
            // Begin once. Subsequent observations cannot write stdin or send an RPC.
            var result = ReadControl("beginCommit", Math.Min(7000, budgetMs));
            while (DesktopSessionFiles.Flag(result, "eofCommitted") &&
                (DesktopSessionFiles.Text(result, "reason") == "backendExitPending" || DesktopSessionFiles.Text(result, "reason") == "backendExitTimeout"))
            {
                int remaining = budgetMs - (int)clock.ElapsedMilliseconds;
                if (remaining < 100 || !mayContinue()) break;
                result = ReadControl("confirmCommit", Math.Min(1000, remaining));
            }
            CompleteConfirmedCommit(result);
            return result;
        }
        internal Dictionary<string, object> CommitWithDeadline(int timeoutMs, int confirmationMs)
        {
            var result = ReadControl("commit", timeoutMs);
            if ((!DesktopSessionFiles.Quiet(result) || DesktopSessionFiles.Text(result, "state") != "quiesced") && DesktopSessionFiles.Flag(result, "eofCommitted"))
                result = ReadControl("confirmCommit", confirmationMs);
            CompleteConfirmedCommit(result);
            return result;
        }
        private void CompleteConfirmedCommit(Dictionary<string, object> result)
        {
            if (!DesktopSessionFiles.Quiet(result) || DesktopSessionFiles.Text(result, "state") != "quiesced") return;
            lock (completionGate)
            {
                if (CommitConfirmed) return;
                CommitConfirmed = true;
                Feed.End(); Running.Wait(2000);
                // The App's stdio relay must receive a normal EOF only after this
                // owned backend has positively committed EOF/exit 0.
                if (!disconnected) Connection.Close(1000, "Account switch backend exited normally.");
            }
        }
        internal void MarkDisconnected() { disconnected = true; }
        internal void EndForConfirmedOwnerExit()
        { OwnerExitCleanup = true; disconnected = true; Feed.End(); }
    }

    internal sealed class DesktopContinuationRetirements
    {
        private readonly int capacity;
        private readonly Queue<string> order = new Queue<string>();
        private readonly Dictionary<string, DesktopContinuationCheckpoint> checkpoints = new Dictionary<string, DesktopContinuationCheckpoint>(StringComparer.Ordinal);
        internal DesktopContinuationRetirements(int capacity = 32)
        { if (capacity < 1 || capacity > 256) throw new ArgumentOutOfRangeException("capacity"); this.capacity = capacity; }
        internal bool IsReleased(DesktopContinuationCheckpoint checkpoint, string operationId)
        {
            DesktopContinuationCheckpoint previous;
            if (operationId == null || !checkpoints.TryGetValue(operationId, out previous)) return false;
            if (!DesktopRuntimeHost.SameContinuation(checkpoint, previous)) throw new IOException("Retired continuation binding changed.");
            return true;
        }
        internal void Remember(DesktopContinuationCheckpoint checkpoint, string operationId)
        {
            if (IsReleased(checkpoint, operationId)) return;
            // Never retain the caller's mutable object as an authorization proof.
            var immutable = Json.Decode<DesktopContinuationCheckpoint>(Json.Encode(checkpoint));
            checkpoints.Add(operationId, immutable); order.Enqueue(operationId);
            while (order.Count > capacity) checkpoints.Remove(order.Dequeue());
        }
    }

    internal sealed class DesktopContinuationNavigationClaims
    {
        private sealed class Attempt
        {
            internal int Count;
            internal long LastMilliseconds;
            internal bool Retired;
        }
        private readonly object gate = new object();
        private readonly Dictionary<string, Attempt> attempted = new Dictionary<string, Attempt>(StringComparer.Ordinal);
        private readonly Queue<string> order = new Queue<string>();
        private readonly Func<long> monotonicMilliseconds;
        internal DesktopContinuationNavigationClaims(Func<long> monotonicMilliseconds = null)
        {
            this.monotonicMilliseconds = monotonicMilliseconds ?? (() => (long)(Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency)));
        }
        private static string Key(string operation, DesktopProcessIdentity app)
        {
            Guid id; DateTimeOffset born;
            if (!Guid.TryParseExact(operation, "N", out id) || app == null || app.Pid <= 0 || String.IsNullOrEmpty(app.ExecutablePath) ||
                !DateTimeOffset.TryParse(app.StartedUtc, out born)) throw new IOException("Navigation operation binding is invalid.");
            return operation + "\n" + app.Pid + "\n" + born.UtcDateTime.Ticks + "\n" + app.ExecutablePath.ToUpperInvariant();
        }
        internal bool WasAttempted(string operation, DesktopProcessIdentity app)
        { lock (gate) { Attempt value; return attempted.TryGetValue(Key(operation, app), out value) && value.Count > 0; } }
        private bool Eligible(Attempt value, long now)
        {
            if (value == null) return true;
            if (value.Retired) throw new IOException("Original conversation navigation previously failed.");
            return value.Count < 8 && now >= value.LastMilliseconds && now - value.LastMilliseconds >= 2000;
        }
        internal bool CanAttempt(string operation, DesktopProcessIdentity app)
        {
            lock (gate) { Attempt value; attempted.TryGetValue(Key(operation, app), out value); return Eligible(value, monotonicMilliseconds()); }
        }
        private Attempt Add(string key)
        {
            var value = new Attempt(); attempted.Add(key, value); order.Enqueue(key);
            while (order.Count > 32) attempted.Remove(order.Dequeue()); return value;
        }
        internal bool TryClaim(string operation, DesktopProcessIdentity app)
        {
            lock (gate)
            {
                string key = Key(operation, app); Attempt value; attempted.TryGetValue(key, out value);
                long now = monotonicMilliseconds(); if (!Eligible(value, now)) return false;
                if (value == null) value = Add(key);
                value.Count++; value.LastMilliseconds = now; return true;
            }
        }
        internal void Retire(string operation, DesktopProcessIdentity app)
        {
            lock (gate) { string key = Key(operation, app); Attempt value; if (!attempted.TryGetValue(key, out value)) value = Add(key); value.Retired = true; }
        }
    }

    internal sealed class DesktopRuntimeHost : IDisposable
    {
        internal static string[] ManagedBackendArguments()
        {
            // Pinned desktop startup uses these fixed choices for both its chat
            // and account connections. Never accept arbitrary relay argv here.
            return new[] { "-c", "features.code_mode_host=true", "app-server", "--stdio", "--analytics-default-enabled",
                "-c", "plugins.codex-app-tools@openai-bundled.mcp_servers.codex_app.enabled=true" };
        }
        internal static void ConfigureRelayTransport(ProcessStartInfo start, Uri endpoint, string relayExecutable)
        {
            // This private URL belongs only to the verified stdio relay. Public
            // App transport settings must keep their ordinary per-host routing.
            start.EnvironmentVariables.Remove("CODEX_APP_SERVER_WS_URL");
            start.EnvironmentVariables.Remove("CODEX_APP_SERVER_FORCE_CLI");
            start.EnvironmentVariables["CAS_DESKTOP_RELAY_ENDPOINT"] = endpoint.AbsoluteUri;
            start.EnvironmentVariables["CODEX_CLI_PATH"] = relayExecutable;
        }
        internal static Dictionary<string, string> ManagedBackendEnvironment(DesktopPackageTarget target, string appToolsPipePath)
        {
            if (target == null || String.IsNullOrWhiteSpace(target.CliExecutable)) throw new IOException("The managed backend package is unavailable.");
            if (appToolsPipePath != null) DesktopNative.AppToolsPipeName(appToolsPipePath);
            string cli = Path.GetFullPath(target.CliExecutable), resources = Path.GetDirectoryName(cli);
            string node = Path.Combine(resources, "cua_node", "bin", "node.exe");
            foreach (string file in new[] { cli, node })
            {
                ManagedBridge.EnsureNoReparsePoint(Path.GetDirectoryName(file));
                if (!File.Exists(file) || (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("A verified bundled backend runtime is unavailable.");
            }
            return new Dictionary<string, string> { { "CODEX_APP_TOOLS_PIPE_PATH", appToolsPipePath }, { "CODEX_MCP_NODE_PATH", node },
                { "CODEX_BROWSER_USE_NODE_PATH", node }, { "CODEX_ELECTRON_RESOURCES_PATH", resources }, { "CODEX_CLI_PATH", cli },
                { "CODEX_INTERNAL_ORIGINATOR_OVERRIDE", "Codex Desktop" } };
        }
        internal static string ParseRelayGreeting(string line)
        {
            if (line == null || new UTF8Encoding(false, true).GetByteCount(line) > 2048) throw new IOException("Invalid managed desktop relay greeting.");
            var message = new JavaScriptSerializer { MaxJsonLength = 2048, RecursionLimit = 4 }.Deserialize<Dictionary<string, object>>(line);
            object path;
            if (message == null || message.Count != 3 || !message.ContainsKey("type") || !message.ContainsKey("version") ||
                !message.TryGetValue("appToolsPipePath", out path) || !Object.Equals(message["type"], "switcherDesktopRelay") ||
                !Object.Equals(message["version"], 1) || path != null && !(path is string))
                throw new IOException("Invalid managed desktop relay greeting.");
            if (path != null) DesktopNative.AppToolsPipeName((string)path);
            return (string)path;
        }
        internal static void RequireContinuationBroker(string frontendContextJson, bool hasVerifiedAppBroker)
        {
            string canonical = ContinuationAdmission.ValidateFrontendContextJson(frontendContextJson);
            var config = Json.Map(Json.Object(canonical), "config"); object value;
            var tools = config != null && config.TryGetValue("plugins.codex-app-tools@openai-bundled.mcp_servers.codex_app.enabled_tools", out value) ? value as System.Collections.IList : null;
            if (tools != null && tools.Count > 0 && !hasVerifiedAppBroker)
                throw new IOException("The conversation requires a verified managed App broker.");
        }
        internal static void RequireContinuationTarget(Account account, string expectedId, string expectedEmail)
        {
            if (account == null || String.IsNullOrEmpty(expectedId) || String.IsNullOrWhiteSpace(expectedEmail) ||
                account.Id != expectedId || !String.Equals(account.Email, expectedEmail, StringComparison.Ordinal))
                throw new IOException("The restored login identity differs from the target.");
        }
        private readonly DesktopHostSpecification spec;
        private readonly DesktopPackageTarget target;
        private readonly DesktopSessionTransaction transaction = new DesktopSessionTransaction();
        private readonly List<DesktopBridgeMember> members = new List<DesktopBridgeMember>();
        private readonly List<DesktopProcessIdentity> baseline;
        private readonly List<DesktopProcessIdentity> appRoots = new List<DesktopProcessIdentity>();
        private readonly Dictionary<string, Process> appHandles = new Dictionary<string, Process>();
        private readonly List<DesktopProcessIdentity> relayRoots = new List<DesktopProcessIdentity>();
        private readonly Dictionary<string, Process> relayHandles = new Dictionary<string, Process>();
        private readonly DesktopProcessIdentity identity;
        private readonly Func<List<ProcessBlocker>> inspectAtStart;
        private readonly CancellationTokenSource stopping = new CancellationTokenSource();
        private readonly string catalog, pipeName;
        private DesktopWebSocketServer server;
        private Process initialApp;
        private DesktopProcessIdentity app;
        private NamedPipeServerStream listening;
        private bool disposed, ownerExitCleanup, metadataHealthy = true, recoveryAccepted;
        private string lastReason;
        private DesktopContinuationCheckpoint continuationCheckpoint;
        private string continuationOperationId;
        private ContinuationAdmission continuationAdmission;
        private DesktopBridgeMember continuationSourceMember;
        private readonly DesktopContinuationNavigationClaims navigationClaims = new DesktopContinuationNavigationClaims();
        private readonly DesktopContinuationRetirements retiredContinuations = new DesktopContinuationRetirements();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool PeekNamedPipe(IntPtr pipe, IntPtr buffer, uint bytes, IntPtr read, IntPtr available, IntPtr remaining);

        internal static void ValidateSpecification(DesktopHostSpecification value, string path)
        {
            Guid id;
            if (value == null || value.ProtocolVersion != 1 || !Guid.TryParseExact(value.SessionId, "N", out id) ||
                DesktopSessionFiles.Canonical(Path.GetDirectoryName(path)) != DesktopSessionFiles.Canonical(value.Directory) || Path.GetFileName(value.Directory) != value.SessionId ||
                Path.GetFileName(path) != "launch.json" || String.IsNullOrEmpty(value.AuthorizationGeneration)) throw new IOException("Invalid desktop launch specification.");
            DesktopSessionFiles.VerifyPrivate(value.Directory, false);
            string ownExe; using (var own = Process.GetCurrentProcess()) ownExe = own.MainModule.FileName;
            if (!String.Equals(Path.GetFullPath(value.HostExecutable), ownExe, StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(Path.GetDirectoryName(Path.GetFullPath(value.ControllerExecutable)), Path.GetDirectoryName(ownExe), StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(value.ControllerExecutable) != "CodexAccountSwitcher.exe" || !File.Exists(value.ControllerExecutable)) throw new IOException("Desktop controller installation changed.");
            ManagedBridge.EnsureNoReparsePoint(Path.GetDirectoryName(value.ControllerExecutable));
            string relay = Path.Combine(Path.GetDirectoryName(ownExe), "DesktopAppRelay.exe");
            if (!File.Exists(relay) || (File.GetAttributes(relay) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The desktop App relay is unavailable.");
            string expectedConfig = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(value.Directory)), "config.json");
            if (!String.Equals(Path.GetFullPath(value.ConfigurationPath), Path.GetFullPath(expectedConfig), StringComparison.OrdinalIgnoreCase)) throw new IOException("Desktop configuration path changed.");
            var config = DesktopSessionFiles.ReadConfiguration(value.ConfigurationPath);
            if (!ConfigurationAuthorizes(config, value)) throw new IOException("Desktop authorization or local Work policy changed.");
            var actual = DesktopPackageTarget.Resolve(value.AppExecutable);
            DesktopSessionFiles.ValidateLocalWorkCliOverride(Environment.GetEnvironmentVariable("CODEX_TPP_LOCAL_EXECUTOR_CLI_PATH"), actual.CliExecutable, value.BlockLocalWork);
            if (actual.PackageFullName != value.PackageFullName || actual.PackageFamilyName != value.PackageFamilyName || actual.AppId != value.AppId) throw new IOException("Desktop package changed.");
            if (value.BlockLocalWork) DesktopLocalWorkPolicy.ValidateTarget(actual);
            using (var current = Process.GetCurrentProcess())
                if (!DesktopSessionFiles.ValidBootstrap(value.BootstrapHost, value.HostExecutable, current.SessionId)) throw new IOException("Desktop bootstrap identity changed.");
            foreach (string profilePath in new[] { value.UiProfile, value.SqliteHome })
                if (!String.IsNullOrEmpty(profilePath) && !Path.IsPathRooted(profilePath)) throw new IOException("Desktop data location is not absolute.");
            if (!String.IsNullOrEmpty(value.FixtureProfileRoot))
            {
                if (!String.Equals(DesktopSessionFiles.Canonical(value.FixtureProfileRoot), DesktopSessionFiles.Canonical(Path.Combine(value.Directory, "fixture-profile")), StringComparison.OrdinalIgnoreCase)) throw new IOException("Fixture profile must be private to the session.");
                DesktopSessionFiles.VerifyPrivate(value.FixtureProfileRoot, false);
                if (!DesktopSessionFiles.Canonical(value.Home).StartsWith(DesktopSessionFiles.Canonical(value.Directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Fixture home must be private to the session.");
                DesktopSessionFiles.VerifyPrivate(value.Home, false);
                if (!String.IsNullOrEmpty(value.FixtureProxyUri))
                {
                    Uri proxy;
                    if (!Uri.TryCreate(value.FixtureProxyUri, UriKind.Absolute, out proxy) || proxy.Scheme != "http" || proxy.Host != "127.0.0.1" || proxy.Port < 1024 || proxy.UserInfo.Length != 0 || proxy.Query.Length != 0 || proxy.Fragment.Length != 0 || proxy.AbsolutePath != "/") throw new IOException("Fixture proxy must be a local rejection server.");
                }
            }
            else if (!String.IsNullOrEmpty(value.FixtureProxyUri)) throw new IOException("Fixture proxy requires an explicit private fixture profile.");
        }
        internal DesktopRuntimeHost(DesktopHostSpecification spec) : this(spec, ProcessInspector.Inspect, false) { }
        // Only a separately compiled fixture entry point injects this guard. Production Main never calls it.
        internal DesktopRuntimeHost(DesktopHostSpecification spec, Func<List<ProcessBlocker>> fixtureGuard) : this(spec, fixtureGuard, true) { }
        private DesktopRuntimeHost(DesktopHostSpecification spec, Func<List<ProcessBlocker>> inspector, bool fixture)
        {
            if (inspector == null || fixture && String.IsNullOrEmpty(spec.FixtureProfileRoot)) throw new IOException("Explicit private fixture configuration required.");
            inspectAtStart = inspector;
            this.spec = spec; target = DesktopPackageTarget.Resolve(spec.AppExecutable); catalog = Path.Combine(spec.Directory, "session.json");
            if (File.Exists(catalog)) throw new IOException("Existing desktop transaction must not be replayed.");
            identity = DesktopNative.Identity(Process.GetCurrentProcess().Id); if (identity == null) throw new IOException("Unable to bind desktop host identity.");
            baseline = DesktopNative.Snapshot(); pipeName = "CodexAccountSwitcher.desktop." + spec.SessionId;
            transaction.Enabled = Enabled; transaction.AppGone = ConfirmedAppGone; transaction.Shutdown = ShutdownApp;
            transaction.Freeze = () => { if (server != null) server.Freeze(); };
            transaction.Unfreeze = () => { if (server != null) server.Unfreeze(); };
            transaction.Persist = Persist;
        }
        internal static bool ConfigurationAuthorizes(DesktopIntegrationConfiguration config, DesktopHostSpecification value)
        {
            return config != null && value != null && config.ProtocolVersion == 1 && config.Enabled && config.ResolveControlRequested() &&
                !String.IsNullOrEmpty(value.AuthorizationGeneration) && config.AuthorizationGeneration == value.AuthorizationGeneration &&
                config.BlockLocalWork == value.BlockLocalWork &&
                String.Equals(DesktopSessionFiles.Canonical(config.Home), DesktopSessionFiles.Canonical(value.Home), StringComparison.OrdinalIgnoreCase);
        }
        internal static void ConfigureIndependentLocalWorkCli(ProcessStartInfo start, string verifiedCliExecutable, bool blockLocalWork)
        {
            if (start == null) throw new ArgumentNullException("start");
            // The dedicated block policy owns its private refusal path. With
            // Local Work allowed, keep this independent consumer off the App's
            // JSON-RPC relay while preserving an explicit user override.
            string configured = start.EnvironmentVariables["CODEX_TPP_LOCAL_EXECUTOR_CLI_PATH"];
            DesktopSessionFiles.ValidateLocalWorkCliOverride(configured, verifiedCliExecutable, blockLocalWork);
            if (blockLocalWork || !String.IsNullOrWhiteSpace(configured)) return;
            if (String.IsNullOrWhiteSpace(verifiedCliExecutable) || !Path.IsPathRooted(verifiedCliExecutable))
                throw new IOException("Verified Local Work CLI is unavailable.");
            start.EnvironmentVariables["CODEX_TPP_LOCAL_EXECUTOR_CLI_PATH"] = Path.GetFullPath(verifiedCliExecutable);
        }
        internal static bool VerifyDisabledAppCommand(string command, DesktopProcessIdentity primaryApp, string verifiedAppExecutable)
        {
            try
            {
                if (primaryApp == null || !DesktopNative.Same(primaryApp) || String.IsNullOrWhiteSpace(command) || command.Length > 4096 || command.Any(Char.IsControl) ||
                    !Path.IsPathRooted(command) || !String.Equals(primaryApp.ExecutablePath, verifiedAppExecutable, StringComparison.OrdinalIgnoreCase)) return false;
                string root = Path.GetPathRoot(command);
                return !String.IsNullOrEmpty(root) && root.Length >= 3 && !root.EndsWith(":", StringComparison.Ordinal) &&
                    String.Equals(Path.GetFullPath(command), Path.GetFullPath(verifiedAppExecutable), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
        private bool Enabled()
        {
            try
            {
                var config = DesktopSessionFiles.ReadConfiguration(spec.ConfigurationPath);
                if (config.BlockLocalWork != spec.BlockLocalWork) lastReason = "localWorkPolicyChanged";
                else if (lastReason == "localWorkPolicyChanged") lastReason = null;
                return ConfigurationAuthorizes(config, spec);
            }
            catch { return false; }
        }
        private string Key(DesktopProcessIdentity value) { return value.Pid + ":" + value.StartedUtc; }
        private bool TrackApp(DesktopProcessIdentity value)
        {
            if (value == null || !DesktopNative.Same(value)) return false;
            string key = Key(value); if (appHandles.ContainsKey(key)) return true;
            try
            {
                var process = Process.GetProcessById(value.Pid); var ignored = process.Handle;
                if (!DesktopNative.Same(value) || process.StartTime.ToUniversalTime().ToString("o") != value.StartedUtc) { process.Dispose(); return false; }
                appHandles.Add(key, process); appRoots.Add(value); return true;
            }
            catch { return false; }
        }
        private string RelayExecutable { get { return Path.Combine(Path.GetDirectoryName(spec.HostExecutable), "DesktopAppRelay.exe"); } }
        private bool TrackRelay(DesktopProcessIdentity value)
        {
            if (value == null || !String.Equals(value.ExecutablePath, RelayExecutable, StringComparison.OrdinalIgnoreCase) || !DesktopNative.Same(value)) return false;
            string key = Key(value); if (relayHandles.ContainsKey(key)) return true;
            if (baseline.Any(p => DesktopSessionFiles.IdentityEquals(p, value)) || !appRoots.Any(root => DesktopNative.IsDescendant(value, root))) return false;
            try
            {
                var process = Process.GetProcessById(value.Pid); var ignored = process.Handle;
                if (!DesktopNative.Same(value) || process.StartTime.ToUniversalTime().ToString("o") != value.StartedUtc) { process.Dispose(); return false; }
                relayHandles.Add(key, process); relayRoots.Add(value); return true;
            }
            catch { return false; }
        }
        private bool Authorize(DesktopWebSocketPeer peer)
        {
            lock (transaction.Admission)
            {
                if (disposed || transaction.Frozen || members.Count(m => !m.Exited0) >= 16 || members.Count >= 128 || app == null) return false;
                var actual = DesktopNative.Identity(peer.Pid);
                bool isApp = actual != null && String.Equals(actual.ExecutablePath, target.AppExecutable, StringComparison.OrdinalIgnoreCase);
                bool isRelay = actual != null && String.Equals(actual.ExecutablePath, RelayExecutable, StringComparison.OrdinalIgnoreCase);
                if (actual == null || actual.StartedUtc != peer.StartedUtc || !isApp && !isRelay ||
                    baseline.Any(b => DesktopSessionFiles.IdentityEquals(b, actual)) ||
                    !appRoots.Any(root => DesktopSessionFiles.IdentityEquals(actual, root) || DesktopNative.IsDescendant(actual, root))) return false;
                return isApp ? TrackApp(actual) : TrackRelay(actual);
            }
        }
        private void Connected(DesktopWebSocketConnection connection)
        {
            DesktopBridgeMember member = null;
            DesktopProcessIdentity primaryApp;
            lock (transaction.Admission)
            {
                if (transaction.Frozen || members.Count(m => !m.Exited0) >= 16 || members.Count >= 128 || !Authorize(connection.Peer))
                { connection.Close(1013, "Previous owned backend is not safely retired."); connection.Dispose(); return; }
                var peer = DesktopNative.Identity(connection.Peer.Pid);
                bool peerIsApp = peer != null && String.Equals(peer.ExecutablePath, target.AppExecutable, StringComparison.OrdinalIgnoreCase);
                if (peer == null || !(peerIsApp ? TrackApp(peer) : TrackRelay(peer))) { connection.Close(1008, "Owned App identity changed."); connection.Dispose(); return; }
                // The directly launched process is the primary App authority while it remains alive.
                // A child utility owning a socket never replaces a still-live main App as the RM target.
                if (peerIsApp && (app == null || !DesktopNative.Same(app))) app = peer;
                primaryApp = app;
            }
            string appToolsPipePath; DesktopProcessIdentity brokerOwner = null;
            try
            {
                using (var greetingDeadline = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token))
                {
                    greetingDeadline.CancelAfter(3000);
                    appToolsPipePath = ParseRelayGreeting(connection.ReadText(greetingDeadline.Token));
                }
                if (appToolsPipePath != null) brokerOwner = DesktopNative.VerifyAppToolsPipe(appToolsPipePath, primaryApp, target.AppExecutable);
            }
            catch
            {
                try { connection.Close(1008, "Managed desktop relay greeting could not be verified."); } catch { }
                connection.Dispose(); return;
            }
            lock (transaction.Admission)
            {
                if (transaction.Frozen || !Authorize(connection.Peer) || !DesktopNative.Same(primaryApp) || !DesktopSessionFiles.IdentityEquals(app, primaryApp) ||
                    brokerOwner != null && (!DesktopNative.Same(brokerOwner) || !TrackApp(brokerOwner)))
                { connection.Close(1008, "Managed App ownership changed during relay greeting."); connection.Dispose(); return; }
                // Creation and admission share the transaction lock; Freeze can never omit an in-flight backend.
                member = new DesktopBridgeMember(spec, target, connection, transaction.Generation + 1, null, continuationAdmission,
                    command => VerifyDisabledAppCommand(command, primaryApp, target.AppExecutable), appToolsPipePath);
                member.HasVerifiedAppBroker = brokerOwner != null;
                if (!transaction.Admit(member)) throw new InvalidOperationException("Desktop admission changed while locked.");
                members.Add(member);
                // A new independently initialized connection changes the group
                // proof. No previous chat-owner admission may survive that race.
                foreach (var item in members) item.ContinuationOwner = false;
                Persist();
            }
            try
            {
                while (!stopping.IsCancellationRequested)
                {
                    string line = connection.ReadText(stopping.Token); if (line == null) break;
                    var message = ManagedBridge.Parse(line); if (message == null) throw new IOException("Invalid desktop protocol.");
                    string method = DesktopSessionFiles.Text(message, "method");
                    // Existing backend callbacks must drain while transaction admission is frozen.
                    // The bridge validates and accounts for responses under its own state/lease locks.
                    if (method == null) { member.Forward(line); continue; }
                    if (!EnterFrontendAdmission(transaction, message, id => connection.SendText(ManagedBridge.Encode(new { id = id,
                        error = new { code = -32070, message = "Desktop account switch admission is frozen." } }), stopping.Token))) continue;
                    try
                    {
                        if (continuationAdmission != null && continuationAdmission.IsArmed && method == "thread/resume" &&
                            continuationCheckpoint != null && DesktopSessionFiles.Text(Json.Map(message, "params"), "threadId") == continuationCheckpoint.ThreadId &&
                            !member.ContinuationOwner)
                            foreach (var item in members) item.ContinuationOwner = false;
                        // Synchronous accounting removes the socket->queued-reader idle gap.
                        member.Forward(line);
                    }
                    finally { Monitor.Exit(transaction.Admission); }
                }
            }
            catch { lastReason = "desktopTransportDisconnected"; }
            finally
            {
                lock (transaction.Admission)
                {
                    member.MarkDisconnected();
                    if (!member.Exited0) { server.Freeze(); transaction.Frozen = true; transaction.Stage = "orphaned"; }
                    // A socket EOF never ends the backend's private input.
                    Persist();
                }
            }
        }
        private void StartListenerAndApp()
        {
            // The listener may accept immediately. Bind the launched App and finish
            // startup state before any connection can pass Authorize/Admit.
            lock (transaction.Admission) StartListenerAndAppLocked();
        }
        private void StartListenerAndAppLocked()
        {
            continuationSourceMember = null;
            continuationAdmission = continuationCheckpoint == null || continuationOperationId == null ? null :
                new ContinuationAdmission(continuationCheckpoint.ThreadId, continuationOperationId, continuationCheckpoint.Model,
                    continuationCheckpoint.Effort, continuationCheckpoint.PermissionsJson, continuationCheckpoint.Cwd, continuationCheckpoint.ModelProvider,
                    continuationCheckpoint.RuntimeWorkspaceRoots, continuationCheckpoint.RolloutPath, continuationCheckpoint.FrontendContextJson,
                    command => VerifyDisabledAppCommand(command, app, target.AppExecutable));
            byte[] random = new byte[32]; using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(random);
            string credential = Convert.ToBase64String(random).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            server = new DesktopWebSocketServer(credential, Authorize, Connected); server.Start();
            var start = new ProcessStartInfo(target.AppExecutable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(target.AppExecutable) };
            var keys = start.EnvironmentVariables.Keys.Cast<string>().ToArray();
            foreach (string key in keys)
                if (DesktopSessionFiles.EphemeralEnvironment(key) || !String.IsNullOrEmpty(spec.FixtureProfileRoot) && (key.StartsWith("CODEX_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("OPENAI_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("OTEL_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("ELECTRON_", StringComparison.OrdinalIgnoreCase))) start.EnvironmentVariables.Remove(key);
            start.EnvironmentVariables["CODEX_HOME"] = Path.GetFullPath(spec.Home);
            ConfigureRelayTransport(start, server.Endpoint, RelayExecutable);
            start.EnvironmentVariables["CAS_DESKTOP_RELAY_APP_EXE"] = target.AppExecutable;
            start.EnvironmentVariables["CAS_DESKTOP_RELAY_HOST_EXE"] = spec.HostExecutable;
            start.EnvironmentVariables["CAS_DESKTOP_RELAY_HOST_PID"] = identity.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture);
            start.EnvironmentVariables["CAS_DESKTOP_RELAY_HOST_STARTED_UTC"] = identity.StartedUtc;
            start.EnvironmentVariables["CAS_DESKTOP_RELAY_SESSION_DIRECTORY"] = spec.Directory;
            DesktopLocalWorkPolicy.Configure(start, target, spec.Directory, spec.BlockLocalWork);
            ConfigureIndependentLocalWorkCli(start, target.CliExecutable, spec.BlockLocalWork);
            if (!String.IsNullOrEmpty(spec.UiProfile)) start.EnvironmentVariables["CODEX_ELECTRON_USER_DATA_PATH"] = spec.UiProfile; else start.EnvironmentVariables.Remove("CODEX_ELECTRON_USER_DATA_PATH");
            if (!String.IsNullOrEmpty(spec.SqliteHome)) start.EnvironmentVariables["CODEX_SQLITE_HOME"] = spec.SqliteHome; else start.EnvironmentVariables.Remove("CODEX_SQLITE_HOME");
            if (!String.IsNullOrEmpty(spec.FixtureProfileRoot))
            {
                string profile = spec.FixtureProfileRoot;
                foreach (string child in new[] { "AppData\\Roaming", "AppData\\Local", "ui", "sqlite", "temp" }) DesktopSessionFiles.CreatePrivate(Path.Combine(profile, child));
                start.EnvironmentVariables["USERPROFILE"] = profile; start.EnvironmentVariables["HOME"] = profile;
                start.EnvironmentVariables["APPDATA"] = Path.Combine(profile, "AppData", "Roaming"); start.EnvironmentVariables["LOCALAPPDATA"] = Path.Combine(profile, "AppData", "Local");
                start.EnvironmentVariables["HOMEDRIVE"] = Path.GetPathRoot(profile).TrimEnd('\\'); start.EnvironmentVariables["HOMEPATH"] = profile.Substring(Path.GetPathRoot(profile).Length - 1);
                start.EnvironmentVariables["TEMP"] = Path.Combine(profile, "temp"); start.EnvironmentVariables["TMP"] = Path.Combine(profile, "temp");
                start.EnvironmentVariables["CODEX_ELECTRON_USER_DATA_PATH"] = Path.Combine(profile, "ui"); start.EnvironmentVariables["CODEX_SQLITE_HOME"] = Path.Combine(profile, "sqlite");
                start.Arguments = "--user-data-dir=" + ManagedBridge.QuoteArgument(Path.Combine(profile, "ui"));
                if (!String.IsNullOrEmpty(spec.FixtureProxyUri))
                {
                    start.Arguments += " --proxy-server=" + ManagedBridge.QuoteArgument(spec.FixtureProxyUri);
                    foreach (string key in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY" }) start.EnvironmentVariables[key] = spec.FixtureProxyUri;
                    start.EnvironmentVariables["NO_PROXY"] = "127.0.0.1,localhost";
                }
            }
            if (continuationCheckpoint != null && continuationOperationId != null)
                start.Arguments = (start.Arguments ?? "") + " " + ManagedBridge.QuoteArgument("codex://threads/" + continuationCheckpoint.ThreadId);
            // Preserve production preferences; remove only foreign session/control context.
            foreach (string key in Environment.GetEnvironmentVariables().Keys.Cast<string>().ToArray())
                if (DesktopSessionFiles.EphemeralEnvironment(key) || !String.IsNullOrEmpty(spec.FixtureProfileRoot) && (key.StartsWith("CODEX_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("OPENAI_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("OTEL_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("ELECTRON_", StringComparison.OrdinalIgnoreCase))) Environment.SetEnvironmentVariable(key, null);
            Environment.SetEnvironmentVariable("CODEX_HOME", Path.GetFullPath(spec.Home));
            Environment.SetEnvironmentVariable("CODEX_APP_SERVER_WS_URL", null); Environment.SetEnvironmentVariable("CODEX_APP_SERVER_FORCE_CLI", null);
            Environment.SetEnvironmentVariable("CODEX_ELECTRON_USER_DATA_PATH", spec.UiProfile);
            Environment.SetEnvironmentVariable("CODEX_SQLITE_HOME", spec.SqliteHome);
            if (!String.IsNullOrEmpty(spec.FixtureProfileRoot))
                foreach (string key in new[] { "USERPROFILE", "HOME", "APPDATA", "LOCALAPPDATA", "HOMEDRIVE", "HOMEPATH", "TEMP", "TMP", "CODEX_ELECTRON_USER_DATA_PATH", "CODEX_SQLITE_HOME" }) Environment.SetEnvironmentVariable(key, start.EnvironmentVariables[key]);
            if (!String.IsNullOrEmpty(spec.FixtureProxyUri))
                foreach (string key in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY" }) Environment.SetEnvironmentVariable(key, start.EnvironmentVariables[key]);
            initialApp = Process.Start(start); app = DesktopNative.Identity(initialApp.Id);
            if (app == null || !TrackApp(app)) throw new IOException("Launched desktop process identity unavailable.");
            transaction.Stage = "starting"; transaction.Frozen = false; transaction.EofCommitted = false; ownerExitCleanup = false; recoveryAccepted = false; Persist();
        }
        internal void Run()
        {
            // This last guard closes the first-enable/launch race without touching any existing process.
            if (inspectAtStart().Any(p => p.Pid != identity.Pid && !(spec.BootstrapHost != null && p.Pid == spec.BootstrapHost.Pid && DesktopNative.Same(spec.BootstrapHost)) &&
                (spec.FirstEnable || p.Kind != "VSCode" && p.Kind != "Integration")))
                throw new UserError("Existing Codex processes must be normally Quit before enabling.");
            StartListenerAndApp();
            Task.Factory.StartNew(ServeControl, TaskCreationOptions.LongRunning);
            while (!stopping.IsCancellationRequested)
            {
                try
                {
                    bool healthy = RefreshMemberStatus(transaction.Admission, transaction.Members);
                    lock (transaction.Admission)
                    {
                        metadataHealthy = healthy; RefreshActors();
                        if (OwnedAppGone() && ShouldCleanupOwnerExit(transaction.Stage, transaction.EofCommitted, Enabled()))
                        {
                            // Retirement concerns only this host's retained App/backend identities.
                            // A separately reopened App still blocks auth writes, but cannot keep
                            // an otherwise empty old host alive. Freeze admission before EOF.
                            server.Freeze(); transaction.Frozen = true;
                            ownerExitCleanup = true;
                            foreach (var member in members.Where(m => !m.Running.IsCompleted)) member.EndForConfirmedOwnerExit();
                            transaction.Stage = "ownerExitCleanup";
                            if (members.All(m => m.BackendExited))
                            {
                                // Nonzero or unexpected exit releases the supervisor, not a
                                // successful quiesce. Its snapshot remains unknown/non-idle.
                                if (members.Any(m => !m.Exited0)) lastReason = "ownerExitedWithUnconfirmedNormalBackendExit";
                                transaction.Stage = "exited"; Persist(); break;
                            }
                        }
                        else if (Enabled() && transaction.Stage == "orphaned") RetireIdleOrphan();
                        Persist();
                    }
                }
                catch { lock (transaction.Admission) { metadataHealthy = false; lastReason = "desktopMetadataUnavailable"; try { Persist(); } catch { } } }
                Thread.Sleep(500);
            }
        }
        internal static bool RefreshMemberStatus(object admission, IList<IDesktopSessionMember> source)
        {
            IDesktopSessionMember[] captured;
            lock (admission) captured = source.Where(m => !m.Exited0).ToArray();
            // A query can wait on many loaded threads. Frontend startup requests must
            // retain admission while this read-only work waits for backend replies.
            bool healthy = true;
            foreach (var member in captured)
                try { member.Status(); } catch { healthy = false; }
            return healthy;
        }
        internal static bool RejectFrozenFrontend(DesktopSessionTransaction transaction, Dictionary<string, object> message, Action<object> reject)
        {
            // Never wait for the long quiesce admission lock just to reject an
            // ordinary request: it would also prevent reading later callbacks.
            // New work still repeats this check under the admission lock.
            if (DesktopSessionFiles.Text(message, "method") == null || !transaction.Frozen) return false;
            object id; if (message.TryGetValue("id", out id)) reject(id);
            return true;
        }
        internal static bool EnterFrontendAdmission(DesktopSessionTransaction transaction, Dictionary<string, object> message, Action<object> reject)
        {
            if (RejectFrozenFrontend(transaction, message, reject)) return false;
            // Frozen may change immediately after that read. Do not queue an
            // unbounded Monitor.Enter ahead of later backend callback replies.
            while (!Monitor.TryEnter(transaction.Admission, 25))
                if (RejectFrozenFrontend(transaction, message, reject)) return false;
            try
            {
                if (!RejectFrozenFrontend(transaction, message, reject)) return true;
            }
            catch { Monitor.Exit(transaction.Admission); throw; }
            Monitor.Exit(transaction.Admission); return false;
        }
        private void RefreshActors()
        {
            foreach (var candidate in DesktopNative.Snapshot())
            {
                if (NeedsAncestryProof(candidate, target.AppExecutable, baseline, appRoots) && appRoots.Any(root => DesktopNative.IsDescendant(candidate, root))) TrackApp(candidate);
                else if (String.Equals(candidate.ExecutablePath, RelayExecutable, StringComparison.OrdinalIgnoreCase)) TrackRelay(candidate);
            }
        }
        internal static bool NeedsAncestryProof(DesktopProcessIdentity candidate, string executable, IList<DesktopProcessIdentity> before, IList<DesktopProcessIdentity> tracked)
        {
            // Snapshot already supplies a fresh user/session/creation identity. Baseline actors can
            // never become ours, and already-held identities do not need their ancestry reproved.
            return candidate != null && String.Equals(candidate.ExecutablePath, executable, StringComparison.OrdinalIgnoreCase) &&
                !before.Any(p => DesktopSessionFiles.IdentityEquals(p, candidate)) && !tracked.Any(p => DesktopSessionFiles.IdentityEquals(p, candidate));
        }
        internal static bool ShouldCleanupOwnerExit(string stage, bool eofCommitted, bool enabled)
        {
            if (stage == "closingApp" || stage == "committing") return false;
            // A transaction retains its independent control host for the reply and restoration.
            // Explicit revocation may end that host only after the owner and every backend are gone.
            return !enabled || !eofCommitted && stage != "quiesced";
        }
        private void RetireIdleOrphan()
        {
            var orphans = members.Where(m => !m.Exited0 && m.Disconnected).ToArray();
            if (orphans.Length == 0) return;
            foreach (var orphan in orphans)
            {
                if (!DesktopSessionFiles.Quiet(orphan.Last)) return;
                var prepared = orphan.Prepare();
                if (!DesktopSessionFiles.Quiet(prepared) || !DesktopSessionFiles.Flag(prepared, "prepared") || !Enabled()) { orphan.Unprepare(); return; }
                transaction.EofCommitted = true;
                var result = orphan.Commit();
                if (!DesktopSessionFiles.Quiet(result) || DesktopSessionFiles.Text(result, "state") != "quiesced" || !orphan.Exited0)
                { transaction.Stage = "recoveryRequired"; lastReason = "orphanEofUnconfirmed"; return; }
            }
            transaction.Frozen = false; transaction.EofCommitted = false;
            transaction.Stage = members.Any(m => !m.Exited0) ? "running" : "starting"; server.Unfreeze();
        }
        internal static bool OwnedAppsExited(Process initial, ICollection<Process> tracked)
        {
            if (initial == null || tracked == null || tracked.Count == 0) return false;
            try
            {
                return initial.HasExited && tracked.All(p => p != null && p.HasExited);
            }
            catch { return false; }
        }
        private bool OwnedAppGone() { return OwnedAppsExited(initialApp, appHandles.Values.Concat(relayHandles.Values).ToArray()); }
        private bool ConfirmedAppGone()
        {
            // Unlike host retirement, account switching also requires global App absence.
            return OwnedAppGone() && !DesktopNative.Snapshot().Any(p => String.Equals(p.ExecutablePath, target.AppExecutable, StringComparison.OrdinalIgnoreCase));
        }
        private DesktopShutdownResult ShutdownApp()
        {
            if (!Enabled()) return new DesktopShutdownResult { Refused = true, Reason = "authorizationRevoked" };
            RefreshActors();
            if (ConfirmedAppGone())
            {
                Process current; return app != null && appHandles.TryGetValue(Key(app), out current) && current.HasExited && current.ExitCode == 0 ?
                    new DesktopShutdownResult { Success = true, ExitCode = 0 } : new DesktopShutdownResult { Refused = true, Reason = "AppExit0Unconfirmed" };
            }
            if (app == null || !DesktopNative.Same(app)) return new DesktopShutdownResult { Refused = true, Reason = "AppIdentityChanged" };
            return DesktopNative.Shutdown(app);
        }
        internal static string PublicSnapshotStage(string transactionStage, bool completeQuiescence)
        { return transactionStage == "quiesced" && !completeQuiescence ? "recoveryRequired" : transactionStage; }
        private DesktopSessionState Snapshot()
        {
            var live = members.Where(m => !m.Exited0).ToArray();
            bool backendKnown = live.Length > 0 && live.All(m => !m.UnexpectedExit && DesktopSessionFiles.Flag(m.Last, "known"));
            bool allExited = members.Count > 0 && members.All(m => m.Exited0), gone = ConfirmedAppGone();
            bool quiesced = transaction.Stage == "quiesced" && metadataHealthy && allExited && gone;
            var actors = new List<DesktopProcessIdentity> { identity }; actors.AddRange(appRoots.Where(DesktopNative.Same));
            actors.AddRange(relayRoots.Where(DesktopNative.Same));
            actors.AddRange(members.Where(m => m.Backend != null && !m.Exited0).Select(m => m.Backend));
            int active = live.Sum(m => Math.Max(0, DesktopSessionFiles.Count(m.Last, "activeTurns"))), pending = live.Sum(m => Math.Max(0, DesktopSessionFiles.Count(m.Last, "pendingRequests"))), background = live.Sum(m => Math.Max(0, DesktopSessionFiles.Count(m.Last, "backgroundTerminals")));
            var failedBackend = members.LastOrDefault(m => m.BackendExited && !m.Exited0);
            bool known = failedBackend == null && metadataHealthy && (quiesced || backendKnown && (transaction.Stage == "running" || transaction.Stage == "prepared" || transaction.Stage == "orphaned"));
            bool transportReady = live.Length > 0 && live.All(m => !m.Disconnected && m.Bridge.TransportReady) && appRoots.Any(DesktopNative.Same);
            var uncertain = live.FirstOrDefault(m => !DesktopSessionFiles.Flag(m.Last, "known"));
            string backendReason = uncertain == null ? null : SafeBackendReason(DesktopSessionFiles.Text(uncertain.Last, "reason"));
            return new DesktopSessionState { SessionId = spec.SessionId, Generation = transaction.Generation, Host = identity, App = app,
                Home = Path.GetFullPath(spec.Home), PipeName = pipeName, OwnedActors = actors.GroupBy(Key).Select(g => g.First()).ToList(),
                Known = known, Idle = known && (quiesced || live.All(m => DesktopSessionFiles.Quiet(m.Last))) && active == 0 && pending == 0 && background == 0, TransportReady = transportReady, ControlEnabled = Enabled(), State = PublicSnapshotStage(transaction.Stage, quiesced),
                ActiveTurns = active, PendingRequests = pending, BackgroundTerminals = background, AllBackendsExited = allExited, AppExitConfirmed = gone,
                RecoveryAccepted = recoveryAccepted, OwnerExitCleanup = ownerExitCleanup, Reason = transaction.Stage == "quiesced" && !quiesced ? "desktopQuiescenceProofLost" : transaction.Reason ?? lastReason ?? backendReason,
                BackendExitCode = failedBackend == null ? (int?)null : failedBackend.Bridge.ExitCode };
        }
        private static string SafeBackendReason(string value)
        {
            return new[] { "initializing", "runtimeQueryUnavailable", "runtimeChangedDuringQuery", "backendUnavailable", "backendExitedUnexpectedly",
                "backendStreamFailed", "backendWriteFailed", "unrecognizedClientProtocol", "unrecognizedBackendProtocol", "duplicateClientRequestId",
                "unknownProcessHandle", "duplicateProcessHandle", "unknownTurn", "notificationDuringQuiesce", "catalogUnavailable", "initializeFailed" }.Contains(value) ? value : "desktopWorkStateUnknown";
        }
        private void Persist() { DesktopSessionFiles.Write(catalog, Snapshot()); }
        private bool AuthorizedClient(NamedPipeServerStream pipe, out DesktopProcessIdentity client)
        {
            client = null; uint pid;
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out pid)) return false;
            client = DesktopNative.Identity(checked((int)pid));
            return client != null && String.Equals(client.ExecutablePath, spec.ControllerExecutable, StringComparison.OrdinalIgnoreCase) && DesktopNative.Same(client);
        }
        internal static bool SameContinuation(DesktopContinuationCheckpoint left, DesktopContinuationCheckpoint right)
        {
            if (left == null || right == null) return false;
            Func<string, string, bool> text = (a, b) => String.Equals(a ?? "", b ?? "", StringComparison.Ordinal);
            Func<string, string, bool> path = (a, b) => String.IsNullOrEmpty(a) && String.IsNullOrEmpty(b) ||
                !String.IsNullOrEmpty(a) && !String.IsNullOrEmpty(b) && String.Equals(DesktopSessionFiles.Canonical(a), DesktopSessionFiles.Canonical(b), StringComparison.OrdinalIgnoreCase);
            Func<string, string, bool> rollout = (a, b) => String.IsNullOrEmpty(a) && String.IsNullOrEmpty(b) ||
                !String.IsNullOrEmpty(a) && !String.IsNullOrEmpty(b) && String.Equals(ContinuationAdmission.CanonicalRolloutPath(a), ContinuationAdmission.CanonicalRolloutPath(b), StringComparison.OrdinalIgnoreCase);
            return text(left.SessionId, right.SessionId) && left.Generation == right.Generation && text(left.ThreadId, right.ThreadId) &&
                text(left.TurnId, right.TurnId) && path(left.Home, right.Home) && path(left.UiProfile, right.UiProfile) && path(left.SqliteHome, right.SqliteHome) &&
                path(left.Cwd, right.Cwd) && rollout(left.RolloutPath, right.RolloutPath) && text(left.Model, right.Model) && text(left.ModelProvider, right.ModelProvider) &&
                text(left.Effort, right.Effort) && text(left.PermissionsJson, right.PermissionsJson) && text(left.FrontendContextJson, right.FrontendContextJson) &&
                text(left.OriginalTurnStatus, right.OriginalTurnStatus) &&
                SameContinuationRoots(left.RuntimeWorkspaceRoots, right.RuntimeWorkspaceRoots);
        }
        private static bool SameContinuationRoots(string[] left, string[] right)
        {
            if (left == null || right == null || left.Length > 64 || right.Length > 64) return false;
            try
            {
                Func<string[], string[]> canonical = roots => roots.Select(root =>
                {
                    if (String.IsNullOrWhiteSpace(root) || root.Length > 4096 || root.Any(Char.IsControl) || !Path.IsPathRooted(root))
                        throw new IOException("Invalid continuation workspace root.");
                    string prefix = Path.GetPathRoot(root);
                    if (String.IsNullOrEmpty(prefix) || prefix.Length < 3 || prefix.EndsWith(":", StringComparison.Ordinal))
                        throw new IOException("Continuation workspace root is not fully qualified.");
                    string full = Path.GetFullPath(root), volume = Path.GetPathRoot(full);
                    return full.Length == volume.Length ? full : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                }).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(root => root, StringComparer.OrdinalIgnoreCase).ToArray();
                return canonical(left).SequenceEqual(canonical(right), StringComparer.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
        private DesktopBridgeMember[] ContinuationMembers(int expectedGeneration)
        {
            lock (transaction.Admission)
            {
                var live = members.Where(m => !m.Exited0).ToArray();
                if (!Enabled() || transaction.Generation != expectedGeneration || transaction.Stage != "running" || transaction.Frozen ||
                    live.Length == 0 || live.Length > 16 || live.Any(m => m.Disconnected || !m.Bridge.TransportReady) || app == null || !DesktopNative.Same(app))
                    throw new IOException("Continuation requires a fully connected owned desktop backend group.");
                return live;
            }
        }
        private void RequireSameContinuationGroup(DesktopBridgeMember[] captured, int expectedGeneration)
        {
            if (!ContinuationMembers(expectedGeneration).SequenceEqual(captured))
                throw new IOException("The owned desktop backend group changed.");
        }
        private static void RequireIdleContinuationPeers(IEnumerable<DesktopBridgeMember> group, DesktopBridgeMember owner)
        {
            if (!IdleContinuationPeers(group, owner)) throw new IOException("Another owned desktop backend is busy or unknown.");
        }
        private static bool IdleContinuationPeers(IEnumerable<DesktopBridgeMember> group, DesktopBridgeMember owner)
        {
            foreach (var member in group)
                if (member != owner)
                    try { if (!DesktopSessionFiles.Quiet(member.Status())) return false; }
                    catch { return false; }
            return true;
        }
        private bool NavigateContinuation(DesktopBridgeMember[] group, int generation, DesktopContinuationCheckpoint checkpoint, string expectedId, string expectedEmail)
        {
            string operation = continuationOperationId; var primaryApp = app;
            if (operation == null || !navigationClaims.CanAttempt(operation, primaryApp)) return false;
            string[] paths = group.Where(member => member.HasVerifiedAppBroker && member.AppToolsPipePath != null)
                .Select(member => member.AppToolsPipePath).Distinct(StringComparer.Ordinal).ToArray();
            if (paths.Length == 0) return false;
            if (paths.Length != 1) throw new IOException("The owned App broker binding is ambiguous.");
            if (!IdleContinuationPeers(group, null)) return false;
            try { return DesktopNative.NavigateOriginalConversation(paths[0], primaryApp, target.AppExecutable, checkpoint.ThreadId, checkpoint.TurnId, navigationToken =>
            {
                lock (transaction.Admission)
                {
                    RequireSameContinuationGroup(group, generation);
                    if (!SameContinuation(checkpoint, continuationCheckpoint) || operation != continuationOperationId ||
                        !DesktopSessionFiles.IdentityEquals(primaryApp, app) || !DesktopNative.Same(primaryApp) ||
                        continuationAdmission == null || !continuationAdmission.IsArmed || continuationAdmission.WasClaimed)
                        throw new IOException("Original conversation navigation authority changed.");
                    string raw = Files.ReadAuth(Path.Combine(spec.Home, "auth.json"));
                    RequireContinuationTarget(raw == null ? null : Account.Parse(raw), expectedId, expectedEmail);
                    if (!IdleContinuationPeers(group, null) || group.Any(member => member.Bridge.HasFrontendResumedThread(checkpoint.ThreadId))) return false;
                    navigationToken.ThrowIfCancellationRequested();
                    if (!DesktopNative.Same(primaryApp)) throw new IOException("The navigation App owner changed.");
                    return navigationClaims.TryClaim(operation, primaryApp);
                }
            }); }
            catch { navigationClaims.Retire(operation, primaryApp); throw; }
        }
        internal static bool MayWaitForReadOnlyPeer(Dictionary<string, object> status, bool onlyReadOnlyPending)
        {
            return onlyReadOnlyPending && DesktopSessionFiles.Flag(status, "known") &&
                DesktopSessionFiles.Count(status, "activeTurns") == 0 && DesktopSessionFiles.Count(status, "backgroundTerminals") == 0 &&
                DesktopSessionFiles.Count(status, "pendingRequests") > 0;
        }
        internal static T UniqueContinuationOwner<T>(IEnumerable<T> group, Func<T, bool> proved) where T : class
        {
            var candidates = group.Where(proved).ToArray();
            if (candidates.Length > 1) throw new IOException("More than one owned backend loaded the continuation conversation.");
            return candidates.SingleOrDefault();
        }
        private DesktopContinuationCheckpoint ReadContinuationValue(Dictionary<string, object> request)
        {
            object raw; Guid thread;
            if (!request.TryGetValue("continuation", out raw)) throw new IOException("Continuation binding is missing.");
            var value = new JavaScriptSerializer().ConvertToType<DesktopContinuationCheckpoint>(raw);
            if (value == null || value.SessionId != spec.SessionId || value.Generation < 1 ||
                !Guid.TryParseExact(value.ThreadId, "D", out thread) || String.IsNullOrWhiteSpace(value.TurnId) || value.TurnId.Length > 200 ||
                !String.Equals(DesktopSessionFiles.Canonical(value.Home), DesktopSessionFiles.Canonical(spec.Home), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Continuation binding changed.");
            value.RolloutPath = ContinuationAdmission.CanonicalRolloutPath(value.RolloutPath);
            return value;
        }
        private DesktopContinuationCheckpoint ReadContinuation(Dictionary<string, object> request)
        {
            var value = ReadContinuationValue(request);
            if (!SameContinuation(value, continuationCheckpoint)) throw new IOException("Continuation binding changed.");
            return value;
        }
        internal static bool ReleaseContinuationBinding(DesktopContinuationCheckpoint requested, string operation,
            DesktopContinuationCheckpoint current, string currentOperation, IEnumerable<ContinuationAdmission> admissions,
            DesktopContinuationRetirements retired)
        {
            Guid identifier;
            if (!Guid.TryParseExact(operation, "N", out identifier)) throw new IOException("Continuation release operation is invalid.");
            if (retired.IsReleased(requested, operation)) return false;
            if (!SameContinuation(requested, current) || currentOperation != null && currentOperation != operation)
                throw new IOException("Continuation operation changed.");
            foreach (var admission in admissions)
                if (admission != null && admission.ReservedOperationId == operation) admission.Cancel(operation);
            retired.Remember(requested, operation);
            return true;
        }
        private object HandleContinuation(string method, int generation, Dictionary<string, object> request)
        {
            if (method == "continuationRelease")
            {
                lock (transaction.Admission)
                {
                    var requested = ReadContinuationValue(request); string operation = DesktopSessionFiles.Text(request, "operationId");
                    if (!ReleaseContinuationBinding(requested, operation, continuationCheckpoint, continuationOperationId,
                        members.Select(item => item.Bridge.ContinuationAdmission), retiredContinuations))
                        return new { released = true, alreadyReleased = true };
                    continuationCheckpoint = null; continuationOperationId = null;
                    continuationSourceMember = null;
                    return new { released = true, alreadyReleased = false };
                }
            }
            var group = ContinuationMembers(generation);
            if (method == "continuationCapture")
            {
                lock (transaction.Admission) if (continuationOperationId != null) throw new IOException("A continuation is already pending.");
                string sourceAuth = Files.ReadAuth(Path.Combine(spec.Home, "auth.json"));
                if (sourceAuth == null) throw new IOException("Continuation requires a saved ChatGPT login.");
                var sourceAccount = Account.Parse(sourceAuth);
                DesktopBridgeMember member = null; Dictionary<string, object> data = null;
                foreach (var candidate in group)
                {
                    var captured = candidate.Bridge.HostContinuation("capture", new Dictionary<string, object> { { "expectedEmail", sourceAccount.Email } }, 15000);
                    if (!DesktopSessionFiles.Flag(captured, "captured")) continue;
                    if (member != null) throw new IOException("More than one owned backend has a resumable conversation.");
                    member = candidate; data = captured;
                }
                if (member != null) RequireContinuationBroker(DesktopSessionFiles.Text(data, "frontendContextJson"), member.HasVerifiedAppBroker);
                bool waitingForReadOnlyPeers = false;
                foreach (var peer in group.Where(item => item != member))
                {
                    var peerStatus = peer.Status();
                    if (DesktopSessionFiles.Quiet(peerStatus)) continue;
                    if (!MayWaitForReadOnlyPeer(peerStatus, peer.Bridge.HasOnlyReadOnlyPendingRequests))
                        throw new IOException("Another owned desktop backend is busy or unknown.");
                    waitingForReadOnlyPeers = true;
                }
                string freshAuth = Files.ReadAuth(Path.Combine(spec.Home, "auth.json"));
                if (freshAuth == null || Account.Parse(freshAuth).Id != sourceAccount.Id) throw new IOException("The source login changed during capture.");
                lock (transaction.Admission)
                {
                    RequireSameContinuationGroup(group, generation);
                    if (waitingForReadOnlyPeers) return new { captureWaiting = true };
                    if (member == null) return null;
                    var value = new DesktopContinuationCheckpoint { SessionId = spec.SessionId, Generation = generation,
                        ThreadId = DesktopSessionFiles.Text(data, "threadId"), TurnId = DesktopSessionFiles.Text(data, "turnId"),
                        Home = Path.GetFullPath(spec.Home), UiProfile = spec.UiProfile, SqliteHome = spec.SqliteHome,
                        Cwd = DesktopSessionFiles.Text(data, "cwd"), Model = DesktopSessionFiles.Text(data, "model"),
                        ModelProvider = DesktopSessionFiles.Text(data, "modelProvider"), Effort = DesktopSessionFiles.Text(data, "effort"),
                        RuntimeWorkspaceRoots = ((object[])data["runtimeWorkspaceRoots"]).Cast<string>().ToArray(),
                        RolloutPath = DesktopSessionFiles.Text(data, "rolloutPath"),
                        PermissionsJson = DesktopSessionFiles.Text(data, "permissionsJson"), FrontendContextJson = DesktopSessionFiles.Text(data, "frontendContextJson"),
                        OriginalTurnStatus = DesktopSessionFiles.Text(data, "originalTurnStatus") };
                    Guid thread; if (!Guid.TryParseExact(value.ThreadId, "D", out thread)) throw new IOException("Continuation thread is not local.");
                    continuationCheckpoint = value; continuationSourceMember = member; return value;
                }
            }
            var checkpoint = ReadContinuation(request);
            if (method == "continuationReceipt")
            {
                string operation = DesktopSessionFiles.Text(request, "operationId");
                var admission = continuationAdmission;
                if (admission == null || continuationOperationId != operation || admission.ReservedOperationId != operation)
                    throw new IOException("Continuation receipt operation changed.");
                return new { turnId = admission.ObservedTurnId, claimed = admission.WasClaimed };
            }
            var parameters = new Dictionary<string, object> { { "threadId", checkpoint.ThreadId }, { "turnId", checkpoint.TurnId },
                { "cwd", checkpoint.Cwd }, { "model", checkpoint.Model }, { "modelProvider", checkpoint.ModelProvider },
                { "effort", checkpoint.Effort }, { "permissionsJson", checkpoint.PermissionsJson }, { "runtimeWorkspaceRoots", checkpoint.RuntimeWorkspaceRoots.ToArray() },
                { "rolloutPath", checkpoint.RolloutPath }, { "frontendContextJson", checkpoint.FrontendContextJson } };
            if (method == "continuationStopped")
            {
                if (generation != checkpoint.Generation) throw new IOException("Continuation interruption generation changed.");
                if (continuationSourceMember == null || !group.Contains(continuationSourceMember))
                    throw new IOException("The captured conversation backend changed.");
                if (!IdleContinuationPeers(group, continuationSourceMember))
                { RequireSameContinuationGroup(group, generation); return new { stopped = false, waitingForPeers = true }; }
                var stopped = continuationSourceMember.Bridge.HostContinuation("stopped", parameters, 15000);
                RequireSameContinuationGroup(group, generation);
                return stopped;
            }
            if (method != "continuationPrepare" || generation <= checkpoint.Generation || continuationOperationId == null)
                throw new IOException("Continuation was not restored into a new backend.");
            string expectedId = DesktopSessionFiles.Text(request, "expectedAccountId"), expectedEmail = DesktopSessionFiles.Text(request, "expectedEmail");
            string rawAuth = Files.ReadAuth(Path.Combine(spec.Home, "auth.json"));
            RequireContinuationTarget(rawAuth == null ? null : Account.Parse(rawAuth), expectedId, expectedEmail);
            foreach (var item in group) item.ContinuationOwner = false;
            var owner = UniqueContinuationOwner(group, item => item.Bridge.HasFrontendResumedThread(checkpoint.ThreadId));
            if (owner == null)
            {
                bool navigated = NavigateContinuation(group, generation, checkpoint, expectedId, expectedEmail);
                return new { ready = false, waitingForConversation = true, navigationAccepted = navigated };
            }
            RequireContinuationBroker(checkpoint.FrontendContextJson, owner.HasVerifiedAppBroker);
            if (!IdleContinuationPeers(group, owner)) return new { ready = false, waitingForConversation = true };
            parameters["expectedEmail"] = expectedEmail;
            var prepared = owner.Bridge.HostContinuation("prepare", parameters, 15000);
            if (!IdleContinuationPeers(group, owner)) return new { ready = false, waitingForConversation = true };
            RequireSameContinuationGroup(group, generation);
            lock (transaction.Admission)
            {
                RequireSameContinuationGroup(group, generation);
                if (group.Any(item => item != owner && item.Bridge.HasPendingFrontendResumeThread(checkpoint.ThreadId)))
                    throw new IOException("Another owned backend is loading the continuation conversation.");
                if (UniqueContinuationOwner(group, item => item.Bridge.HasFrontendResumedThread(checkpoint.ThreadId)) != owner)
                    throw new IOException("The restored frontend conversation owner changed.");
                owner.ContinuationOwner = true;
            }
            return prepared;
        }
        private void ServeControl()
        {
            while (!stopping.IsCancellationRequested)
            {
                try
                {
                    var security = new PipeSecurity(); security.SetAccessRuleProtection(true, false);
                    security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl, AccessControlType.Allow));
                    using (var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security))
                    {
                        listening = pipe; pipe.WaitForConnection(); DesktopProcessIdentity client;
                        if (!AuthorizedClient(pipe, out client)) continue;
                        using (var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true))
                        using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true })
                        {
                            string line = ReadBounded(reader); var request = ManagedBridge.Parse(line); string id = DesktopSessionFiles.Text(request, "id"); object response;
                            try
                            {
                                if (String.IsNullOrEmpty(id) || DesktopSessionFiles.Text(request, "expectedSessionId") != spec.SessionId)
                                    throw new IOException("Stale desktop session generation.");
                                int expectedGeneration = DesktopSessionFiles.Count(request, "expectedGeneration");
                                string method = DesktopSessionFiles.Text(request, "method");
                                Func<bool> connected = () => DesktopNative.Same(client) && PeekNamedPipe(pipe.SafePipeHandle.DangerousGetHandle(), IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                                object continuationResult = null;
                                bool continuationCommand = method.StartsWith("continuation", StringComparison.Ordinal);
                                if (continuationCommand) continuationResult = HandleContinuation(method, expectedGeneration, request);
                                else if (method == "status")
                                {
                                    lock (transaction.Admission) if (expectedGeneration != transaction.Generation) throw new IOException("Stale desktop generation.");
                                    bool healthy = RefreshMemberStatus(transaction.Admission, transaction.Members);
                                    lock (transaction.Admission)
                                    {
                                        if (expectedGeneration != transaction.Generation) throw new IOException("Stale desktop generation.");
                                        metadataHealthy = healthy; RefreshActors();
                                    }
                                }
                                else if (method == "quiesce") transaction.Quiesce(connected, expectedGeneration);
                                else if (method == "disable")
                                { lock (transaction.Admission) { if (expectedGeneration != transaction.Generation || Enabled()) throw new IOException("Configuration revocation was not committed."); transaction.CancelBeforeEof(); } }
                                else if (method == "restore") Restore(connected, expectedGeneration, request);
                                else throw new IOException("Unsupported desktop method.");
                                lock (transaction.Admission) { Persist(); response = new { id = id, result = Snapshot(), continuation = continuationResult }; }
                            }
                            catch { response = new { id = id, error = new { code = -32071, message = "Desktop session request could not be completed safely." } }; }
                            writer.WriteLine(new JavaScriptSerializer { MaxJsonLength = 131072 }.Serialize(response));
                        }
                    }
                }
                catch { if (!stopping.IsCancellationRequested) Thread.Sleep(100); }
            }
        }
        private static string ReadBounded(TextReader reader)
        {
            var read = Task.Factory.StartNew(() =>
            { var text = new StringBuilder(); int next; while ((next = reader.Read()) >= 0 && next != '\n') { if (text.Length >= 65536) throw new IOException("Desktop request too large."); text.Append((char)next); } if (next < 0) throw new IOException("Desktop request closed."); return text.ToString(); });
            if (!read.Wait(5000)) throw new IOException("Desktop request timed out."); return read.Result;
        }
        private void Restore(Func<bool> connected, int expectedGeneration, Dictionary<string, object> request)
        {
            lock (transaction.Admission)
            {
                if (expectedGeneration != transaction.Generation || !Enabled() || !connected()) throw new IOException("Desktop recovery authorization or generation changed.");
                if (!transaction.EofCommitted && members.Any(m => !m.Exited0))
                { transaction.CancelBeforeEof(); recoveryAccepted = true; Persist(); return; }
                if (members.Any(m => !m.Exited0)) throw new IOException("Old desktop backend has not exited safely.");
                if (!ConfirmedAppGone()) throw new UserError("桌面程式仍在執行，請先正常 Quit 再恢復；不會強制結束。");
                if (request.ContainsKey("continuation"))
                {
                    ReadContinuation(request); Guid operation;
                    string requested = DesktopSessionFiles.Text(request, "operationId");
                    if (!Guid.TryParseExact(requested, "N", out operation) || continuationOperationId != null && continuationOperationId != requested)
                        throw new IOException("Continuation restore operation changed.");
                    continuationOperationId = requested;
                }
                else { continuationCheckpoint = null; continuationOperationId = null; }
                if (server != null) server.Dispose();
                StartListenerAndApp(); recoveryAccepted = true; Persist();
                // This is an accepted recovery, not readiness. Fresh status requests observe startup.
                // Never monopolize the control pipe while the new App initializes/reconnects.
            }
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            // Disposal is reached only after owner-exit cleanup or before an App was launched.
            // No backend input is closed because the switcher or a WebSocket disconnected.
            stopping.Cancel(); try { if (listening != null) listening.Dispose(); } catch { }
            if (server != null) server.Dispose();
            foreach (var process in appHandles.Values) process.Dispose(); foreach (var process in relayHandles.Values) process.Dispose(); if (initialApp != null) initialApp.Dispose(); stopping.Dispose();
        }
    }
}
