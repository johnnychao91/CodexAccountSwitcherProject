using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CodexAccountSwitcher
{
    // A separate, short-lived app-server receives only an access token in memory.
    // In particular, it must never refresh or replace a saved account's credentials.
    public sealed class UsageSession : IDisposable
    {
        private const string Unavailable = "無法查詢使用上限。請確認網路連線與 Codex 版本後重試。";
        private const string Unsupported = "目前的 Codex 登入工具不支援額度查詢。請更新 Codex 後重試。";
        private const string SignInRequired = "這個帳號的登入已失效或無法驗證。請重新登入這個帳號後再查詢。";
        private readonly object gate = new object();
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private Process process;
        private string folder;
        private string storageRoot;
        private string folderName;
        private bool started;
        private bool disposed;

        public async Task<AccountUsage> ReadAsync(string cli, string storage, Account account, CancellationToken cancellation)
        {
            CancellationTokenSource timeout = null;
            CancellationTokenSource linked = null;
            bool ownsSession = false;
            try
            {
                lock (gate)
                {
                    if (disposed || started) throw new UserError("這次額度查詢已結束，請重新查詢。");
                    started = true;
                    ownsSession = true;
                }
                cancellation.ThrowIfCancellationRequested();
                if (String.IsNullOrWhiteSpace(cli) || !File.Exists(cli))
                    throw new UserError("找不到 Codex 登入工具，請先安裝並啟動 Codex 桌面 App。");
                cli = Path.GetFullPath(cli);

                string accessToken;
                try
                {
                    accessToken = Json.Str(Json.Map(Json.Object(account.Auth), "tokens"), "access_token");
                    if (String.IsNullOrWhiteSpace(accessToken) || String.IsNullOrWhiteSpace(account.Workspace))
                        throw new Exception();
                }
                catch { throw new UserError(SignInRequired); }

                timeout = new CancellationTokenSource(30000);
                linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, timeout.Token, lifetime.Token);
                CancellationToken token = linked.Token;
                Process child;
                lock (gate)
                {
                    token.ThrowIfCancellationRequested();
                    storageRoot = Path.GetFullPath(storage).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    folderName = "usage-" + Guid.NewGuid().ToString("N");
                    folder = Path.GetFullPath(Path.Combine(storageRoot + Path.DirectorySeparatorChar, folderName));
                    Files.PrivateDirectory(folder);
                    // No credentials are written here. Disable persistent credentials and telemetry.
                    File.WriteAllText(Path.Combine(folder, "config.toml"),
                        "cli_auth_credentials_store = \"ephemeral\"\r\n" +
                        "[analytics]\r\nenabled = false\r\n" +
                        "[otel]\r\nexporter = \"none\"\r\ntrace_exporter = \"none\"\r\nlog_user_prompt = false\r\n" +
                        "[features]\r\nplugins = false\r\nremote_plugin = false\r\napps = false\r\ndaemon_auto_start = false\r\n",
                        new UTF8Encoding(false));
                    var start = new ProcessStartInfo(cli, "app-server --stdio")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8,
                        WorkingDirectory = folder
                    };
                    start.EnvironmentVariables["CODEX_HOME"] = folder;
                    start.EnvironmentVariables["RUST_LOG"] = "off";
                    foreach (string key in new[] { "OPENAI_API_KEY", "CODEX_API_KEY", "CODEX_ACCESS_TOKEN", "OPENAI_BASE_URL", "OPENAI_IDENTITY_TOKEN_FILE", "OPENAI_FEDERATION_RULE_ID" })
                        start.EnvironmentVariables.Remove(key);
                    process = child = new Process { StartInfo = start };
                    child.ErrorDataReceived += delegate { }; // Drain and discard; never expose server diagnostics.
                    if (!child.Start()) throw new UserError(Unavailable);
                    child.BeginErrorReadLine();
                }

                using (token.Register(Stop))
                {
                    await SendAsync(child, new
                    {
                        id = 1,
                        method = "initialize",
                        @params = new
                        {
                            clientInfo = new { name = "codex_account_switcher", title = "Codex Account Switcher", version = "1.1.0" },
                            capabilities = new { experimentalApi = true }
                        }
                    }, token).ConfigureAwait(false);

                    int expectedId = 1;
                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        Task<string> read = child.StandardOutput.ReadLineAsync();
                        await WaitAsync(read, token).ConfigureAwait(false);
                        string line = read.GetAwaiter().GetResult();
                        if (line == null) throw new UserError(Unavailable);
                        Dictionary<string, object> message;
                        try { message = Json.Object(line); }
                        catch { throw new UserError(Unavailable); }
                        if (message == null) throw new UserError(Unavailable);

                        object id;
                        bool hasId = message.TryGetValue("id", out id) && id != null;
                        string method = Json.Str(message, "method");
                        if (method.Length > 0)
                        {
                            if (!hasId) continue; // Notifications do not complete a request.
                            bool needsSignIn = method == "account/chatgptAuthTokens/refresh";
                            var reply = new
                            {
                                id = id,
                                error = new { code = needsSignIn ? -32000 : -32601, message = needsSignIn ? "Sign-in required." : "Method not found." }
                            };
                            if (needsSignIn)
                            {
                                try { await SendAsync(child, reply, token).ConfigureAwait(false); }
                                catch { token.ThrowIfCancellationRequested(); }
                                throw new UserError(SignInRequired);
                            }
                            await SendAsync(child, reply, token).ConfigureAwait(false);
                            continue;
                        }
                        if (!hasId || Convert.ToString(id, CultureInfo.InvariantCulture) != expectedId.ToString(CultureInfo.InvariantCulture))
                            continue;
                        if (message.ContainsKey("error"))
                        {
                            var error = Json.Map(message, "error");
                            object code;
                            string errorCode = error != null && error.TryGetValue("code", out code) ? Convert.ToString(code, CultureInfo.InvariantCulture) : "";
                            if (errorCode == "-32601" || errorCode == "-32602") throw new UserError(Unsupported);
                            throw new UserError(expectedId == 2 ? SignInRequired : Unavailable);
                        }
                        var result = Json.Map(message, "result");
                        if (result == null) throw new UserError(Unavailable);
                        if (expectedId == 1)
                        {
                            await SendAsync(child, new { method = "initialized" }, token).ConfigureAwait(false);
                            await SendAsync(child, new
                            {
                                id = 2,
                                method = "account/login/start",
                                @params = new { type = "chatgptAuthTokens", accessToken = accessToken, chatgptAccountId = account.Workspace, chatgptPlanType = account.Plan }
                            }, token).ConfigureAwait(false);
                            accessToken = null;
                            expectedId = 2;
                        }
                        else if (expectedId == 2)
                        {
                            if (Json.Str(result, "type") != "chatgptAuthTokens") throw new UserError(Unsupported);
                            await SendAsync(child, new { id = 3, method = "account/rateLimits/read", @params = new { } }, token).ConfigureAwait(false);
                            expectedId = 3;
                        }
                        else
                        {
                            object returnedAccountId;
                            if (result.TryGetValue("accountId", out returnedAccountId) && returnedAccountId != null && !(returnedAccountId is string))
                                throw new UserError(Unavailable);
                            string accountId = Json.Str(result, "accountId");
                            if (accountId.Length > 0 && !String.Equals(accountId, account.Workspace, StringComparison.Ordinal))
                                throw new UserError("額度資料的帳號不符，已取消顯示。請重新登入這個帳號後再查詢。");
                            return AccountUsage.Parse(result);
                        }
                    }
                }
            }
            catch (Exception error)
            {
                cancellation.ThrowIfCancellationRequested();
                if (timeout != null && timeout.IsCancellationRequested)
                    throw new UserError("查詢使用上限逾時，請稍後重試。");
                if (lifetime.IsCancellationRequested) throw new OperationCanceledException("查詢已取消。");
                if (error is UserError) throw;
                throw new UserError(Unavailable);
            }
            finally
            {
                if (linked != null) linked.Dispose();
                if (timeout != null) timeout.Dispose();
                if (ownsSession) Dispose();
            }
        }

        private static async Task SendAsync(Process child, object message, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            await WaitAsync(child.StandardInput.WriteLineAsync(Json.Encode(message)), cancellation).ConfigureAwait(false);
            await WaitAsync(child.StandardInput.FlushAsync(), cancellation).ConfigureAwait(false);
        }

        private static async Task WaitAsync(Task operation, CancellationToken cancellation)
        {
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellation.Register(() => cancelled.TrySetResult(true)))
            {
                if (await Task.WhenAny(operation, cancelled.Task).ConfigureAwait(false) != operation)
                {
                    // Process termination may finish an outstanding pipe operation with an error.
                    ObserveFailure(operation);
                    throw new OperationCanceledException(cancellation);
                }
                await operation.ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
            }
        }

        private static void ObserveFailure(Task operation)
        {
            operation.ContinueWith(task => { var ignored = task.Exception; },
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private void Stop()
        {
            lock (gate)
            {
                try { if (process != null && !process.HasExited) process.Kill(); }
                catch { } // Cleanup below confirms that our own process has exited.
            }
        }

        public void Dispose()
        {
            lock (gate) { disposed = true; }
            lifetime.Cancel();
            lock (gate)
            {
                bool stopped = true;
                if (process != null)
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            // Closing stdin lets a normal stdio session release its resources first.
                            try { process.StandardInput.Close(); }
                            catch { }
                            if (!process.WaitForExit(1000)) process.Kill();
                        }
                        stopped = process.WaitForExit(3000);
                    }
                    catch (InvalidOperationException) { } // The process may never have started.
                    catch { stopped = false; }
                    if (stopped) { process.Dispose(); process = null; }
                }
                if (!stopped) throw new UserError("額度查詢程序尚未結束。請關閉帳號切換器後重試。");
                if (folder != null)
                {
                    try
                    {
                        // This exact GUID directory was created by this session; never delete its parent.
                        string target = Path.GetFullPath(folder);
                        if (!String.Equals(Path.GetDirectoryName(target).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), storageRoot, StringComparison.OrdinalIgnoreCase) ||
                            !String.Equals(Path.GetFileName(target), folderName, StringComparison.Ordinal))
                            throw new InvalidOperationException();
                        // A completed CLI may leave short-lived file handles during shutdown.
                        for (int attempt = 0; ; attempt++)
                        {
                            try
                            {
                                if (Directory.Exists(target)) Directory.Delete(target, true);
                                break;
                            }
                            catch (IOException) { if (attempt == 4) throw; }
                            catch (UnauthorizedAccessException) { if (attempt == 4) throw; }
                            Thread.Sleep(50 << attempt);
                        }
                        folder = null;
                    }
                    catch { throw new UserError("額度查詢暫存資料無法清除。請關閉帳號切換器後重試。"); }
                }
            }
        }
    }
}
