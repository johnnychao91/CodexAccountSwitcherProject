using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CodexAccountSwitcher
{
    public static class ManagedRuntimeClientTests
    {
        private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        private static void Run(Func<Task> operation) { Task.Run(operation).GetAwaiter().GetResult(); }
        private static async Task Finish(Task operation)
        { Check(await Task.WhenAny(operation, Task.Delay(15000)) == operation, "The synthetic pipe test did not finish."); await operation; }
        private static TaskCompletionSource<bool> Signal()
        { return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); }
        private static Task<RuntimeState> Request(ManagedRuntimeClient client, ManagedRuntimeRecord record, string method, CancellationToken token)
        { return (Task<RuntimeState>)typeof(ManagedRuntimeClient).GetMethod("Request", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(client, new object[] { record, method, token }); }
        private static List<ManagedRuntimeRecord> Paused(ManagedRuntimeClient client)
        { return (List<ManagedRuntimeRecord>)typeof(ManagedRuntimeClient).GetField("paused", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(client); }
        private static List<ManagedRuntimeRecord> Records(ManagedRuntimeClient client)
        { return (List<ManagedRuntimeRecord>)typeof(ManagedRuntimeClient).GetMethod("Records", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(client, new object[0]); }

        private sealed class Fixture : IDisposable
        {
            public readonly string Root, Home, Catalog;
            public readonly Process Child;
            public readonly ManagedRuntimeRecord Record;
            public readonly ManagedRuntimeClient Client;
            public readonly List<ProcessBlocker> Blockers = new List<ProcessBlocker>();
            public int Inspections;
            public readonly CancellationTokenSource Lifetime = new CancellationTokenSource(15000);
            public Fixture(bool syntheticInspection = false)
            {
                Root = Path.Combine(Path.GetTempPath(), "codex-managed-client-tests-" + Guid.NewGuid().ToString("N"));
                Home = Path.Combine(Root, "synthetic-home");
                Catalog = Path.Combine(Root, "integrations", "vscode", "runtimes");
                Files.PrivateDirectory(Catalog); Directory.CreateDirectory(Home);
                string shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
                Child = Process.Start(new ProcessStartInfo(shell, "-NoLogo -NoProfile -NonInteractive -Command \"[Console]::ReadLine() | Out-Null\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true });
                using (var self = Process.GetCurrentProcess())
                {
                    string identity = Guid.NewGuid().ToString("N");
                    Record = new ManagedRuntimeRecord { ProtocolVersion = 1, Kind = "vscode-stdio", InstanceId = identity, Generation = identity,
                        PipeName = "codex-managed-client-test-" + identity, WrapperPid = self.Id, WrapperStartedUtc = Utc(self.StartTime),
                        OwnerPid = self.Id, OwnerStartedUtc = Utc(self.StartTime), BackendPid = Child.Id, BackendStartedUtc = Utc(Child.StartTime), CodexHome = Home, State = "running" };
                }
                Client = syntheticInspection ? new ManagedRuntimeClient(Root, Home, delegate
                {
                    Interlocked.Increment(ref Inspections);
                    return new List<ProcessBlocker>(Blockers);
                }) : new ManagedRuntimeClient(Root, Home);
            }
            public static string Utc(DateTime value) { return value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture); }
            public Dictionary<string, object> Data()
            {
                return new Dictionary<string, object> { { "protocolVersion", 1 }, { "generation", Record.Generation }, { "instanceId", Record.InstanceId },
                    { "ownerAlive", true }, { "controlEnabled", true }, { "state", Record.State }, { "known", true }, { "idle", true }, { "activeTurns", 0 }, { "pendingRequests", 0 },
                    { "backgroundTerminals", 0 }, { "backendPid", Record.BackendPid }, { "backendStartedUtc", Record.BackendStartedUtc },
                    { "requiresReload", Record.State == "quiesced" }, { "resumeRequested", false } };
            }
            public void WriteRecord()
            { File.WriteAllText(Path.Combine(Catalog, Record.InstanceId + ".json"), Json.Encode(Record), new UTF8Encoding(false)); }
            public void StopChild()
            {
                if (Child.HasExited) return;
                Child.StandardInput.WriteLine(); Child.StandardInput.Flush();
                Check(Child.WaitForExit(5000) && Child.ExitCode == 0, "The owned synthetic backend did not exit normally.");
                Record.State = "quiesced";
                Blockers.RemoveAll(blocker => blocker.Pid == Record.BackendPid);
            }
            public NamedPipeServerStream Pipe()
            {
                var security = new PipeSecurity(); security.SetAccessRuleProtection(true, false);
                security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl, AccessControlType.Allow));
                return new NamedPipeServerStream(Record.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security);
            }
            public async Task Serve(NamedPipeServerStream pipe, Dictionary<string, object> data, Action<Dictionary<string, object>> received = null)
            {
                using (pipe)
                using (Lifetime.Token.Register(() => { try { pipe.Dispose(); } catch { } }))
                {
                    await pipe.WaitForConnectionAsync(Lifetime.Token);
                    using (var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true))
                    {
                        string line = await reader.ReadLineAsync();
                        if (line == null) return;
                        var request = Json.Object(line); if (received != null) received(request);
                        using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true))
                        { await writer.WriteLineAsync(Json.Encode(new { id = request["id"], result = data })); await writer.FlushAsync(); }
                    }
                }
            }
            public void Dispose()
            {
                Lifetime.Cancel(); Lifetime.Dispose();
                try { if (!Child.HasExited) { Child.Kill(); Child.WaitForExit(5000); } } finally { Child.Dispose(); }
                string target = Path.GetFullPath(Root);
                string parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                Check(String.Equals(Path.GetDirectoryName(target), parent, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(target).StartsWith("codex-managed-client-tests-", StringComparison.Ordinal), "Unexpected synthetic cleanup directory.");
                if (Directory.Exists(target)) Directory.Delete(target, true);
            }
        }

        public static void Register(Action<string, Action> test)
        {
            test("Manual switching pauses an idle owned backend before allowing credential application", delegate { Run(async delegate
            {
                using (var f = new Fixture(true))
                {
                    f.Blockers.Add(new ProcessBlocker { Pid = f.Record.WrapperPid, Kind = "Integration" });
                    f.Blockers.Add(new ProcessBlocker { Pid = f.Record.BackendPid, Kind = "VSCode" });
                    f.WriteRecord();
                    var methods = new List<string>();
                    var server = Task.Run(async delegate
                    {
                        await f.Serve(f.Pipe(), f.Data(), request => { methods.Add(Json.Str(request, "method")); Check(Json.Str(request, "method") == "status", "Manual switching did not first inspect the runtime."); });
                        var quiesced = f.Data(); quiesced["state"] = "quiesced"; quiesced["requiresReload"] = true;
                        await f.Serve(f.Pipe(), quiesced, request =>
                        {
                            methods.Add(Json.Str(request, "method")); Check(Json.Str(request, "method") == "quiesce", "Idle manual switching sent an unexpected mutation.");
                            f.StopChild(); f.WriteRecord();
                        });
                        await f.Serve(f.Pipe(), f.Data(), request => { methods.Add(Json.Str(request, "method")); Check(Json.Str(request, "method") == "status", "Manual switching did not confirm the paused runtime."); });
                    });
                    bool applied = false;
                    await Finish(f.Client.WaitForManualExitAsync(null, f.Lifetime.Token));
                    Check(f.Child.HasExited && f.Inspections >= 4, "Manual switching returned before normal exit and the quiet checks.");
                    if (!f.Client.CredentialProcessesRunning()) applied = true;
                    await Finish(server);
                    Check(applied && String.Join(",", methods) == "status,quiesce,status", "Idle manual switching did not permit application after the expected lifecycle.");
                }
            }); });
            test("Manual switching waits without interrupting active turns or other blocked runtime states", delegate { Run(async delegate
            {
                foreach (string reason in new[] { "active", "pending", "background", "unknown", "unmanaged", "disabled" })
                using (var f = new Fixture(true))
                using (var cancel = new CancellationTokenSource())
                {
                    f.Blockers.Add(new ProcessBlocker { Pid = f.Record.WrapperPid, Kind = "Integration" });
                    f.Blockers.Add(new ProcessBlocker { Pid = f.Record.BackendPid, Kind = "VSCode" });
                    if (reason == "unmanaged") f.Blockers.Add(new ProcessBlocker { Pid = Int32.MaxValue, Kind = "Desktop", Description = "Synthetic unmanaged runtime" });
                    f.WriteRecord(); var state = f.Data();
                    if (reason == "active") { state["idle"] = false; state["activeTurns"] = 1; }
                    if (reason == "pending") { state["idle"] = false; state["pendingRequests"] = 1; }
                    if (reason == "background") { state["idle"] = false; state["backgroundTerminals"] = 1; }
                    if (reason == "unknown") state["known"] = false;
                    if (reason == "disabled") state["controlEnabled"] = false;
                    int requests = 0;
                    var server = Task.Run(async delegate
                    {
                        for (int attempt = 0; attempt < 2; attempt++)
                            await f.Serve(f.Pipe(), state, request => { requests++; Check(Json.Str(request, "method") == "status", "Manual switching attempted to stop blocked work: " + reason); });
                    });
                    bool applied = false, cancelled = false; int reports = 0;
                    try
                    {
                        await Finish(f.Client.WaitForManualExitAsync(text => { if (++reports == 2) cancel.Cancel(); }, cancel.Token));
                        applied = true;
                    }
                    catch (OperationCanceledException) { cancelled = true; }
                    await Finish(server);
                    Check(cancelled && !applied && requests == 2 && Paused(f.Client).Count == 0 && !f.Child.HasExited,
                        "Manual switching did not preserve blocked work until cancellation: " + reason);
                }
            }); });
            test("Manual switching refuses a newly busy runtime and recovery preserves its running backend", delegate { Run(async delegate
            {
                using (var f = new Fixture(true))
                {
                    f.Blockers.Add(new ProcessBlocker { Pid = f.Record.WrapperPid, Kind = "Integration" });
                    f.Blockers.Add(new ProcessBlocker { Pid = f.Record.BackendPid, Kind = "VSCode" });
                    f.WriteRecord(); var busy = f.Data(); busy["idle"] = false; busy["activeTurns"] = 1;
                    var server = Task.Run(async delegate
                    {
                        await f.Serve(f.Pipe(), f.Data(), request => Check(Json.Str(request, "method") == "status", "Missing initial manual status check."));
                        await f.Serve(f.Pipe(), busy, request => Check(Json.Str(request, "method") == "quiesce", "Unexpected manual stop request."));
                        await f.Serve(f.Pipe(), busy, request => Check(Json.Str(request, "method") == "status", "Recovery tried to reload a runtime that rejected quiesce."));
                    });
                    bool applied = false, rejected = false;
                    try { await Finish(f.Client.WaitForManualExitAsync(null, f.Lifetime.Token)); applied = true; }
                    catch (UserError) { rejected = true; }
                    Check(rejected && !applied && Paused(f.Client).Count == 1 && !f.Child.HasExited, "A rejected manual quiesce allowed credential application.");
                    await Finish(f.Client.RestoreAsync()); await Finish(server);
                    Check(Paused(f.Client).Count == 0 && !f.Child.HasExited, "Manual recovery disturbed the live backend.");
                }
            }); });
            test("Managed runtime status requires a complete consistent protocol snapshot", delegate
            {
                using (var f = new Fixture())
                {
                    Check(ManagedRuntimeClient.ParseState(f.Data(), f.Record).Known, "The complete synthetic status was rejected.");
                    foreach (string field in new[] { "protocolVersion", "activeTurns", "pendingRequests", "backgroundTerminals", "idle", "known" })
                    {
                        var data = f.Data(); data.Remove(field);
                        Check(!ManagedRuntimeClient.ParseState(data, f.Record).Known, "Missing status data was treated as known: " + field);
                    }
                    foreach (object invalid in new object[] { null, "0", -1, 0.5, true })
                    {
                        var data = f.Data(); data["pendingRequests"] = invalid;
                        Check(!ManagedRuntimeClient.ParseState(data, f.Record).Known, "Malformed pending requests were treated as idle.");
                    }
                    var contradictory = f.Data(); contradictory["activeTurns"] = 1;
                    Check(!ManagedRuntimeClient.ParseState(contradictory, f.Record).Known, "Busy and idle status was accepted together.");
                    contradictory = f.Data(); contradictory["state"] = "unknown";
                    Check(!ManagedRuntimeClient.ParseState(contradictory, f.Record).Known, "An unknown runtime was treated as known.");
                }
            });
            test("Managed pipe validates the server PID before sending a mutation", delegate { Run(async delegate
            {
                using (var f = new Fixture())
                {
                    bool received = false; var server = f.Serve(f.Pipe(), f.Data(), request => received = true);
                    f.Record.WrapperPid = f.Child.Id; f.Record.WrapperStartedUtc = Fixture.Utc(f.Child.StartTime);
                    bool rejected = false;
                    try { await Request(f.Client, f.Record, "quiesce", f.Lifetime.Token); } catch { rejected = true; }
                    await Finish(server);
                    Check(rejected && !received, "A mutation reached an unverified pipe server.");
                }
            }); });
            test("Managed pipe rejects stale process identity before sending a mutation", delegate { Run(async delegate
            {
                using (var f = new Fixture())
                {
                    bool received = false; var server = f.Serve(f.Pipe(), f.Data(), request => received = true);
                    f.Record.WrapperStartedUtc = DateTimeOffset.Parse(f.Record.WrapperStartedUtc, CultureInfo.InvariantCulture).AddTicks(1).ToString("o");
                    bool rejected = false;
                    try { await Request(f.Client, f.Record, "interrupt", f.Lifetime.Token); } catch { rejected = true; }
                    f.Lifetime.Cancel();
                    try { await Finish(server); } catch { if (!f.Lifetime.IsCancellationRequested) throw; }
                    Check(rejected && !received, "A mutation reached a process with stale identity.");
                }
            }); });
            test("Managed pipe rejects mismatched generation and backend identity", delegate { Run(async delegate
            {
                using (var f = new Fixture())
                {
                    foreach (string field in new[] { "generation", "backendPid", "backendStartedUtc" })
                    {
                        var data = f.Data();
                        if (field == "backendPid") data[field] = f.Record.WrapperPid;
                        else data[field] = "synthetic-mismatch";
                        var server = f.Serve(f.Pipe(), data); bool rejected = false;
                        try { var result = await Request(f.Client, f.Record, "status", f.Lifetime.Token); rejected = !result.Known; }
                        catch { rejected = true; }
                        await Finish(server); Check(rejected, "Mismatched runtime identity was accepted: " + field);
                    }
                }
            }); });
            test("Managed catalogs retain a quiesced owned child and ignore foreign homes", delegate
            {
                using (var f = new Fixture())
                {
                    f.WriteRecord(); Check(Records(f.Client).Count == 1, "A valid private catalog was ignored.");
                    f.StopChild(); f.WriteRecord();
                    Check(Records(f.Client).Count == 1, "A quiesced record disappeared when its owned child exited.");
                    f.Record.CodexHome = Path.Combine(f.Root, "different-home"); f.WriteRecord();
                    Check(Records(f.Client).Count == 0, "A different Codex home was included in the controlled cohort.");
                }
            });
            test("Rejected quiesce preserves a running backend without requesting an unnecessary reload", delegate { Run(async delegate
            {
                using (var f = new Fixture())
                {
                    Paused(f.Client).Add(f.Record);
                    var busy = f.Data(); busy["idle"] = false; busy["activeTurns"] = 1;
                    var server = f.Serve(f.Pipe(), busy, request => Check(Json.Str(request, "method") == "status", "A still-running backend received an unexpected recovery mutation."));
                    await Finish(f.Client.RestoreAsync()); await Finish(server);
                    Check(Paused(f.Client).Count == 0 && !f.Child.HasExited, "Recovery changed or retained ownership of the running backend.");
                }
            }); });
            test("Cancelled rejected quiesce recovery waits for status without reloading the running backend", delegate { Run(async delegate
            {
                using (var f = new Fixture())
                using (var cancel = new CancellationTokenSource())
                {
                    var receivedQuiesce = Signal(); Paused(f.Client).Add(f.Record);
                    var pipe = f.Pipe();
                    var server = Task.Run(async delegate
                    {
                        using (pipe)
                        using (f.Lifetime.Token.Register(() => { try { pipe.Dispose(); } catch { } }))
                        {
                            await pipe.WaitForConnectionAsync(f.Lifetime.Token);
                            using (var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true))
                            {
                                var request = Json.Object(await reader.ReadLineAsync());
                                Check(Json.Str(request, "method") == "quiesce", "The first synthetic request was not quiesce.");
                                receivedQuiesce.TrySetResult(true);
                                // Cancellation does not release an in-progress query; it can later reject quiesce.
                                await Task.Delay(10300, f.Lifetime.Token);
                            }
                        }
                        var busy = f.Data(); busy["idle"] = false; busy["activeTurns"] = 1;
                        await f.Serve(f.Pipe(), busy, request => Check(Json.Str(request, "method") == "status", "Recovery requested reload instead of checking the running backend."));
                    });
                    var requestTask = Request(f.Client, f.Record, "quiesce", cancel.Token);
                    await Finish(receivedQuiesce.Task); cancel.Cancel();
                    try { await requestTask; } catch { }
                    await Finish(f.Client.RestoreAsync()); await Finish(server);
                    Check(Paused(f.Client).Count == 0 && !f.Child.HasExited, "Cancelled rejected quiesce did not preserve the running backend.");
                }
            }); });
            test("Managed restoration retains ownership until resume is explicitly confirmed", delegate { Run(async delegate
            {
                using (var f = new Fixture())
                {
                    f.StopChild(); Paused(f.Client).Add(f.Record);
                    foreach (string field in new[] { "resumeRequested", "requiresReload", "controlEnabled" })
                    {
                        var incomplete = f.Data(); incomplete["resumeRequested"] = true; incomplete[field] = false;
                        var rejectedServer = f.Serve(f.Pipe(), incomplete); bool rejected = false;
                        try { await f.Client.RestoreAsync(); } catch { rejected = true; }
                        await Finish(rejectedServer);
                        Check(rejected && Paused(f.Client).Count == 1, "Unconfirmed resume discarded ownership of the paused runtime: " + field);
                    }
                    var confirmed = f.Data(); confirmed["resumeRequested"] = true;
                    var server = f.Serve(f.Pipe(), confirmed); await Finish(f.Client.RestoreAsync()); await Finish(server);
                    Check(Paused(f.Client).Count == 0, "Confirmed resume did not clear paused ownership.");
                }
            }); });
            test("Cancelled quiesce recovery waits for the previous server operation to release its pipe", delegate { Run(async delegate
            {
                using (var f = new Fixture())
                using (var cancel = new CancellationTokenSource())
                {
                    var quiesced = Signal(); Paused(f.Client).Add(f.Record);
                    var pipe = f.Pipe();
                    var server = Task.Run(async delegate
                    {
                        using (pipe)
                        using (f.Lifetime.Token.Register(() => { try { pipe.Dispose(); } catch { } }))
                        {
                            await pipe.WaitForConnectionAsync(f.Lifetime.Token);
                            using (var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true))
                            {
                                var request = Json.Object(await reader.ReadLineAsync());
                                Check(Json.Str(request, "method") == "quiesce", "The first synthetic request was not quiesce.");
                                f.StopChild(); quiesced.TrySetResult(true);
                                // The bridge handles one request at a time; cancellation cannot stop its in-progress cleanup.
                                await Task.Delay(3100, f.Lifetime.Token);
                            }
                        }
                        var data = f.Data(); data["resumeRequested"] = true;
                        await f.Serve(f.Pipe(), data, request => Check(Json.Str(request, "method") == "resume", "Recovery did not request resume."));
                    });
                    var requestTask = Request(f.Client, f.Record, "quiesce", cancel.Token);
                    await Finish(quiesced.Task); cancel.Cancel();
                    try { await requestTask; } catch { }
                    await Finish(f.Client.RestoreAsync()); await Finish(server);
                    Check(Paused(f.Client).Count == 0, "Cancelled quiesce did not complete recovery.");
                }
            }); });
        }
    }
}
