using System;
using System.Collections.Generic;
using System.Linq;

namespace CodexAccountSwitcher
{
    public static class ProcessInspectorTests
    {
        private static void Check(bool value) { if (!value) throw new Exception("Process inspection assertion failed."); }
        private static ProcessSnapshot Snapshot(int pid, string name, string path = null, string parent = null, int session = 1)
        { return new ProcessSnapshot { Pid = pid, Name = name, ExecutablePath = path, ParentName = parent, SessionId = session }; }

        public static void Register(Action<string, Action> test)
        {
            test("Bulk stop targets Codex executables and leaves unrelated child work alone", delegate
            {
                var snapshots = new[] { Snapshot(1, "codex.exe"), Snapshot(2, "DesktopRuntimeHost.exe"),
                    Snapshot(3, "DesktopAppRelay.exe"), Snapshot(4, "ManagedBridge.exe"), Snapshot(5, "ChatGPT.exe"),
                    Snapshot(6, "Code.exe"), Snapshot(7, "CodexAccountSwitcher.exe"), Snapshot(8, "codex.exe", session: 2),
                    Snapshot(9, "codex.exe"), Snapshot(10, "codex.exe"), Snapshot(11, "codex.exe"), Snapshot(12, "conhost.exe"),
                    Snapshot(13, "node.exe"), Snapshot(14, "pwsh.exe"), Snapshot(15, "codex-windows-sandbox-setup.exe") };
                snapshots[5].ParentPid = 1;
                snapshots[11].ParentPid = 1;
                snapshots[12].ParentPid = 1;
                snapshots[13].ParentPid = 13;
                snapshots[14].ParentPid = 1;
                var selected = ProcessInspector.FindCodexStopTargets(snapshots, 1, 9, pid =>
                {
                    if (pid == 11) return null;
                    var item = snapshots.Single(process => process.Pid == pid);
                    return new DesktopProcessIdentity { Pid = pid, ParentPid = item.ParentPid, SessionId = item.SessionId,
                        ExecutablePath = System.IO.Path.Combine("fixture", pid == 10 ? "renamed.exe" : item.Name) };
                });
                Check(selected.Select(item => item.Pid).SequenceEqual(new[] { 1, 2, 3, 4 }));
            });
            test("Bulk stop lets Codex desktop exit and retire its relay before stopping an old backend", delegate
            {
                var app = new DesktopProcessIdentity { Pid = 20, ExecutablePath = "ChatGPT.exe" };
                var relay = new DesktopProcessIdentity { Pid = 21, ExecutablePath = "DesktopAppRelay.exe" };
                var backend = new DesktopProcessIdentity { Pid = 22, ExecutablePath = "codex.exe" };
                var host = new DesktopProcessIdentity { Pid = 23, ExecutablePath = "DesktopRuntimeHost.exe" };
                var exited = new HashSet<int>();
                var actions = new List<string>();
                var remaining = ProcessInspector.StopCodexTargets(new[] { host, backend, relay, app }, actor => actor == app,
                    actor => actor == app, actor => { actions.Add("close"); exited.Add(actor.Pid); return true; },
                    actor => exited.Contains(actor.Pid),
                    actors =>
                    {
                        actions.Add("wait");
                        if (actors.Contains(relay)) exited.Add(relay.Pid);
                        if (exited.Contains(backend.Pid)) exited.Add(host.Pid);
                    }, () => false,
                    actor => { actions.Add("force " + actor.Pid); exited.Add(actor.Pid); });
                Check(remaining.Count == 0 && actions.SequenceEqual(new[] { "close", "wait", "wait", "force 22", "wait" }));
            });
            test("Bulk stop protects desktop dependencies when the App refuses to exit or has no window", delegate
            {
                var app = new DesktopProcessIdentity { Pid = 20, ExecutablePath = "ChatGPT.exe" };
                var backend = new DesktopProcessIdentity { Pid = 22, ExecutablePath = "codex.exe" };
                foreach (bool hasWindow in new[] { true, false })
                {
                    int forced = 0;
                    var remaining = ProcessInspector.StopCodexTargets(new[] { app, backend }, actor => actor == app,
                        actor => hasWindow, actor => false, actor => false, actors => { }, () => false, actor => forced++);
                    Check(remaining.Count == 2 && forced == 0);
                }
                int forceNewDesktop = 0;
                var pending = ProcessInspector.StopCodexTargets(new[] { backend }, actor => false, actor => false,
                    actor => false, actor => false, actors => { }, () => true, actor => forceNewDesktop++);
                Check(pending.Count == 1 && forceNewDesktop == 0);
            });
            test("Reopening Codex after a stop requires a stable interval without old desktop processes", delegate
            {
                int checks = 0;
                bool quiet = ProcessInspector.WaitForDesktopQuietAsync(() => ++checks == 2,
                    System.Threading.CancellationToken.None, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(35),
                    TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
                Check(quiet && checks == 4);
                bool blocked = ProcessInspector.WaitForDesktopQuietAsync(() => true, System.Threading.CancellationToken.None,
                    TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(100)).GetAwaiter().GetResult();
                Check(!blocked);
            });
            test("VS Code extension codex remains a blocker after desktop exits", delegate
            {
                var items = new[] { Snapshot(100, "codex.exe", @"fixture\profile\.vscode\extensions\openai.chatgpt-test\bin\windows-x86_64\codex.exe") };
                var blockers = ProcessInspector.FromSnapshots(items, 1);
                Check(blockers.Count == 1 && blockers[0].Kind == "VSCode" && blockers[0].Pid == 100);
                string reason = ProcessInspector.Describe(blockers);
                Check(reason.Contains("VS Code") && reason.Contains("100") && reason.Contains("正常停止") && reason.Contains("關閉對應 VS Code 視窗"));
            });
            test("VS Code parent is recognized with an unavailable executable path", delegate
            {
                var child = Snapshot(2, "CoDeX.ExE"); child.ParentPid = 1;
                var blockers = ProcessInspector.FromSnapshots(new[] { Snapshot(1, "Code.exe"), child }, 1);
                Check(blockers.Count == 1 && blockers[0].Kind == "VSCode");
                Check(child.ParentName == null); // Classification does not mutate input snapshots.
            });
            test("VS Code Insiders and other IDE extensions receive appropriate instructions", delegate
            {
                Check(ProcessInspector.Classify(Snapshot(1, "codex", "fixture/profile/.vscode-insiders/extensions/openai.chatgpt/bin/codex.exe")).Kind == "VSCode");
                Check(ProcessInspector.Classify(Snapshot(2, "codex.exe", null, "Code-Insiders.exe")).Kind == "VSCode");
                Check(ProcessInspector.Classify(Snapshot(3, "codex.exe", @"fixture\profile\.cursor\extensions\openai.chatgpt\codex.exe")).Kind == "OtherIde");
                Check(ProcessInspector.Classify(Snapshot(4, "codex.exe", null, "Windsurf.exe")).Kind == "OtherIde");
            });
            test("Desktop UI and its bundled backend remain blocking processes", delegate
            {
                var blockers = ProcessInspector.FromSnapshots(new[] { Snapshot(1, "ChatGPT.exe"),
                    Snapshot(2, "codex.exe", @"fixture\WindowsApps\OpenAI.Codex_1_x64__test\app\resources\codex.exe"),
                    Snapshot(3, "codex.exe", null, "ChatGPT.exe") }, 1);
                Check(blockers.Count == 3 && blockers.All(value => value.Kind == "Desktop"));
            });
            test("IDE parent takes precedence over bundled desktop runtime path", delegate
            {
                var blocker = ProcessInspector.Classify(Snapshot(1, "codex.exe",
                    @"fixture\WindowsApps\OpenAI.Codex_1_x64__test\app\resources\codex.exe", "Code.exe"));
                Check(blocker.Kind == "VSCode");
            });
            test("An orphaned desktop relay remains a desktop blocker", delegate
            {
                var source = Snapshot(70, "DesktopAppRelay.exe", @"fixture\switcher\DesktopAppRelay.exe");
                source.CreationTime = "synthetic-relay-creation";
                var blockers = ProcessInspector.FromSnapshots(new[] { source,
                    Snapshot(71, "DesktopAppRelay-helper.exe"), Snapshot(72, "DesktopAppRelay.exe", session: 2) }, 1);
                Check(blockers.Count == 1 && blockers[0].Kind == "Desktop" && blockers[0].Pid == 70);
                Check(blockers[0].CreationTime == source.CreationTime);
            });
            test("Downloaded desktop CLI remains a desktop blocker when its parent is gone", delegate
            {
                var first = Snapshot(51, "codex.exe", @"fixture\profile\OpenAI\Codex\bin\synthetic-version\codex.exe");
                first.ParentPid = 900; first.CreationTime = "synthetic-cached-cli-creation";
                var second = Snapshot(52, "CoDeX.ExE", "fixture/profile/openai/codex/bin/synthetic-version/codex.exe"); second.ParentPid = 901;
                var similar = Snapshot(53, "codex.exe", @"fixture\profile\OpenAI\Codex\binary\codex.exe");
                var blockers = ProcessInspector.FromSnapshots(new[] { first, second, similar }, 1);
                Check(blockers.Count == 3 && blockers.Count(value => value.Kind == "Desktop") == 2);
                Check(blockers.Single(value => value.Pid == 51).CreationTime == first.CreationTime);
                Check(blockers.Single(value => value.Pid == 53).Kind == "CLI");
                Check(ProcessInspector.Describe(blockers).Contains("系統匣"));
            });
            test("IDE parents and extension paths take precedence over downloaded desktop CLI paths", delegate
            {
                string cached = @"fixture\profile\OpenAI\Codex\bin\synthetic-version\codex.exe";
                var editorChild = Snapshot(61, "codex.exe", cached); editorChild.ParentPid = 60;
                var ideChild = Snapshot(63, "codex.exe", cached); ideChild.ParentPid = 62;
                var blockers = ProcessInspector.FromSnapshots(new[] { Snapshot(60, "Code.exe"), editorChild,
                    Snapshot(62, "Cursor.exe"), ideChild,
                    Snapshot(64, "codex.exe", @"fixture\profile\.vscode\extensions\synthetic\OpenAI\Codex\bin\codex.exe"),
                    Snapshot(65, "codex.exe", @"fixture\profile\.windsurf\extensions\synthetic\OpenAI\Codex\bin\codex.exe") }, 1);
                Check(blockers.Count == 4 && blockers.Single(value => value.Pid == 61).Kind == "VSCode" &&
                    blockers.Single(value => value.Pid == 63).Kind == "OtherIde" && blockers.Single(value => value.Pid == 64).Kind == "VSCode" &&
                    blockers.Single(value => value.Pid == 65).Kind == "OtherIde");
            });
            test("Standalone CLI and inaccessible process paths cannot be ignored", delegate
            {
                var blockers = ProcessInspector.FromSnapshots(new[] { Snapshot(1, "codex.exe", @"fixture\tools\codex.exe", "pwsh.exe"), Snapshot(2, "codex.exe") }, 1);
                Check(blockers.Count == 2 && blockers.Any(value => value.Kind == "CLI") && blockers.Any(value => value.Kind == "Unknown"));
                Check(ProcessInspector.Describe(blockers).Contains("工作管理員"));
            });
            test("Only exact candidate names in this Windows session block switching", delegate
            {
                var blockers = ProcessInspector.FromSnapshots(new[] { Snapshot(1, "codex.exe", session: 2), Snapshot(2, "ChatGPT.exe", session: 2),
                    Snapshot(3, "CodexAccountSwitcher.exe"), Snapshot(4, "my-codex.exe"), Snapshot(5, "Code.exe"), Snapshot(6, "codex.exe") }, 1);
                Check(blockers.Count == 1 && blockers[0].Pid == 6);
            });
            test("Parent name from another Windows session is not used as ownership evidence", delegate
            {
                var child = Snapshot(2, "codex.exe"); child.ParentPid = 1;
                var blockers = ProcessInspector.FromSnapshots(new[] { Snapshot(1, "Code.exe", session: 2), child }, 1);
                Check(blockers.Single().Kind == "Unknown");
            });
            test("Empty process snapshots produce no blockers", delegate
            {
                var blockers = ProcessInspector.FromSnapshots(new ProcessSnapshot[0], 1);
                Check(blockers.Count == 0 && ProcessInspector.Describe(blockers).Contains("可以套用帳號"));
            });
            test("Integration supervisors remain blockers and retain process creation identity", delegate
            {
                var source = Snapshot(41, "ManagedBridge.exe"); source.CreationTime = "synthetic-creation";
                var blockers = ProcessInspector.FromSnapshots(new[] { source }, 1);
                Check(blockers.Count == 1 && blockers[0].Kind == "Integration" && blockers[0].CreationTime == source.CreationTime);
                Check(ProcessInspector.Describe(blockers).Contains("正常暫停"));
            });
            test("A desktop supervisor remains a blocker until its managed session is verified", delegate
            {
                var source = Snapshot(42, "DesktopRuntimeHost.exe"); source.CreationTime = "synthetic-desktop-creation";
                var blockers = ProcessInspector.FromSnapshots(new[] { source, Snapshot(43, "DesktopRuntimeHost-helper.exe") }, 1);
                Check(blockers.Count == 1 && blockers[0].Kind == "Integration" && blockers[0].CreationTime == source.CreationTime);
                Check(blockers[0].Description.Contains("桌面") && ProcessInspector.Describe(blockers).Contains("桌面控制"));
            });
        }
    }
}
