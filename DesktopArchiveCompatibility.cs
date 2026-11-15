using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace CodexAccountSwitcher
{
    internal static class DesktopArchiveCompatibility
    {
        private const string ReviewedApp = "CF2EE1CE895DF9EE158EE61830D9D0F805C77D9B212AD0E159AE605AE04260C6";
        private const string ReviewedCli = "094D4DB493DE22025712CC5963854E7654B7C97F4CEE795E3E986270A0F4F6B4";
        private const string ReviewedContents = "11530809958CF5F76F2E85270AC7947611ACB78F366F9AA4154B699A58535429";
        private static readonly byte[] PngSignature = { 137, 80, 78, 71, 13, 10, 26, 10 };

        internal static bool Matches(Stream archive, string archivePath, DesktopPackageTarget target)
        {
            if (String.IsNullOrEmpty(target.PackageFullName) || String.IsNullOrEmpty(target.CliExecutable))
                return false;
            if (FileDigest(target.AppExecutable) != ReviewedApp || FileDigest(target.CliExecutable) != ReviewedCli)
                return false;
            return Fingerprint(archive, archivePath + ".unpacked") == ReviewedContents;
        }

        internal static string Fingerprint(Stream archive, string unpackedRoot)
        {
            const int maxHeader = 16 * 1024 * 1024;
            if (archive.Length > 1024L * 1024 * 1024) throw InvalidArchive();
            archive.Position = 0;
            using (var reader = new BinaryReader(archive, Encoding.UTF8, true))
            {
                int first = reader.ReadInt32();
                int headerSize = reader.ReadInt32();
                int headerPayload = reader.ReadInt32();
                int jsonSize = reader.ReadInt32();
                if (first != 4 || headerSize < 8 || headerSize > maxHeader || headerSize + 8L > archive.Length ||
                    headerPayload != headerSize - 4 || jsonSize <= 0 || jsonSize > headerSize - 8 ||
                    headerSize - 8 - jsonSize > 3) throw InvalidArchive();

                var serializer = new JavaScriptSerializer { MaxJsonLength = maxHeader, RecursionLimit = 96 };
                var utf8 = new UTF8Encoding(false, true);
                var root = serializer.Deserialize<Dictionary<string, object>>(utf8.GetString(reader.ReadBytes(jsonSize)));
                if (root == null || root.Count != 1) throw InvalidArchive();
                long data = headerSize + 8L;
                var entries = new List<Entry>();
                ReadEntries(Json.Map(root, "files"), "", entries);

                // ASAR may deduplicate identical ranges. Reject partial overlaps and hidden trailing data.
                long end = data;
                var ranges = entries.Where(item => item.Offset >= 0 && item.Size > 0)
                    .Select(item => new { item.Offset, item.Size }).Distinct().OrderBy(range => range.Offset);
                foreach (var range in ranges)
                {
                    if (range.Offset != end - data || range.Size > archive.Length - end) throw InvalidArchive();
                    end += range.Size;
                }
                if (end != archive.Length || entries.Any(item => item.Offset > archive.Length - data)) throw InvalidArchive();

                using (var records = new MemoryStream())
                using (var writer = new BinaryWriter(records, utf8, true))
                using (var sha = SHA256.Create())
                {
                    var buffer = new byte[65536];
                    foreach (var entry in entries)
                    {
                        writer.Write(entry.Path);
                        writer.Write(entry.Offset < 0);
                        if (entry.Path.EndsWith("/", StringComparison.Ordinal)) continue;
                        if (entry.Offset >= 0)
                        {
                            archive.Position = data + entry.Offset;
                            bool png = entry.Path.StartsWith("webview/assets/", StringComparison.Ordinal) &&
                                entry.Path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) &&
                                entry.Size >= PngSignature.Length && entry.Size <= 16 * 1024 * 1024 &&
                                reader.ReadBytes(PngSignature.Length).SequenceEqual(PngSignature);
                            writer.Write(png);
                            if (png) continue;
                            writer.Write(entry.Integrity);
                            archive.Position = data + entry.Offset;
                            writer.Write(HashEntry(archive, entry.Size, sha, buffer));
                        }
                        else
                        {
                            string path = Path.Combine(unpackedRoot, entry.Path.Replace('/', Path.DirectorySeparatorChar));
                            using (var file = OpenRegularFile(path))
                            {
                                // The Store signs unpacked native binaries after ASAR records their original sizes.
                                if (file.Length > 64 * 1024 * 1024) throw InvalidArchive();
                                writer.Write(entry.Size);
                                writer.Write(entry.Integrity);
                                writer.Write(HashEntry(file, file.Length, sha, buffer));
                            }
                        }
                    }
                    writer.Flush();
                    records.Position = 0;
                    sha.Initialize();
                    return Hex(sha.ComputeHash(records));
                }
            }
        }

        private sealed class Entry
        {
            internal string Path;
            internal string Integrity;
            internal long Offset, Size;
        }

        private static void ReadEntries(Dictionary<string, object> files, string parent, List<Entry> entries)
        {
            if (files == null) throw InvalidArchive();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                if (entries.Count >= 40000 || !names.Add(file.Key) || String.IsNullOrEmpty(file.Key) ||
                    file.Key == "." || file.Key.EndsWith(".", StringComparison.Ordinal) ||
                    file.Key.EndsWith(" ", StringComparison.Ordinal) ||
                    file.Key.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw InvalidArchive();
                var metadata = file.Value as Dictionary<string, object>;
                if (metadata == null) throw InvalidArchive();
                string path = parent + file.Key;
                if (metadata.ContainsKey("files"))
                {
                    if (metadata.Count != 1) throw InvalidArchive();
                    entries.Add(new Entry { Path = path + "/", Offset = -1 });
                    ReadEntries(Json.Map(metadata, "files"), path + "/", entries);
                    continue;
                }
                object sizeValue, unpacked;
                if (metadata.Count != 3 || Json.Map(metadata, "integrity") == null ||
                    !metadata.TryGetValue("size", out sizeValue) || !(sizeValue is int) || (int)sizeValue < 0) throw InvalidArchive();
                long offset;
                if (metadata.TryGetValue("unpacked", out unpacked))
                {
                    if (!Object.Equals(unpacked, true)) throw InvalidArchive();
                    offset = -1;
                }
                else if (!Int64.TryParse(Json.Str(metadata, "offset"), NumberStyles.None, CultureInfo.InvariantCulture, out offset))
                    throw InvalidArchive();
                entries.Add(new Entry { Path = path, Offset = offset, Size = (int)sizeValue,
                    Integrity = Json.Encode(Json.Map(metadata, "integrity")) });
            }
        }

        private static byte[] HashEntry(Stream stream, long size, SHA256 sha, byte[] buffer)
        {
            sha.Initialize();
            while (size != 0)
            {
                int read = stream.Read(buffer, 0, (int)Math.Min(size, buffer.Length));
                if (read == 0) throw InvalidArchive();
                sha.TransformBlock(buffer, 0, read, null, 0);
                size -= read;
            }
            sha.TransformFinalBlock(new byte[0], 0, 0);
            return sha.Hash;
        }

        private static FileStream OpenRegularFile(string path)
        {
            ManagedBridge.EnsureNoReparsePoint(Path.GetDirectoryName(path));
            if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw InvalidArchive();
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }

        private static string FileDigest(string path)
        {
            using (var stream = OpenRegularFile(path))
            using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(stream));
        }

        private static string Hex(byte[] digest) { return BitConverter.ToString(digest).Replace("-", ""); }
        private static IOException InvalidArchive() { return new IOException("Unknown desktop archive layout."); }
    }
}
