using System;
using System.Collections.Generic;
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

namespace CodexAccountSwitcher
{
    public sealed class AccountRow
    {
        public Account Account { get; set; }
        public string Name { get { return Account.DisplayName; } }
        public string Initial { get { return Name.Substring(0, 1).ToUpperInvariant(); } }
        public string Detail { get { return Account.Detail; } }
        public string Workspace { get { return Account.WorkspaceDisplay; } }
        public string Saved { get { return "儲存於 " + Account.SavedAt; } }
        public string Status { get; set; }
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
            List.SelectionChanged += delegate { UpdateButtons(); };
            Click("RefreshButton", delegate { Guard(Refresh); });
            Click("SaveButton", delegate { Guard(SaveCurrent); });
            Click("RenameButton", delegate { Guard(Rename); });
            Click("RemoveButton", delegate { Guard(Remove); });
            Click("AddButton", async delegate { await AddAccount(); });
            Click("SwitchButton", async delegate { if (Selected != null) await Switch(Selected.Auth); });
            Click("RestoreButton", async delegate { if (vault != null && vault.Data.PreviousAuth != null) await Switch(vault.Data.PreviousAuth, true); });
            Click("CancelButton", delegate { if (cancellation != null) cancellation.Cancel(); });
            Click("BrowserButton", delegate { Guard(OpenBrowser); });
            Window.Closing += delegate(object sender, System.ComponentModel.CancelEventArgs args)
            {
                if (busy) { args.Cancel = true; cancellation.Cancel(); Status("正在取消操作，完成後即可關閉。"); }
            };
            if (demo) LoadDemo();
            else Window.Loaded += delegate { Guard(delegate { desktop = Installation.DesktopExe(); vault = new Vault(storage); Refresh(); }); };
        }

