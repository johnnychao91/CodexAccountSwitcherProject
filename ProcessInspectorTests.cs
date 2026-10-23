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
            test("VS Code extension codex remains a blocker after desktop exits", delegate
            {
                var items = new[] { Snapshot(100, "codex.exe", @"fixture\profile\.vscode\extensions\openai.chatgpt-test\bin\windows-x86_64\codex.exe") };
                var blockers = ProcessInspector.FromSnapshots(items, 1);
                Check(blockers.Count == 1 && blockers[0].Kind == "VSCode" && blockers[0].Pid == 100);
                string reason = ProcessInspector.Describe(blockers);
                Check(reason.Contains("VS Code") && reason.Contains("100") && reason.Contains("關閉所有 VS Code 視窗"));
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
        }
    }
}
