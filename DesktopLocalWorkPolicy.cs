using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace CodexAccountSwitcher
{
    internal static class DesktopLocalWorkPolicy
    {
        internal const string CliOverride = "CODEX_TPP_LOCAL_EXECUTOR_CLI_PATH";
        internal const string InitialEnable = "CODEX_TPP_LOCAL_EXECUTOR_ENABLED";
        internal const long VerifiedArchiveLength = 640129388;
        internal const string VerifiedArchiveSha256 = "04862A659C6A90FC50514D8C0B8016709D97DECE58839726C57495ED72F1D9F0";
        internal const long UpdatedArchiveLength = 674296148;
        internal const string UpdatedArchiveSha256 = "6FBEDF054A8D8CAFAF5C745B40AA59176512E106E0BD6308275AE123B60FD074";
        private const string UnsupportedVersion = "目前 Codex 桌面版本尚未通過 Local Work 封鎖相容性驗證。請先關閉 Local Work 封鎖選項，或更新切換器後再啟動受管桌面。";

        [StructLayout(LayoutKind.Sequential)]
        private struct ArchiveInformation
        {
            public uint Attributes, CreatedLow, CreatedHigh, AccessedLow, AccessedHigh,
                WrittenLow, WrittenHigh, Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle OpenArchive(string path, uint access, uint share, IntPtr security,
            uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
        private static extern bool ReadArchiveInformation(SafeFileHandle file, out ArchiveInformation information);

        internal static void ValidateTarget(DesktopPackageTarget target)
        { ValidateArchive(target, length => length == VerifiedArchiveLength || length == UpdatedArchiveLength, ValidateArchiveDigest, UnsupportedVersion); }

        // Shared file identity reader only; each feature supplies its own policy.
        internal static void ValidateArchive(DesktopPackageTarget target, Func<long, bool> acceptsLength,
            Action<long, byte[]> validateDigest, string unsupportedVersion)
        {
            try
            {
                if (target == null || String.IsNullOrWhiteSpace(target.AppExecutable) || !Path.IsPathRooted(target.AppExecutable))
                    throw new IOException("The Desktop package target is unavailable.");
                string app = Path.GetFullPath(target.AppExecutable);
                string parent = Path.GetDirectoryName(app);
                ManagedBridge.EnsureNoReparsePoint(parent);
                var appAttributes = File.GetAttributes(app);
                if ((appAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                    throw new IOException("The Desktop executable is not a regular file.");
                string archive = Path.Combine(parent, "resources", "app.asar");
                ManagedBridge.EnsureNoReparsePoint(Path.GetDirectoryName(archive));
                // Retain one non-reparse handle while checking the actual file and its hash.
                // Disallow write/delete sharing so an update cannot change the verified image.
                using (var file = OpenArchive(archive, 0x80000000, 1, IntPtr.Zero, 3, 0x00200000 | 0x08000000, IntPtr.Zero))
                {
                    ArchiveInformation information;
                    if (file.IsInvalid || !ReadArchiveInformation(file, out information) ||
                        (information.Attributes & (uint)(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                        throw new IOException("The Desktop archive is not a regular file.");
                    long length = ((long)information.SizeHigh << 32) | information.SizeLow;
                    if (!acceptsLength(length)) throw new UserError(unsupportedVersion);
                    using (var stream = new FileStream(file, FileAccess.Read))
                    using (var sha256 = SHA256.Create()) validateDigest(length, sha256.ComputeHash(stream));
                }
            }
            catch (UserError) { throw; }
            catch (Exception ex)
            {
                if (!(ex is IOException) && !(ex is UnauthorizedAccessException) && !(ex is ArgumentException) &&
                    !(ex is NotSupportedException) && !(ex is System.Security.SecurityException)) throw;
                throw new UserError(unsupportedVersion);
            }
        }

        internal static void ValidateArchiveDigest(long length, byte[] digest)
        {
            string expected = length == VerifiedArchiveLength ? VerifiedArchiveSha256 : length == UpdatedArchiveLength ? UpdatedArchiveSha256 : null;
            if (expected == null || digest == null || digest.Length != 32 ||
                !String.Equals(BitConverter.ToString(digest).Replace("-", ""), expected, StringComparison.Ordinal))
                throw new UserError(UnsupportedVersion);
        }

        internal static void Configure(ProcessStartInfo start, DesktopPackageTarget target, string privateSessionDirectory, bool blockLocalWork)
        {
            if (!blockLocalWork) return;
            ValidateTarget(target);
            ApplyEnvironment(start, privateSessionDirectory);
        }

        internal static void ApplyEnvironment(ProcessStartInfo start, string privateSessionDirectory)
        {
            string directory;
            try
            {
                if (start == null || start.UseShellExecute || String.IsNullOrWhiteSpace(privateSessionDirectory) ||
                    !Path.IsPathRooted(privateSessionDirectory)) throw new IOException("A private managed launch directory is required.");
                directory = Path.GetFullPath(privateSessionDirectory);
                if (!Directory.Exists(directory) ||
                    (File.GetAttributes(directory) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
                    throw new IOException("The Local Work blocker must be an existing directory, never an executable.");
                DesktopSessionFiles.VerifyPrivate(directory, false);
            }
            catch (Exception ex)
            {
                if (!(ex is IOException) && !(ex is UnauthorizedAccessException) && !(ex is ArgumentException) &&
                    !(ex is NotSupportedException) && !(ex is System.Security.SecurityException)) throw;
                throw new UserError("無法驗證受管桌面的私人工作目錄，Local Work 封鎖尚未套用。");
            }
            // Experimental availability block, not a native App on/off preference.
            // In both reviewed Apps, executor resolution rejects a directory as a non-retryable error
            // before cloud registration or exec-server spawn. ENABLED=0 alone is
            // insufficient: the renderer can subsequently authorize the feature.
            // Set only the executor-specific override; keep the local CLI/backend intact.
            start.EnvironmentVariables[CliOverride] = directory;
            start.EnvironmentVariables[InitialEnable] = "0";
        }
    }
}
