using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace CodexAccountSwitcher
{
    public static class AccountUiTests
    {
        private static void Check(bool condition) { if (!condition) throw new Exception("Account UI assertion failed."); }
        private static Button Button(MainView view, string name) { return (Button)view.Window.FindName(name); }
        private static void Click(MainView view, string name)
        {
            var button = Button(view, name);
            Check(button.IsEnabled);
            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        }
        public static void Register(Action<string, Action> test)
        {
            test("Stop all Codex processes is always visible next to Open App and disabled in demo mode", delegate
            {
                var view = new MainView(true);
                try
                {
                    var stop = Button(view, "StopCodexButton");
                    var open = Button(view, "OpenAppButton");
                    var toolbar = (Panel)stop.Parent;
                    Check(stop.Visibility == Visibility.Visible && !stop.IsEnabled);
                    Check(toolbar.Children.IndexOf(open) == toolbar.Children.IndexOf(stop) + 1);
                }
                finally { view.Window.Close(); }
            });
            test("Stop all Codex processes remains available during a manual switch wait", delegate
            {
                var view = new MainView(false);
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var busy = typeof(MainView).GetField("busy", flags);
                var manual = typeof(MainView).GetField("manualOperation", flags);
                var stopping = typeof(MainView).GetField("stoppingCodexProcesses", flags);
                var update = typeof(MainView).GetMethod("UpdateButtons", flags);
                try
                {
                    busy.SetValue(view, true);
                    manual.SetValue(view, true);
                    update.Invoke(view, null);
                    Check(Button(view, "StopCodexButton").IsEnabled && Button(view, "CancelButton").IsEnabled);
                    Check(!Button(view, "SwitchButton").IsEnabled && !Button(view, "OpenAppButton").IsEnabled);
                    stopping.SetValue(view, true);
                    update.Invoke(view, null);
                    Check(!Button(view, "StopCodexButton").IsEnabled);
                }
                finally
                {
                    busy.SetValue(view, false);
                    manual.SetValue(view, false);
                    stopping.SetValue(view, false);
                    view.Window.Close();
                }
            });
            test("Confirmed Codex stop cancels the pending operation and waits for its cleanup", delegate
            {
                var view = new MainView(true);
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var manual = typeof(MainView).GetField("manualOperation", flags);
                var cleanup = new TaskCompletionSource<bool>();
                var context = SynchronizationContext.Current;
                using (var pending = new CancellationTokenSource())
                {
                    try
                    {
                        manual.SetValue(view, true);
                        typeof(MainView).GetField("manualOperationTask", flags).SetValue(view, cleanup.Task);
                        typeof(MainView).GetField("cancellation", flags).SetValue(view, pending);
                        SynchronizationContext.SetSynchronizationContext(null);
                        var ready = (Task)typeof(MainView).GetMethod("WaitForOperationsBeforeCodexStop", flags).Invoke(view, null);
                        Check(pending.IsCancellationRequested && !ready.IsCompleted);
                        cleanup.SetResult(true);
                        Check(ready.Wait(1000));
                    }
                    finally
                    {
                        cleanup.TrySetResult(true);
                        SynchronizationContext.SetSynchronizationContext(context);
                        manual.SetValue(view, false);
                        view.Window.Close();
                    }
                }
            });
            test("Automatic continuation button toggles its preference without sending or changing the selected account", delegate
            {
                var view = new MainView(true);
                try
                {
                    var list = (ListBox)view.Window.FindName("AccountList");
                    var selected = list.SelectedItem;
                    string auth = ((AccountRow)selected).Account.Auth;
                    Check(Button(view, "ContinueDesktopButton").Content.ToString() == "關閉自動續跑");
                    Click(view, "ContinueDesktopButton");
                    Check(Button(view, "ContinueDesktopButton").Content.ToString() == "開啟自動續跑");
                    Check(((TextBlock)view.Window.FindName("ContinuationModeText")).Text == "自動續跑：已關閉");
                    Click(view, "ContinueDesktopButton");
                    Check(Button(view, "ContinueDesktopButton").Content.ToString() == "關閉自動續跑");
                    Check(((TextBlock)view.Window.FindName("ContinuationModeText")).Text.Contains("版本尚未確認"));
                    Check(ReferenceEquals(selected, list.SelectedItem) && ((AccountRow)selected).Account.Auth == auth);
                }
                finally { view.Window.Close(); }
            });
            test("Desktop startup failure preserves the active monitor and saved preference", delegate
            {
                var view = new MainView(false);
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "roster-monitor-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var vault = new Vault(root);
                    vault.Data.AutoMonitorEnabled = true;
                    vault.Save();
                    int cancelledQuotaWaits = 0;
                    var monitor = new AutoSwitchController(new AutoSwitchHost
                    {
                        ReadCurrent = () => null, ReadAccounts = () => vault.Data.Accounts,
                        QueryUsage = (account, token) => Task.FromResult<AccountUsage>(null),
                        WaitForRuntimeExit = token => Task.FromResult(0), ConfirmRuntimeQuiet = token => Task.FromResult(0),
                        Apply = (account, expected) => { }, Launch = () => Task.FromResult(0),
                        CancelQuotaWait = () => { cancelledQuotaWaits++; return Task.FromResult(0); }
                    });
                    typeof(MainView).GetField("vault", flags).SetValue(view, vault);
                    typeof(MainView).GetField("automatic", flags).SetValue(view, monitor);
                    typeof(MainView).GetField("monitorRequested", flags).SetValue(view, true);
                    typeof(MainView).GetMethod("SetMonitorCheck", flags).Invoke(view, new object[] { true });
                    var timer = (System.Windows.Threading.DispatcherTimer)typeof(MainView).GetField("monitorTimer", flags).GetValue(view);
                    timer.Start();
                    monitor.Enable();

                    Func<Task> attempt = () => { throw new UserError("Synthetic desktop startup failure."); };
                    var operation = (Task)typeof(MainView).GetMethod("RunManual", flags).Invoke(view, new object[] { attempt, true });
                    operation.GetAwaiter().GetResult();

                    Check(monitor.Enabled && timer.IsEnabled && cancelledQuotaWaits == 0);
                    Check(((CheckBox)view.Window.FindName("MonitorCheck")).IsChecked == true);
                    Check(new Vault(root).Data.AutoMonitorEnabled);
                }
                finally
                {
                    view.Window.Close();
                    if (System.IO.Directory.Exists(root)) System.IO.Directory.Delete(root, true);
                }
            });
            test("Automatic continuation badge distinguishes an enabled preference from an unsupported version and busy operations", delegate
            {
                var view = new MainView(true);
                try
                {
                    var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                    var support = typeof(MainView).GetField("continuationSupport", flags);
                    var enabled = typeof(MainView).GetProperty("DesktopContinuationEnabled", flags);
                    var update = typeof(MainView).GetMethod("UpdateButtons", flags);
                    string fixture = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
                    typeof(MainView).GetField("runtimes", flags).SetValue(view, new RuntimeCoordinator(fixture, fixture));
                    Check(!(bool)enabled.GetValue(view, null));
                    support.SetValue(view, DesktopContinuationSupport.Supported);
                    Check((bool)enabled.GetValue(view, null));
                    support.SetValue(view, DesktopContinuationSupport.LocalWorkOnly);
                    Check(!(bool)enabled.GetValue(view, null));
                    update.Invoke(view, null);
                    Check(((TextBlock)view.Window.FindName("ContinuationModeText")).Text.Contains("暫不支援桌面續跑"));
                    var detail = (TextBlock)view.Window.FindName("ContinuationStatusText");
                    Check(detail.Text.Contains("手動續跑") && !detail.Text.Contains("自動監看"));
                    typeof(MainView).GetField("monitorRequested", flags).SetValue(view, true);
                    update.Invoke(view, null);
                    Check(detail.Text.Contains("自動監看") && detail.Text.Contains("手動續跑"));
                    Check(Button(view, "ContinueDesktopButton").Content.ToString() == "關閉自動續跑");
                    support.SetValue(view, DesktopContinuationSupport.Unverified);
                    Check(!(bool)enabled.GetValue(view, null));
                    update.Invoke(view, null);
                    Check(((TextBlock)view.Window.FindName("ContinuationModeText")).Text.Contains("版本未通過驗證，暫不執行"));
                    Check(((TextBlock)view.Window.FindName("ContinuationStatusText")).Visibility == Visibility.Collapsed);
                    typeof(MainView).GetField("busy", flags).SetValue(view, true);
                    update.Invoke(view, null);
                    Check(!Button(view, "ContinueDesktopButton").IsEnabled);
                    typeof(MainView).GetField("busy", flags).SetValue(view, false);
                }
                finally { view.Window.Close(); }
            });
            test("Automatic continuation on and off preferences survive reopening the encrypted vault", delegate
            {
                string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CodexAccountSwitcher-continue-toggle-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var vault = new Vault(root);
                    vault.Data.AutoContinueDesktop = false; vault.Save();
                    vault = new Vault(root); Check(vault.Data.AutoContinueDesktop == false);
                    vault.Data.AutoContinueDesktop = true; vault.Save();
                    Check(new Vault(root).Data.AutoContinueDesktop == true);
                }
                finally
                {
                    Check(System.IO.Path.GetDirectoryName(root).TrimEnd('\\') == System.IO.Path.GetTempPath().TrimEnd('\\'));
                    ManagedBridge.EnsureNoReparsePoint(root); if (System.IO.Directory.Exists(root)) System.IO.Directory.Delete(root, true);
                }
            });
            test("Account order buttons preserve selection, usage snapshots, and boundary states", delegate
            {
                var view = new MainView(true);
                try
                {
                    var list = (ListBox)view.Window.FindName("AccountList");
                    var selected = (AccountRow)list.SelectedItem;
                    var usage = selected.Usage;
                    string auth = selected.Account.Auth;
                    Check(list.SelectedIndex == 1 && Button(view, "MoveUpButton").IsEnabled && !Button(view, "MoveDownButton").IsEnabled);
                    Click(view, "MoveUpButton");
                    Check(list.SelectedIndex == 0 && ReferenceEquals(list.SelectedItem, selected));
                    Check(ReferenceEquals(selected.Usage, usage) && selected.Account.Auth == auth);
                    Check(selected.Priority == 1 && ((AccountRow)list.Items[1]).Priority == 2);
                    Check(!Button(view, "MoveUpButton").IsEnabled && Button(view, "MoveDownButton").IsEnabled);
                    Click(view, "UsageButton");
                    Check(ReferenceEquals(list.Items[0], selected));
                    Click(view, "MoveDownButton");
                    Check(list.SelectedIndex == 1 && selected.Priority == 2);
                }
                finally { view.Window.Close(); }
            });
            test("Priority selection accepts weekly-only accounts and changes only selection", delegate
            {
                var view = new MainView(true);
                try
                {
                    var list = (ListBox)view.Window.FindName("AccountList");
                    var first = (AccountRow)list.Items[0];
                    var weeklyOnly = (AccountRow)list.Items[1];
                    string currentName = ((TextBlock)view.Window.FindName("CurrentName")).Text;
                    first.SetUsage(new AccountUsage { Weekly = new UsageWindow { RemainingPercent = 0 }, FetchedAt = DateTimeOffset.UtcNow });
                    list.SelectedIndex = 0;
                    Click(view, "SelectPriorityButton");
                    Check(ReferenceEquals(list.SelectedItem, weeklyOnly));
                    Check(weeklyOnly.Availability == UsageAvailability.Available && weeklyOnly.FiveHourText == "未提供");
                    Check(((TextBlock)view.Window.FindName("CurrentName")).Text == currentName);
                    Check(ReferenceEquals(list.Items[0], first) && first.Priority == 1);
                    first.SetUsage(new AccountUsage { Weekly = new UsageWindow { RemainingPercent = 50 }, FetchedAt = DateTimeOffset.UtcNow });
                    Click(view, "SelectPriorityButton");
                    Check(ReferenceEquals(list.SelectedItem, first));
                }
                finally { view.Window.Close(); }
            });
            test("Priority button rechecks expired or failed snapshots at click time", delegate
            {
                var view = new MainView(true);
                try
                {
                    var list = (ListBox)view.Window.FindName("AccountList");
                    var first = (AccountRow)list.Items[0];
                    var second = (AccountRow)list.Items[1];
                    first.Usage.FetchedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
                    second.UsageFailed("Synthetic failure");
                    var before = list.SelectedItem;
                    Click(view, "SelectPriorityButton");
                    Check(ReferenceEquals(before, list.SelectedItem));
                    Check(!Button(view, "SelectPriorityButton").IsEnabled);
                    Check(first.Availability == UsageAvailability.Unknown && second.Availability == UsageAvailability.Unknown);
                }
                finally { view.Window.Close(); }
            });
            test("Usage badges distinguish querying, exhausted, missing, and stale snapshots", delegate
            {
                var row = new AccountRow();
                Check(row.Availability == UsageAvailability.Unknown);
                row.SetUsage(new AccountUsage { Weekly = new UsageWindow { RemainingPercent = 0 }, FetchedAt = DateTimeOffset.UtcNow });
                Check(row.Availability == UsageAvailability.Exhausted);
                row.BeginUsage();
                Check(row.IsUsageRefreshing && row.Availability == UsageAvailability.Unknown);
                row.SetUsage(new AccountUsage { Weekly = new UsageWindow { RemainingPercent = 25 }, FetchedAt = DateTimeOffset.UtcNow });
                Check(!row.IsUsageRefreshing && row.Availability == UsageAvailability.Available);
                row.Usage.FetchedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
                Check(row.Availability == UsageAvailability.Unknown);
                row.UsageFailed("Synthetic cancellation");
                Check(row.Usage == null && !row.IsUsageRefreshing);
            });
        }
    }
}
