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
        internal const long NovemberArchiveLength = 761023153;
        internal const string NovemberArchiveSha256 = "FB47D6491DC3006B708CB1E14DE479B882BF50F88B7DAA81818F154C253266AA";
        private const long November3ArchiveLength = 761033804;
        private const string November3ArchiveSha256 = "EE88FDD77FB7346EFB5E91CE73C7C0EEE4EBA390FFBA3192635D5CDBE44E4097";
        private const long November6ArchiveLength = 772825694;
        private const string November6ArchiveSha256 = "54B74A9131680FC9EEAD711ED5D9F718F6127C22222CA60D2E3B577A707E2485";
        internal const long CurrentArchiveLength = 783750654;
        internal const string CurrentArchiveSha256 = "2551CBA1057A05CDC5CF5D75B146FAC4092BFF4CE77A10C43E3286C88D24C82D";
        internal const long November20ArchiveLength = 783725118;
        internal const string November20ArchiveSha256 = "A538C8BFA0BF6A43B06C4C62AAD93616A78C4CAD306602CC05F528844A551598";
        private const long November21ArchiveLength = 443772048;
        private const string November21ArchiveSha256 = "2334574ABBF17086F34F60C228D2A32FC18DDB4044477771DB6763023299A871";
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
        { ValidateArchive(target, ExpectedDigest, UnsupportedVersion, allowLocalWorkOnly: true); }

        private static string ExpectedDigest(long length)
        {
            if (length == VerifiedArchiveLength) return VerifiedArchiveSha256;
            if (length == UpdatedArchiveLength) return UpdatedArchiveSha256;
            if (length == NovemberArchiveLength) return NovemberArchiveSha256;
            if (length == November3ArchiveLength) return November3ArchiveSha256;
            if (length == November6ArchiveLength) return November6ArchiveSha256;
            if (length == CurrentArchiveLength) return CurrentArchiveSha256;
            if (length == November20ArchiveLength) return November20ArchiveSha256;
            if (length == November21ArchiveLength) return November21ArchiveSha256;
            return null;
        }

        // Shared file identity reader only; each feature supplies its own policy.
        internal static void ValidateArchive(DesktopPackageTarget target, Func<long, string> expectedDigest,
            string unsupportedVersion, bool allowLocalWorkOnly = false)
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
                    using (var stream = new FileStream(file, FileAccess.Read))
                    {
                        string expected = expectedDigest(length);
                        if (expected != null)
                        {
                            using (var sha = SHA256.Create())
                                if (BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "") == expected) return;
                        }
                        if (!DesktopArchiveCompatibility.Matches(stream, archive, target, allowLocalWorkOnly))
                            throw new UserError(unsupportedVersion);
                    }
                }
            }
            catch (UserError) { throw; }
            catch (Exception ex)
            {
                if (!(ex is IOException) && !(ex is UnauthorizedAccessException) && !(ex is ArgumentException) &&
                    !(ex is NotSupportedException) && !(ex is InvalidOperationException) &&
                    !(ex is System.Security.SecurityException)) throw;
                throw new UserError(unsupportedVersion);
            }
        }

        internal static void ValidateArchiveDigest(long length, byte[] digest)
        {
            string expected = ExpectedDigest(length);
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
            // In the reviewed Apps, executor resolution rejects a directory as a non-retryable error
            // before cloud registration or exec-server spawn. ENABLED=0 alone is
            // insufficient: the renderer can subsequently authorize the feature.
            // Set only the executor-specific override; keep the local CLI/backend intact.
            start.EnvironmentVariables[CliOverride] = directory;
            start.EnvironmentVariables[InitialEnable] = "0";
        }
    }
}
