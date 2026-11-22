using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;

namespace CodexAccountSwitcher
{
    internal static class LocalCodexSnapshot
    {
        internal const string DirectoryName = "local-history-backups";
        private static readonly Regex DatabaseName = new Regex(
            @"^(?:state|thread_history|memories(?:_v2)?|goals)_\d+\.sqlite(?:-wal|-journal)?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex SqliteSetting = new Regex(@"^(?:sqlite_home|""sqlite_home""|'sqlite_home')\s*=\s*(.*)$");
        private static readonly Regex QuotedPath = new Regex(@"^(""(?:\\.|[^""\\])*""|'[^']*')\s*(?:#.*)?$");

        internal static string SqliteHome(string home)
        {
            try
            {
                CheckLocalDirectory(home);
                string directory = ConfiguredSqliteHome(home) ?? DesktopSessionFiles.CapturePath("CODEX_SQLITE_HOME") ?? home;
                CheckLocalDirectory(directory);
                return directory;
            }
            catch (IOException) { throw SaveFailed(); }
            catch (UnauthorizedAccessException) { throw SaveFailed(); }
            catch (ArgumentException) { throw SaveFailed(); }
        }

        private static string ConfiguredSqliteHome(string home)
        {
            string config = Path.Combine(home, "config.toml");
            if (!Exists(config)) return null;
            CheckRegular(config);
            foreach (string line in File.ReadLines(config))
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith("[", StringComparison.Ordinal)) break;
                var match = SqliteSetting.Match(trimmed);
                if (!match.Success) continue;
                // Recognize the normal one-line basic/literal TOML path, never guess an unsupported value.
                var quoted = QuotedPath.Match(match.Groups[1].Value);
                if (!quoted.Success) throw UnknownSqliteHome();
                string token = quoted.Groups[1].Value;
                string path = token[0] == '\'' ? token.Substring(1, token.Length - 2) : Json.Decode<string>(token);
                if (String.IsNullOrWhiteSpace(path) || path.StartsWith("~", StringComparison.Ordinal) ||
                    (Path.IsPathRooted(path) && Path.GetPathRoot(path).Length < 3)) throw UnknownSqliteHome();
                return Path.GetFullPath(Path.Combine(home, path));
            }
            return null;
        }

        // The helpers must not initialize, migrate, or inspect the live SQLite state.
        internal static void IsolateHelper(System.Diagnostics.ProcessStartInfo start, string directory)
        {
            start.EnvironmentVariables["CODEX_HOME"] = directory;
            start.EnvironmentVariables["CODEX_SQLITE_HOME"] = directory;
        }