        private void Status(string text) { Element<TextBlock>("StatusText").Text = text; }
        private void Guard(Action action)
        {
            try { action(); }
            catch (Exception ex) { ShowError(ex); }
            UpdateButtons();
        }
        private void ShowError(Exception ex)
        {
            string message = ex is UserError ? ex.Message :
                ex is UnauthorizedAccessException ? "無法存取資料夾，請以原本的 Windows 使用者開啟本工具。" :
                ex is IOException ? "檔案目前無法讀寫。請關閉占用檔案的程式後重試；尚未完成的操作不會覆寫帳號。" :
                "操作未完成（" + ex.GetType().Name + "）。請重新開啟工具後重試。";
            Status(message);
        }
        private void UpdateButtons()
        {
            foreach (string name in new[] { "AddButton", "SaveButton", "RefreshButton", "RenameButton", "RemoveButton", "SwitchButton", "RestoreButton" })
                Element<Button>(name).IsEnabled = !busy && (vault != null || demo);
            Element<Button>("SaveButton").IsEnabled &= current != null;
            foreach (string name in new[] { "RenameButton", "RemoveButton", "SwitchButton" }) Element<Button>(name).IsEnabled &= Selected != null;
            Element<Button>("SwitchButton").IsEnabled &= Selected != null && (current == null || Selected.Id != current.Id || (Selected.PendingLogin && Selected.Auth != currentRaw));
            Element<Button>("RestoreButton").IsEnabled &= vault != null && vault.Data.PreviousAuth != null;
            List.IsEnabled = !busy;
            Element<Button>("CancelButton").IsEnabled = busy;
            Element<StackPanel>("ProgressActions").Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
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
            var rows = vault.Data.Accounts.Select(a => new AccountRow { Account = a, Status = a.PendingLogin ? "新登入，待套用" : current != null && a.Id == current.Id ? "目前帳號" : "已儲存" }).ToList();
            List.ItemsSource = rows;
            List.SelectedItem = rows.FirstOrDefault(r => r.Account.Id == selectedId) ?? rows.FirstOrDefault(r => current == null || r.Account.Id != current.Id) ?? rows.FirstOrDefault();
            Element<TextBlock>("AccountCount").Text = rows.Count + " 個已儲存帳號";
            Element<StackPanel>("EmptyState").Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateButtons();
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
                    List.SelectedItem = List.Items.Cast<AccountRow>().FirstOrDefault(r => r.Account.Id == account.Id);
                    Status("已儲存「" + account.DisplayName + "」。選取後按「切換並開啟 Codex」。");
                }
            }
            catch (OperationCanceledException) { Status("登入已取消或逾時。目前 Codex 帳號不變。"); }
            catch (Exception ex) { ShowError(ex); }
            finally { busy = false; loginUrl = null; cancellation.Dispose(); cancellation = null; UpdateButtons(); }
        }
        private async Task Switch(string raw, bool restoring = false)
        {
            if (busy || vault == null) return;
            busy = true; cancellation = new CancellationTokenSource();
            Element<Button>("BrowserButton").Visibility = Visibility.Collapsed; UpdateButtons();
            bool applied = false;
            string blockersText = null;
            try
            {
                Account target = Account.Parse(raw);
                Installation.CheckConfig(home, target);
                if (desktop == null || !File.Exists(desktop)) desktop = Installation.DesktopExe();
                await SwitchWorkflow.RunAsync(async delegate
                {
                    var blockers = await Task.Run(() => ProcessInspector.Inspect());
                    blockersText = blockers.Count > 0 ? ProcessInspector.Describe(blockers) : null;
                    if (blockers.Count > 0)
                        Status("尚未套用「" + target.DisplayName + "」。\n" + blockersText + "\n相關程序結束後會自動繼續，請等工具開啟 Codex。");
                    else Status("相關程序已結束，正在確認後台已停止…");
                    return blockers.Count > 0;
                }, delegate
                {
                    Element<Button>("CancelButton").IsEnabled = false;
                    Status("正在保存目前帳號並套用選取帳號…");
                    new SwitchService(vault, home, Installation.IsRunning).Apply(raw, restoring); applied = true;
                }, delegate
                {
                    Refresh();
                    Installation.Launch(desktop);
                    Status("已寫入選取帳號並送出啟動要求。請在 Codex 左下角確認帳號；若提示登入失效，請重新新增登入。");
                }, cancellation.Token, TimeSpan.FromMilliseconds(1000), TimeSpan.FromMilliseconds(1000), TimeSpan.FromMinutes(5));
            }
            catch (OperationCanceledException) { Status("已取消切換，目前登入資料未變更。"); }
            catch (Exception ex)
            {
                if (applied) Status(ex is UserError ? ex.Message : "帳號已套用。Codex 未能自動開啟，請從開始功能表開啟並確認帳號。");
                else if (ex is UserError && blockersText != null) Status(ex.Message + "\n" + blockersText);
                else ShowError(ex);
            }
            finally { busy = false; cancellation.Dispose(); cancellation = null; UpdateButtons(); }
        }
        private void LoadDemo()
        {
            current = Account.Parse(SelfTests.Auth("personal", "workspace-personal", "personal@example.com", "plus", "sample"));
            var work = Account.Parse(SelfTests.Auth("work", "workspace-team", "work@example.com", "business", "sample"));
            current.Label = "個人帳號"; work.Label = "工作帳號";
            Element<TextBlock>("CurrentName").Text = current.Email;
            Element<TextBlock>("CurrentDetail").Text = "PLUS  ·  示範資料";
            List.ItemsSource = new[] { new AccountRow { Account = current, Status = "目前帳號" }, new AccountRow { Account = work, Status = "已儲存" } };
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
                foreach (string name in new[] { "AddButton", "SaveButton", "RefreshButton", "RenameButton", "RemoveButton", "SwitchButton", "RestoreButton" }) Element<Button>(name).IsEnabled = false;
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
        [STAThread]
        private static int Main(string[] args)
        {
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
