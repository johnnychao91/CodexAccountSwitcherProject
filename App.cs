using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CodexAccountSwitcher
{
    public sealed class AccountRow : INotifyPropertyChanged
    {
        public static readonly TimeSpan UsageMaxAge = TimeSpan.FromMinutes(1);
        public Account Account { get; set; }
        public int Priority { get; set; }
        public string PriorityText { get { return "優先 " + Priority; } }
        public string Name { get { return Account.DisplayName; } }
        public string Initial { get { return Name.Substring(0, 1).ToUpperInvariant(); } }
        public string Detail { get { return Account.Detail; } }
        public string Workspace { get { return Account.WorkspaceDisplay; } }
        public string Saved { get { return "儲存於 " + Account.SavedAt; } }
        public string Status { get; set; }
        public AccountUsage Usage { get; private set; }
        public string UsageNote { get; private set; }
        public bool IsUsageRefreshing { get; private set; }
        public UsageAvailability Availability
        { get { return Usage == null || IsUsageRefreshing ? UsageAvailability.Unknown : Usage.GetAvailability(DateTimeOffset.UtcNow, UsageMaxAge); } }
        public string AvailabilityText
        { get { return IsUsageRefreshing ? "查詢中" : Availability == UsageAvailability.Available ? "額度可用" : Availability == UsageAvailability.Exhausted ? "額度已用完" : "狀態未知 · 請更新額度"; } }
        public Brush AvailabilityBrush
        { get { return Availability == UsageAvailability.Available ? Brushes.SeaGreen : Availability == UsageAvailability.Exhausted ? Brushes.Firebrick : Brushes.DimGray; } }
        public event PropertyChangedEventHandler PropertyChanged;
        public AccountRow() { UsageNote = "尚未查詢額度"; }
        public bool FiveHourAvailable { get { return Usage != null && Usage.FiveHour != null && Usage.FiveHour.RemainingPercent.HasValue; } }
        public bool WeeklyAvailable { get { return Usage != null && Usage.Weekly != null && Usage.Weekly.RemainingPercent.HasValue; } }
        public double FiveHourValue { get { return FiveHourAvailable ? Usage.FiveHour.RemainingPercent.Value : 0; } }
        public double WeeklyValue { get { return WeeklyAvailable ? Usage.Weekly.RemainingPercent.Value : 0; } }
        public string FiveHourText { get { return FiveHourAvailable ? "剩餘 " + FiveHourValue.ToString("0.#") + "%" : Usage == null ? "—" : Usage.FiveHour == null ? "未提供" : "資料異常"; } }
        public string WeeklyText { get { return WeeklyAvailable ? "剩餘 " + WeeklyValue.ToString("0.#") + "%" : Usage == null ? "—" : Usage.Weekly == null ? "未提供" : "資料異常"; } }
        public string FiveHourReset { get { return ResetText(Usage == null ? null : Usage.FiveHour); } }
        public string WeeklyReset { get { return ResetText(Usage == null ? null : Usage.Weekly); } }
        private static string ResetText(UsageWindow window)
        {
            if (window == null) return "";
            if (!window.ResetsAt.HasValue) return "重設時間未提供";
            if (window.ResetsAt.Value <= DateTimeOffset.UtcNow) return "已到重設時間，請更新額度";
            return window.ResetsAt.Value.ToLocalTime().ToString("MM/dd HH:mm") + " 重設（本機時間）";
        }
        public void Notify()
        { var handler = PropertyChanged; if (handler != null) handler(this, new PropertyChangedEventArgs(null)); }
        public void BeginUsage()
        { IsUsageRefreshing = true; UsageNote = Usage == null ? "正在查詢…" : "正在更新…（目前顯示上次資料）"; Notify(); }
        public void SetUsage(AccountUsage usage)
        {
            Usage = usage;
            IsUsageRefreshing = false;
            UsageNote = (usage.HasMalformedData ? "額度資料異常 · " : usage.FiveHour == null && usage.Weekly == null ? "服務未提供這兩種上限 · " : usage.FiveHour == null && usage.Weekly != null ? "僅提供每週上限，依每週額度判斷 · " : "") + "更新於 " + usage.FetchedAt.ToLocalTime().ToString("HH:mm:ss");
            Notify();
        }
        public void UsageFailed(string message)
        { IsUsageRefreshing = false; Usage = null; UsageNote = message; Notify(); }
    }

    public sealed class MainView
    {
        public Window Window { get; private set; }
        private Vault vault;
        private readonly string home, storage;
        private string desktop, currentRaw, loginUrl;
        private Account current;
        private bool busy, demo;
        private CancellationTokenSource cancellation;
        private readonly DispatcherTimer freshnessTimer;
        private readonly DispatcherTimer monitorTimer;
        private readonly DispatcherTimer desktopStartupTimer;
        private readonly CancellationTokenSource desktopStartupLifetime = new CancellationTokenSource();
        private AutoSwitchController automatic;
        private RuntimeCoordinator runtimes;
        private DesktopContinuationWorkflow continuation;
        private bool monitorRequested, suppressMonitorChange, manualOperation, manualFailed, closing;
        private bool demoDesktopControlRequested = true, desktopStateQuery, desktopAutoStartChecking;
        private bool desktopConnected;
        private bool demoContinueRequested = true;
        private bool? continuationCompatible;
        private int desktopConnectionRefreshTicks;
        private string desktopConnectionText = "尚未檢查連線";
        private T Element<T>(string name) where T : FrameworkElement { return (T)Window.FindName(name); }
        private void Click(string name, RoutedEventHandler handler) { Element<Button>(name).Click += handler; }
        private ListBox List { get { return Element<ListBox>("AccountList"); } }
        private Account Selected { get { var row = List.SelectedItem as AccountRow; return row == null ? null : row.Account; } }
        public MainView(bool demoMode)
        {
            demo = demoMode;
            home = Installation.CodexHome;
            storage = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexAccountSwitcher");
            using (var source = typeof(MainView).Assembly.GetManifestResourceStream("MainWindow.xaml")) Window = (Window)XamlReader.Load(source);
            Window.Height = Math.Min(Window.Height, Math.Max(Window.MinHeight, SystemParameters.WorkArea.Height - 24));
            List.SelectionChanged += delegate { UpdateButtons(); };
            Click("RefreshButton", async delegate { await RunManual(async delegate { Refresh(); await RefreshUsage(); }); });
            Click("UsageButton", async delegate { await RunManual(RefreshUsage); });
            Click("SaveButton", async delegate { await RunManual(async delegate { SaveCurrent(); await RefreshUsage(); }); });
            Click("RenameButton", async delegate { await RunManual(delegate { Rename(); return Task.FromResult(0); }); });
            Click("RemoveButton", async delegate { await RunManual(delegate { Remove(); return Task.FromResult(0); }); });
            Click("MoveUpButton", async delegate { await RunManual(delegate { MoveSelected(-1); return Task.FromResult(0); }); });
            Click("MoveDownButton", async delegate { await RunManual(delegate { MoveSelected(1); return Task.FromResult(0); }); });
            Click("SelectPriorityButton", delegate { Guard(SelectPriority); });
            Click("AddButton", async delegate { await RunManual(AddAccount); });
            Click("SwitchButton", async delegate { if (Selected != null) { string raw = Selected.Auth; await RunManual(() => Switch(raw)); } });
            Click("RestoreButton", async delegate { if (vault != null && vault.Data.PreviousAuth != null) { string raw = vault.Data.PreviousAuth; await RunManual(() => Switch(raw, true)); } });
            Click("OpenAppButton", async delegate { await RunManual(OpenCodexApp); });
            Click("CancelButton", async delegate { if (cancellation != null) cancellation.Cancel(); else await SetMonitoring(false); });
            Click("BrowserButton", delegate { Guard(OpenBrowser); });
            Click("RuntimeButton", async delegate { await InspectRuntimes(); });
            Click("DesktopControlButton", delegate { Guard(ShowDesktopControl); });
            Click("IntegrationButton", delegate { Guard(ShowIntegration); });
            Element<CheckBox>("MonitorCheck").Checked += async delegate { if (!suppressMonitorChange) await SetMonitoring(true); };
            Element<CheckBox>("MonitorCheck").Unchecked += async delegate { if (!suppressMonitorChange) await SetMonitoring(false); };
            Element<CheckBox>("InterruptCheck").Click += delegate { Guard(delegate { if (vault != null) { vault.Data.AllowInterrupt = Element<CheckBox>("InterruptCheck").IsChecked == true; vault.Save(); } }); };
            Click("ContinueDesktopButton", async delegate { await ToggleContinuation(); });
            Window.Closing += async delegate(object sender, System.ComponentModel.CancelEventArgs args)
            {
                if (closing) return;
                if (busy || manualOperation) { args.Cancel = true; if (cancellation != null) cancellation.Cancel(); Status("正在取消操作，完成後即可關閉。"); return; }
                if (automatic != null && automatic.IsRunning)
                {
                    args.Cancel = true; closing = true; monitorTimer.Stop();
                    await automatic.StopAsync(true); Window.Close();
                }
            };
            // Only refresh snapshot labels. This does not query accounts or switch logins.
            freshnessTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            freshnessTimer.Tick += async delegate
            {
                foreach (AccountRow row in List.Items) row.Notify();
                UpdateRecommendation();
                if (++desktopConnectionRefreshTicks >= 10)
                {
                    desktopConnectionRefreshTicks = 0;
                    if (!busy && !manualOperation && !desktopAutoStartChecking && (automatic == null || !automatic.IsRunning)) await RefreshDesktopControlState();
                }
            };
            monitorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
            monitorTimer.Tick += async delegate { await MonitorTick(); };
            desktopStartupTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            desktopStartupTimer.Tick += async delegate { await TryStartDesktopAutomatically(); };
            Window.Loaded += delegate { freshnessTimer.Start(); };
            Window.Closed += delegate { freshnessTimer.Stop(); monitorTimer.Stop(); desktopStartupTimer.Stop(); desktopStartupLifetime.Cancel(); };
            if (demo) LoadDemo();
            else Window.Loaded += async delegate
            {
                Guard(delegate { desktop = Installation.DesktopExe(); vault = new Vault(storage); CreateAutomatic(); runtimes.Desktop.InitializeControlPreference(); Refresh(); });
                await RefreshContinuationCompatibility();
                if (continuation != null) await continuation.RecoverPendingAsync();
                if (continuation != null && continuation.WaitingForQuotaSourceId != null &&
                    (!vault.Data.AutoMonitorEnabled || !(vault.Data.AutoContinueDesktop ?? true) || !runtimes.Desktop.ControlRequested))
                    await continuation.CancelAsync();
                if (desktopStartupLifetime.IsCancellationRequested || closing) return;
                if (runtimes != null) await RefreshDesktopControlState();
                if (desktopStartupLifetime.IsCancellationRequested || closing) return;
                if (runtimes != null) { desktopStartupTimer.Start(); await TryStartDesktopAutomatically(); }
                if (desktopStartupLifetime.IsCancellationRequested || closing) return;
                await RefreshUsage();
                if (desktopStartupLifetime.IsCancellationRequested || closing) return;
                if (vault != null && vault.Data.AutoMonitorEnabled) await SetMonitoring(true);
            };
        }

        private void CreateAutomatic()
        {
            runtimes = new RuntimeCoordinator(storage, home);
            continuation = CreateContinuationWorkflow();
            automatic = new AutoSwitchController(new AutoSwitchHost
            {
                ReadCurrent = delegate { string raw = Files.ReadAuth(Path.Combine(home, "auth.json")); return raw == null ? null : Account.Parse(raw); },
                ReadAccounts = () => vault.Data.Accounts.ToList(), QueryUsage = QueryAutomaticUsage,
                PrepareContinuation = (source, target, revalidate, token) => continuation.PrepareAsync(source, target, revalidate, token),
                ReadQuotaWaitSource = () => continuation.WaitingForQuotaSourceId,
                ObserveQuotaWait = (source, token) => continuation.ObserveQuotaWaitAsync(source, token),
                PrepareQuotaContinuation = (source, target, revalidate, token) => continuation.PrepareQuotaResetAsync(source, target, revalidate, token),
                RetainCurrentAccount = target => continuation.MarkAccountApplied(target),
                CancelQuotaWait = () => continuation.WaitingForQuotaSourceId == null ? Task.FromResult(0) : continuation.CancelAsync(),
                WaitForRuntimeExit = async token =>
                {
                    await runtimes.WaitForExitAsync(vault.Data.AllowInterrupt,
                        text => Window.Dispatcher.Invoke(new Action(() => Element<TextBlock>("AutoStatus").Text = "等待切換：\n" + text)), token);
                    continuation.MarkQuiesced();
                },
                ConfirmRuntimeQuiet = runtimes.ConfirmQuietAsync,
                Apply = (target, expectedId) =>
                {
                    new SwitchService(vault, home, runtimes.CredentialProcessesRunning).Apply(target.Auth, false, expectedId);
                    continuation.MarkAccountApplied(target);
                },
                Launch = async delegate { Refresh(); await runtimes.RestoreAsync(); if (runtimes.DesktopWasRunning) await runtimes.LaunchDesktopAsync(Installation.DesktopExe()); },
                ContinueAfterSwitch = (target, token) => continuation.ContinueAsync(target, token),
                CancelContinuation = continuation.CancelAsync,
                RestoreRuntimes = runtimes.RestoreAsync, UtcNow = () => DateTimeOffset.UtcNow
            });
            automatic.Changed += delegate
            {
                if (!manualOperation) Element<TextBlock>("AutoStatus").Text = automatic.StatusMessage;
                if (!manualOperation && !closing && !automatic.Enabled && monitorRequested && automatic.State == AutoSwitchState.Faulted)
                {
                    monitorRequested = false; monitorTimer.Stop(); SetMonitorCheck(false);
                    Guard(delegate { vault.Data.AutoMonitorEnabled = false; vault.Save(); });
                }
                UpdateButtons();
            };
            Element<CheckBox>("InterruptCheck").IsChecked = vault.Data.AllowInterrupt;
            UpdateContinuationButton();
        }

        private bool ContinuationRequested { get { return demo ? demoContinueRequested : vault != null && (vault.Data.AutoContinueDesktop ?? true); } }
        private async Task ToggleContinuation()
        {
            if (busy || manualOperation || automatic != null && automatic.IsRunning) return;
            if (demo) { demoContinueRequested = !demoContinueRequested; UpdateButtons(); return; }
            if (vault == null) return;
            await RunManual(delegate
            {
                vault.Data.AutoContinueDesktop = !ContinuationRequested;
                vault.Save();
                Status(ContinuationRequested ? "自動續跑設定已開啟；版本相容、自動監看及桌面連線均就緒後才會觸發，按鈕不會立即送出訊息。" : "自動續跑已關閉；不會自動送出「繼續執行」。");
                return Task.FromResult(0);
            });
        }
        private async Task RefreshContinuationCompatibility()
        {
            bool supported = false;
            try { supported = await Task.Run(delegate { DesktopLocalWorkPolicy.ValidateTarget(DesktopPackageTarget.Resolve(desktop)); return true; }); }
            catch { }
            continuationCompatible = supported;
            UpdateContinuationButton();
        }
        private void UpdateContinuationButton()
        {
            bool requested = ContinuationRequested;
            var button = Element<Button>("ContinueDesktopButton");
            button.Content = requested ? "關閉自動續跑" : "開啟自動續跑";
            button.Background = new SolidColorBrush(requested ? Color.FromRgb(18, 119, 93) : Colors.White);
            button.Foreground = new SolidColorBrush(requested ? Colors.White : Color.FromRgb(18, 119, 93));
            button.BorderBrush = new SolidColorBrush(Color.FromRgb(18, 119, 93));
            button.ToolTip = "記住此開關；不會立即送出訊息。需版本相容、自動監看及受管桌面連線。";
            var label = Element<TextBlock>("ContinuationModeText");
            label.Text = !requested ? "自動續跑：已關閉" : continuationCompatible == false ?
                "自動續跑：設定已開啟 · Codex 版本未通過驗證，暫不執行" : !continuationCompatible.HasValue ?
                "自動續跑：設定已開啟 · 版本尚未確認" : !monitorRequested ?
                "自動續跑：已開啟 · 等待開啟自動監看" : !desktopConnected ?
                "自動續跑：已開啟 · 等待桌面連線" : "自動續跑：已開啟 · 等待額度觸發";
            label.Foreground = new SolidColorBrush(requested && continuationCompatible != true ? Color.FromRgb(148, 97, 21) : Color.FromRgb(18, 119, 93));
        }

        private DesktopContinuationWorkflow CreateContinuationWorkflow()
        {
            DesktopContinuationCheckpoint capturedCheckpoint = null;
            return new DesktopContinuationWorkflow(new ContinuationTicketStore(storage, home), new DesktopContinuationOperations
            {
                Enabled = () => vault != null && (vault.Data.AutoContinueDesktop ?? true) && runtimes.Desktop.ControlRequested,
                ReadCurrentId = delegate { string raw = Files.ReadAuth(Path.Combine(home, "auth.json")); return raw == null ? null : Account.Parse(raw).Id; },
                Capture = async token => { capturedCheckpoint = await runtimes.Desktop.CaptureContinuationAsync(token); return capturedCheckpoint; },
                BeforeInterrupt = WaitForContinuationPrerequisites,
                BindRestore = (checkpoint, operation, token) => { token.ThrowIfCancellationRequested(); runtimes.Desktop.BindContinuationRestore(checkpoint, operation); return Task.FromResult(0); },
                Interrupt = async (checkpoint, token) =>
                {
                    var state = await runtimes.Desktop.GetContinuationSessionAsync(checkpoint, token);
                    using (var client = new DesktopConversationClient(state)) await client.InterruptAsync(checkpoint, token);
                },
                WaitStopped = (checkpoint, token) => runtimes.Desktop.WaitContinuationStoppedAsync(checkpoint, token),
                PrepareAfterRestore = (checkpoint, target, token) => runtimes.Desktop.PrepareContinuationAsync(checkpoint, target.Id, target.Email, token),
                Open = async (checkpoint, token) =>
                {
                    var state = await runtimes.Desktop.GetContinuationSessionAsync(checkpoint, token);
                    using (var client = new DesktopConversationClient(state)) await client.OpenAsync(checkpoint, token);
                },
                Start = async (checkpoint, operation, token) =>
                {
                    var state = await runtimes.Desktop.GetContinuationSessionAsync(checkpoint, token);
                    Exception failure;
                    try
                    {
                        using (var client = new DesktopConversationClient(state))
                        {
                            await client.OpenAsync(checkpoint, token);
                            return await client.StartTurnAsync(checkpoint, operation, token);
                        }
                    }
                    catch (Exception error) { failure = error; }
                    // Keep the asynchronous receipt lookup outside catch for the
                    // .NET Framework C# 5 compiler.
                    using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                    {
                        // A positive receipt can reconcile a lost frontend reply.
                        // This is a status lookup only, never another send attempt.
                        try
                        {
                            string receipt = await runtimes.Desktop.GetContinuationTurnAsync(capturedCheckpoint, operation, timeout.Token);
                            if (!String.IsNullOrEmpty(receipt)) return receipt;
                        }
                        catch { }
                    }
                    throw failure;
                },
                Release = (checkpoint, operation, token) => runtimes.Desktop.CancelContinuationAsync(checkpoint, operation, token),
                Status = text => Window.Dispatcher.Invoke(new Action(() => Element<TextBlock>("ContinuationStatusText").Text = text))
            });
        }
        private async Task WaitForContinuationPrerequisites(DesktopContinuationCheckpoint checkpoint, CancellationToken token)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var inspection = await runtimes.InspectAsync(token);
                var desktops = await runtimes.Desktop.InspectAsync(token);
                if (RuntimeCoordinator.MayInterruptDesktopForContinuation(inspection, desktops, checkpoint, vault.Data.AllowInterrupt)) return;
                Window.Dispatcher.Invoke(new Action(() => Element<TextBlock>("AutoStatus").Text = "等待正常中止原對話：\n" + inspection.Describe()));
                if (clock.Elapsed >= TimeSpan.FromMinutes(5)) throw new UserError("仍有其他忙碌、未知或未受管的工作，尚未中止或切換帳號。");
                await Task.Delay(700, token);
            }
        }

        private void SetMonitorCheck(bool value)
        { suppressMonitorChange = true; Element<CheckBox>("MonitorCheck").IsChecked = value; suppressMonitorChange = false; }

        private async Task SetMonitoring(bool enabled)
        {
            if (demo || automatic == null || manualOperation || busy) { SetMonitorCheck(monitorRequested); return; }
            Exception failure = null;
            try
            {
                monitorRequested = enabled; SetMonitorCheck(enabled);
                vault.Data.AutoMonitorEnabled = enabled; vault.Save();
                if (enabled) { automatic.Enable(); monitorTimer.Start(); await MonitorTick(); }
                else { monitorTimer.Stop(); await automatic.StopAsync(); }
            }
            catch (Exception ex)
            {
                monitorRequested = false; monitorTimer.Stop(); SetMonitorCheck(false);
                vault.Data.AutoMonitorEnabled = false;
                try { vault.Save(); } catch { }
                failure = ex;
            }
            if (failure != null) { if (automatic != null) await automatic.StopAsync(); ShowError(failure); }
            UpdateButtons();
        }

        private async Task MonitorTick()
        {
            if (!monitorRequested || manualOperation || busy || automatic == null || automatic.IsRunning) return;
            await automatic.TickAsync();
            if (!closing && !manualOperation) Guard(Refresh);
        }

        private async Task RunManual(Func<Task> action)
        {
            if (busy || manualOperation) return;
            manualOperation = true; manualFailed = false; UpdateButtons();
            bool succeeded = false;
            try
            {
                if (automatic != null) await automatic.StopAsync();
                Element<TextBlock>("AutoStatus").Text = monitorRequested ? "手動操作中，自動監看暫停。" : "自動監看已關閉。";
                await action(); succeeded = !manualFailed;
            }
            catch (Exception ex) { ShowError(ex); }
            finally { manualOperation = false; }
            if (monitorRequested && automatic != null)
            {
                if (succeeded && automatic.State != AutoSwitchState.Faulted) automatic.Enable();
                else { monitorRequested = false; monitorTimer.Stop(); SetMonitorCheck(false); Guard(delegate { vault.Data.AutoMonitorEnabled = false; vault.Save(); }); }
            }
            UpdateButtons();
        }

        private async Task<AccountUsage> QueryAutomaticUsage(Account account, CancellationToken token)
        {
            Refresh();
            var row = List.Items.Cast<AccountRow>().FirstOrDefault(item => item.Account.Id == account.Id && item.Account.Auth == account.Auth);
            if (row != null) row.BeginUsage();
            try
            {
                string cli = Installation.CliExe();
                var result = await Task.Run(async delegate
                {
                    using (var session = new UsageSession()) return await session.ReadAsync(cli, storage, account, token).ConfigureAwait(false);
                }, token);
                token.ThrowIfCancellationRequested();
                if (row != null && List.Items.Contains(row) && row.Account.Auth == account.Auth) row.SetUsage(result);
                return result;
            }
            catch
            {
                if (row != null && List.Items.Contains(row)) row.UsageFailed(token.IsCancellationRequested ? "監看查詢已取消。" : "監看查詢失敗，額度狀態未知。");
                throw;
            }
        }

        private async Task InspectRuntimes()
        {
            if (demo || runtimes == null || manualOperation || busy || automatic != null && automatic.IsRunning) return;
            Element<Button>("RuntimeButton").IsEnabled = false;
            try
            {
                var result = await runtimes.InspectAsync(CancellationToken.None); Status(result.Describe());
                await RefreshDesktopControlState();
                await RefreshContinuationCompatibility();
            }
            catch (Exception ex) { ShowError(ex); }
            UpdateButtons();
        }

        private bool DesktopControlRequested
        {
            get
            {
                if (demo) return demoDesktopControlRequested;
                if (runtimes == null) throw new UserError("桌面控制設定尚未讀取完成。");
                return runtimes.Desktop.ControlRequested;
            }
        }
        private void UpdateDesktopControlIndicator()
        {
            bool requested;
            try { requested = DesktopControlRequested; }
            catch
            {
                Element<Button>("DesktopControlButton").Content = "桌面控制：狀態未知…";
                Element<Button>("DesktopControlButton").Background = new SolidColorBrush(Color.FromRgb(255, 244, 220));
                Element<Button>("DesktopControlButton").BorderBrush = new SolidColorBrush(Color.FromRgb(170, 119, 36));
                Element<Button>("DesktopControlButton").Foreground = new SolidColorBrush(Color.FromRgb(130, 83, 18));
                Element<TextBlock>("DesktopControlStatusText").Text = "桌面控制：無法讀取設定，尚未確認開關狀態。";
                return;
            }
            var button = Element<Button>("DesktopControlButton");
            button.Content = requested ? "桌面控制：已開啟…" : "桌面控制：已關閉…";
            var outline = new SolidColorBrush(requested ? Color.FromRgb(18, 119, 93) : Color.FromRgb(190, 56, 56));
            bool filled = requested && desktopConnected;
            button.Background = filled ? outline : Brushes.Transparent;
            button.BorderBrush = outline;
            button.Foreground = filled ? Brushes.White : outline;
            string detail = requested ? desktopConnectionText : "重開切換器後仍維持關閉";
            Element<TextBlock>("DesktopControlStatusText").Text = "桌面控制：" + (requested ? "已開啟" : "已關閉") + " · " + detail;
            Element<TextBlock>("DesktopControlStatusText").Foreground = outline;
        }
        private static bool IsConnectedDesktopSession(DesktopSessionState state)
        {
            return state != null && state.ControlEnabled && state.TransportReady && state.State == "running" &&
                !state.BackendExitCode.HasValue && DesktopNative.Same(state.Host) && DesktopNative.Same(state.App);
        }
        private static bool HasDesktopConnection(IList<DesktopSessionState> states)
        {
            return states != null && states.Any(IsConnectedDesktopSession);
        }
        private async Task RefreshDesktopControlState()
        {
            if (desktopStateQuery || demo || runtimes == null) return;
            desktopStateQuery = true;
            try
            {
                if (!runtimes.Desktop.ControlRequested) { desktopConnected = false; desktopConnectionText = "自動控制已停用"; return; }
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(desktopStartupLifetime.Token))
                {
                    deadline.CancelAfter(TimeSpan.FromSeconds(15));
                    var states = await runtimes.Desktop.InspectAsync(deadline.Token);
                    var connected = states.Where(IsConnectedDesktopSession).ToList();
                    desktopConnected = connected.Count > 0;
                    if (desktopConnected)
                        desktopConnectionText = connected.All(s => s.Known) ? (connected.All(s => s.Idle) ? "已連線，閒置" : "已連線，忙碌") : "已連線，工作狀態尚待確認";
                    else if (states.Any(s => s.Reason == "previousInstallationStillRunning")) desktopConnectionText = "等待正常 Quit，接續新版控制";
                    else if (states.Any(s => s.State == "quiesced" && s.ControlEnabled)) desktopConnectionText = "已暫停，等待恢復";
                    else desktopConnectionText = desktopStartupTimer.IsEnabled ? "待開啟受管桌面" : "尚未連線；按「開啟 Codex App」連線";
                }
            }
            catch { desktopConnected = false; desktopConnectionText = "無法確認連線，請檢查工作狀態"; }
            finally { desktopStateQuery = false; UpdateDesktopControlIndicator(); UpdateContinuationButton(); }
        }
        private async Task TryStartDesktopAutomatically()
        {
            if (demo || runtimes == null || desktopAutoStartChecking || desktopStateQuery || closing || desktopStartupLifetime.IsCancellationRequested ||
                busy || manualOperation || automatic != null && automatic.IsRunning || Window.OwnedWindows.Cast<Window>().Any(w => w.IsVisible)) return;
            desktopAutoStartChecking = true;
            try
            {
                if (!runtimes.Desktop.ControlRequested) { desktopConnected = false; desktopStartupTimer.Stop(); return; }
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(desktopStartupLifetime.Token))
                {
                    deadline.CancelAfter(TimeSpan.FromSeconds(15));
                    var states = await runtimes.Desktop.InspectAsync(deadline.Token);
                    var processes = await Task.Run(() => ProcessInspector.Inspect(), deadline.Token);
                    var decision = DesktopAutoStart.Decide(runtimes.Desktop.ControlRequested, states, processes, DesktopNative.Same);
                    desktopConnected = HasDesktopConnection(states);
                    if (decision == DesktopAutoStartDecision.Disabled || decision == DesktopAutoStartDecision.Connected)
                    {
                        desktopStartupTimer.Stop();
                        if (decision == DesktopAutoStartDecision.Connected)
                            desktopConnectionText = states.All(s => s.Known) ? (states.All(s => s.Idle) ? "已連線，閒置" : "已連線，忙碌") : "已連線，工作狀態尚待確認";
                        return;
                    }
                    if (decision == DesktopAutoStartDecision.WaitingForExit)
                    {
                        desktopConnectionText = desktopConnected ? "已連線；等待其他 Codex 相關程序正常退出" : "待連線；等待現有 Codex 相關程序正常退出";
                        UpdateDesktopControlIndicator(); return;
                    }
                    if (busy || manualOperation || closing || desktopStartupLifetime.IsCancellationRequested || !runtimes.Desktop.ControlRequested ||
                        Window.OwnedWindows.Cast<Window>().Any(w => w.IsVisible)) return;
                    desktopStartupTimer.Stop();
                    await StartDesktopControl(true);
                }
            }
            catch (OperationCanceledException) { desktopConnected = false; if (!desktopStartupLifetime.IsCancellationRequested) desktopConnectionText = "待連線；工作狀態查詢逾時"; }
            catch { desktopConnected = false; desktopStartupTimer.Stop(); desktopConnectionText = "自動啟動尚未完成，請從桌面控制檢查"; }
            finally { desktopAutoStartChecking = false; UpdateDesktopControlIndicator(); }
        }
        private async Task StartDesktopControl(bool automaticStart)
        {
            desktopStartupTimer.Stop();
            await RunManual(() => StartDesktopControlCore(automaticStart));
        }
        private async Task StartDesktopControlCore(bool automaticStart)
        {
            desktopStartupTimer.Stop();
            if (automaticStart && !runtimes.Desktop.ControlRequested) return;
            busy = true; cancellation = new CancellationTokenSource(); UpdateButtons();
            try
            {
                Status("正在準備桌面控制。既有程序需正常 Quit，退出後會自動繼續；可按取消。");
                desktopConnected = false; desktopConnectionText = "正在啟動受管桌面"; UpdateDesktopControlIndicator();
                string executable = Installation.DesktopExe();
                if (!automaticStart) runtimes.Desktop.SetControlRequested(true);
                await runtimes.Desktop.OpenManagedAsync(executable,
                    text => Window.Dispatcher.Invoke(new Action(() => Status(text))), cancellation.Token);
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token))
                {
                    deadline.CancelAfter(TimeSpan.FromSeconds(15));
                    var states = await runtimes.Desktop.InspectAsync(deadline.Token);
                    if (!HasDesktopConnection(states)) throw new UserError("Codex 桌面控制尚未確認連線，請稍後檢查工作狀態；不會重複啟動 App。");
                }
                Status("受管後端連線已確認。請先確認 Codex 畫面完成載入；可按「檢查工作狀態」查看是否已可查詢，狀態未明或工作未結束時會繼續等待。");
            }
            catch (OperationCanceledException) { manualFailed = true; Status("已取消啟動等待。若受管桌面已開啟，它會繼續運作；開關設定已保留。"); }
            catch (Exception error) { manualFailed = true; ShowError(error); }
            finally { busy = false; cancellation.Dispose(); cancellation = null; UpdateButtons(); }
            await RefreshDesktopControlState();
        }
        private async Task OpenCodexApp()
        {
            if (demo || runtimes == null) return;
            string executable = Installation.DesktopExe();
            if (String.IsNullOrEmpty(executable) || !File.Exists(executable)) throw new UserError("找不到 Codex App，請先完成安裝後再試。");
            if (runtimes.Desktop.ControlRequested)
            {
                desktopStartupTimer.Stop();
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(desktopStartupLifetime.Token))
                {
                    deadline.CancelAfter(TimeSpan.FromSeconds(15));
                    var states = await runtimes.Desktop.InspectAsync(deadline.Token);
                    var connected = states.Where(IsConnectedDesktopSession).ToList();
                    if (connected.Count > 0)
                    {
                        bool shown = connected.Any(state => DesktopNative.TryShowApp(state.App));
                        Status(shown ? "已顯示受管 Codex App。" : "受管 Codex App 已在執行，請從工作列或系統匣開啟現有視窗。");
                        await RefreshDesktopControlState();
                        return;
                    }
                }
                await StartDesktopControlCore(true);
                return;
            }
            var existing = await Task.Run(() => DesktopNative.Snapshot().Where(actor =>
                String.Equals(actor.ExecutablePath, executable, StringComparison.OrdinalIgnoreCase) && DesktopNative.Same(actor)).ToList());
            if (existing.Count > 0)
            {
                bool shown = existing.Any(DesktopNative.TryShowApp);
                Status(shown ? "已顯示目前的 Codex App。" : "Codex App 已在執行，請從工作列或系統匣開啟現有視窗。");
                await RefreshDesktopControlState();
                return;
            }
            Installation.Launch(executable);
            Status("已送出 Codex App 開啟要求。");
        }

        private void ShowDesktopControl() { CreateDesktopControlDialog().ShowDialog(); }
        private Window CreateDesktopControlDialog()
        {
            var dialog = new Window { Title = "桌面控制", Owner = Window, Width = 610, Height = Math.Min(570, SystemParameters.WorkArea.Height - 40),
                WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, FontFamily = Window.FontFamily, Background = Brushes.White };
            var panel = new StackPanel { Margin = new Thickness(25) };
            bool requested = DesktopControlRequested;
            var stateTitle = new TextBlock { Name = "DesktopControlStateTitle", FontSize = 18, FontWeight = FontWeights.SemiBold };
            var stateDetail = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), FontSize = 12 };
            var statePanel = new StackPanel(); statePanel.Children.Add(stateTitle); statePanel.Children.Add(stateDetail);
            var stateBadge = new Border { Child = statePanel, Padding = new Thickness(14, 12, 14, 12), CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 0, 12) };
            panel.Children.Add(stateBadge);
            var controlSwitch = new CheckBox { Name = "DesktopControlSwitch", Content = "開啟桌面自動控制（記住此設定）", IsChecked = requested,
                IsEnabled = !demo && runtimes != null && !busy && !manualOperation && (automatic == null || !automatic.IsRunning), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14) };
            panel.Children.Add(controlSwitch);
            Action refreshState = delegate
            {
                bool enabled = DesktopControlRequested;
                controlSwitch.IsChecked = enabled;
                stateTitle.Text = enabled ? "● 桌面控制已開啟" : "○ 桌面控制已關閉";
                stateTitle.Foreground = new SolidColorBrush(enabled ? Color.FromRgb(18, 119, 93) : Color.FromRgb(91, 102, 96));
                stateBadge.Background = new SolidColorBrush(enabled ? Color.FromRgb(231, 247, 239) : Color.FromRgb(238, 241, 239));
                stateDetail.Text = enabled ? "開啟設定已保留 · " + desktopConnectionText : "關閉設定已保留；重新開啟切換器不會自動啟用。";
                UpdateDesktopControlIndicator();
            };
            controlSwitch.Click += async delegate
            {
                controlSwitch.IsEnabled = false;
                try
                {
                    bool enabled = controlSwitch.IsChecked == true;
                    runtimes.Desktop.SetControlRequested(enabled);
                    if (!enabled) { desktopStartupTimer.Stop(); await RunManual(() => runtimes.Desktop.DisableAsync(CancellationToken.None)); }
                    else desktopStartupTimer.Start();
                    desktopConnected = false; desktopConnectionText = enabled ? "待開啟受管桌面" : "自動控制已停用";
                    refreshState();
                }
                catch (Exception error) { ShowError(error); refreshState(); stateDetail.Text = Element<TextBlock>("StatusText").Text; }
                finally { controlSwitch.IsEnabled = !demo && runtimes != null && !busy && !manualOperation && (automatic == null || !automatic.IsRunning); }
            };
            refreshState();
            dialog.Loaded += async delegate { await RefreshDesktopControlState(); refreshState(); };
            panel.Children.Add(new TextBlock { Text = "桌面受管模式", FontWeight = FontWeights.SemiBold, FontSize = 16 });
            panel.Children.Add(new TextBlock { Text = "預設開啟並記住選擇。已有一般 Codex 時，請完成工作並正常 Quit，再按「開啟 Codex App」建立連線。\n\n手動切換會等待對話與背景工作結束，再正常關閉、套用帳號並重開。自動監看時，主畫面續聊選項可在額度耗盡後正常中止，切換後在原對話送出一次「繼續執行」。", TextWrapping = TextWrapping.Wrap, LineHeight = 22, Margin = new Thickness(0, 10, 0, 0) });
            bool savedBlockLocalWork = !demo && runtimes != null && runtimes.Desktop.BlockLocalWork;
            var blockLocalWork = new CheckBox { Content = "受管桌面封鎖 Local Work（重新啟動後生效）", IsChecked = savedBlockLocalWork,
                IsEnabled = !demo && runtimes != null && !busy && !manualOperation && (automatic == null || !automatic.IsRunning), Margin = new Thickness(0, 14, 0, 0) };
            var localWorkStatus = new TextBlock { Text = "僅影響下次由切換器啟動的桌面。請正常 Quit 後重新開啟；封鎖後 Local Work 無法使用，可能顯示設定錯誤。無法驗證 App 版本時會停止啟動。",
                TextWrapping = TextWrapping.Wrap, LineHeight = 20, Foreground = new SolidColorBrush(Color.FromRgb(82, 102, 90)), Margin = new Thickness(0, 6, 0, 0) };
            blockLocalWork.Click += delegate
            {
                try
                {
                    runtimes.Desktop.SetBlockLocalWork(blockLocalWork.IsChecked == true);
                    savedBlockLocalWork = blockLocalWork.IsChecked == true;
                    desktopConnected = false; desktopConnectionText = "Local Work 選項已變更，等待重新啟動"; UpdateDesktopControlIndicator();
                    localWorkStatus.Text = "設定已儲存。完成目前工作並正常 Quit，再按「開啟受管桌面」後生效；現有桌面會繼續運作。";
                }
                catch (Exception error)
                {
                    blockLocalWork.IsChecked = savedBlockLocalWork;
                    ShowError(error); localWorkStatus.Text = Element<TextBlock>("StatusText").Text;
                }
            };
            panel.Children.Add(blockLocalWork); panel.Children.Add(localWorkStatus);
            var desktopStart = new Button { Content = !demo && runtimes != null && runtimes.Desktop.Enabled ? "開啟受管桌面" : "啟用並開啟受管桌面", Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(12, 8, 12, 8), IsEnabled = !demo && runtimes != null };
            desktopStart.Click += async delegate
            {
                dialog.Close();
                await StartDesktopControl(false);
            };
            panel.Children.Add(desktopStart);
            AddRecoveryButton(dialog, panel);
            dialog.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            return dialog;
        }
        private void ShowIntegration() { CreateVSCodeIntegrationDialog().ShowDialog(); }
        private Window CreateVSCodeIntegrationDialog()
        {
            string package = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "codex-account-switcher-vscode.vsix");
            var dialog = new Window { Title = "VS Code 實驗整合", Owner = Window, Width = 610, Height = Math.Min(290, SystemParameters.WorkArea.Height - 40),
                WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, FontFamily = Window.FontFamily, Background = Brushes.White };
            var panel = new StackPanel { Margin = new Thickness(25) };
            panel.Children.Add(new TextBlock { Text = "VS Code 整合（實驗功能）", FontWeight = FontWeights.SemiBold, FontSize = 16 });
            panel.Children.Add(new TextBlock { Text = "安裝下方 VSIX，再於 VS Code 命令選單啟用。首次需正常重新載入對應視窗；整合會在確認閒置後暫停後端，透過視窗重載恢復。", TextWrapping = TextWrapping.Wrap, LineHeight = 22, Margin = new Thickness(0, 8, 0, 0) });
            var open = new Button { Content = "開啟整合安裝檔位置", Margin = new Thickness(0, 18, 0, 0), Padding = new Thickness(12, 8, 12, 8) };
            open.Click += delegate { if (File.Exists(package)) Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + package + "\"") { UseShellExecute = true }); else Status("找不到整合安裝檔，請重新執行建置。"); };
            panel.Children.Add(open);
            AddRecoveryButton(dialog, panel);
            dialog.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            return dialog;
        }
        private void AddRecoveryButton(Window dialog, StackPanel panel)
        {
            var recover = new Button { Content = "恢復已暫停的桌面／VS Code", Margin = new Thickness(0, 18, 0, 0), Padding = new Thickness(12, 8, 12, 8), IsEnabled = !demo && runtimes != null };
            recover.Click += async delegate
            {
                dialog.Close();
                await RunManual(async delegate
                {
                    await runtimes.RestoreDetectedAsync(CancellationToken.None);
                    Status("已檢查並重送已辨識工作階段的恢復要求。請確認桌面／VS Code 視窗與登入帳號；未重送聊天訊息。");
                });
            };
            panel.Children.Add(recover);
        }

        private void Status(string text) { Element<TextBlock>("StatusText").Text = text; }
        private void Guard(Action action)
        {
            try { action(); }
            catch (Exception ex) { ShowError(ex); }
            UpdateButtons();
        }
        private void ShowError(Exception ex, [System.Runtime.CompilerServices.CallerMemberName] string operation = null)
        {
            if (manualOperation) manualFailed = true;
            string message;
            if (ex is UserError) message = ex.Message;
            else
            {
                var diagnostic = OperationDiagnostics.Project(ex, operation);
                bool saved = !demo && OperationDiagnostics.TrySave(storage, diagnostic);
                message = OperationDiagnostics.Describe(diagnostic);
                if (saved) message += @" 診斷已存於 %LOCALAPPDATA%\CodexAccountSwitcher\diagnostics\last-error.json。";
            }
            Status(message);
        }
        private void UpdateButtons()
        {
            foreach (string name in new[] { "AddButton", "SaveButton", "RefreshButton", "UsageButton", "RenameButton", "RemoveButton", "SwitchButton", "RestoreButton", "MoveUpButton", "MoveDownButton" })
                Element<Button>(name).IsEnabled = !busy && !manualOperation && (vault != null || demo);
            Element<Button>("SaveButton").IsEnabled &= current != null;
            foreach (string name in new[] { "RenameButton", "RemoveButton", "SwitchButton" }) Element<Button>(name).IsEnabled &= Selected != null;
            Element<Button>("SwitchButton").IsEnabled &= Selected != null && (current == null || Selected.Id != current.Id || (Selected.PendingLogin && Selected.Auth != currentRaw));
            Element<Button>("RestoreButton").IsEnabled &= vault != null && vault.Data.PreviousAuth != null;
            Element<Button>("UsageButton").IsEnabled &= List.Items.Count > 0;
            Element<Button>("MoveUpButton").IsEnabled &= List.SelectedIndex > 0;
            Element<Button>("MoveDownButton").IsEnabled &= List.SelectedIndex >= 0 && List.SelectedIndex < List.Items.Count - 1;
            if (demo)
                foreach (string name in new[] { "AddButton", "SaveButton", "RenameButton", "RemoveButton", "SwitchButton", "RestoreButton" }) Element<Button>(name).IsEnabled = false;
            List.IsEnabled = !busy && !manualOperation;
            bool autoRunning = automatic != null && automatic.IsRunning;
            Element<Button>("OpenAppButton").IsEnabled = !demo && runtimes != null && !busy && !manualOperation && !autoRunning;
            Element<Button>("CancelButton").IsEnabled = busy || autoRunning;
            Element<StackPanel>("ProgressActions").Visibility = busy || autoRunning ? Visibility.Visible : Visibility.Collapsed;
            Element<CheckBox>("MonitorCheck").IsEnabled = !demo && vault != null && !manualOperation && !busy;
            Element<CheckBox>("InterruptCheck").IsEnabled = !demo && vault != null && !manualOperation && !busy && !autoRunning;
            Element<Button>("ContinueDesktopButton").IsEnabled = (demo || vault != null) && !manualOperation && !busy && !autoRunning;
            UpdateContinuationButton();
            Element<Button>("RuntimeButton").IsEnabled = !demo && vault != null && !manualOperation && !busy && !autoRunning;
            Element<Button>("DesktopControlButton").IsEnabled = (demo || runtimes != null) && !busy && !manualOperation && !autoRunning;
            Element<Button>("IntegrationButton").IsEnabled = !busy && !manualOperation && !autoRunning;
            UpdateDesktopControlIndicator();
            UpdateRecommendation();
        }
        private void Refresh()
        {
            if (vault == null) return;
            string selectedId = Selected == null ? null : Selected.Id;
            currentRaw = Files.ReadAuth(Path.Combine(home, "auth.json"));
            current = currentRaw == null ? null : Account.Parse(currentRaw);
            // Only synchronize profiles already explicitly saved by the user.
            var savedCurrent = current == null ? null : vault.Data.Accounts.FirstOrDefault(a => a.Id == current.Id);
            if (savedCurrent != null && ((!savedCurrent.PendingLogin && savedCurrent.Auth != currentRaw) || (savedCurrent.PendingLogin && savedCurrent.Auth == currentRaw)))
            { vault.Upsert(currentRaw, null); vault.Save(); }
            Element<TextBlock>("CurrentName").Text = current == null ? "尚未登入或使用其他儲存方式" : current.Email;
            Element<TextBlock>("CurrentDetail").Text = current == null ? "可先使用「新增帳號」完成登入。" : current.Plan.ToUpperInvariant() + "  ·  本機快取；實際登入狀態請在 Codex 確認";
            RebuildRows(selectedId);
        }
        private void RebuildRows(string selectedId)
        {
            var previousRows = List.Items.Cast<AccountRow>().ToList();
            var rows = vault.Data.Accounts.Select((a, index) =>
            {
                var row = previousRows.FirstOrDefault(r => r.Account.Id == a.Id && r.Account.Auth == a.Auth) ?? new AccountRow();
                row.Account = a;
                row.Priority = index + 1;
                row.Status = a.PendingLogin ? "新登入，待套用" : current != null && a.Id == current.Id ? "目前帳號" : "已儲存";
                row.Notify();
                return row;
            }).ToList();
            List.ItemsSource = rows;
            List.SelectedItem = rows.FirstOrDefault(r => r.Account.Id == selectedId) ?? rows.FirstOrDefault(r => current == null || r.Account.Id != current.Id) ?? rows.FirstOrDefault();
            Element<TextBlock>("AccountCount").Text = rows.Count + " 個已儲存帳號";
            Element<StackPanel>("EmptyState").Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateButtons();
        }
        private void MoveSelected(int direction)
        {
            if (busy || Selected == null) return;
            string id = Selected.Id;
            if (demo)
            {
                var rows = List.Items.Cast<AccountRow>().ToList();
                int index = List.SelectedIndex, next = index + direction;
                if (next < 0 || next >= rows.Count) return;
                var row = rows[index]; rows.RemoveAt(index); rows.Insert(next, row);
                for (int i = 0; i < rows.Count; i++) { rows[i].Priority = i + 1; rows[i].Notify(); }
                List.ItemsSource = rows; List.SelectedItem = row;
            }
            else
            {
                if (vault == null || !vault.Move(id, direction)) return;
                RebuildRows(id);
            }
            List.ScrollIntoView(List.SelectedItem);
            Status("已更新優先順序，越上方越優先。目前 Codex 登入不變。");
        }
        private AccountRow RecommendedRow(DateTimeOffset now)
        {
            var rows = List.Items.Cast<AccountRow>().ToList();
            var recommended = AccountUsage.SelectFirstEligible(rows.Select(r => r.Account),
                account => { var row = rows.First(r => r.Account.Id == account.Id); return row.IsUsageRefreshing ? null : row.Usage; },
                null, now, AccountRow.UsageMaxAge);
            return recommended == null ? null : rows.First(r => r.Account.Id == recommended.Id);
        }
        private void UpdateRecommendation()
        {
            var recommended = RecommendedRow(DateTimeOffset.UtcNow);
            Element<Button>("SelectPriorityButton").IsEnabled = !busy && !manualOperation && recommended != null;
            Element<TextBlock>("PriorityHint").Text = "越上方越優先 · 僅採用 1 分鐘內且未到重設時間的額度";
        }
        private void SelectPriority()
        {
            if (busy) return;
            var row = RecommendedRow(DateTimeOffset.UtcNow);
            if (row == null) { Status("沒有已確認可用的帳號，請更新額度；未知狀態不代表額度已用完。"); return; }
            List.SelectedItem = row; List.ScrollIntoView(row);
            Status("已選取優先 " + row.Priority + "「" + row.Name + "」，這是目前資料中順位最高且額度可用的帳號。" +
                (current != null && row.Account.Id == current.Id && !row.Account.PendingLogin ? "目前已在使用此帳號。" : "按「切換並開啟 Codex」才會套用。"));
        }
        private async Task RefreshUsage()
        {
            if (demo)
            {
                foreach (AccountRow row in List.Items)
                    if (row.Usage != null) { row.Usage.FetchedAt = DateTimeOffset.UtcNow; row.SetUsage(row.Usage); }
                UpdateButtons(); return;
            }
            if (busy || vault == null || List.Items.Count == 0) return;
            busy = true; cancellation = new CancellationTokenSource();
            Element<Button>("BrowserButton").Visibility = Visibility.Collapsed;
            UpdateButtons();
            var rows = List.Items.Cast<AccountRow>().ToList();
            int completed = 0, failed = 0;
            try
            {
                // Read current credentials again before querying; never replace a pending login.
                Refresh();
                rows = List.Items.Cast<AccountRow>().ToList();
                string cli = Installation.CliExe();
                foreach (var row in rows)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    row.BeginUsage();
                    Status("正在查詢帳號額度（" + (completed + failed + 1) + "/" + rows.Count + "）… 可按取消。查詢不會切換目前帳號。");
                    try
                    {
                        using (var session = new UsageSession())
                        {
                            var usage = await session.ReadAsync(cli, storage, row.Account, cancellation.Token);
                            cancellation.Token.ThrowIfCancellationRequested();
                            row.SetUsage(usage); completed++;
                        }
                    }
                    catch (OperationCanceledException) { row.UsageFailed("查詢已取消，請重新更新額度。"); throw; }
                    catch (Exception ex)
                    { row.UsageFailed(ex is UserError ? ex.Message : "暫時無法查詢，請稍後重試。"); failed++; }
                }
                Status("已更新 " + completed + " 個帳號的額度。" + (failed > 0 ? "另有 " + failed + " 個查詢失敗，請查看各帳號提示。" : "") + " 數值以查詢時間為準。");
            }
            catch (OperationCanceledException) { Status("額度查詢已取消，目前登入不受影響。"); }
            catch (Exception ex) { ShowError(ex); }
            finally { busy = false; cancellation.Dispose(); cancellation = null; UpdateButtons(); }
        }
        private void SaveCurrent()
        {
            Installation.CheckConfig(home, null);
            string raw = Files.ReadAuth(Path.Combine(home, "auth.json"));
            if (raw == null) throw new UserError("找不到目前的登入快取，請使用「新增帳號」。");
            string id = Account.Parse(raw).Id;
            if (vault.Data.Accounts.Any(a => a.Id == id && a.PendingLogin && a.Auth != raw))
                throw new UserError("此帳號有新登入尚未套用。請先選取它並切換，避免蓋掉新憑證。");
            vault.Upsert(raw, null); vault.Save(); Refresh();
            Status("目前帳號已加密儲存。你可以新增另一個帳號了。");
        }
        private void Rename()
        {
            if (Selected == null) return;
            Account account = Selected;
            string label = Prompt("帳號名稱", "例如：個人、工作、研究用", account.DisplayName);
            if (label == null) return;
            bool pending = account.PendingLogin;
            vault.Upsert(account.Auth, label).PendingLogin = pending; vault.Save(); Refresh(); Status("已更新帳號名稱。");
        }
        private void Remove()
        {
            if (Selected == null) return;
            string name = Selected.DisplayName;
            vault.Remove(Selected.Id); Refresh();
            Status("已從此工具移除「" + name + "」；目前 Codex 登入不受影響。");
        }
        private string Prompt(string title, string hint, string value)
        {
            var dialog = new Window { Title = title, Width = 430, Height = 215, Owner = Window, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, Background = Brushes.White, FontFamily = Window.FontFamily };
            var panel = new StackPanel { Margin = new Thickness(24) };
            panel.Children.Add(new TextBlock { Text = hint, Margin = new Thickness(0, 0, 0, 12), Foreground = Brushes.DimGray });
            var input = new TextBox { Text = value, MaxLength = 60, FontSize = 16, Padding = new Thickness(8) }; panel.Children.Add(input);
            var save = new Button { Content = "儲存", IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 15, 0, 0), Padding = new Thickness(24, 7, 24, 7) };
            save.Click += delegate { dialog.DialogResult = true; }; panel.Children.Add(save); dialog.Content = panel;
            dialog.Loaded += delegate { input.Focus(); input.SelectAll(); };
            return dialog.ShowDialog() == true ? input.Text : null;
        }
        private void OpenBrowser()
        { if (loginUrl != null) Process.Start(new ProcessStartInfo(loginUrl) { UseShellExecute = true }); }
        private async Task AddAccount()
        {
            if (busy || vault == null) return;
            bool added = false;
            busy = true; cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10)); loginUrl = null;
            Element<Button>("BrowserButton").Visibility = Visibility.Collapsed;
            UpdateButtons(); Status("正在開啟官方登入… 請在瀏覽器選擇要新增的帳號。");
            try
            {
                using (var login = new LoginSession())
                {
                    string raw = await login.Login(Installation.CliExe(), storage, url => Window.Dispatcher.Invoke(new Action(delegate
                    {
                        loginUrl = url; Element<Button>("BrowserButton").Visibility = Visibility.Visible;
                        Status("請完成瀏覽器登入。若自動選到原帳號，請在登入頁改選另一個帳號；完成後會自動儲存。");
                        Guard(OpenBrowser);
                    })), cancellation.Token);
                    cancellation.Token.ThrowIfCancellationRequested();
                    Account account = vault.Upsert(raw, null); account.PendingLogin = true; vault.Save(); Refresh();
                    added = true;
                    List.SelectedItem = List.Items.Cast<AccountRow>().FirstOrDefault(r => r.Account.Id == account.Id);
                    Status("已儲存「" + account.DisplayName + "」。選取後按「切換並開啟 Codex」。");
                }
            }
            catch (OperationCanceledException) { Status("登入已取消或逾時。目前 Codex 帳號不變。"); }
            catch (Exception ex) { ShowError(ex); }
            finally { busy = false; loginUrl = null; cancellation.Dispose(); cancellation = null; UpdateButtons(); }
            if (added) await RefreshUsage();
        }
        private async Task Switch(string raw, bool restoring = false)
        {
            if (busy || vault == null) return;
            busy = true; cancellation = new CancellationTokenSource();
            Element<Button>("BrowserButton").Visibility = Visibility.Collapsed; UpdateButtons();
            bool applied = false, waiting = false, recovered = false;
            try
            {
                Account target = Account.Parse(raw);
                string sourceRaw = Files.ReadAuth(Path.Combine(home, "auth.json"));
                string sourceId = sourceRaw == null ? null : Account.Parse(sourceRaw).Id;
                Installation.CheckConfig(home, target);
                if (desktop == null || !File.Exists(desktop)) desktop = Installation.DesktopExe();
                waiting = true;
                Status("正在檢查工作狀態。確認閒置後，會自動關閉受管 Codex 後端並套用選取帳號。");
                await runtimes.WaitForManualExitAsync(
                    text => Window.Dispatcher.Invoke(new Action(() => Status("尚未套用選取帳號。\n" + text + "\n手動切換會等待對話與背景工作結束，再自動關閉受管後端；可按取消。"))), cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                Element<Button>("CancelButton").IsEnabled = false;
                new SwitchService(vault, home, runtimes.CredentialProcessesRunning).Apply(raw, restoring, sourceId);
                applied = true; Refresh();
                await runtimes.RestoreAsync(); recovered = true;
                await runtimes.LaunchDesktopAsync(desktop);
                Status("帳號已套用並送出啟動／恢復要求，請在 Codex 確認帳號。");
            }
            catch (OperationCanceledException) { manualFailed = true; Status("已取消切換，目前登入資料未變更。"); }
            catch (Exception ex)
            {
                manualFailed = true;
                if (applied) Status("帳號已套用，但程式恢復未完成。請手動開啟 Codex／重新載入 VS Code，並確認帳號。");
                else ShowError(ex);
            }
            if (waiting && !recovered)
            {
                try { await runtimes.RestoreAsync(); }
                catch { manualFailed = true; Status("程式恢復未完成，請檢查桌面整合或手動重新載入 VS Code。"); }
            }
            busy = false; cancellation.Dispose(); cancellation = null; UpdateButtons();
        }
        private void LoadDemo()
        {
            current = Account.Parse(SelfTests.Auth("personal", "workspace-personal", "personal@example.com", "plus", "sample"));
            var work = Account.Parse(SelfTests.Auth("work", "workspace-team", "work@example.com", "business", "sample"));
            current.Label = "個人帳號"; work.Label = "工作帳號";
            Element<TextBlock>("CurrentName").Text = current.Email;
            Element<TextBlock>("CurrentDetail").Text = "PLUS  ·  示範資料";
            var currentRow = new AccountRow { Account = current, Priority = 1, Status = "目前帳號" };
            currentRow.SetUsage(new AccountUsage { FiveHour = new UsageWindow { RemainingPercent = 80, ResetsAt = DateTimeOffset.UtcNow.AddHours(3) }, Weekly = new UsageWindow { RemainingPercent = 60, ResetsAt = DateTimeOffset.UtcNow.AddDays(6) }, FetchedAt = DateTimeOffset.UtcNow });
            var workRow = new AccountRow { Account = work, Priority = 2, Status = "已儲存" };
            workRow.SetUsage(new AccountUsage { Weekly = new UsageWindow { RemainingPercent = 90, ResetsAt = DateTimeOffset.UtcNow.AddDays(4) }, FetchedAt = DateTimeOffset.UtcNow });
            List.ItemsSource = new[] { currentRow, workRow };
            List.SelectedIndex = 1;
            Element<TextBlock>("AccountCount").Text = "2 個已儲存帳號";
            Element<StackPanel>("EmptyState").Visibility = Visibility.Collapsed;
            Status("介面預覽：此畫面使用示範帳號。實際帳號會在開啟工具後讀取。");
            UpdateButtons();
        }
        public void Render(string output, bool waiting = false)
        {
            if (waiting)
            {
                var blockers = ProcessInspector.FromSnapshots(new[] {
                    new ProcessSnapshot { Pid = 100, Name = "codex.exe", ParentName = "Code.exe", SessionId = 1 },
                    new ProcessSnapshot { Pid = 101, Name = "ChatGPT.exe", SessionId = 1 }
                }, 1);
                Status("尚未套用「work@example.com」。\n" + ProcessInspector.Describe(blockers) + "\n相關程序結束後會自動繼續，請等工具開啟 Codex。");
                foreach (string name in new[] { "AddButton", "SaveButton", "RefreshButton", "UsageButton", "RenameButton", "RemoveButton", "SwitchButton", "RestoreButton", "OpenAppButton", "MoveUpButton", "MoveDownButton", "SelectPriorityButton" }) Element<Button>(name).IsEnabled = false;
                Element<StackPanel>("ProgressActions").Visibility = Visibility.Visible;
            }
            Window.Show(); Window.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Window.ActualWidth, (int)Window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(Window);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(output)) png.Save(stream);
            Window.Close();
        }
    }

    internal static class Program
    {
        private static string DiagnosticReason(string reason)
        {
            if (String.IsNullOrEmpty(reason)) return null;
            string[] known = { "initializing", "backendUnavailable", "runtimeQueryUnavailable", "runtimeChangedDuringQuery",
                "runtimeQueryInvalid", "runtimeQueryTimeout", "unrecognizedClientProtocol", "duplicateClientRequestId",
                "unknownProcessHandle", "duplicateProcessHandle", "notificationDuringQuiesce", "backendWriteFailed",
                "backendStreamFailed", "backendExitedUnexpectedly", "unrecognizedBackendProtocol", "unknownTurn", "initializeFailed", "catalogUnavailable", "preparedRuntimeChanged",
                "desktopTransportDisconnected", "desktopMetadataUnavailable", "desktopBackendExited", "desktopWorkStateUnknown",
                "desktopStatusTimeout", "desktopStatusRejected", "desktopStatusUnavailable",
                "desktopQuiescenceProofLost", "previousInstallationStillRunning", "localWorkPolicyChanged", "normalCloseUnconfirmed", "cancelledBeforeEof",
                "orphanEofUnconfirmed", "ownerExitedWithUnconfirmedNormalBackendExit", "disabledOwnerExitedWithUnconfirmedBackendExit" };
            return known.Contains(reason) ? reason : "other";
        }
        internal static object CreateDesktopDiagnosticsReport(IEnumerable<DesktopSessionState> states, string errorType)
        {
            // Deliberately project status fields rather than serializing complete sessions:
            // diagnostic output must not contain paths, process identities or user content.
            return new { success = errorType == null, errorType = errorType, sessions = states.Select(state => new {
                state.SessionId, state.Generation, state.State, state.Known, state.Idle, state.TransportReady,
                state.ControlEnabled, Reason = DiagnosticReason(state.Reason), state.ActiveTurns, state.PendingRequests,
                state.BackgroundTerminals, state.BackendExitCode }).ToArray() };
        }
        private static int RunDesktopDiagnostics()
        {
            string directory = null;
            try
            {
                string storage = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexAccountSwitcher");
                directory = Path.Combine(storage, "integrations", "desktop", "diagnostics");
                DesktopSessionFiles.CreatePrivate(directory);
                using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                {
                    var client = new DesktopSessionClient(storage, Installation.CodexHome);
                    var states = client.InspectAsync(deadline.Token).ConfigureAwait(false).GetAwaiter().GetResult();
                    DesktopSessionFiles.Write(Path.Combine(directory, "latest.json"), CreateDesktopDiagnosticsReport(states, null));
                }
                return 0;
            }
            catch (Exception error)
            {
                if (directory != null)
                    try { DesktopSessionFiles.Write(Path.Combine(directory, "latest.json"), CreateDesktopDiagnosticsReport(new DesktopSessionState[0], error.GetType().Name)); }
                    catch { }
                return 1;
            }
        }
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length == 1 && args[0] == "--desktop-diagnostics") return RunDesktopDiagnostics();
            if (args.Length == 2 && args[0] == "--self-test") return SelfTests.Run(args[1]);
            if (args.Length == 2 && args[0] == "--preview")
            { var app = new Application(); new MainView(true).Render(args[1]); return 0; }
            if (args.Length == 2 && args[0] == "--preview-wait")
            { var app = new Application(); new MainView(true).Render(args[1], true); return 0; }
            if (args.Length != 0) return 2;
            bool first;
            using (var mutex = new Mutex(true, "Local\\CodexAccountSwitcher-" + System.Security.Principal.WindowsIdentity.GetCurrent().User.Value, out first))
            {
                if (!first) { MessageBox.Show("帳號切換器已經開啟，請使用原本的視窗。", "Codex 帳號切換器"); return 0; }
                try { var app = new Application(); var view = new MainView(false); app.Run(view.Window); return 0; }
                catch { MessageBox.Show("無法開啟帳號切換器。請確認使用 Windows 10 / 11 與 .NET Framework 4.8。", "Codex 帳號切換器"); return 1; }
                finally { mutex.ReleaseMutex(); }
            }
        }
    }
}