        internal static bool HasLiveRollout(string home, string path)
        {
            if (String.IsNullOrEmpty(path) || !Path.IsPathRooted(path)) return false;
            // Codex can persist an extended-length Windows drive path.
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal) && path.Length > 6 && path[5] == ':')
                path = path.Substring(4);
            try
            {
                string root = Path.GetFullPath(Path.Combine(home, "sessions")) + Path.DirectorySeparatorChar;
                path = Path.GetFullPath(path);
                if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
                    !Path.GetFileName(path).StartsWith("rollout-", StringComparison.OrdinalIgnoreCase) ||
                    !String.Equals(Path.GetExtension(path), ".jsonl", StringComparison.OrdinalIgnoreCase)) return false;
                CheckRegular(path);
                return true;
            }
            catch (ArgumentException) { return false; }
            catch (NotSupportedException) { return false; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        internal static void Save(string home, string sqliteHome, string storage, Action confirmQuiet)
        {
            string temporary = null;
            try
            {
                CheckLocalDirectory(home);
                CheckLocalDirectory(sqliteHome);
                var files = FilesToSave(home, sqliteHome);
                if (files.Count == 0) return;
                var stamps = files.Values.Select(FileStamp).ToArray();

                string directory = Path.Combine(storage, DirectoryName);
                CheckLocalDirectory(directory);
                Files.PrivateDirectory(directory);
                // Keep the private directory and filename within the legacy Windows path limit.
                string archive = Path.Combine(directory, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" +
                    Guid.NewGuid().ToString("N").Substring(0, 16) + ".zip");
                using (var output = new FileStream(archive + ".tmp", FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    temporary = archive + ".tmp";
                    using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
                    {
                        foreach (var file in files)
                        {
                            CheckRegular(file.Value);
                            using (var source = new FileStream(file.Value, FileMode.Open, FileAccess.Read, FileShare.Read))
                            using (var destination = zip.CreateEntry(file.Key, CompressionLevel.Fastest).Open())
                                source.CopyTo(destination);
                        }
                    }
                    output.Flush(true);
                }
                confirmQuiet();
                if (!files.SequenceEqual(FilesToSave(home, sqliteHome)) ||
                    !stamps.SequenceEqual(files.Values.Select(FileStamp)))
                    throw new IOException("Local history files changed while saving.");
                File.Move(temporary, archive);
                temporary = null;
            }
            catch (IOException) { throw SaveFailed(); }
            catch (UnauthorizedAccessException) { throw SaveFailed(); }
            finally
            {
                // Earlier completed snapshots are never replaced or pruned by a switch.
                if (temporary != null)
                {
                    try { File.Delete(temporary); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }

        private static SortedDictionary<string, string> FilesToSave(string home, string sqliteHome)
        {
            var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (string name in new[] { "history.jsonl", "session_index.jsonl" })
            {
                string path = Path.Combine(home, name);
                if (Exists(path)) files.Add("home/" + name, path);
            }
            foreach (string name in new[] { "sessions", "archived_sessions", "memories", "memories_v2", "memories_extensions" })
            {
                string path = Path.Combine(home, name);
                if (Exists(path)) AddTree(files, path, "home/" + name,
                    name.StartsWith("memories", StringComparison.Ordinal) ? ".md" : ".jsonl");
            }
            if (Exists(sqliteHome))
                foreach (string path in Directory.EnumerateFiles(sqliteHome))
                    if (DatabaseName.IsMatch(Path.GetFileName(path))) files.Add("sqlite/" + Path.GetFileName(path), path);
            return files;
        }

        private static void AddTree(SortedDictionary<string, string> files, string path, string name, string extension)
        {
            if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
                throw new IOException("A local history directory is not a regular directory.");
            foreach (string file in Directory.EnumerateFiles(path))
                if (String.Equals(Path.GetExtension(file), extension, StringComparison.OrdinalIgnoreCase))
                    files.Add(name + "/" + Path.GetFileName(file), file);
            foreach (string directory in Directory.EnumerateDirectories(path))
                AddTree(files, directory, name + "/" + Path.GetFileName(directory), extension);
        }

        private static bool Exists(string path)
        {
            try { File.GetAttributes(path); return true; }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }

        private static void CheckLocalDirectory(string path)
        {
            if (new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))).DriveType != DriveType.Fixed)
                throw new IOException("Local history requires a fixed drive.");
            ManagedBridge.EnsureNoReparsePoint(path);
        }

        private static void CheckRegular(string path)
        {
            ManagedBridge.EnsureNoReparsePoint(Path.GetDirectoryName(path));
            if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0)
                throw new IOException("A local history file is not a regular on-disk file.");
        }

        private static Tuple<long, long> FileStamp(string path)
        {
            CheckRegular(path);
            var info = new FileInfo(path);
            return Tuple.Create(info.Length, info.LastWriteTimeUtc.Ticks);
        }

        private static UserError SaveFailed()
        { return new UserError("無法在本機完整備份對話與記憶，已停止切換，登入資料未變更。請確認資料夾權限、可用磁碟空間，並完全結束 Codex 後再試。"); }
        private static UserError UnknownSqliteHome()
        { return new UserError("無法確認 config.toml 中的 SQLite 資料位置，已停止切換。請檢查 sqlite_home 是否為單行加引號的本機路徑。"); }
    }
}
