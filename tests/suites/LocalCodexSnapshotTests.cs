using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;

namespace CodexAccountSwitcher
{
    internal static class LocalCodexSnapshotTests
    {
        internal static void Register(Action<string, Action> test, string root)
        {
            test("Round-trip account switch preserves local rollouts, memories and SQLite with separate private snapshots", delegate
            {
                var f = new Fixture(root);
                f.Put(f.Home, @"sessions\2026\11\21\rollout-fixture.jsonl", "live thread");
                f.Put(f.Home, @"archived_sessions\rollout-archived.jsonl", "archived thread");
                string memory = f.Put(f.Home, @"memories\memory_summary.md", "memory A");
                f.Put(f.Home, @"memories_v2\rollout_summaries\one.md", "memory v2");
                f.Put(f.Home, "history.jsonl", "history");
                f.Put(f.Home, "session_index.jsonl", "index");
                f.Put(f.Home, @"cache\cloud.jsonl", "do not preserve");
                f.Put(f.Home, @"memories\auth.json", "do not preserve");
                f.Put(f.Home, ProjectSidebar.FileName, "{\"cloud\":\"do not preserve\"}");
                f.Put(f.Home, "state_5.sqlite", "wrong directory");
                f.Put(f.Sqlite, "state_5.sqlite", "state db");
                f.Put(f.Sqlite, "state_5.sqlite-wal", "uncheckpointed db update");
                f.Put(f.Sqlite, "state_5.sqlite-shm", "rebuildable index");
                f.Put(f.Sqlite, "memories_v2_1.sqlite", "memory db");
                f.Put(f.Sqlite, "logs_2.sqlite", "do not preserve");
                string originalSetting = Environment.GetEnvironmentVariable("CODEX_SQLITE_HOME");
                try
                {
                    Environment.SetEnvironmentVariable("CODEX_SQLITE_HOME", f.Sqlite);
                    var service = new SwitchService(new Vault(f.Storage), f.Home, () => false);
                    service.Apply(f.Target);
                    Check(File.ReadAllText(f.Auth) == f.Target);
                    string first = Directory.GetFiles(f.Backups).Single();
                    using (var stream = File.OpenRead(first))
                    using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
                    {
                        Check(zip.Entries.Count == 9);
                        Check(Read(zip, "home/sessions/2026/11/21/rollout-fixture.jsonl") == "live thread");
                        Check(Read(zip, "home/archived_sessions/rollout-archived.jsonl") == "archived thread");
                        Check(Read(zip, "home/memories/memory_summary.md") == "memory A");
                        Check(Read(zip, "sqlite/state_5.sqlite") == "state db");
                        Check(Read(zip, "sqlite/state_5.sqlite-wal") == "uncheckpointed db update");
                        Check(Read(zip, "sqlite/memories_v2_1.sqlite") == "memory db");
                    }
                    File.WriteAllText(memory, "memory after B");
                    service.Apply(f.Source, true);
                    Check(File.ReadAllText(f.Auth) == f.Source);
                    Check(File.ReadAllText(memory) == "memory after B");
                    Check(File.ReadAllText(Path.Combine(f.Sqlite, "state_5.sqlite-wal")) == "uncheckpointed db update");
                    Check(File.ReadAllText(Path.Combine(f.Home, ProjectSidebar.FileName)) == "{\"cloud\":\"do not preserve\"}");
                    var snapshots = Directory.GetFiles(f.Backups, "*.zip");
                    Check(snapshots.Length == 2);
                    using (var stream = File.OpenRead(first))
                    using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
                        Check(Read(zip, "home/memories/memory_summary.md") == "memory A");
                }
                finally { Environment.SetEnvironmentVariable("CODEX_SQLITE_HOME", originalSetting); }
            });

            test("An unavailable or changing local snapshot leaves auth and the prior backup unchanged", delegate
            {
                var f = new Fixture(root);
                string memory = f.Put(f.Home, @"memories\MEMORY.md", "first");
                var service = new SwitchService(new Vault(f.Storage), f.Home, () => false);
                string originalSetting = Environment.GetEnvironmentVariable("CODEX_SQLITE_HOME");
                try
                {
                    Environment.SetEnvironmentVariable("CODEX_SQLITE_HOME", f.Sqlite);
                    service.Apply(f.Target);
                    string first = Directory.GetFiles(f.Backups).Single();
                    using (File.Open(memory, FileMode.Open, FileAccess.Write, FileShare.None))
                        Reject(() => service.Apply(f.Source, true));
                    int checks = 0;
                    var changing = new SwitchService(new Vault(f.Storage), f.Home, delegate
                    {
                        if (++checks == 2) File.AppendAllText(memory, " changed during snapshot");
                        return false;
                    });
                    Reject(() => changing.Apply(f.Source, true));
                    Check(File.ReadAllText(f.Auth) == f.Target && Directory.GetFiles(f.Backups).Single() == first);
                    using (var stream = File.OpenRead(first))
                    using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
                        Check(Read(zip, "home/memories/MEMORY.md") == "first");
                }
                finally { Environment.SetEnvironmentVariable("CODEX_SQLITE_HOME", originalSetting); }
            });

            test("Cancelling a background snapshot removes its temporary archive and preserves auth", delegate
            {
                var f = new Fixture(root);
                string path = f.Put(f.Home, @"sessions\rollout-large.jsonl", "");
                using (var stream = File.OpenWrite(path)) stream.SetLength(4 * 1024 * 1024);
                var vault = new Vault(f.Storage);
                var service = new SwitchService(vault, f.Home, () => false);
                using (var cancellation = new CancellationTokenSource())
                {
                    var progress = new SnapshotProgress(percent =>
                    {
                        Check(percent > 0 && percent < 100);
                        Check(Directory.GetFiles(f.Backups, "*.tmp").Length == 1);
                        cancellation.Cancel();
                    });
                    bool cancelled = false;
                    try { service.PrepareAsync(f.Target, false, null, cancellation.Token, progress).GetAwaiter().GetResult(); }
                    catch (OperationCanceledException) { cancelled = true; }
                    Check(cancelled && Directory.GetFiles(f.Backups).Length == 0);
                    Check(File.ReadAllText(f.Auth) == f.Source && vault.Data.PreviousAuth == null);
                }
            });

            test("A prepared switch rechecks auth before changing the vault or writing credentials", delegate
            {
                var f = new Fixture(root);
                f.Put(f.Home, "history.jsonl", "fixture history");
                var vault = new Vault(f.Storage);
                var service = new SwitchService(vault, f.Home, () => false);
                var commit = service.PrepareAsync(f.Target, false, null, CancellationToken.None, null).GetAwaiter().GetResult();
                Check(Directory.GetFiles(f.Backups, "*.zip").Length == 1);
                Check(File.ReadAllText(f.Auth) == f.Source && vault.Data.PreviousAuth == null);
                string changed = SelfTests.Auth("history-a", "workspace", "a@example.com", "plus", "rotated");
                File.WriteAllText(f.Auth, changed);
                Reject(commit);
                Check(File.ReadAllText(f.Auth) == changed && vault.Data.PreviousAuth == null);
            });
        }

