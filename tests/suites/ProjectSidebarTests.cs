using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Map = System.Collections.Generic.Dictionary<string, object>;

namespace CodexAccountSwitcher
{
    internal static class ProjectSidebarTests
    {
        private static void Check(bool condition)
        { if (!condition) throw new Exception("Unexpected project sidebar state."); }

        private static void Reject(Action operation)
        {
            try { operation(); }
            catch (UserError) { return; }
            throw new Exception("An unsafe sidebar switch was accepted.");
        }

        internal static void Register(Action<string, Action> test, string root)
        {
            test("Account switch seeds only verified local sidebar records before changing auth", delegate
            {
                var f = new Fixture(root);
                string before = f.Save();
                byte[] database = File.ReadAllBytes(f.Database);
                f.Apply();
                var state = Json.Object(File.ReadAllText(f.StatePath));
                var owners = Json.Map(state, ProjectSidebar.Owners);
                Check(Json.Str(owners, "owner") == ProjectSidebar.OwnerKey(f.SourceAuth));
                var incoming = Json.Map(Json.Map(owners, "inactive"), ProjectSidebar.OwnerKey(f.TargetAuth));
                var target = Json.Map(incoming, "globals");
                Check(Json.Str(Json.Map(incoming, "atoms"), "destination-preference") == "kept");
                Check(Json.Str(target, "destination-only") == "kept" && !target.ContainsKey("source-only"));
                Check(Json.Map(state, ProjectSidebar.Projects).Count == 1);
                Check(Json.Map(target, ProjectSidebar.Projects).Count == 2);
                var mappings = Json.Map(target, ProjectSidebar.Mappings);
                Check(Json.Str(Json.Map(mappings, f.Host), "legacy-first") == "db-first");
                Check(Json.Str(Json.Map(mappings, "remote-fixture"), "remote-entry") == "kept");
                Check(Json.Map(target, ProjectSidebar.Assignments).Count == 2);
                Check(Json.Str(Json.Map(Json.Map(target, ProjectSidebar.Assignments), "existing-chat"), "projectId") == "legacy-first");
                Check(Json.Str(Json.Map(target, ProjectSidebar.Hosts), "existing-chat") == "local");
                Check(Json.Encode(target[ProjectSidebar.Order]) == "[\"legacy-second\",\"legacy-first\"]");
                Check(Json.Encode(target[ProjectSidebar.Pins]) == "[\"legacy-second\",\"legacy-first\"]");
                Check(File.ReadAllText(f.AuthPath) == f.TargetAuth);
                Check(File.ReadAllText(Path.Combine(f.Home, ProjectSidebar.BackupName, "sidebar-before-switch.json")) == before);
                Check(File.ReadAllBytes(f.Database).SequenceEqual(database));
                Check(ProjectSidebar.Prepare(f.Home, f.SourceAuth, f.TargetAuth) == null);
            });

            test("An account without a sidebar gets a new empty renderer envelope and original project IDs", delegate
            {
                var f = new Fixture(root);
                Json.Map(Json.Map(f.State, ProjectSidebar.Owners), "inactive").Clear();
                f.Save();
                f.Apply();
                var state = Json.Object(File.ReadAllText(f.StatePath));
                var incoming = Json.Map(Json.Map(Json.Map(state, ProjectSidebar.Owners), "inactive"), ProjectSidebar.OwnerKey(f.TargetAuth));
                Check(Json.Map(incoming, "atoms").Count == 0);
                Check(Json.Map(Json.Map(incoming, "globals"), ProjectSidebar.Projects).ContainsKey("legacy-first"));
            });

            test("Project name, root, mapping and missing database conflicts leave credentials and sidebar untouched", delegate
            {
                foreach (var scenario in new[] { "name", "root", "mapping", "missing" })
                {
                    var f = new Fixture(root);
                    var target = Json.Map(f.Target, ProjectSidebar.Projects);
                    if (scenario == "name") Json.Map(target, "legacy-second")["name"] = "FIRST";
                    if (scenario == "root")
                    {
                        target.Add("legacy-first", Fixture.Project("legacy-first", "First", Path.Combine(f.Home, "different")));
                        Json.Map(Json.Map(f.Target, ProjectSidebar.Mappings), f.Host)["legacy-first"] = "db-first";
                    }
                    if (scenario == "mapping") Json.Map(Json.Map(f.Target, ProjectSidebar.Mappings), f.Host)["legacy-alias"] = "db-first";
                    if (scenario == "missing") File.Delete(f.Database);
                    string before = f.Save();
                    Reject(f.Apply);
                    Check(File.ReadAllText(f.AuthPath) == f.SourceAuth && File.ReadAllText(f.StatePath) == before);
                    Check(!Directory.Exists(Path.Combine(f.Home, ProjectSidebar.BackupName)));
                }
            });

            test("A concurrent sidebar edit or returning desktop blocks the switch before auth changes", delegate
            {
                foreach (bool edit in new[] { true, false })
                {
                    var f = new Fixture(root);
                    string before = f.Save();
                    int checks = 0;
                    var service = new SwitchService(new Vault(Path.Combine(f.Home, "vault")), f.Home, delegate
                    {
                        if (++checks != 2) return false;
                        if (!edit) return true;
                        File.AppendAllText(f.StatePath, "\n");
                        return false;
                    });
                    Reject(() => service.Apply(f.TargetAuth));
                    Check(File.ReadAllText(f.AuthPath) == f.SourceAuth);
                    Check(File.ReadAllText(f.StatePath) == before + (edit ? "\n" : ""));
                }
            });

            test("An unscoped sidebar is preserved byte for byte and does not require SQLite", delegate
            {
                var f = new Fixture(root);
                f.State.Remove(ProjectSidebar.Owners);
                string before = f.Save();
                File.Delete(f.Database);
                f.Apply();
                Check(File.ReadAllText(f.AuthPath) == f.TargetAuth && File.ReadAllText(f.StatePath) == before);
            });

            test("An old machine's local-host mapping cannot be used for a different Codex home", delegate
            {
                var f = new Fixture(root);
                var mappings = Json.Map(f.State, ProjectSidebar.Mappings);
                mappings.Add("local:" + Path.Combine(root, "different fixture home"), mappings[f.Host]);
                mappings.Remove(f.Host);
                string before = f.Save();
                Reject(f.Apply);
                Check(File.ReadAllText(f.AuthPath) == f.SourceAuth && File.ReadAllText(f.StatePath) == before);
            });
        }

