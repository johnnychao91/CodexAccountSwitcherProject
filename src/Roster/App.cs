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

[assembly: System.Reflection.AssemblyTitle("Codex Roster")]
[assembly: System.Reflection.AssemblyProduct("Codex Roster")]

namespace CodexAccountSwitcher
{
    public static class RosterTheme
    {
        public static readonly Brush Primary = BrandBrush(0x5A4C8C);
        public static readonly Brush Lavender = BrandBrush(0x8E7EC6);
        public static readonly Brush Mist = BrandBrush(0xE8DEF5);
        public static readonly Brush Gold = BrandBrush(0xB48257);
        public static readonly Brush SoftGold = BrandBrush(0xD9B996);
        public static readonly Brush Cream = BrandBrush(0xF9F2EA);
        public static readonly Brush Surface = BrandBrush(0xFFFBF7);
        public static readonly Brush Ink = BrandBrush(0x312644);
        public static readonly Brush Muted = BrandBrush(0x5E536C);
        public static readonly Brush Warning = BrandBrush(0x79472F);

        private static Brush BrandBrush(int rgb)
        {
            var brush = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
            brush.Freeze();
            return brush;
        }
    }

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
        { get { return Availability == UsageAvailability.Available ? RosterTheme.Primary : Availability == UsageAvailability.Exhausted ? RosterTheme.Warning : RosterTheme.Muted; } }
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
        private DesktopProcessIdentity startupPreviousApp;
        private DesktopProcessIdentity[] startupPreviousBackends = new DesktopProcessIdentity[0];
        private AutoSwitchController automatic;
        private RuntimeCoordinator runtimes;
        private DesktopContinuationWorkflow continuation;
        private bool monitorRequested, suppressMonitorChange, manualOperation, manualFailed, closing;
        private bool startupQuitRequested, finishingSwitch;
        private bool OperationsBusy { get { return busy || manualOperation; } }
        private bool demoDesktopControlRequested = true, desktopStateQuery, desktopAutoStartChecking;
        private bool desktopConnected;
        private bool demoContinueRequested = true;
        private DesktopContinuationSupport? continuationSupport;
        private string continuationDetail;
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
            Window.Icon = LoadArtwork("RosterIcon.ico", 32);
            Element<Image>("RosterLogo").Source = LoadArtwork("RosterLogo.png");
            Click("CaptionMinimizeButton", delegate { SystemCommands.MinimizeWindow(Window); });
            Click("CaptionMaximizeButton", delegate
            {
                if (Window.WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(Window);
                else SystemCommands.MaximizeWindow(Window);
            });
            Click("CaptionCloseButton", delegate { SystemCommands.CloseWindow(Window); });
            Window.Height = Math.Min(Window.Height, Math.Max(Window.MinHeight, SystemParameters.WorkArea.Height - 24));
            List.SelectionChanged += delegate { UpdateButtons(); };
            Click("UsageButton", async delegate { await RunManual(async delegate { Refresh(); await RefreshUsage(); }); });
            Click("SaveButton", async delegate { await RunManual(async delegate { SaveCurrent(); await RefreshUsage(); }); });
            Click("RenameButton", async delegate { await RunManual(delegate { Rename(); return Task.FromResult(0); }); });
            Click("RemoveButton", async delegate { await RunManual(delegate { Remove(); return Task.FromResult(0); }); });
            Click("MoveUpButton", async delegate { await RunManual(delegate { MoveSelected(-1); return Task.FromResult(0); }); });
            Click("MoveDownButton", async delegate { await RunManual(delegate { MoveSelected(1); return Task.FromResult(0); }); });
            Click("SelectPriorityButton", delegate { Guard(SelectPriority); });
            Click("AddButton", async delegate { await RunManual(AddAccount); });
            Click("SwitchButton", async delegate { if (Selected != null) { string raw = Selected.Auth; await RunManual(() => Switch(raw)); } });
            Click("RestoreButton", async delegate { if (vault != null && vault.Data.PreviousAuth != null) { string raw = vault.Data.PreviousAuth; await RunManual(() => Switch(raw, true)); } });
            Click("OpenAppButton", async delegate { await RunManual(OpenCodexApp, preserveMonitoring: true); });
            Click("CancelButton", async delegate { if (cancellation != null) cancellation.Cancel(); else await SetMonitoring(false); });
            Click("BrowserButton", delegate { Guard(OpenBrowser); });
            Click("RuntimeButton", async delegate { await InspectRuntimes(); });
            Click("RetireBackendButton", async delegate { await RunManual(TerminateRetiringBackend, preserveMonitoring: true); });
            Click("DesktopControlButton", delegate { Guard(ShowDesktopControl); });
            Click("IntegrationButton", delegate { Guard(ShowIntegration); });
            Element<CheckBox>("MonitorCheck").Checked += async delegate { if (!suppressMonitorChange) await SetMonitoring(true); };
            Element<CheckBox>("MonitorCheck").Unchecked += async delegate { if (!suppressMonitorChange) await SetMonitoring(false); };
            Element<CheckBox>("InterruptCheck").Click += delegate { Guard(delegate { if (vault != null) { vault.Data.AllowInterrupt = Element<CheckBox>("InterruptCheck").IsChecked == true; vault.Save(); } }); };
            Click("ContinueDesktopButton", async delegate { await ToggleContinuation(); });
            Window.Closing += async delegate(object sender, System.ComponentModel.CancelEventArgs args)
            {
                if (closing) return;
                if (busy || manualOperation)
                {
                    args.Cancel = true;
                    if (finishingSwitch) Status("正在完成帳號切換，完成後即可關閉。");
                    else
                    {
                        if (cancellation != null) cancellation.Cancel();
                        Status("正在取消操作，完成後即可關閉。");
                    }
                    return;
                }
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
                    if (!OperationsBusy && !desktopAutoStartChecking && (automatic == null || !automatic.IsRunning)) await RefreshDesktopControlState();
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
                    (!vault.Data.AutoMonitorEnabled || !DesktopContinuationEnabled))
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

        private static BitmapFrame LoadArtwork(string resource, int width = 0)
        {
            using (var stream = typeof(MainView).Assembly.GetManifestResourceStream(resource))
            {
                var frames = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames;
                return width == 0 ? frames[0] : frames.First(frame => frame.PixelWidth == width);
            }
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
                        text => Window.Dispatcher.Invoke(new Action(() => Element<TextBlock>("AutoStatus").Text = "等待切換：\n" + text)), token, TryRecoverStartupBackend);
                    continuation.MarkQuiesced();
                },
                ConfirmRuntimeQuiet = runtimes.ConfirmQuietAsync,
                PrepareApply = async (target, expectedId, token) =>
                {
                    var progress = new Progress<int>(percent =>
                    {
                        if (!token.IsCancellationRequested && automatic.State == AutoSwitchState.BackingUp)
                            Element<TextBlock>("AutoStatus").Text = "正在備份本機對話與記憶… " + percent + "%";
                    });
                    var commit = await new SwitchService(vault, home, runtimes.CredentialProcessesRunning)
                        .PrepareAsync(target.Auth, false, expectedId, token, progress);
                    return delegate { commit(); continuation.MarkAccountApplied(target); };
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
        private bool DesktopContinuationEnabled
        { get { return continuationSupport == DesktopContinuationSupport.Supported && ContinuationRequested && runtimes != null && runtimes.Desktop.ControlRequested; } }
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
            var support = DesktopContinuationSupport.Unverified;
            try { support = await Task.Run(() => DesktopContinuationCompatibility.CheckTarget(DesktopPackageTarget.Resolve(Installation.DesktopExe()))); }
            catch { }
            continuationSupport = support;
            UpdateContinuationButton();
            if (support != DesktopContinuationSupport.Supported && continuation != null && continuation.WaitingForQuotaSourceId != null)
            {
                await continuation.CancelAsync();
                continuationDetail = "已取消這次續跑的額度等待。自動監看仍可依設定切換帳號。";
                UpdateContinuationDetail();
            }
        }
        private void UpdateContinuationButton()
        {
            bool requested = ContinuationRequested;
            var button = Element<Button>("ContinueDesktopButton");
            button.Content = requested ? "關閉自動續跑" : "開啟自動續跑";
            button.Background = requested ? RosterTheme.Primary : RosterTheme.Surface;
            button.Foreground = requested ? Brushes.White : RosterTheme.Primary;
            button.BorderBrush = RosterTheme.Primary;
            button.ToolTip = "記住此開關；不會立即送出訊息。需版本相容、自動監看及受管桌面連線。";
            var label = Element<TextBlock>("ContinuationModeText");
            label.Text = !requested ? "自動續跑：已關閉" : continuationSupport == DesktopContinuationSupport.LocalWorkOnly ?
                "自動續跑：設定已開啟 · 此版 Codex 暫不支援桌面續跑" : continuationSupport == DesktopContinuationSupport.Unverified ?
                "自動續跑：設定已開啟 · Codex 版本未通過驗證，暫不執行" : !continuationSupport.HasValue ?
                "自動續跑：設定已開啟 · 版本尚未確認" : !monitorRequested ?
                "自動續跑：已開啟 · 等待開啟自動監看" : !desktopConnected ?
                "自動續跑：已開啟 · 等待桌面連線" : "自動續跑：已開啟 · 等待額度觸發";
            label.Foreground = requested && continuationSupport != DesktopContinuationSupport.Supported ? RosterTheme.Warning : RosterTheme.Primary;
            UpdateContinuationDetail();
        }

