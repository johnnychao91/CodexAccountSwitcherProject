using System;
using System.IO;
using System.IO.Pipes;
using System.Diagnostics;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace CodexAccountSwitcher
{
    internal static class ManagedBridgeProgram
    {
        public static int Main(string[] args)
        {
            try
            {
                string executable = Assembly.GetExecutingAssembly().Location;
                string configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexAccountSwitcher", "integrations", "vscode", "config.json");
                if (args.Length == 2 && args[0] == "--configure") { Configure(configPath, executable, args[1]); return 0; }
                if (args.Length == 1 && args[0] == "--disable-control") { DisableControl(configPath); return 0; }
                if (args.Length == 3 && args[0] == "--check-resume") return CheckResume(executable, args[1], args[2]) ? 0 : 1;
                ManagedBridge.EnsureNoReparsePoint(Path.GetDirectoryName(configPath));
                if ((File.GetAttributes(configPath) & FileAttributes.ReparsePoint) != 0) throw new IOException();
                var config = ManagedBridge.Parse(File.ReadAllText(configPath, Encoding.UTF8));
                object value;
                if (config == null || !config.TryGetValue("RealCliPath", out value) || !(value is string)) throw new IOException();
                string backend = Path.GetFullPath((string)value);
                if (!File.Exists(backend) || String.Equals(backend, Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase)) throw new IOException();
                if (Array.IndexOf(args, "app-server") < 0)
                {
                    using (var child = Process.Start(new ProcessStartInfo(backend, String.Join(" ", Array.ConvertAll(args, ManagedBridge.QuoteArgument))) { UseShellExecute = false, CreateNoWindow = true }))
                    { child.WaitForExit(); return child.ExitCode; }
                }
                Console.InputEncoding = new UTF8Encoding(false); Console.OutputEncoding = new UTF8Encoding(false);
                using (var bridge = new ManagedBridge(new ManagedBridgeOptions { BackendExecutable = backend, BackendArguments = args,
                    HelperExecutable = executable, ControllerExecutable = Path.Combine(Path.GetDirectoryName(executable), "CodexAccountSwitcher.exe"),
                    AuthorizationFile = configPath, AuthorizationGeneration = Text(config, "AuthorizationGeneration") }, Console.In, Console.Out))
                { bridge.Run(); return bridge.ExitCode; }
            }
            catch
            {
                Console.Error.WriteLine("The optional Codex integration could not start. Check its local configuration or disable it in VS Code.");
                return 1;
            }
        }

        private static string Text(Dictionary<string, object> value, string key)
        { object item; return value != null && value.TryGetValue(key, out item) && item != null ? Convert.ToString(item, CultureInfo.InvariantCulture) : null; }
        private static bool IsTrue(Dictionary<string, object> value, string key)
        { object item; return value != null && value.TryGetValue(key, out item) && Object.Equals(item, true); }
        private static bool MatchesProcess(string pidText, string started)
        {
            int pid;
            if (!Int32.TryParse(pidText, out pid) || pid <= 0 || started == null) return false;
            try { using (var process = Process.GetProcessById(pid)) return process.StartTime.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) == started; }
            catch { return false; }
        }
        private static Dictionary<string, object> ReadPrivateJson(string path)
        {
            ManagedBridge.EnsureNoReparsePoint(Path.GetDirectoryName(path));
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 65536 || (info.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException();
            return ManagedBridge.Parse(File.ReadAllText(path, Encoding.UTF8));
        }
        private static void Configure(string path, string executable, string requestedBackend)
        {
            string backend = Path.GetFullPath(requestedBackend);
            if (!File.Exists(backend) || String.Equals(backend, Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase)) throw new IOException();
            string directory = Path.GetDirectoryName(path);
            ManagedBridge.PrivateDirectory(directory);
            ManagedBridge.PrivateDirectory(ManagedBridgeOptions.DefaultCatalogDirectory);
            string generation = Guid.NewGuid().ToString("N");
            if (File.Exists(path))
            {
                var existing = ReadPrivateJson(path);
                bool changes = !String.Equals(Text(existing, "RealCliPath"), backend, StringComparison.OrdinalIgnoreCase) ||
                    !String.Equals(Text(existing, "BridgeExecutable"), executable, StringComparison.OrdinalIgnoreCase);
                if (changes)
                {
                    foreach (string file in Directory.GetFiles(ManagedBridgeOptions.DefaultCatalogDirectory, "*.json"))
                    {
                        var catalog = ReadPrivateJson(file);
                        if (MatchesProcess(Text(catalog, "WrapperPid"), Text(catalog, "WrapperStartedUtc"))) throw new IOException("A managed window still uses the previous integration configuration.");
                    }
                }
                else if (IsTrue(existing, "Enabled") && Text(existing, "AuthorizationGeneration") != null) generation = Text(existing, "AuthorizationGeneration");
            }
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, ManagedBridge.Encode(new { ProtocolVersion = 1, Enabled = true, AuthorizationGeneration = generation,
                    RealCliPath = backend, BridgeExecutable = executable }), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        private static void DisableControl(string path)
        {
            var config = ReadPrivateJson(path); config["Enabled"] = false;
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllText(temporary, ManagedBridge.Encode(config), new UTF8Encoding(false)); File.Replace(temporary, path, null); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint processId);
        private static bool CheckResume(string executable, string instance, string owner)
        {
            Guid parsed;
            if (instance.Length != 32 || !Guid.TryParseExact(instance, "N", out parsed)) return false;
            var catalog = ReadPrivateJson(Path.Combine(ManagedBridgeOptions.DefaultCatalogDirectory, instance + ".json"));
            if (Text(catalog, "ProtocolVersion") != "1" || Text(catalog, "Kind") != "vscode-stdio" || Text(catalog, "InstanceId") != instance ||
                Text(catalog, "Generation") != instance || Text(catalog, "OwnerPid") != owner || Text(catalog, "State") != "quiesced" || !IsTrue(catalog, "ResumeRequested") ||
                Text(catalog, "PipeName") != "CodexAccountSwitcher.vscode." + instance ||
                !MatchesProcess(owner, Text(catalog, "OwnerStartedUtc")) ||
                !MatchesProcess(Text(catalog, "WrapperPid"), Text(catalog, "WrapperStartedUtc")) ||
                MatchesProcess(Text(catalog, "BackendPid"), Text(catalog, "BackendStartedUtc"))) return false;
            using (var wrapper = Process.GetProcessById(Int32.Parse(Text(catalog, "WrapperPid"), CultureInfo.InvariantCulture)))
                if (!String.Equals(Path.GetFullPath(wrapper.MainModule.FileName), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase)) return false;
            using (var pipe = new NamedPipeClientStream(".", Text(catalog, "PipeName"), PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                pipe.Connect(2000); uint pid;
                if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out pid) || pid.ToString(CultureInfo.InvariantCulture) != Text(catalog, "WrapperPid")) return false;
                if (!MatchesProcess(Text(catalog, "WrapperPid"), Text(catalog, "WrapperStartedUtc"))) return false;
                using (var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true))
                using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true })
                {
                    string id = Guid.NewGuid().ToString("N");
                    writer.WriteLine(ManagedBridge.Encode(new { id = id, method = "status", @params = new { expectedGeneration = instance, timeoutMs = 1000 } }));
                    var responseTask = Task.Factory.StartNew(() => reader.ReadLine());
                    if (!responseTask.Wait(2000) || responseTask.Result == null || responseTask.Result.Length > 65536) return false;
                    var response = ManagedBridge.Parse(responseTask.Result); object resultObject;
                    if (response == null || Text(response, "id") != id || response.ContainsKey("error") || !response.TryGetValue("result", out resultObject)) return false;
                    var result = resultObject as Dictionary<string, object>;
                    return result != null && Text(result, "instanceId") == instance && Text(result, "generation") == instance && Text(result, "state") == "quiesced" &&
                        IsTrue(result, "known") && IsTrue(result, "idle") && IsTrue(result, "ownerAlive") && IsTrue(result, "resumeRequested") && IsTrue(result, "requiresReload");
                }
            }
        }
    }
}