        private sealed class Fixture
        {
            internal readonly string Home, Host, FirstRoot, Database, AuthPath, StatePath;
            internal readonly string SourceAuth = SelfTests.Auth("sidebar-source", "fixture-workspace", "source@example.com", "plus", "fixture");
            internal readonly string TargetAuth = SelfTests.Auth("sidebar-target", "fixture-workspace", "target@example.com", "plus", "fixture");
            internal readonly Map State, Target;
            internal Fixture(string root)
            {
                Home = Path.Combine(root, "側欄 fixture-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Home);
                Host = "local:" + Home;
                FirstRoot = Path.Combine(Home, "first");
                Database = Path.Combine(Home, "state_5.sqlite");
                StatePath = Path.Combine(Home, ProjectSidebar.FileName);
                AuthPath = Path.Combine(Home, "auth.json");
                File.WriteAllText(AuthPath, SourceAuth);
                Target = new Map
                {
                    { ProjectSidebar.Projects, new Map { { "legacy-second", Project("legacy-second", "Second", FirstRoot) } } },
                    { ProjectSidebar.Mappings, new Map { { Host, new Map { { "legacy-second", "db-second" } } },
                        { "remote-fixture", new Map { { "remote-entry", "kept" } } } } },
                    { ProjectSidebar.Order, new[] { "legacy-second" } }, { ProjectSidebar.Pins, new[] { "legacy-second" } },
                    { ProjectSidebar.Projectless, new[] { "projectless-chat" } },
                    { ProjectSidebar.Assignments, new Map { { "target-chat", Assignment("legacy-second") } } },
                    { "destination-only", "kept" }
                };
                State = new Map
                {
                    { ProjectSidebar.Owners, new Map { { "owner", ProjectSidebar.OwnerKey(SourceAuth) },
                        { "inactive", new Map { { ProjectSidebar.OwnerKey(TargetAuth), new Map
                            { { "atoms", new Map { { "destination-preference", "kept" } } }, { "globals", Target } } } } } } },
                    { ProjectSidebar.Projects, new Map { { "legacy-first", Project("legacy-first", "First", FirstRoot) } } },
                    { ProjectSidebar.Mappings, new Map { { Host, new Map { { "legacy-first", "db-first" } } } } },
                    { ProjectSidebar.Order, new[] { "legacy-first" } }, { ProjectSidebar.Pins, new[] { "legacy-first" } },
                    { ProjectSidebar.Assignments, new Map { { "existing-chat", Assignment("legacy-first") },
                        { "absent-chat", Assignment("legacy-first") }, { "conflicting-chat", Assignment("legacy-first") },
                        { "projectless-chat", Assignment("legacy-first") } } },
                    { "source-only", "kept" }
                };
                IntPtr db;
                Check(sqlite3_open_v2(Utf8(Database), out db, 6, IntPtr.Zero) == 0);
                try
                {
                    string sql = "CREATE TABLE projects(id TEXT, name TEXT); INSERT INTO projects VALUES ('db-first','First'),('db-second','Second');" +
                        "CREATE TABLE project_roots(project_id TEXT, position INTEGER, path TEXT);" +
                        "INSERT INTO project_roots VALUES ('db-first',0,'" + FirstRoot.Replace("'", "''") + "')," +
                        "('db-second',0,'" + FirstRoot.Replace("'", "''") + "');" +
                        "CREATE TABLE threads(id TEXT, project_id TEXT);" +
                        "INSERT INTO threads VALUES ('existing-chat','db-first'),('projectless-chat',NULL),('conflicting-chat','db-second');";
                    Check(sqlite3_exec(db, Utf8(sql), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) == 0);
                }
                finally { sqlite3_close(db); }
            }
            internal string Save() { string text = Json.Encode(State); File.WriteAllText(StatePath, text); return text; }
            internal void Apply() { new SwitchService(new Vault(Path.Combine(Home, "vault")), Home, () => false).Apply(TargetAuth); }
            internal static Map Project(string id, string name, string path)
            { return new Map { { "id", id }, { "name", name }, { "rootPaths", new[] { path } }, { "createdAt", 1 }, { "updatedAt", 2 } }; }
            private static Map Assignment(string project)
            { return new Map { { "projectId", project }, { "projectKind", "local" } }; }
        }

        private static byte[] Utf8(string value) { return Encoding.UTF8.GetBytes(value + "\0"); }
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_open_v2(byte[] path, out IntPtr db, int flags, IntPtr vfs);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_exec(IntPtr db, byte[] sql, IntPtr callback, IntPtr argument, IntPtr error);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_close(IntPtr db);
    }
}