        private void UpdateContinuationDetail()
        {
            var label = Element<TextBlock>("ContinuationStatusText");
            string detail = continuationDetail;
            if (String.IsNullOrEmpty(detail) && monitorRequested && ContinuationRequested &&
                continuationSupport == DesktopContinuationSupport.LocalWorkOnly)
                detail = "此版 Codex 需回原對話手動續跑。";
            label.Text = detail ?? String.Empty;
            label.Visibility = String.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;
        }

        private DesktopContinuationWorkflow CreateContinuationWorkflow()
        {
            DesktopContinuationCheckpoint capturedCheckpoint = null;
            return new DesktopContinuationWorkflow(new ContinuationTicketStore(storage, home), new DesktopContinuationOperations
            {
                Enabled = () => DesktopContinuationEnabled,
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
                Status = text => Window.Dispatcher.Invoke(new Action(() => { continuationDetail = text; UpdateContinuationDetail(); }))
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
            if (demo || automatic == null || OperationsBusy) { SetMonitorCheck(monitorRequested); return; }
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
            if (!monitorRequested || OperationsBusy || automatic == null || automatic.IsRunning) return;
            await automatic.TickAsync();
            if (!closing && !manualOperation) Guard(Refresh);
        }

        private Task RunManual(Func<Task> action, bool preserveMonitoring = false)
        {
            if (OperationsBusy || preserveMonitoring && automatic != null && automatic.IsRunning) return Task.FromResult(0);
            return RunManualCore(action, preserveMonitoring);
        }

        private async Task RunManualCore(Func<Task> action, bool preserveMonitoring)
        {
            manualOperation = true; manualFailed = false; UpdateButtons();
            bool succeeded = false;
            try
            {
                if (!preserveMonitoring && automatic != null) await automatic.StopAsync();
                if (!preserveMonitoring)
                    Element<TextBlock>("AutoStatus").Text = monitorRequested ? "手動操作中，自動切換暫停。" : String.Empty;
                await action(); succeeded = !manualFailed;
            }
            catch (Exception ex) { ShowError(ex); }
            finally { manualOperation = false; }
            if (!preserveMonitoring && monitorRequested && automatic != null)
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
                var result = await runtimes.InspectAsync(CancellationToken.None);
                int pid = result.Unmanaged.Count == 0 ? 0 : await WithUnmanagedDesktop(CancellationToken.None, app => app.Pid);
                Status(result.Describe() + (pid > 0 ? "\n可核對的一般 Codex App：PID " + pid + "。" :
                    result.Unmanaged.Count > 0 ? "\n目前程序無法核對為單一可自動關閉的 Codex 桌面。" : ""));
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
            var status = Element<TextBlock>("DesktopControlStatusText");
            bool requested;
            try { requested = DesktopControlRequested; }
            catch
            {
                status.Text = "無法讀取桌面控制設定";
                status.Foreground = RosterTheme.Warning;
                return;
            }
            status.Text = requested ? "桌面 · " + desktopConnectionText : "桌面控制已關閉";
            status.Foreground = requested && desktopConnected ? RosterTheme.Primary : RosterTheme.Muted;
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
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(desktopStartupLifetime.Token))
                {
                    deadline.CancelAfter(TimeSpan.FromSeconds(15));
                    if (!runtimes.Desktop.ControlRequested) { desktopConnected = false; desktopConnectionText = "自動控制已停用"; return; }
                    var states = await runtimes.Desktop.InspectAsync(deadline.Token);
                    var connected = states.Where(IsConnectedDesktopSession).ToList();
                    desktopConnected = connected.Count > 0;
                    if (desktopConnected)
                        desktopConnectionText = connected.All(s => s.Known) ? (connected.All(s => s.Idle) ? "已連線，閒置" : "已連線，忙碌") : "已連線，工作狀態尚待確認";
                    else if (states.Any(s => s.Reason == "previousInstallationStillRunning")) desktopConnectionText = "等待正常 Quit，接續新版控制";
                    else if (states.Any(s => s.State == "quiesced" && s.ControlEnabled)) desktopConnectionText = "已暫停，等待恢復";
                    else if (!desktopStartupTimer.IsEnabled) desktopConnectionText = "尚未連線";
                }
            }
            catch { desktopConnected = false; desktopConnectionText = "無法確認連線，請檢查工作狀態"; }
            finally { desktopStateQuery = false; UpdateDesktopControlIndicator(); UpdateContinuationButton(); }
        }
        private async Task TryStartDesktopAutomatically()
        {
            if (demo || runtimes == null || desktopAutoStartChecking || desktopStateQuery || closing || desktopStartupLifetime.IsCancellationRequested ||
                OperationsBusy || automatic != null && automatic.IsRunning || Window.OwnedWindows.Cast<Window>().Any(w => w.IsVisible)) return;
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
                    if (OperationsBusy || closing || deadline.IsCancellationRequested || !runtimes.Desktop.ControlRequested ||
                        Window.OwnedWindows.Cast<Window>().Any(w => w.IsVisible)) return;
                    if (decision == DesktopAutoStartDecision.WaitingForExit)
                    {
                        desktopConnectionText = desktopConnected ? "已連線，其他 Codex 仍在運作" :
                            startupQuitRequested ? "等待 Codex 收尾或從選單結束" : "等待現有 Codex 正常退出";
                        if (!desktopConnected && processes.Count > 0 && processes.Count <= 2)
                        {
                            bool recovered = false;
                            await RunManual(async () =>
                            {
                                recovered = await TryRecoverStartupBackend(deadline.Token);
                                if (recovered) await StartDesktopControlCore(waitForExit: true);
                            }, preserveMonitoring: true);
                            if (recovered) return;
                        }
                        if (!desktopConnected && !startupQuitRequested && processes.Count > 0 && processes.All(p => p.Kind == "Desktop"))
                            await RunManual(() => TryQuitUnmanagedDesktop(deadline.Token), preserveMonitoring: true);
                        return;
                    }
                    await RunManual(() => StartDesktopControlCore(), preserveMonitoring: true);
                }
            }
            catch (OperationCanceledException) { desktopConnected = false; if (!desktopStartupLifetime.IsCancellationRequested) desktopConnectionText = "待連線；工作狀態查詢逾時"; }
            catch { desktopConnected = false; desktopStartupTimer.Stop(); desktopConnectionText = "自動啟動尚未完成，請從桌面控制檢查"; }
            finally { desktopAutoStartChecking = false; UpdateDesktopControlIndicator(); }
        }
        private async Task TryQuitUnmanagedDesktop(CancellationToken token)
        {
            var quit = await RequestUnmanagedDesktopQuit(token, rememberStartupOwner: true);
            if (quit == null) return;
            startupQuitRequested = true;
            if (!quit.Success)
            {
                Status("Codex 尚未正常退出。請從 Codex 選單選「結束」，Roster 會接著連線。");
                return;
            }
            await StartDesktopControlCore(waitForExit: true);
        }
        private Task<DesktopShutdownResult> RequestUnmanagedDesktopQuit(CancellationToken token, bool rememberStartupOwner = false)
        {
            if (!runtimes.Desktop.ControlRequested) return Task.FromResult<DesktopShutdownResult>(null);
            return WithUnmanagedDesktop(token, app =>
            {
                runtimes.Desktop.ValidateLaunchPrerequisites(app.ExecutablePath);
                token.ThrowIfCancellationRequested();
                return runtimes.Desktop.ControlRequested ? DesktopNative.Shutdown(app) : null;
            }, rememberStartupOwner);
        }
        private Task<T> WithUnmanagedDesktop<T>(CancellationToken token, Func<DesktopProcessIdentity, T> action, bool rememberStartupOwner = false)
        {
            return Task.Run(async () =>
            {
                token.ThrowIfCancellationRequested();
                string executable = Installation.DesktopExe();
                if (String.IsNullOrEmpty(executable)) return default(T);
                var states = await runtimes.Desktop.InspectAsync(token).ConfigureAwait(false);
                var blockers = ProcessInspector.Inspect();
                var actors = DesktopNative.Snapshot();
                try
                {
                    using (var current = Process.GetCurrentProcess())
                    {
                        var app = DesktopAutoStart.FindUnmanagedApp(states, blockers, actors, executable, current.SessionId,
                            DesktopNative.Same, DesktopNative.HasAppWindow);
                        if (app != null && rememberStartupOwner)
                        {
                            var target = DesktopPackageTarget.Resolve(executable);
                            startupPreviousApp = app;
                            startupPreviousBackends = actors.Where(actor => String.Equals(actor.ExecutablePath, target.CliExecutable, StringComparison.OrdinalIgnoreCase) &&
                                blockers.Any(b => b.Pid == actor.Pid)).ToArray();
                        }
                        return app == null ? default(T) : action(app);
                    }
                }
                finally { DisposeDesktopActors(actors); }
            }, token);
        }
        private Task<bool> TryRecoverStartupBackend(CancellationToken token)
        {
            return Task.Run(async () =>
            {
                if (!runtimes.Desktop.ControlRequested) return false;
                var blockers = ProcessInspector.Inspect();
                if (blockers.Count == 0 || blockers.Count > 2) return false;
                var states = await runtimes.Desktop.InspectAsync(token).ConfigureAwait(false);
                if (states.Count > 1) return false;
                var previous = states.SingleOrDefault();
                var closing = previous != null && previous.CanRecoverOwnerExit && DesktopNative.Same(previous.Host) ? previous : null;
                string executable = Installation.DesktopExe();
                if (String.IsNullOrEmpty(executable)) return false;
                var target = DesktopPackageTarget.Resolve(executable);
                var actors = DesktopNative.Snapshot();
                try
                {
                    var backend = DesktopAutoStart.FindStartupRecoveryBackend(blockers, actors, target, previous, startupPreviousApp,
                        startupPreviousBackends, DesktopNative.Same, DesktopNative.ConfirmedExited);
                    if (backend == null) return false;
                    runtimes.Desktop.ValidateLaunchPrerequisites(executable);
                    token.ThrowIfCancellationRequested();
                    if (!runtimes.Desktop.ControlRequested) return false;
                    if (closing != null) await runtimes.Desktop.TerminateRetiringBackendsAsync(closing, token, backend).ConfigureAwait(false);
                    else
                    {
                        // Rescan before taking the termination handle. A newly opened App
                        // or another backend invalidates a single-process recovery.
                        var currentSessions = await runtimes.Desktop.InspectAsync(token).ConfigureAwait(false);
                        if (currentSessions.Count > 1) return false;
                        var fresh = DesktopNative.Snapshot();
                        try
                        {
                            var confirmed = DesktopAutoStart.FindStartupRecoveryBackend(ProcessInspector.Inspect(), fresh, target,
                                currentSessions.SingleOrDefault(), startupPreviousApp, startupPreviousBackends, DesktopNative.Same, DesktopNative.ConfirmedExited);
                            if (!DesktopNative.Equal(backend, confirmed)) return false;
                            token.ThrowIfCancellationRequested();
                            if (!runtimes.Desktop.ControlRequested || !DesktopNative.TerminateVerified(backend)) return false;
                        }
                        finally { DisposeDesktopActors(fresh); }
                    }
                    return true;
                }
                finally { DisposeDesktopActors(actors); }
            }, token);
        }
        private static void DisposeDesktopActors(IEnumerable<DesktopProcessIdentity> actors)
        {
            foreach (var actor in actors)
            {
                ProcessInspector.DisposeIdentity(actor);
                if (actor.ParentIdentity != null) ProcessInspector.DisposeIdentity(actor.ParentIdentity);
            }
        }
        private async Task StartDesktopControlCore(bool waitForExit = false)
        {
            desktopStartupTimer.Stop();
            if (!runtimes.Desktop.ControlRequested) return;
            busy = true; cancellation = new CancellationTokenSource(); UpdateButtons();
            try
            {
                Status(waitForExit ? "正在等待 Codex 收尾，隨後會自動重開。" : "正在連線至 Codex…");
                desktopConnected = false; desktopConnectionText = "正在啟動受管桌面"; UpdateDesktopControlIndicator();
                string executable = Installation.DesktopExe();
                if (String.IsNullOrEmpty(executable)) throw new UserError("找不到 Codex App。請先在目前的 Windows 使用者安裝官方 Codex App。");
                if (waitForExit)
                {
                    try
                    {
                        await DesktopFirstEnableWaiter.WaitAsync(async () =>
                        {
                            await TryRecoverStartupBackend(cancellation.Token).ConfigureAwait(false);
                            return ProcessInspector.Inspect();
                        },
                            null, cancellation.Token, TimeSpan.FromMilliseconds(700), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(20));
                    }
                    catch (UserError)
                    {
                        desktopConnectionText = "等待 Codex 相關程序收尾";
                        desktopStartupTimer.Start();
                        Status("Codex 還在收尾，退出後會自動連線。");
                        return;
                    }
                }
                await runtimes.Desktop.OpenManagedAsync(executable,
                    text => Window.Dispatcher.Invoke(new Action(() => Status(text))), cancellation.Token, TryRecoverStartupBackend);
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token))
                {
                    deadline.CancelAfter(TimeSpan.FromSeconds(15));
                    var states = await runtimes.Desktop.InspectAsync(deadline.Token);
                    if (!HasDesktopConnection(states)) throw new UserError("尚未確認桌面連線，可從「進階選項」檢查工作狀態。");
                }
                Status("已連線至 Codex。");
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
            var actors = await Task.Run(() => DesktopNative.Snapshot());
            try
            {
                var existing = actors.Where(actor => String.Equals(actor.ExecutablePath, executable, StringComparison.OrdinalIgnoreCase) &&
                    DesktopNative.Same(actor)).ToList();
                if (existing.Count > 0)
                {
                    Status(existing.Any(DesktopNative.TryShowApp) ? "已顯示 Codex。" : "Codex 已在執行，可從工作列或系統匣開啟。");
                    await RefreshDesktopControlState();
                    return;
                }
            }
            finally { DisposeDesktopActors(actors); }
            if (runtimes.Desktop.ControlRequested)
            {
                await StartDesktopControlCore();
                return;
            }
            Installation.Launch(executable);
            Status("已送出 Codex App 開啟要求。");
        }

        private Window CreateThemedDialog(string title, double width)
        {
            var dialog = new Window { Title = title, Owner = Window, Width = width, Icon = Window.Icon,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
                Background = RosterTheme.Cream, Foreground = RosterTheme.Ink, FontFamily = Window.FontFamily };
            dialog.Resources[typeof(Button)] = Window.Resources[typeof(Button)];
            return dialog;
        }

        private void ShowDesktopControl() { CreateDesktopControlDialog().ShowDialog(); }
        private Window CreateDesktopControlDialog()
        {
            var dialog = CreateThemedDialog("桌面控制", 610);
            dialog.Height = Math.Min(570, SystemParameters.WorkArea.Height - 40);
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
                stateTitle.Foreground = enabled ? RosterTheme.Primary : RosterTheme.Muted;
                stateBadge.Background = enabled ? RosterTheme.Mist : RosterTheme.Surface;
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
                    else { startupQuitRequested = false; desktopStartupTimer.Start(); }
                    desktopConnected = false; desktopConnectionText = enabled ? "待開啟受管桌面" : "自動控制已停用";
                    refreshState();
                }
                catch (Exception error) { ShowError(error); refreshState(); stateDetail.Text = Element<TextBlock>("StatusText").Text; }
                finally { controlSwitch.IsEnabled = !demo && runtimes != null && !busy && !manualOperation && (automatic == null || !automatic.IsRunning); }
            };
            refreshState();
            dialog.Loaded += async delegate { await RefreshDesktopControlState(); refreshState(); };
            panel.Children.Add(new TextBlock { Text = "Roster 開啟時會自動連線。若已有一般 Codex 視窗，會在確認後送出正常退出，再接管重開。", TextWrapping = TextWrapping.Wrap, LineHeight = 22 });
            bool savedBlockLocalWork = !demo && runtimes != null && runtimes.Desktop.BlockLocalWork;
            var blockLocalWork = new CheckBox { Content = "受管桌面封鎖 Local Work（重新啟動後生效）", IsChecked = savedBlockLocalWork,
                IsEnabled = !demo && runtimes != null && !busy && !manualOperation && (automatic == null || !automatic.IsRunning), Margin = new Thickness(0, 14, 0, 0) };
            var localWorkStatus = new TextBlock { Text = "僅影響下次由切換器啟動的桌面。請正常 Quit 後重新開啟；封鎖後 Local Work 無法使用，可能顯示設定錯誤。無法驗證 App 版本時會停止啟動。",
                TextWrapping = TextWrapping.Wrap, LineHeight = 20, Foreground = RosterTheme.Muted, Margin = new Thickness(0, 6, 0, 0) };
            blockLocalWork.Click += delegate
            {
                try
                {
                    runtimes.Desktop.SetBlockLocalWork(blockLocalWork.IsChecked == true);
                    savedBlockLocalWork = blockLocalWork.IsChecked == true;
                    localWorkStatus.Text = "設定已儲存。切換器下次重開 Codex 時會套用。";
                }
                catch (Exception error)
                {
                    blockLocalWork.IsChecked = savedBlockLocalWork;
                    ShowError(error); localWorkStatus.Text = Element<TextBlock>("StatusText").Text;
                }
            };
            panel.Children.Add(blockLocalWork); panel.Children.Add(localWorkStatus);
            AddRecoveryButton(dialog, panel);
            dialog.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            return dialog;
        }
        private void ShowIntegration() { CreateVSCodeIntegrationDialog().ShowDialog(); }
        private Window CreateVSCodeIntegrationDialog()
        {
            string package = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "codex-account-switcher-vscode.vsix");
            var dialog = CreateThemedDialog("VS Code 實驗整合", 610);
            dialog.Height = Math.Min(290, SystemParameters.WorkArea.Height - 40);
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
        private async Task TerminateRetiringBackend()
        {
            var token = desktopStartupLifetime.Token;
            var states = await runtimes.Desktop.InspectAsync(token);
            if (states.Count != 1 || !states[0].CanRecoverOwnerExit)
                throw new UserError("沒有單一可核對且桌面已退出的舊 Codex 後端。");
            if (MessageBox.Show(Window, "強制結束已關閉桌面的舊後端，可能中斷該後端尚未完成的工作。要繼續嗎？",
                "結束舊 Codex 後端", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            await runtimes.Desktop.TerminateRetiringBackendsAsync(states[0], token);
            Status("舊 Codex 後端已退出。可重試切換帳號或開啟 Codex。");
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
            bool operationsReady = !OperationsBusy;
            foreach (string name in new[] { "AddButton", "SaveButton", "UsageButton", "RenameButton", "RemoveButton", "SwitchButton", "RestoreButton", "MoveUpButton", "MoveDownButton" })
                Element<Button>(name).IsEnabled = operationsReady && (vault != null || demo);
            Element<Button>("SaveButton").IsEnabled &= current != null;
            foreach (string name in new[] { "RenameButton", "RemoveButton", "SwitchButton" }) Element<Button>(name).IsEnabled &= Selected != null;
            Element<Button>("SwitchButton").IsEnabled &= Selected != null && (current == null || Selected.Id != current.Id || (Selected.PendingLogin && Selected.Auth != currentRaw));
            Element<Button>("RestoreButton").IsEnabled &= vault != null && vault.Data.PreviousAuth != null;
            Element<Button>("MoveUpButton").IsEnabled &= List.SelectedIndex > 0;
            Element<Button>("MoveDownButton").IsEnabled &= List.SelectedIndex >= 0 && List.SelectedIndex < List.Items.Count - 1;
            if (demo)
                foreach (string name in new[] { "AddButton", "SaveButton", "RenameButton", "RemoveButton", "SwitchButton", "RestoreButton" }) Element<Button>(name).IsEnabled = false;
            List.IsEnabled = operationsReady;
            bool autoRunning = automatic != null && automatic.IsRunning;
            Element<Button>("OpenAppButton").IsEnabled = !demo && runtimes != null && operationsReady && !autoRunning;
            Element<Button>("CancelButton").IsEnabled = !finishingSwitch && (busy || autoRunning);
            Element<StackPanel>("ProgressActions").Visibility = busy || autoRunning ? Visibility.Visible : Visibility.Collapsed;
            Element<CheckBox>("MonitorCheck").IsEnabled = !demo && vault != null && operationsReady;
            Element<CheckBox>("InterruptCheck").IsEnabled = !demo && vault != null && operationsReady && !autoRunning;
            Element<Button>("ContinueDesktopButton").IsEnabled = (demo || vault != null) && operationsReady && !autoRunning;
            UpdateContinuationButton();
            Element<Button>("RuntimeButton").IsEnabled = !demo && vault != null && operationsReady && !autoRunning;
            Element<Button>("RetireBackendButton").IsEnabled = !demo && runtimes != null && operationsReady && !autoRunning;
            Element<Button>("DesktopControlButton").IsEnabled = (demo || runtimes != null) && operationsReady && !autoRunning;
            Element<Button>("IntegrationButton").IsEnabled = operationsReady && !autoRunning;
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
            Element<Button>("SelectPriorityButton").IsEnabled = !OperationsBusy && recommended != null;
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
            var dialog = CreateThemedDialog(title, 430);
            dialog.Height = 215;
            var panel = new StackPanel { Margin = new Thickness(24) };
            panel.Children.Add(new TextBlock { Text = hint, Margin = new Thickness(0, 0, 0, 12), Foreground = RosterTheme.Muted });
            var input = new TextBox { Text = value, MaxLength = 60, FontSize = 16, Padding = new Thickness(8),
                Background = RosterTheme.Surface, Foreground = RosterTheme.Ink, BorderBrush = RosterTheme.SoftGold };
            panel.Children.Add(input);
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
            bool applied = false, waiting = false, recovered = false, closedUnmanaged = false;
            try
            {
                Account target = Account.Parse(raw);
                string sourceRaw = Files.ReadAuth(Path.Combine(home, "auth.json"));
                string sourceId = sourceRaw == null ? null : Account.Parse(sourceRaw).Id;
                Installation.CheckConfig(home, target);
                if (desktop == null || !File.Exists(desktop)) desktop = Installation.DesktopExe();
                waiting = true;
                Status("正在檢查工作狀態。確認閒置後，會自動關閉受管 Codex 後端並套用選取帳號。");
                    var quit = await RequestUnmanagedDesktopQuit(cancellation.Token, rememberStartupOwner: true);
                closedUnmanaged = quit != null && quit.Success;
                await runtimes.WaitForManualExitAsync(
                    text => Window.Dispatcher.Invoke(new Action(() => Status("尚未套用選取帳號。\n" + text + "\n手動切換會等待對話與背景工作結束，再自動關閉受管後端；可按取消。"))), cancellation.Token, TryRecoverStartupBackend);
                var token = cancellation.Token;
                Status("正在備份本機對話與記憶，可按取消。");
                var progress = new Progress<int>(percent =>
                {
                    if (!token.IsCancellationRequested && busy && !finishingSwitch)
                        Status("正在備份本機對話與記憶… " + percent + "%，可按取消。");
                });
                var commit = await new SwitchService(vault, home, runtimes.CredentialProcessesRunning)
                    .PrepareAsync(raw, restoring, sourceId, token, progress);
                token.ThrowIfCancellationRequested();
                finishingSwitch = true;
                Element<Button>("CancelButton").IsEnabled = false;
                Status("正在套用帳號並恢復 Codex…");
                commit();
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
                try
                {
                    await runtimes.RestoreAsync();
                    if (closedUnmanaged) await runtimes.LaunchDesktopAsync(desktop);
                }
                catch { manualFailed = true; Status("程式恢復未完成，請檢查桌面整合或手動重新載入 VS Code。"); }
            }
            finishingSwitch = false; busy = false; cancellation.Dispose(); cancellation = null; UpdateButtons();
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
                foreach (string name in new[] { "AddButton", "SaveButton", "UsageButton", "RenameButton", "RemoveButton", "SwitchButton", "RestoreButton", "OpenAppButton", "MoveUpButton", "MoveDownButton", "SelectPriorityButton" }) Element<Button>(name).IsEnabled = false;
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
            string[] known = { "initializing", "backendUnavailable", "runtimeQueryUnavailable", "runtimeChangedDuringQuery", "desktopConversationLoading", "desktopResponseUnavailable",
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
            if (args.Length == 3 && args[0] == "--export-history")
            {
                try
                {
                    string storage = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexAccountSwitcher");
                    LocalCodexSnapshot.Export(storage, args[1], args[2]);
                    return 0;
                }
                catch { return 1; }
            }
            if (args.Length == 2 && args[0] == "--self-test") return SelfTests.Run(args[1]);
            if (args.Length == 2 && args[0] == "--preview")
            { var app = new Application(); new MainView(true).Render(args[1]); return 0; }
            if (args.Length == 2 && args[0] == "--preview-wait")
            { var app = new Application(); new MainView(true).Render(args[1], true); return 0; }
            if (args.Length != 0) return 2;
            bool first;
            using (var mutex = new Mutex(true, "Local\\CodexAccountSwitcher-" + System.Security.Principal.WindowsIdentity.GetCurrent().User.Value, out first))
            {
                if (!first) { MessageBox.Show("Codex Roster 已經開啟，請使用原本的視窗。", "Codex Roster"); return 0; }
                try { var app = new Application(); var view = new MainView(false); app.Run(view.Window); return 0; }
                catch { MessageBox.Show("無法開啟 Codex Roster。請確認使用 Windows 10 / 11 與 .NET Framework 4.8。", "Codex Roster"); return 1; }
                finally { mutex.ReleaseMutex(); }
            }
        }
    }
}