        private sealed class SnapshotProgress : IProgress<int>
        {
            private readonly Action<int> report;
            internal SnapshotProgress(Action<int> report) { this.report = report; }
            public void Report(int value) { report(value); }
        }

        private static string Read(ZipArchive zip, string name)
        { using (var reader = new StreamReader(zip.GetEntry(name).Open())) return reader.ReadToEnd(); }

        private static void Check(bool value)
        { if (!value) throw new Exception("Unexpected local history snapshot state."); }

        private static void Reject(Action action)
        {
            try { action(); }
            catch (UserError) { return; }
            throw new Exception("Account switch accepted an incomplete local snapshot.");
        }

        private sealed class Fixture
        {
            internal readonly string Home, Storage, Sqlite, Auth, Backups;
            internal readonly string Source = SelfTests.Auth("history-a", "workspace", "a@example.com", "plus", "fixture");
            internal readonly string Target = SelfTests.Auth("history-b", "workspace", "b@example.com", "plus", "fixture");

            internal Fixture(string root)
            {
                string folder = Path.Combine(root, "history-fixture-" + Guid.NewGuid().ToString("N"));
                Home = Path.Combine(folder, "Codex 家目錄");
                Sqlite = Path.Combine(folder, "SQLite");
                Storage = Path.Combine(folder, "private");
                Backups = Path.Combine(Storage, LocalCodexSnapshot.DirectoryName);
                Directory.CreateDirectory(Sqlite);
                Auth = Put(Home, "auth.json", Source);
            }

            internal string Put(string root, string name, string text)
            {
                string path = Path.Combine(root, name);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, text);
                return path;
            }
        }
    }
}
