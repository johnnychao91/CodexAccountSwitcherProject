using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CodexAccountSwitcher
{
    public static class UsageSessionTests
    {
        private static void Check(bool condition, string message)
        { if (!condition) throw new Exception(message); }

        private static Account TestAccount(string scenario)
        { return Account.Parse(SelfTests.Auth("usage-test", scenario, "usage@example.com", "plus", "fake-refresh-do-not-send")); }

        private static void Fixture(Action<string> action)
        {
            string tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string root = Path.Combine(tempRoot, "codex-usage-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try { action(root); }
            finally
            {
                // Only remove the newly generated test directory under the system temporary directory.
                string resolved = Path.GetFullPath(root);
                if (!resolved.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) ||
                    !Path.GetFileName(resolved).StartsWith("codex-usage-tests-", StringComparison.Ordinal))
                    throw new Exception("Unexpected test cleanup directory.");
                if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
            }
        }

        private static void CheckClean(string storage)
        {
            Check(!Directory.Exists(storage) || Directory.GetDirectories(storage, "usage-*").Length == 0,
                "The isolated usage directory was not removed.");
            string pidFile = Path.Combine(storage, "fake-usage.pid");
            if (!File.Exists(pidFile)) return;
            int pid = Int32.Parse(File.ReadAllText(pidFile));
            bool exited;
            try { using (var process = Process.GetProcessById(pid)) exited = process.HasExited || process.WaitForExit(3000); }
            catch (ArgumentException) { exited = true; }
            Check(exited, "The isolated usage process did not exit.");
        }

        private static UserError ExpectError(string fake, string storage, string scenario)
        {
            UserError failure = null;
            using (var session = new UsageSession())
            using (var timeout = new CancellationTokenSource(10000))
            {
                try { session.ReadAsync(fake, storage, TestAccount(scenario), timeout.Token).GetAwaiter().GetResult(); }
                catch (UserError ex) { failure = ex; }
            }
            Check(failure != null, "Expected a public usage error.");
            Check(failure.Message.IndexOf("rawserversecret", StringComparison.OrdinalIgnoreCase) < 0 &&
                failure.Message.IndexOf("fake-access", StringComparison.OrdinalIgnoreCase) < 0 &&
                failure.Message.IndexOf("fake-refresh-do-not-send", StringComparison.OrdinalIgnoreCase) < 0 &&
                failure.Message.IndexOf(storage, StringComparison.OrdinalIgnoreCase) < 0,
                "A usage error exposed private process details.");
            CheckClean(storage);
            return failure;
        }

        public static void Register(Action<string, Action> test)
        {
            string fake = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FakeUsageCodex.exe");
            test("Usage JSON-RPC test helper is available", delegate
            { Check(File.Exists(fake), "FakeUsageCodex.exe must be built beside the test executable."); });
            if (!File.Exists(fake)) return;

            test("Usage lookup reads both allowances with an isolated process and no refresh token", delegate
            {
                Fixture(storage =>
                {
                    var previous = new Dictionary<string, string>();
                    string[] keys = { "CODEX_HOME", "OPENAI_API_KEY", "CODEX_API_KEY", "CODEX_ACCESS_TOKEN", "OPENAI_BASE_URL", "OPENAI_IDENTITY_TOKEN_FILE", "OPENAI_FEDERATION_RULE_ID" };
                    foreach (string key in keys) previous[key] = Environment.GetEnvironmentVariable(key);
                    string currentHome = Path.Combine(storage, "current-home");
                    Directory.CreateDirectory(currentHome);
                    string auth = Path.Combine(currentHome, "auth.json");
                    string config = Path.Combine(currentHome, "config.toml");
                    File.WriteAllText(auth, "synthetic-current-auth-must-remain-untouched");
                    File.WriteAllText(config, "synthetic-current-config-must-remain-untouched");
                    try
                    {
                        foreach (string key in keys) Environment.SetEnvironmentVariable(key, "synthetic-inherited-value");
                        Environment.SetEnvironmentVariable("CODEX_HOME", currentHome);
                        AccountUsage usage;
                        using (var session = new UsageSession())
                        using (var timeout = new CancellationTokenSource(10000))
                            usage = session.ReadAsync(fake, storage, TestAccount("success"), timeout.Token).GetAwaiter().GetResult();
                        Check(usage.FiveHour != null && usage.FiveHour.RemainingPercent == 60, "Incorrect five-hour allowance.");
                        Check(usage.Weekly != null && usage.Weekly.RemainingPercent == 25, "Incorrect weekly allowance.");
                        Check(usage.FiveHour.ResetsAt.HasValue && usage.Weekly.ResetsAt.HasValue, "Reset times were not retained.");
                        Check(File.ReadAllText(auth) == "synthetic-current-auth-must-remain-untouched" &&
                            File.ReadAllText(config) == "synthetic-current-config-must-remain-untouched", "The current home was changed.");
                        CheckClean(storage);
                    }
                    finally { foreach (var entry in previous) Environment.SetEnvironmentVariable(entry.Key, entry.Value); }
                });
            });

            test("Usage RPC errors do not expose server messages or tokens", delegate
            { Fixture(storage => ExpectError(fake, storage, "rpc-error-with-secret")); });

            test("Rejected usage login requests a new login without exposing server messages", delegate
            {
                Fixture(storage =>
                {
                    UserError error = ExpectError(fake, storage, "login-error-with-secret");
                    Check(error.Message.Contains("重新登入"), "Rejected login did not request a new login.");
                });
            });

            test("Usage refresh requests require a new login without sending a refresh token", delegate
            {
                Fixture(storage =>
                {
                    UserError error = ExpectError(fake, storage, "refresh-request");
                    Check(error.Message.Contains("重新登入"), "Expired credentials did not request a new login.");
                });
            });

            test("Usage for a different account is rejected", delegate
            { Fixture(storage => ExpectError(fake, storage, "wrong-account")); });

            test("An invalid usage account identifier cannot bypass the account check", delegate
            { Fixture(storage => ExpectError(fake, storage, "invalid-account-id")); });

            test("Unknown server requests are answered without blocking usage lookup", delegate
            {
                Fixture(storage =>
                {
                    AccountUsage usage;
                    using (var session = new UsageSession())
                    using (var timeout = new CancellationTokenSource(10000))
                        usage = session.ReadAsync(fake, storage, TestAccount("unknown-server-request"), timeout.Token).GetAwaiter().GetResult();
                    Check(usage.FiveHour != null && usage.FiveHour.RemainingPercent == 60 &&
                        usage.Weekly != null && usage.Weekly.RemainingPercent == 25, "The server request interrupted the usage response.");
                    CheckClean(storage);
                });
            });

            test("Malformed usage protocol output fails safely and cleans up", delegate
            { Fixture(storage => ExpectError(fake, storage, "malformed")); });

            test("An exited usage server fails safely and cleans up", delegate
            { Fixture(storage => ExpectError(fake, storage, "eof")); });

            test("Cancelling usage lookup terminates its process and removes temporary files", delegate
            {
                Fixture(storage =>
                {
                    using (var session = new UsageSession())
                    using (var cancel = new CancellationTokenSource(10000))
                    {
                        Task<AccountUsage> pending = session.ReadAsync(fake, storage, TestAccount("wait"), cancel.Token);
                        string pidFile = Path.Combine(storage, "fake-usage.pid");
                        bool started = SpinWait.SpinUntil(() => File.Exists(pidFile) || pending.IsCompleted, 5000);
                        cancel.Cancel();
                        bool cancelled = false;
                        try { pending.GetAwaiter().GetResult(); }
                        catch (OperationCanceledException) { cancelled = true; }
                        Check(started && File.Exists(pidFile), "The fake usage process never reached its waiting state.");
                        Check(cancelled, "The usage lookup ignored cancellation.");
                    }
                    CheckClean(storage);
                });
            });

            test("A missing usage CLI creates no temporary directory", delegate
            {
                Fixture(root =>
                {
                    string storage = Path.Combine(root, "not-created");
                    using (var session = new UsageSession())
                    {
                        bool failed = false;
                        try { session.ReadAsync(Path.Combine(root, "missing.exe"), storage, TestAccount("success"), CancellationToken.None).GetAwaiter().GetResult(); }
                        catch (UserError) { failed = true; }
                        Check(failed && !Directory.Exists(storage), "A missing CLI created usage storage.");
                    }
                });
            });
        }
    }
}
