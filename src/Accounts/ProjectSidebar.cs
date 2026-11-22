using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace CodexAccountSwitcher
{
    // Seed the destination's renderer state. Codex remains responsible for changing the
    // active owner and loading that owner's settings at startup.
    internal sealed class ProjectSidebar
    {
        internal const string Owners = "electron-account-renderer-state-v1";
        internal const string Projects = "local-projects";
        internal const string Mappings = "app-server-project-id-by-legacy-project-id-by-host";
        internal const string Assignments = "thread-project-assignments";
        internal const string Hosts = "thread-project-membership-host-ids";
        internal const string Projectless = "projectless-thread-ids";
        internal const string Order = "project-order";
        internal const string Pins = "pinned-project-ids";
        internal const string FileName = ".codex-global-state.json";
        internal const string BackupName = ".codex-switcher-sidebar-backup";
        private const int MaxFileBytes = 32 * 1024 * 1024;
        private readonly string path;
        private readonly byte[] original;
        private readonly byte[] updated;

        private ProjectSidebar(string path, byte[] original, byte[] updated)
        { this.path = path; this.original = original; this.updated = updated; }

        internal static string OwnerKey(string raw)
        {
            if (raw == null) return null;
            var tokens = Json.Map(Json.Object(raw), "tokens");
            var claims = Account.ReadIdentityClaims(Json.Str(tokens, "id_token"));
            var account = Json.Map(claims, "https://api.openai.com/auth");
            string user = Json.Str(account, "chatgpt_user_id");
            string workspace = Json.Str(tokens, "account_id");
            if (!System.Text.RegularExpressions.Regex.IsMatch(user, "^[a-zA-Z0-9_-]+$") ||
                !System.Text.RegularExpressions.Regex.IsMatch(workspace, "^[a-zA-Z0-9_-]+$")) return null;
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Json.Encode(new[] { user, workspace }))))
                    .Replace("-", "").ToLowerInvariant();
        }

        internal static ProjectSidebar Prepare(string home, string oldAuth, string targetAuth, string sqliteHome = null)
        {
            string path = Path.Combine(home, FileName);
            if (oldAuth == null) return null;
            try
            {
                byte[] original = Read(path);
                if (original == null) return null;
                var serializer = new JavaScriptSerializer { MaxJsonLength = MaxFileBytes };
                var state = serializer.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(original).TrimStart('\uFEFF'));
                if (state == null) throw InvalidState();
                if (!state.ContainsKey(Owners)) return null;

                var owners = Map(state, Owners);
                object ownerValue;
                if (!owners.TryGetValue("owner", out ownerValue) || (ownerValue != null && !(ownerValue is string))) throw InvalidState();
                string owner = ownerValue as string;
                var inactive = Map(owners, "inactive");
                if (owner != null && inactive.ContainsKey(owner)) throw InvalidState();
                string sourceKey = OwnerKey(oldAuth);
                string targetKey = OwnerKey(targetAuth);
                if (sourceKey == null || targetKey == null) throw InvalidState();
                if (sourceKey == targetKey) return null;
                var source = Globals(state, inactive, owner, sourceKey);
                if (owner != sourceKey && !inactive.ContainsKey(sourceKey) && Map(state, Projects).Count != 0)
                    throw InvalidState();
                var sourceProjects = Map(source, Projects);
                if (sourceProjects.Count == 0) return null;
                var target = Globals(state, inactive, owner, targetKey);
                string host = "local:" + Path.GetFullPath(home).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string database = Path.Combine(sqliteHome ?? home, "state_5.sqlite");
                var attributes = File.Exists(database) ? File.GetAttributes(database) : FileAttributes.Offline;
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0)
                    throw InvalidState();
                using (var db = new SidebarDatabase(database))
                {
                    if (!Merge(source, target, host, sourceProjects, db, home)) return null;
                }
                if (owner != targetKey && !inactive.ContainsKey(targetKey))
                    inactive.Add(targetKey, new Dictionary<string, object> { { "atoms", new Dictionary<string, object>() }, { "globals", target } });
                byte[] updated = Encoding.UTF8.GetBytes(serializer.Serialize(state));
                if (updated.Length > MaxFileBytes) throw InvalidState();
                return new ProjectSidebar(path, original, updated);
            }
            catch (ArgumentException) { throw InvalidState(); }
            catch (InvalidOperationException) { throw InvalidState(); }
            catch (DllNotFoundException) { throw InvalidState(); }
            catch (EntryPointNotFoundException) { throw InvalidState(); }
        }

        internal void Commit(Action confirmQuiet)
        {
            ConfirmOriginal();
            string directory = Path.Combine(Path.GetDirectoryName(path), BackupName);
            if (Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw InvalidState();
            Files.PrivateDirectory(directory);
            Files.AtomicWrite(Path.Combine(directory, "sidebar-before-switch.json"), original);
            Files.AtomicWrite(path, updated, delegate { confirmQuiet(); ConfirmOriginal(); });
        }

        private void ConfirmOriginal()
        {
            var current = Read(path);
            if (current == null || !current.SequenceEqual(original))
                throw new UserError("Codex 側欄設定剛剛改變，已取消切換。請完全結束 Codex 後重試。");
        }

        private static byte[] Read(string path)
        {
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw InvalidState();
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (stream.Length > MaxFileBytes) throw InvalidState();
                    var bytes = new byte[(int)stream.Length];
                    int offset = 0;
                    while (offset < bytes.Length)
                    {
                        int count = stream.Read(bytes, offset, bytes.Length - offset);
                        if (count == 0) throw InvalidState();
                        offset += count;
                    }
                    return bytes;
                }
            }
            catch (FileNotFoundException) { return null; }
        }

        private static Dictionary<string, object> Globals(Dictionary<string, object> state,
            Dictionary<string, object> inactive, string owner, string key)
        {
            if (owner == key) return state;
            if (!inactive.ContainsKey(key)) return new Dictionary<string, object>();
            var entry = Map(inactive, key);
            if (!entry.ContainsKey("atoms") || !entry.ContainsKey("globals")) throw InvalidState();
            Map(entry, "atoms"); // Validate the envelope Codex will load.
            return Map(entry, "globals");
        }

        private static bool Merge(Dictionary<string, object> source, Dictionary<string, object> target,
            string host, Dictionary<string, object> sourceProjects, SidebarDatabase db, string home)
        {
            var sourceHosts = Map(source, Mappings);
            string sourceHost = HostKey(sourceHosts, host);
            var sourceMap = Map(sourceHosts, sourceHost);
            var targetHosts = Map(target, Mappings);
            string targetHost = HostKey(targetHosts, sourceHost);
            var targetMap = Map(targetHosts, targetHost);
            var targetProjects = Map(target, Projects);
            var knownProjects = targetProjects.ToDictionary(item => item.Key, item => new Project(item.Key, item.Value));
            var added = new HashSet<string>(StringComparer.Ordinal);
            bool changed = false;

            foreach (var item in sourceProjects)
            {
                var project = new Project(item.Key, item.Value);
                string serverId = Json.Str(sourceMap, item.Key);
                if (String.IsNullOrWhiteSpace(serverId) ||
                    sourceMap.Any(entry => entry.Key != item.Key && Object.Equals(entry.Value, serverId)) ||
                    targetMap.Any(entry => entry.Key != item.Key && Object.Equals(entry.Value, serverId)) ||
                    (targetMap.ContainsKey(item.Key) && !Object.Equals(targetMap[item.Key], serverId)))
                    throw Conflict();

                // Separate projects can intentionally use the same folder. Identity comes
                // from the existing project record and its database ID, not its root alone.
                if (knownProjects.Any(entry => entry.Key != item.Key &&
                    String.Equals(entry.Value.Name, project.Name, StringComparison.OrdinalIgnoreCase)))
                    throw Conflict();
                Project existing;
                bool present = knownProjects.TryGetValue(item.Key, out existing);
                if (present && !existing.Roots.SequenceEqual(project.Roots, StringComparer.OrdinalIgnoreCase)) throw Conflict();
                if (!db.MatchesProject(serverId, project.Name, present ? existing.Name : project.Name, project.Roots))
                    throw InvalidState();
                if (!present)
                {
                    knownProjects.Add(item.Key, project);
                    targetProjects.Add(item.Key, item.Value);
                    added.Add(item.Key);
                }
                if (!targetMap.ContainsKey(item.Key))
                {
                    targetMap.Add(item.Key, serverId);
                    changed = true;
                }
            }

            var assignments = Map(target, Assignments);
            var hosts = Map(target, Hosts);
            var sourceMembershipHosts = Map(source, Hosts);
            var projectless = new HashSet<string>(Strings(target, Projectless), StringComparer.Ordinal);
            foreach (var item in Map(source, Assignments))
            {
                var assignment = item.Value as Dictionary<string, object>;
                string id = Json.Str(assignment, "projectId");
                if (Json.Str(assignment, "projectKind") != "local" || !sourceProjects.ContainsKey(id) ||
                    projectless.Contains(item.Key) || assignments.ContainsKey(item.Key) ||
                    (sourceMembershipHosts.ContainsKey(item.Key) && Json.Str(sourceMembershipHosts, item.Key) != "local") ||
                    (hosts.ContainsKey(item.Key) && Json.Str(hosts, item.Key) != "local") ||
                    !db.MatchesThread(item.Key, Json.Str(sourceMap, id), home)) continue;
                assignments.Add(item.Key, new Dictionary<string, object> { { "projectId", id }, { "projectKind", "local" } });
                hosts[item.Key] = "local";
                changed = true;
            }
            if (!changed && added.Count == 0) return false;

            target[Projects] = targetProjects;
            targetHosts[targetHost] = targetMap;
            target[Mappings] = targetHosts;
            if (assignments.Count != 0) target[Assignments] = assignments;
            if (hosts.Count != 0) target[Hosts] = hosts;
            if (added.Count != 0)
            {
                var targetOrder = target.ContainsKey(Order) ? Strings(target, Order) : targetProjects.Keys.Where(id => !added.Contains(id));
                target[Order] = Append(targetOrder, Strings(source, Order).Concat(sourceProjects.Keys).Where(added.Contains));
                target[Pins] = Append(Strings(target, Pins), Strings(source, Pins).Where(added.Contains));
            }
            return true;
        }

        private static string[] Append(IEnumerable<string> existing, IEnumerable<string> incoming)
        {
            // Preserve even unfamiliar destination IDs. Only append missing entries.
            var seen = new HashSet<string>(existing, StringComparer.Ordinal);
            return existing.Concat(incoming.Where(seen.Add)).ToArray();
        }

        private static string HostKey(Dictionary<string, object> hosts, string expected)
        {
            var matches = hosts.Keys.Where(key => String.Equals(key, expected, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length > 1) throw Conflict();
            return matches.Length == 1 ? matches[0] : expected;
        }

        private static Dictionary<string, object> Map(Dictionary<string, object> state, string key)
        {
            object value;
            if (!state.TryGetValue(key, out value)) return new Dictionary<string, object>();
            var map = value as Dictionary<string, object>;
            if (map == null) throw InvalidState();
            return map;
        }

        private static IEnumerable<string> Strings(Dictionary<string, object> state, string key)
        {
            object value;
            if (!state.TryGetValue(key, out value)) return new string[0];
            var list = value as IList;
            if (list == null || list.Cast<object>().Any(item => !(item is string))) throw InvalidState();
            return list.Cast<string>().ToArray();
        }

        private sealed class Project
        {
            public readonly string Name;
            public readonly string[] Roots;
            public Project(string id, object value)
            {
                var record = value as Dictionary<string, object>;
                Name = Json.Str(record, "name");
                if (record == null || String.IsNullOrWhiteSpace(id) || id != Json.Str(record, "id") ||
                    String.IsNullOrWhiteSpace(Name) || record.Keys.Any(key =>
                        key != "id" && key != "name" && key != "rootPaths" && key != "createdAt" && key != "updatedAt"))
                    throw InvalidState();
                Roots = Strings(record, "rootPaths").ToArray();
                if (Roots.Length == 0 || Roots.Any(root => !Path.IsPathRooted(root))) throw InvalidState();
            }
        }

        internal static UserError InvalidState()
        { return new UserError("無法確認 Codex 本機專案或側欄設定，已停止切換。請先在 Codex 確認專案正常顯示，再試一次。"); }
        private static UserError Conflict()
        { return new UserError("本機專案的名稱、路徑或 ID 有衝突，已停止切換以避免重複專案。"); }
    }

    internal sealed class SidebarDatabase : IDisposable
    {
        private IntPtr handle;
        internal SidebarDatabase(string path)
        {
            try
            {
                Check(sqlite3_open_v2(Utf8(path), out handle, 1, IntPtr.Zero)); // SQLITE_OPEN_READONLY
                Query("BEGIN", null).ToArray(); // Hold a consistent read snapshot across the checks.
            }
            catch { Dispose(); throw; }
        }

        internal bool MatchesProject(string id, string sourceName, string targetName, string[] roots)
        {
            string[] names = Query("SELECT name FROM projects WHERE id = ?1", id).ToArray();
            return names.Length == 1 && (names[0] == sourceName || names[0] == targetName) &&
                Query("SELECT path FROM project_roots WHERE project_id = ?1 ORDER BY position", id)
                    .SequenceEqual(roots, StringComparer.OrdinalIgnoreCase);
        }

        internal bool MatchesThread(string id, string project, string home)
        {
            string[] results = Query("SELECT project_id FROM threads WHERE id = ?1", id).ToArray();
            if (results.Length != 1 || (results[0] != null && results[0] != project)) return false;
            string[] rollouts = Query("SELECT rollout_path FROM threads WHERE id = ?1", id).ToArray();
            return rollouts.Length == 1 && LocalCodexSnapshot.HasLiveRollout(home, rollouts[0]);
        }

        private IEnumerable<string> Query(string sql, string id)
        {
            IntPtr statement = IntPtr.Zero;
            try
            {
                Check(sqlite3_prepare_v2(handle, Utf8(sql), -1, out statement, IntPtr.Zero));
                if (id != null) Check(sqlite3_bind_text(statement, 1, Utf8(id), -1, new IntPtr(-1)));
                int status;
                while ((status = sqlite3_step(statement)) == 100) // SQLITE_ROW
                {
                    IntPtr value = sqlite3_column_text(statement, 0);
                    if (value == IntPtr.Zero) { yield return null; continue; }
                    var bytes = new byte[sqlite3_column_bytes(statement, 0)];
                    Marshal.Copy(value, bytes, 0, bytes.Length);
                    yield return Encoding.UTF8.GetString(bytes);
                }
                if (status != 101) throw ProjectSidebar.InvalidState(); // SQLITE_DONE
            }
            finally { if (statement != IntPtr.Zero) sqlite3_finalize(statement); }
        }

        private static byte[] Utf8(string value) { return Encoding.UTF8.GetBytes(value + "\0"); }
        private static void Check(int result) { if (result != 0) throw ProjectSidebar.InvalidState(); }
        public void Dispose()
        {
            if (handle != IntPtr.Zero) sqlite3_close(handle);
            handle = IntPtr.Zero;
        }

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_open_v2(byte[] path, out IntPtr db, int flags, IntPtr vfs);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_close(IntPtr db);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int size, out IntPtr statement, IntPtr tail);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_bind_text(IntPtr statement, int index, byte[] text, int size, IntPtr destructor);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_step(IntPtr statement);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_column_text(IntPtr statement, int index);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_bytes(IntPtr statement, int index);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_finalize(IntPtr statement);
    }
}
