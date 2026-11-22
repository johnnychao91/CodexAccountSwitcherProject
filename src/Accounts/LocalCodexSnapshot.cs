using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32.SafeHandles;

namespace CodexAccountSwitcher
{
    internal static class LocalCodexSnapshot
    {
        internal const string DirectoryName = "local-history-backups";
        private const int CopyBufferSize = 1024 * 1024;
        private const string IncrementalDirectory = "incremental";
        private const string SnapshotSuffix = ".snapshot.json";
        private static readonly Regex Digest = new Regex(@"\A[a-z2-7]{52}\z", RegexOptions.CultureInvariant);
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

        internal static void Save(string home, string sqliteHome, string storage, Action confirmQuiet,
            CancellationToken cancellation, IProgress<int> progress)
        {
            try
            {
                cancellation.ThrowIfCancellationRequested();
                CheckLocalDirectory(home);
                CheckLocalDirectory(sqliteHome);
                var files = FilesToSave(home, sqliteHome);
                if (files.Count == 0) return;
                string directory = Path.Combine(storage, DirectoryName, IncrementalDirectory);
                CheckLocalDirectory(directory);
                ManagedBridge.PrivateDirectory(Path.GetDirectoryName(directory));
                ManagedBridge.PrivateDirectory(directory);
                string parts = Path.Combine(directory, "parts");
                ManagedBridge.PrivateDirectory(parts);
                using (EnterSnapshotLock(directory))
                    SaveSnapshot(files, home, sqliteHome, directory, parts, confirmQuiet, cancellation, progress);
            }
            catch (IOException) { throw SaveFailed(); }
            catch (UnauthorizedAccessException) { throw SaveFailed(); }
        }

