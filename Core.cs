using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CodexAccountSwitcher
{
    public static class Json
    {
        public static string Encode(object value) { return new JavaScriptSerializer().Serialize(value); }
        public static T Decode<T>(string text) { return new JavaScriptSerializer { MaxJsonLength = 4194304 }.Deserialize<T>(text); }
        public static Dictionary<string, object> Object(string text) { return Decode<Dictionary<string, object>>(text); }
        public static string Str(Dictionary<string, object> obj, string key)
        { object value; return obj != null && obj.TryGetValue(key, out value) ? value as string ?? "" : ""; }
        public static Dictionary<string, object> Map(Dictionary<string, object> obj, string key)
        { object value; return obj != null && obj.TryGetValue(key, out value) ? value as Dictionary<string, object> : null; }
    }

    public sealed class UserError : Exception { public UserError(string message) : base(message) { } }

    public sealed class Account
    {
        public string Id { get; set; }
        public string Label { get; set; }
        public string Email { get; set; }
        public string Plan { get; set; }
        public string Workspace { get; set; }
        public string SavedAt { get; set; }
        public string Auth { get; set; }
        public bool PendingLogin { get; set; }
        public string DisplayName { get { return String.IsNullOrWhiteSpace(Label) ? Email : Label; } }
        public string Detail { get { return Email + "  ·  " + Plan.ToUpperInvariant(); } }
        public string WorkspaceDisplay { get { return "工作區  " + Workspace; } }

        // JWT claims are display metadata, not a verification of the login's validity.
        public static Account Parse(string raw)
        {
            try
            {
                var root = Json.Object(raw);
                if (root == null) throw new Exception();
                string mode = Json.Str(root, "auth_mode");
                if (mode != "" && mode != "chatgpt") throw new Exception();
                if (!String.IsNullOrEmpty(Json.Str(root, "OPENAI_API_KEY"))) throw new Exception();
                var tokens = Json.Map(root, "tokens");
                string idToken = Json.Str(tokens, "id_token");
                if (String.IsNullOrWhiteSpace(Json.Str(tokens, "access_token")) ||
                    String.IsNullOrWhiteSpace(Json.Str(tokens, "refresh_token"))) throw new Exception();
                var parts = idToken.Split('.');
                if (parts.Length != 3) throw new Exception();
                string payload = parts[1].Replace('-', '+').Replace('_', '/');
                payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
                var claims = Json.Object(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
                var authClaims = Json.Map(claims, "https://api.openai.com/auth");
                string subject = Json.Str(claims, "sub");
                string workspace = Json.Str(tokens, "account_id");
                if (String.IsNullOrWhiteSpace(subject) || String.IsNullOrWhiteSpace(workspace)) throw new Exception();
                string identity;
                using (var sha = SHA256.Create())
                    identity = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(subject + "\n" + workspace))).Replace("-", "");
                string email = Json.Str(claims, "email");
                return new Account { Id = identity, Email = email == "" ? "ChatGPT 帳號" : email,
                    Label = "", Workspace = workspace, Plan = Json.Str(authClaims, "chatgpt_plan_type") == "" ? "ChatGPT" : Json.Str(authClaims, "chatgpt_plan_type"),
                    Auth = raw, SavedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm") };
            }
            catch { throw new UserError("登入資料格式不支援或不完整。請使用「新增帳號」重新登入 ChatGPT。"); }
        }
    }

    public static class Files
    {
        public static void PrivateDirectory(string path)
        {
            Directory.CreateDirectory(path);
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            Directory.SetAccessControl(path, security);
        }

        public static string ReadAuth(string path)
        {
            if (!File.Exists(path)) return null;
            if (new FileInfo(path).Length > 1048576) throw new UserError("登入快取異常，請先在 Codex 重新登入。");
            return File.ReadAllText(path, Encoding.UTF8);
        }

        public static void AtomicWrite(string path, byte[] bytes)
        {
            string temp = path + ".switch-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }

    public sealed class VaultData
    {
        public int Version { get; set; }
        public List<Account> Accounts { get; set; }
        public string PreviousAuth { get; set; }
        public VaultData() { Version = 1; Accounts = new List<Account>(); }
    }

    public sealed class Vault
    {
        private readonly string path;
        private string committed;
        private static readonly byte[] entropy = Encoding.UTF8.GetBytes("CodexAccountSwitcher/v1");
        public VaultData Data { get; private set; }
        public Vault(string directory)
        {
            Files.PrivateDirectory(directory);
            path = Path.Combine(directory, "accounts.dpapi");
            if (!File.Exists(path)) { Data = new VaultData(); committed = Json.Encode(Data); return; }
            try
            {
                byte[] clear = ProtectedData.Unprotect(File.ReadAllBytes(path), entropy, DataProtectionScope.CurrentUser);
                try { Data = Json.Decode<VaultData>(Encoding.UTF8.GetString(clear)); }
                finally { Array.Clear(clear, 0, clear.Length); }
                if (Data == null || Data.Version != 1 || Data.Accounts == null) throw new Exception();
                foreach (var item in Data.Accounts)
                    if (Account.Parse(item.Auth).Id != item.Id) throw new Exception();
                committed = Json.Encode(Data);
            }
            catch { throw new UserError("無法解密帳號庫。請使用建立它的 Windows 使用者；原始帳號庫已保留。"); }
        }
        public void Save()
        {
            byte[] clear = Encoding.UTF8.GetBytes(Json.Encode(Data));
            try { Files.AtomicWrite(path, ProtectedData.Protect(clear, entropy, DataProtectionScope.CurrentUser)); committed = Json.Encode(Data); }
            catch { Data = Json.Decode<VaultData>(committed); throw; }
            finally { Array.Clear(clear, 0, clear.Length); }
        }
        public Account Upsert(string raw, string label)
        {
            Account fresh = Account.Parse(raw);
            Account previous = Data.Accounts.FirstOrDefault(a => a.Id == fresh.Id);
            fresh.Label = label == null ? (previous == null ? "" : previous.Label) : label.Trim();
            if (fresh.Label.Length > 60) throw new UserError("帳號名稱最多 60 個字元。");
            if (previous != null) Data.Accounts[Data.Accounts.IndexOf(previous)] = fresh;
            else Data.Accounts.Add(fresh);
            return fresh;
        }
        public void Remove(string id)
        {
            Data.Accounts.RemoveAll(a => a.Id == id);
            if (Data.PreviousAuth != null && Account.Parse(Data.PreviousAuth).Id == id) Data.PreviousAuth = null;
            Save();
        }
    }

    public static class Installation
    {
        public static string CodexHome
        {
            get { return Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"); }
        }
        public static List<Process> Running()
        {
            // A separate CLI can also refresh shared credentials, so wait for it too.
            int session = Process.GetCurrentProcess().SessionId;
            return Process.GetProcesses().Where(p =>
            {
                try { return p.SessionId == session && (p.ProcessName.Equals("codex", StringComparison.OrdinalIgnoreCase) || p.ProcessName.Equals("chatgpt", StringComparison.OrdinalIgnoreCase)); }
                catch { return false; }
            }).ToList();
        }
        public static bool IsRunning() { return ProcessInspector.Inspect().Count > 0; }
        public static string DesktopExe()
        {
            foreach (var p in Running())
            {
                try
                {
                    string path = p.MainModule.FileName;
                    if ((p.ProcessName.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase) ||
                         path.IndexOf("WindowsApps", StringComparison.OrdinalIgnoreCase) >= 0) && !path.Contains("resources")) return path;
                }
                catch { }
                finally { p.Dispose(); }
            }
            // Handles the Store package and its branding change from Codex to ChatGPT.
            string packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
            try
            {
                foreach (string folder in Directory.GetDirectories(packages, "OpenAI.Codex_*").OrderByDescending(x => x))
                    foreach (string name in new[] { "ChatGPT.exe", "Codex.exe" })
                    { string path = Path.Combine(folder, "app", name); if (File.Exists(path)) return path; }
            }
            catch { }
            return null;
        }
        public static string CliExe()
        {
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
            if (Directory.Exists(root))
                foreach (string folder in Directory.GetDirectories(root).OrderByDescending(Directory.GetLastWriteTimeUtc))
                { string path = Path.Combine(folder, "codex.exe"); if (File.Exists(path)) return path; }
            string desktop = DesktopExe();
            if (desktop != null)
            { string path = Path.Combine(Path.GetDirectoryName(desktop), "resources", "codex.exe"); if (File.Exists(path)) return path; }
            return null;
        }
        public static uint Launch(string exe)
        {
            return DesktopLauncher.Launch(DesktopLauncher.Resolve(exe), DesktopLauncher.ActivatePackage,
                path => { using (var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })) return process == null ? 0 : (uint)process.Id; });
        }
        public static void CheckConfig(string codexHome, Account target)
        {
            string config = Path.Combine(codexHome, "config.toml");
            if (!File.Exists(config)) return;
            foreach (string raw in File.ReadAllLines(config))
            {
                string line = raw.Trim();
                if (line.StartsWith("[")) break; // These settings are root-level TOML keys.
                var m = Regex.Match(line, "^(cli_auth_credentials_store|forced_login_method|forced_chatgpt_workspace_id)\\s*=\\s*[\"']([^\"']+)[\"']");
                if (!m.Success) continue;
                string key = m.Groups[1].Value, value = m.Groups[2].Value;
                if (key == "cli_auth_credentials_store" && value != "file")
                    throw new UserError("此工具只支援檔案登入快取。目前 Codex 使用 " + value + " 儲存方式，尚未切換。");
                if (key == "forced_login_method" && value != "chatgpt")
                    throw new UserError("Codex 設定限制使用其他登入方式，尚未切換。");
                if (key == "forced_chatgpt_workspace_id" && target != null && value != target.Workspace)
                    throw new UserError("這個帳號的工作區不符合 Codex 設定限制，尚未切換。");
            }
        }
    }

    public sealed class SwitchService
    {
        private readonly Vault vault;
        private readonly string home;
        private readonly Func<bool> busy;
        public SwitchService(Vault vault, string home, Func<bool> busy) { this.vault = vault; this.home = home; this.busy = busy; }
        public void Apply(string targetRaw, bool restoring = false)
        {
            Account target = Account.Parse(targetRaw);
            Installation.CheckConfig(home, target);
            if (busy()) throw new UserError("仍有 Codex 桌面、VS Code 擴充功能或 CLI 程序正在執行，尚未套用帳號。請關閉對應來源後再試。");
            string path = Path.Combine(home, "auth.json");
            string old = Files.ReadAuth(path);
            var savedTarget = vault.Data.Accounts.FirstOrDefault(a => a.Id == target.Id);
            bool freshLogin = savedTarget != null && savedTarget.PendingLogin && savedTarget.Auth == targetRaw;
            if (old != null && Account.Parse(old).Id == target.Id && !freshLogin && !restoring)
            {
                // A saved copy of the active account may have an obsolete refresh token.
                vault.Upsert(old, null); vault.Save(); return;
            }
            if (!Directory.Exists(home)) throw new UserError("找不到 Codex 資料夾，請先啟動 Codex 並登入。");
            if (old != null)
            {
                // Keep a newly logged-in session when replacing the same user's old session.
                string oldId = Account.Parse(old).Id;
                var savedOld = vault.Data.Accounts.FirstOrDefault(a => a.Id == oldId);
                if (oldId != target.Id && (savedOld == null || !savedOld.PendingLogin)) vault.Upsert(old, null);
            }
            vault.Data.PreviousAuth = old;
            vault.Save(); // If the backup cannot be saved, do not touch active credentials.
            if (busy() || Files.ReadAuth(path) != old)
                throw new UserError("登入狀態剛剛改變，已取消切換。請完全結束 Codex 後重試。");
            Files.AtomicWrite(path, Encoding.UTF8.GetBytes(targetRaw));
            string confirmed;
            try { confirmed = Files.ReadAuth(path); }
            catch (IOException) { throw new UserError("登入快取已寫入，但無法讀回確認，尚未自動開啟 Codex。請重新整理檢查，或使用「切回上一個」復原。"); }
            catch (UnauthorizedAccessException) { throw new UserError("登入快取已寫入，但讀回時被拒絕存取，尚未自動開啟 Codex。請檢查檔案權限後重新整理。"); }
            if (confirmed != targetRaw)
                throw new UserError("登入快取寫入後被其他程序變更，尚未自動開啟 Codex。請關閉 VS Code 的 Codex 擴充功能與所有 Codex 程序後重試。");
        }
    }

    public sealed class LoginSession : IDisposable
    {
        private Process process;
        private string folder;
        private readonly object gate = new object();
        public async Task<string> Login(string cli, string storage, Action<string> link, CancellationToken cancellation)
        {
            if (cli == null) throw new UserError("找不到 Codex 登入工具，請先安裝並啟動 Codex 桌面 App。");
            folder = Path.Combine(storage, "login-" + Guid.NewGuid().ToString("N"));
            Files.PrivateDirectory(folder);
            var start = new ProcessStartInfo(cli, "app-server --stdio -c cli_auth_credentials_store=\"file\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true,
                RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = folder };
            start.EnvironmentVariables["CODEX_HOME"] = folder;
            foreach (string key in new[] { "OPENAI_API_KEY", "CODEX_API_KEY", "CODEX_ACCESS_TOKEN", "OPENAI_IDENTITY_TOKEN_FILE", "OPENAI_FEDERATION_RULE_ID" }) start.EnvironmentVariables.Remove(key);
            try
            {
                process = new Process { StartInfo = start };
                process.ErrorDataReceived += delegate { }; // Drain stderr; never persist auth URLs or tokens.
                if (!process.Start()) throw new UserError("無法啟動登入工具。");
                process.BeginErrorReadLine();
                using (cancellation.Register(Stop))
                {
                    Send(new { id = 1, method = "initialize", @params = new { clientInfo = new { name = "codex_account_switcher", title = "Codex Account Switcher", version = "1.0.0" } } });
                    bool started = false;
                    string loginId = null;
                    while (true)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        string line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false);
                        if (line == null) throw new UserError("登入程序已結束，請重新新增帳號。");
                        var msg = Json.Object(line);
                        if (msg.ContainsKey("error")) throw new UserError("Codex 無法開始登入。請確認網路連線，並關閉其他正在進行的登入視窗後重試。");
                        if (msg.ContainsKey("id") && Convert.ToString(msg["id"]) == "1" && !started)
                        {
                            started = true;
                            Send(new { method = "initialized" });
                            Send(new { id = 2, method = "account/login/start", @params = new { type = "chatgpt" } });
                        }
                        else if (msg.ContainsKey("id") && Convert.ToString(msg["id"]) == "2")
                        {
                            var result = Json.Map(msg, "result");
                            string url = Json.Str(result, "authUrl");
                            loginId = Json.Str(result, "loginId");
                            Uri uri;
                            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || uri.Scheme != "https" ||
                                !(uri.Host == "auth.openai.com" || uri.Host == "auth0.openai.com" || uri.Host == "chatgpt.com"))
                                throw new UserError("登入工具回傳了不支援的網址，請更新此工具。");
                            link(url);
                        }
                        else if (Json.Str(msg, "method") == "account/login/completed")
                        {
                            var result = Json.Map(msg, "params");
                            if (loginId == null || Json.Str(result, "loginId") != loginId) continue;
                            object success;
                            if (!result.TryGetValue("success", out success) || !Object.Equals(success, true))
                                throw new UserError("登入未完成，請重試並完成官方瀏覽器登入。");
                            string raw = Files.ReadAuth(Path.Combine(folder, "auth.json"));
                            if (raw == null) throw new UserError("登入未產生檔案快取，可能有系統登入儲存政策限制。");
                            Account.Parse(raw);
                            return raw;
                        }
                    }
                }
            }
            catch (Exception)
            { cancellation.ThrowIfCancellationRequested(); throw; }
            finally { Dispose(); }
        }
        private void Send(object obj)
        { lock (gate) { process.StandardInput.WriteLine(Json.Encode(obj)); process.StandardInput.Flush(); } }
        private void Stop()
        {
            lock (gate)
            { try { if (process != null && !process.HasExited) { process.Kill(); process.WaitForExit(3000); } } catch { } }
        }
        public void Dispose()
        {
            Stop();
            if (process != null) { process.Dispose(); process = null; }
            if (folder != null && Directory.Exists(folder))
            {
                // Only remove the GUID directory created by this login instance.
                try { Directory.Delete(folder, true); }
                catch { throw new UserError("登入暫存資料無法清除。請關閉本工具後刪除資料夾：" + folder); }
                folder = null;
            }
        }
    }
}
