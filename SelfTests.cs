using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CodexAccountSwitcher
{
    public static class SelfTests
    {
        public static string Auth(string user, string workspace, string email, string plan, string refresh)
        {
            var claims = new Dictionary<string, object> { { "sub", user }, { "email", email },
                { "https://api.openai.com/auth", new Dictionary<string, object> { { "chatgpt_plan_type", plan } } } };
            string payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(Json.Encode(claims))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return Json.Encode(new { auth_mode = "chatgpt", tokens = new { id_token = "test." + payload + ".signature", access_token = "fake-access", refresh_token = refresh, account_id = workspace }, last_refresh = "2000-01-01T00:00:00Z" });
        }
        private static void Check(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
        private static void Reject(Action operation) { bool thrown = false; try { operation(); } catch { thrown = true; } Check(thrown); }
        public static int Run(string output)
        {
            string root = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output)), "test-data-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var passed = new List<string>(); var failed = new List<string>();
            Action<string, Action> test = (name, action) => { try { action(); passed.Add(name); } catch (Exception ex) { failed.Add(name + ": " + ex.ToString()); } };
            ProcessInspectorTests.Register(test);
            SwitchWorkflowTests.Register(test);
            UsageModelTests.Register(test);
            UsageSessionTests.Register(test);
            AccountOrderTests.Register(test);
            AccountUiTests.Register(test);
            AutoSwitchTests.Register(test);
            QuotaResetTests.Register(test);
            QuotaResetIntegrationTests.Register(test);
            ManagedRuntimeClientTests.Register(test);
            ManagedBridgeTests.Register(test);
            DesktopNativeTests.Register(test);
            DesktopLocalWorkPolicyTests.Register(test);
            DesktopWebSocketTests.Register(test);
            DesktopSessionTests.Register(test);
            RuntimeCoordinatorTests.Register(test);
            DesktopAutoStartTests.Register(test);
            ContinuationTicketTests.Register(test);
            ContinuationAdmissionTests.Register(test);
            DesktopConversationClientTests.Register(test);
            DesktopContinuationWorkflowTests.Register(test);
            DesktopAppRelayTests.Register(test);
            string a = Auth("u1", "ws1", "one@example.com", "plus", "fake-refresh-old");
            string b = Auth("u2", "ws2", "two@example.com", "pro", "fake-refresh-b");
            string updated = Auth("u1", "ws1", "one@example.com", "plus", "fake-refresh-rotated");
            string package = Path.Combine(root, "package 測試", "OpenAI.Codex_1.0.0.0_x64__2p2nqsd0c76g0");
            string packagedExe = Path.Combine(package, "app", "ChatGPT.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(packagedExe)); File.WriteAllText(packagedExe, "test-only");
            string manifest = "<Package xmlns=\"http://schemas.microsoft.com/appx/manifest/foundation/windows10\"><Applications><Application Id=\"Helper\" Executable=\"app/resources/helper.exe\"/><Application Id=\"App\" Executable=\"app/ChatGPT.exe\"/></Applications></Package>";
            File.WriteAllText(Path.Combine(package, "AppxManifest.xml"), manifest);
            test("Packaged executable resolves the matching application, not the first manifest entry", delegate
            { Check(DesktopLauncher.Resolve(packagedExe).AppUserModelId == "OpenAI.Codex_2p2nqsd0c76g0!App"); });
            test("Package upgrades retain a stable activation identity", delegate
            {
                string newer = Path.Combine(root, "OpenAI.Codex_2.0.0.0_x64__2p2nqsd0c76g0");
                Directory.CreateDirectory(Path.Combine(newer, "app")); string exe = Path.Combine(newer, "app", "ChatGPT.exe"); File.WriteAllText(exe, "test-only");
                File.WriteAllText(Path.Combine(newer, "AppxManifest.xml"), manifest);
                Check(DesktopLauncher.Resolve(exe).AppUserModelId == DesktopLauncher.Resolve(packagedExe).AppUserModelId);
            });
            test("Packaged launch dispatches only to Windows activation", delegate
            {
                bool direct = false; string activated = null;
                uint result = DesktopLauncher.Launch(DesktopLauncher.Resolve(packagedExe), id => { activated = id; return 42; }, exe => { direct = true; return 1; });
                Check(result == 42 && activated == "OpenAI.Codex_2p2nqsd0c76g0!App" && !direct);
            });
            test("Failed package activation never falls back to direct executable launch", delegate
            {
                bool direct = false;
                Reject(() => DesktopLauncher.Launch(DesktopLauncher.Resolve(packagedExe), id => { throw new UserError("Test failure"); }, exe => { direct = true; return 1; }));
                Check(!direct);
            });
            test("Unpackaged applications retain normal executable launch", delegate
            {
                string path = Path.Combine(root, "portable.exe"); File.WriteAllText(path, "test-only"); string launched = null;
                DesktopLauncher.Launch(DesktopLauncher.Resolve(path), id => { throw new Exception(); }, exe => { launched = exe; return 1; }); Check(launched == path);
            });
            test("Unrecognized WindowsApps executables fail safely", delegate
            {
                string folder = Path.Combine(root, "WindowsApps", "UnknownPackage", "app"); Directory.CreateDirectory(folder);
                string exe = Path.Combine(folder, "ChatGPT.exe"); File.WriteAllText(exe, "test-only"); Reject(() => DesktopLauncher.Resolve(exe));
                File.WriteAllText(Path.Combine(package, "app", "other.exe"), "test-only"); Reject(() => DesktopLauncher.Resolve(Path.Combine(package, "app", "other.exe")));
            });
            test("An empty current-user package registration does not invent an installed app", delegate
            {
                Check(DesktopLauncher.InstalledExecutable(new string[0], name => { throw new Exception("No package path should be requested"); }) == null);
            });
            test("Registered app discovery works without a running process or a WindowsApps directory scan", delegate
            {
                string fullName = "OpenAI.Codex_1.0.0.1_x64__2p2nqsd0c76g0";
                string folder = InstalledFixture(root, "registered packages 測試", fullName, "ChatGPT.exe", "app/ChatGPT.exe");
                int pathQueries = 0;
                string found = DesktopLauncher.InstalledExecutable(new[] { fullName }, name => { Check(name == fullName); pathQueries++; return folder; });
                Check(found == Path.Combine(folder, "app", "ChatGPT.exe") && pathQueries == 1);
            });
            test("Registered app discovery supports Codex executable branding", delegate
            {
                string fullName = "OpenAI.Codex_1.0.0.2_x64__2p2nqsd0c76g0";
                string folder = InstalledFixture(root, "branding", fullName, "Codex.exe", "app/Codex.exe");
                File.WriteAllText(Path.Combine(folder, "app", "ChatGPT.exe"), "obsolete-branding-test-only");
                Check(DesktopLauncher.InstalledExecutable(new[] { fullName }, name => folder) == Path.Combine(folder, "app", "Codex.exe"));
            });
            test("Registered app discovery selects the newest numeric package version", delegate
            {
                string older = "OpenAI.Codex_1.0.0.9_x64__2p2nqsd0c76g0", newer = "OpenAI.Codex_1.0.0.10_x64__2p2nqsd0c76g0";
                var folders = new Dictionary<string, string> {
                    { older, InstalledFixture(root, "version order", older, "ChatGPT.exe", "app/ChatGPT.exe") },
                    { newer, InstalledFixture(root, "version order", newer, "ChatGPT.exe", "app/ChatGPT.exe") } };
                Check(DesktopLauncher.InstalledExecutable(new[] { older, newer }, name => folders[name]) == Path.Combine(folders[newer], "app", "ChatGPT.exe"));
            });
            test("One missing or unreadable registered package does not hide another installed app", delegate
            {
                string unreadable = "OpenAI.Codex_1.0.0.12_x64__2p2nqsd0c76g0", missing = "OpenAI.Codex_1.0.0.11_x64__2p2nqsd0c76g0";
                string valid = "OpenAI.Codex_1.0.0.10_x64__2p2nqsd0c76g0";
                string folder = InstalledFixture(root, "stale registration", valid, "ChatGPT.exe", "app/ChatGPT.exe");
                string absent = Path.Combine(root, "stale registration", missing); Directory.CreateDirectory(absent);
                Check(DesktopLauncher.InstalledExecutable(new[] { unreadable, missing, valid }, name => {
                    if (name == unreadable) throw new IOException("Synthetic registration changed during discovery");
                    return name == missing ? absent : folder;
                }) == Path.Combine(folder, "app", "ChatGPT.exe"));
            });
            test("Registered app discovery rejects unrelated package families", delegate
            {
                string unrelated = "Other.Codex_1.0.0.1_x64__2p2nqsd0c76g0";
                string folder = InstalledFixture(root, "unrelated registration", unrelated, "ChatGPT.exe", "app/ChatGPT.exe");
                Check(DesktopLauncher.InstalledExecutable(new[] { unrelated }, name => folder) == null);
            });
            test("Registered app discovery rejects executables that do not match a manifest application", delegate
            {
                string fullName = "OpenAI.Codex_1.0.0.3_x64__2p2nqsd0c76g0";
                string folder = InstalledFixture(root, "manifest mismatch", fullName, "ChatGPT.exe", "app/resources/codex.exe");
                Check(DesktopLauncher.InstalledExecutable(new[] { fullName }, name => folder) == null);
                folder = InstalledFixture(root, "helper only", fullName, "helper.exe", "app/helper.exe");
                Check(DesktopLauncher.InstalledExecutable(new[] { fullName }, name => folder) == null);
            });
            test("Reject malformed / API-key / partial credentials", delegate
            {
                Reject(() => Account.Parse("{}")); Reject(() => Account.Parse("not-json"));
                Reject(() => Account.Parse("{\"auth_mode\":\"apikey\",\"OPENAI_API_KEY\":\"fake\"}"));
                Reject(() => Account.Parse(a.Replace("fake-access", "")));
            });
            test("Identity separates users sharing a workspace", delegate
            { Check(Account.Parse(a).Id != Account.Parse(Auth("u2", "ws1", "two@example.com", "plus", "f")).Id); });
            test("DPAPI round trip; no readable token or email on disk", delegate
            {
                string dir = Path.Combine(root, "vault-roundtrip"); var v = new Vault(dir); v.Upsert(a, "Personal"); v.Save();
                string bytes = Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(dir, "accounts.dpapi")));
                Check(!bytes.Contains("fake-refresh") && !bytes.Contains("one@example.com"));
                var loaded = new Vault(dir); Check(loaded.Data.Accounts.Single().Auth == a); Check(loaded.Data.Accounts.Single().Label == "Personal");
            });
            test("Updating a refreshed token preserves label and deduplicates", delegate
            { var v = new Vault(Path.Combine(root, "dedupe")); v.Upsert(a, "Personal"); v.Upsert(updated, null); Check(v.Data.Accounts.Count == 1 && v.Data.Accounts[0].Auth == updated && v.Data.Accounts[0].Label == "Personal"); });
            test("Monitoring and interruption require opt-in and persist in the encrypted vault", delegate
            {
                string folder = Path.Combine(root, "monitor-settings"); var v = new Vault(folder);
                Check(!v.Data.AutoMonitorEnabled && !v.Data.AllowInterrupt);
                v.Data.AutoMonitorEnabled = true; v.Data.AllowInterrupt = true; v.Save();
                var loaded = new Vault(folder); Check(loaded.Data.AutoMonitorEnabled && loaded.Data.AllowInterrupt);
            });
            test("Corrupted vault fails without overwriting original", delegate
            { string dir = Path.Combine(root, "corrupt"); Directory.CreateDirectory(dir); string path = Path.Combine(dir, "accounts.dpapi"); File.WriteAllText(path, "broken"); Reject(() => new Vault(dir)); Check(File.ReadAllText(path) == "broken"); });
            test("Running app blocks credential changes", delegate
            { string home = Path.Combine(root, "busy-home"); Directory.CreateDirectory(home); string path = Path.Combine(home, "auth.json"); File.WriteAllText(path, a); var v = new Vault(Path.Combine(root, "busy-vault")); Reject(() => new SwitchService(v, home, () => true).Apply(b)); Check(File.ReadAllText(path) == a); });
            test("Automatic switching rejects a changed source identity before changing the vault or login", delegate
            {
                string home = Path.Combine(root, "automatic-source-home"); Directory.CreateDirectory(home);
                string path = Path.Combine(home, "auth.json"); File.WriteAllText(path, b);
                var v = new Vault(Path.Combine(root, "automatic-source-vault")); v.Upsert(a, null); v.Upsert(b, null); v.Save();
                string saved = Json.Encode(v.Data);
                Reject(() => new SwitchService(v, home, () => false).Apply(a, false, Account.Parse(a).Id));
                Check(File.ReadAllText(path) == b && Json.Encode(v.Data) == saved);
            });
            test("Automatic switching accepts token rotation for the same source and preserves its latest credentials", delegate
            {
                string home = Path.Combine(root, "automatic-rotation-home"); Directory.CreateDirectory(home);
                string path = Path.Combine(home, "auth.json"); File.WriteAllText(path, updated);
                var v = new Vault(Path.Combine(root, "automatic-rotation-vault")); v.Upsert(a, null); v.Upsert(b, null); v.Save();
                new SwitchService(v, home, () => false).Apply(b, false, Account.Parse(a).Id);
                Check(File.ReadAllText(path) == b && v.Data.PreviousAuth == updated);
            });
            test("Switch, preserve rotated current token, and restore", delegate
            {
                string home = Path.Combine(root, "switch-home"); Directory.CreateDirectory(home); string path = Path.Combine(home, "auth.json"); File.WriteAllText(path, updated);
                var v = new Vault(Path.Combine(root, "switch-vault")); v.Upsert(a, "Personal"); v.Upsert(b, "Work"); v.Save();
                var service = new SwitchService(v, home, () => false); service.Apply(b); Check(File.ReadAllText(path) == b);
                Check(v.Data.PreviousAuth == updated); Check(v.Data.Accounts.Single(x => x.Id == Account.Parse(a).Id).Auth == updated);
                string previous = v.Data.PreviousAuth; service.Apply(previous); Check(File.ReadAllText(path) == updated); Check(v.Data.PreviousAuth == b);
            });
            test("Selecting active account never installs its stale token", delegate
            { string home = Path.Combine(root, "same-home"); Directory.CreateDirectory(home); string path = Path.Combine(home, "auth.json"); File.WriteAllText(path, updated); var v = new Vault(Path.Combine(root, "same-vault")); new SwitchService(v, home, () => false).Apply(a); Check(File.ReadAllText(path) == updated); });
            test("A new login can replace and restore the same account's expired session", delegate
            {
                string home = Path.Combine(root, "relogin-home"); Directory.CreateDirectory(home); string path = Path.Combine(home, "auth.json"); File.WriteAllText(path, a);
                var v = new Vault(Path.Combine(root, "relogin-vault")); v.Upsert(updated, "Personal").PendingLogin = true; v.Save();
                var service = new SwitchService(v, home, () => false); service.Apply(updated); Check(File.ReadAllText(path) == updated); Check(v.Data.Accounts[0].Auth == updated); Check(v.Data.PreviousAuth == a);
                service.Apply(v.Data.PreviousAuth, true); Check(File.ReadAllText(path) == a);
            });
            test("Leaving an account preserves its newer pending login", delegate
            {
                string home = Path.Combine(root, "pending-home"); Directory.CreateDirectory(home); File.WriteAllText(Path.Combine(home, "auth.json"), a);
                var v = new Vault(Path.Combine(root, "pending-vault")); v.Upsert(updated, "Personal").PendingLogin = true; v.Upsert(b, "Work"); v.Save();
                new SwitchService(v, home, () => false).Apply(b);
                var saved = v.Data.Accounts.Single(x => x.Id == Account.Parse(a).Id);
                Check(saved.PendingLogin && saved.Auth == updated && v.Data.PreviousAuth == a);
            });
            test("Concurrent auth change is detected before replacement", delegate
            {
                string home = Path.Combine(root, "race-home"); Directory.CreateDirectory(home); string path = Path.Combine(home, "auth.json"); File.WriteAllText(path, a);
                var v = new Vault(Path.Combine(root, "race-vault")); int checks = 0;
                Reject(() => new SwitchService(v, home, () => { if (++checks == 2) File.WriteAllText(path, updated); return false; }).Apply(b)); Check(File.ReadAllText(path) == updated);
            });
            test("Keyring / forced workspace settings block unsupported switches", delegate
            {
                string home = Path.Combine(root, "config-home"); Directory.CreateDirectory(home); string path = Path.Combine(home, "config.toml");
                File.WriteAllText(path, "cli_auth_credentials_store = \"keyring\"\n"); Reject(() => Installation.CheckConfig(home, Account.Parse(b)));
                File.WriteAllText(path, "forced_chatgpt_workspace_id = 'ws1'\n"); Reject(() => Installation.CheckConfig(home, Account.Parse(b)));
                File.WriteAllText(path, "cli_auth_credentials_store = \"file\"\n"); Installation.CheckConfig(home, Account.Parse(b));
            });
            test("Backup failure leaves active credentials intact", delegate
            {
                string home = Path.Combine(root, "locked-home"); Directory.CreateDirectory(home); string path = Path.Combine(home, "auth.json"); File.WriteAllText(path, a);
                string dir = Path.Combine(root, "locked-vault"); var v = new Vault(dir); v.Save();
                using (var locked = File.Open(Path.Combine(dir, "accounts.dpapi"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    Reject(() => new SwitchService(v, home, () => false).Apply(b)); Check(File.ReadAllText(path) == a);
                Check(v.Data.Accounts.Count == 0 && v.Data.PreviousAuth == null);
                Check(Directory.GetFiles(dir, "*.switch-*").Length == 0);
            });
            test("Removing an account also clears its recovery copy", delegate
            { var v = new Vault(Path.Combine(root, "remove-vault")); v.Upsert(a, null); v.Data.PreviousAuth = a; v.Remove(Account.Parse(a).Id); Check(v.Data.Accounts.Count == 0 && v.Data.PreviousAuth == null); });
            test("Login helper rejects missing CLI without touching current auth", delegate
            { using (var login = new LoginSession()) Reject(() => login.Login(null, root, url => { }, CancellationToken.None).GetAwaiter().GetResult()); });
            string fake = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FakeCodex.exe");
            if (File.Exists(fake))
            {
                test("Isolated browser login completes through JSON-RPC and cleans temporary credentials", delegate
                {
                    string url = null; string result;
                    using (var login = new LoginSession()) using (var timeout = new CancellationTokenSource(10000))
                        result = login.Login(fake, root, value => url = value, timeout.Token).GetAwaiter().GetResult();
                    Check(url == "https://auth.openai.com/test-only"); Check(Account.Parse(result).Email == "test@example.com");
                    Check(Directory.GetDirectories(root, "login-*").Length == 0);
                });
                test("Cancel isolated login and clean temporary credentials", delegate
                {
                    using (var login = new LoginSession()) using (var timeout = new CancellationTokenSource())
                    {
                        bool cancelled = false;
                        try { login.Login(fake, root, value => timeout.Cancel(), timeout.Token).GetAwaiter().GetResult(); }
                        catch (OperationCanceledException) { cancelled = true; }
                        Check(cancelled);
                    }
                    Check(Directory.GetDirectories(root, "login-*").Length == 0);
                });
            }
            File.WriteAllText(output, Json.Encode(new { passed = passed, failed = failed, count = passed.Count + failed.Count }), Encoding.UTF8);
            // This path is a freshly generated child of the explicit test output directory.
            Directory.Delete(root, true);
            return failed.Count == 0 ? 0 : 1;
        }
        private static string InstalledFixture(string root, string fixture, string fullName, string executable, string declaredExecutable)
        {
            string folder = Path.Combine(root, fixture, fullName), app = Path.Combine(folder, "app", executable);
            Directory.CreateDirectory(Path.GetDirectoryName(app)); File.WriteAllText(app, "test-only");
            string manifest = "<Package xmlns=\"http://schemas.microsoft.com/appx/manifest/foundation/windows10\"><Applications><Application Id=\"App\" Executable=\"" + declaredExecutable + "\"/></Applications></Package>";
            File.WriteAllText(Path.Combine(folder, "AppxManifest.xml"), manifest);
            return folder;
        }
    }
}