        private static void SaveSnapshot(SortedDictionary<string, string> files, string home, string sqliteHome,
            string directory, string parts, Action confirmQuiet, CancellationToken cancellation, IProgress<int> progress)
        {
            var stamps = new Dictionary<string, SavedFile>(StringComparer.Ordinal);
            foreach (var file in files)
            {
                cancellation.ThrowIfCancellationRequested();
                stamps.Add(file.Key, FileStamp(file.Value));
            }
            var previous = ReadLatest(directory);
            var snapshot = new Snapshot { Version = 1, Files = new Dictionary<string, SavedFile>(StringComparer.Ordinal) };
            foreach (var file in stamps)
            {
                cancellation.ThrowIfCancellationRequested();
                SavedFile earlier;
                if (file.Value.CanReuse && previous != null && previous.Files.TryGetValue(file.Key, out earlier) &&
                    file.Value.SameSource(earlier) && PartsAvailable(parts, earlier))
                    snapshot.Files.Add(file.Key, earlier);
            }
            var changed = files.Where(file => !snapshot.Files.ContainsKey(file.Key)).ToArray();
            long total = changed.Sum(file => stamps[file.Key].Length), readBytes = 0;
            int reported = 0;
            string manifest = null;
            var added = new List<string>();
            bool published = false;
            try
            {
                if (changed.Length > 0)
                {
                    var buffer = new byte[CopyBufferSize];
                    using (var sha = new SHA256Cng())
                    foreach (var file in changed)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        var saved = stamps[file.Key];
                        var hashes = new List<string>();
                        using (var input = OpenSource(file.Value))
                        {
                            if (!saved.SameSource(FileStamp(input))) throw new IOException("Local history file changed.");
                            long remaining = saved.Length;
                            while (remaining > 0)
                            {
                                cancellation.ThrowIfCancellationRequested();
                                int size = (int)Math.Min(remaining, buffer.Length);
                                ReadFully(input, buffer, size);
                                string hash = Hash(sha, buffer, size);
                                SavePart(parts, hash, buffer, size, added, cancellation);
                                hashes.Add(hash);
                                remaining -= size;
                                readBytes += size;
                                int percent = (int)Math.Min(99, 100.0 * readBytes / total);
                                if (progress != null && percent > reported)
                                {
                                    reported = percent;
                                    progress.Report(percent);
                                }
                            }
                        }
                        saved.Parts = hashes.ToArray();
                        snapshot.Files.Add(file.Key, saved);
                    }
                }
                cancellation.ThrowIfCancellationRequested();
                confirmQuiet();
                if (!files.SequenceEqual(FilesToSave(home, sqliteHome)))
                    throw new IOException("Local history files changed while saving.");
                foreach (var file in files)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (!stamps[file.Key].SameSource(FileStamp(file.Value)))
                        throw new IOException("Local history files changed while saving.");
                }
                if (changed.Length == 0 && previous.Files.Count == files.Count) return;
                string name = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss.fffffff", CultureInfo.InvariantCulture) +
                    "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + SnapshotSuffix;
                manifest = Path.Combine(directory, name + ".tmp");
                byte[] data = Encoding.UTF8.GetBytes(Serializer().Serialize(snapshot));
                using (var output = ManagedBridge.CreatePrivateFile(manifest))
                {
                    output.Write(data, 0, data.Length);
                    output.Flush(true);
                }
                cancellation.ThrowIfCancellationRequested();
                File.Move(manifest, Path.Combine(directory, name));
                published = true;
            }
            finally
            {
                if (!published)
                {
                    DeleteTemporary(manifest);
                    foreach (string path in added) DeleteTemporary(path);
                }
            }
        }

        private static FileStream EnterSnapshotLock(string directory)
        {
            string path = Path.Combine(directory, "snapshot.lock");
            if (!Exists(path))
            {
                try { using (ManagedBridge.CreatePrivateFile(path)) { } }
                catch (IOException) { if (!Exists(path)) throw; }
            }
            CheckRegular(path);
            return new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }

        private static void SavePart(string parts, string hash, byte[] bytes, int size, List<string> added, CancellationToken cancellation)
        {
            string path = PartPath(parts, hash);
            if (PartAvailable(path, size)) return;
            string temporary = Path.Combine(parts, Guid.NewGuid().ToString("N").Substring(0, 16) + ".tmp");
            try
            {
                using (var output = ManagedBridge.CreatePrivateFile(temporary))
                {
                    output.Write(bytes, 0, size);
                    output.Flush(true);
                }
                cancellation.ThrowIfCancellationRequested();
                File.Move(temporary, path);
                added.Add(path);
            }
            finally { DeleteTemporary(temporary); }
        }

        private static bool PartsAvailable(string parts, SavedFile file)
        {
            long remaining = file.Length;
            foreach (string hash in file.Parts)
            {
                int size = (int)Math.Min(remaining, CopyBufferSize);
                if (!PartAvailable(PartPath(parts, hash), size)) return false;
                remaining -= size;
            }
            return true;
        }

        private static string PartPath(string directory, string hash) { return Path.Combine(directory, hash + ".part"); }

        private static bool PartAvailable(string path, int size)
        {
            if (!Exists(path)) return false;
            var info = new FileInfo(path);
            if ((info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0 || info.Length != size)
                throw new IOException("Invalid history backup part.");
            return true;
        }

        private static void DeleteTemporary(string path)
        {
            if (path == null) return;
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static void ReadFully(Stream stream, byte[] buffer, int size)
        {
            int offset = 0;
            while (offset < size)
            {
                int read = stream.Read(buffer, offset, size - offset);
                if (read == 0) throw new IOException("Local history file ended early.");
                offset += read;
            }
        }

        private static string Hash(SHA256 sha, byte[] bytes, int size)
        {
            // Base32 keeps all 256 bits on case-insensitive filesystems and
            // leaves room for nested user profiles under legacy path limits.
            const string alphabet = "abcdefghijklmnopqrstuvwxyz234567";
            var result = new StringBuilder(52);
            int window = 0, bits = 0;
            foreach (byte value in sha.ComputeHash(bytes, 0, size))
            {
                window = (window << 8) | value;
                bits += 8;
                while (bits >= 5)
                {
                    bits -= 5;
                    result.Append(alphabet[(window >> bits) & 31]);
                }
            }
            if (bits > 0) result.Append(alphabet[(window << (5 - bits)) & 31]);
            return result.ToString();
        }

        private static JavaScriptSerializer Serializer()
        { return new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 }; }

        private static string LatestManifest(string directory)
        {
            return Directory.EnumerateFiles(directory, "*" + SnapshotSuffix)
                .Where(path => Path.GetFileName(path).EndsWith(SnapshotSuffix, StringComparison.Ordinal))
                .OrderByDescending(Path.GetFileName, StringComparer.Ordinal).FirstOrDefault();
        }

        private static Snapshot ReadLatest(string directory)
        {
            string path = LatestManifest(directory);
            return path == null ? null : ReadSnapshot(path);
        }

        private static Snapshot ReadSnapshot(string path)
        {
            CheckRegular(path);
            if (new FileInfo(path).Length > 32 * 1024 * 1024) throw new IOException("Oversized local history manifest.");
            Snapshot snapshot;
            try { snapshot = Serializer().Deserialize<Snapshot>(File.ReadAllText(path, Encoding.UTF8)); }
            catch (ArgumentException error) { throw new IOException("Invalid local history manifest.", error); }
            catch (InvalidOperationException error) { throw new IOException("Invalid local history manifest.", error); }
            if (snapshot == null || snapshot.Version != 1 || snapshot.Files == null)
                throw new IOException("Unknown local history manifest format.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in snapshot.Files)
            {
                string[] segments = file.Key.Split('/');
                SavedFile value = file.Value;
                if (segments.Length < 2 || (segments[0] != "home" && segments[0] != "sqlite") ||
                    segments.Any(segment => String.IsNullOrEmpty(segment) || segment.EndsWith(".", StringComparison.Ordinal) ||
                        segment.EndsWith(" ", StringComparison.Ordinal) || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) ||
                    !names.Add(file.Key) || value == null || value.Length < 0 || String.IsNullOrEmpty(value.Identity) ||
                    value.Parts == null || value.Parts.LongLength != value.Length / CopyBufferSize + (value.Length % CopyBufferSize == 0 ? 0 : 1) ||
                    value.Parts.Any(hash => hash == null || !Digest.IsMatch(hash)))
                    throw new IOException("Invalid local history snapshot entry.");
            }
            return snapshot;
        }

        internal static void Export(string storage, string point, string destination)
        {
            string directory = Path.Combine(storage, DirectoryName, IncrementalDirectory);
            DesktopSessionFiles.VerifyPrivate(directory, false);
            string parts = Path.Combine(directory, "parts");
            DesktopSessionFiles.VerifyPrivate(parts, false);
            if (point != "latest" && (Path.GetFileName(point) != point || !point.EndsWith(SnapshotSuffix, StringComparison.Ordinal)))
                throw new IOException("Invalid snapshot name.");
            if (!Path.IsPathRooted(destination) || !String.Equals(Path.GetExtension(destination), ".zip", StringComparison.OrdinalIgnoreCase))
                throw new IOException("A full output ZIP path is required.");
            destination = Path.GetFullPath(destination);
            if (destination.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || Exists(destination))
                throw new IOException("Choose a new ZIP outside the incremental backup directory.");
            CheckLocalDirectory(Path.GetDirectoryName(destination));
            using (EnterSnapshotLock(directory))
            {
                string manifest = point == "latest" ? LatestManifest(directory) : Path.Combine(directory, point);
                if (manifest == null) throw new IOException("No incremental history snapshot exists.");
                var snapshot = ReadSnapshot(manifest);
                string temporary = Path.Combine(Path.GetDirectoryName(destination), "history-" + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    using (var output = ManagedBridge.CreatePrivateFile(temporary))
                    {
                        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
                        using (var sha = new SHA256Cng())
                        {
                            var buffer = new byte[CopyBufferSize];
                            foreach (var file in snapshot.Files)
                            {
                                long remaining = file.Value.Length;
                                using (var entry = zip.CreateEntry(file.Key, CompressionLevel.NoCompression).Open())
                                foreach (string hash in file.Value.Parts)
                                {
                                    int size = (int)Math.Min(remaining, CopyBufferSize);
                                    using (var input = OpenSource(PartPath(parts, hash)))
                                    {
                                        if (input.Length != size) throw new IOException("Invalid history backup part size.");
                                        ReadFully(input, buffer, size);
                                    }
                                    if (Hash(sha, buffer, size) != hash) throw new IOException("History backup part checksum changed.");
                                    entry.Write(buffer, 0, size);
                                    remaining -= size;
                                }
                            }
                        }
                        output.Flush(true);
                    }
                    File.Move(temporary, destination);
                }
                finally { DeleteTemporary(temporary); }
            }
        }

        public sealed class Snapshot
        {
            public int Version { get; set; }
            public Dictionary<string, SavedFile> Files { get; set; }
        }

        public sealed class SavedFile
        {
            public long Length { get; set; }
            public string Identity { get; set; }
            public string[] Parts { get; set; }
            internal bool CanReuse { get { return Identity.Length > 0 && Identity[0] == '1'; } }
            internal bool SameSource(SavedFile other)
            { return Length == other.Length && Identity == other.Identity; }
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

        private static FileStream OpenSource(string path)
        {
            CheckRegular(path);
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, FileOptions.SequentialScan);
        }

        private static SavedFile FileStamp(string path)
        { using (var stream = OpenSource(path)) return FileStamp(stream); }

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicInfo
        { public long Created, Accessed, Written, Changed; public uint Attributes; }
        [StructLayout(LayoutKind.Sequential)]
        private struct FileIdInfo
        { public ulong Volume, Low, High; }

        [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
        private static extern bool ReadBasic(SafeFileHandle file, int kind, out BasicInfo info, uint size);
        [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
        private static extern bool ReadId(SafeFileHandle file, int kind, out FileIdInfo info, uint size);

        private static SavedFile FileStamp(FileStream stream)
        {
            BasicInfo basic;
            FileIdInfo id;
            if (!ReadBasic(stream.SafeFileHandle, 0, out basic, (uint)Marshal.SizeOf(typeof(BasicInfo))) ||
                (basic.Attributes & (uint)(FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0)
                throw new IOException("Cannot verify local history file identity.");
            // Filesystems without a stable local file ID still use content hashing.
            bool stable = ReadId(stream.SafeFileHandle, 18, out id, (uint)Marshal.SizeOf(typeof(FileIdInfo))) &&
                (id.Low != 0 || id.High != 0) && basic.Changed != 0;
            string identity = String.Format(CultureInfo.InvariantCulture, "{0}{1:x16}{2:x16}{3:x16}{4:x16}{5:x16}{6:x16}",
                stable ? "1" : "0", id.Volume, id.Low, id.High, basic.Created, basic.Written, basic.Changed);
            return new SavedFile { Length = stream.Length, Identity = identity };
        }

        private static UserError SaveFailed()
        { return new UserError("無法在本機完整備份對話與記憶，已停止切換，登入資料未變更。請確認資料夾權限、可用磁碟空間，並完全結束 Codex 後再試。"); }
        private static UserError UnknownSqliteHome()
        { return new UserError("無法確認 config.toml 中的 SQLite 資料位置，已停止切換。請檢查 sqlite_home 是否為單行加引號的本機路徑。"); }
    }
}
