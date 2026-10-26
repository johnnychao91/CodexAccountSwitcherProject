using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;

namespace CodexAccountSwitcher
{
    public sealed class DesktopLaunchTarget
    {
        public string Executable { get; set; }
        public string AppUserModelId { get; set; }
    }

    public static class DesktopLauncher
    {
        private const string CodexPackageFamily = "OpenAI.Codex_2p2nqsd0c76g0";

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int PackageFamilyNameFromFullName(string fullName, ref uint length, StringBuilder familyName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int GetPackagesByPackageFamily(string familyName, ref uint count, IntPtr fullNames,
            ref uint bufferLength, IntPtr buffer);

        // Query the current user's registration rather than enumerating WindowsApps.
        // Normal desktop users can read a known package while its parent directory
        // denies listing, and the App need not already be running.
        internal static string InstalledExecutable()
        {
            return InstalledExecutable(RegisteredPackages(), DesktopNative.PackagePath);
        }

        private static IList<string> RegisteredPackages()
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                uint count = 0, length = 0;
                int result = GetPackagesByPackageFamily(CodexPackageFamily, ref count, IntPtr.Zero, ref length, IntPtr.Zero);
                if (result == 0 && count == 0) return new List<string>();
                if (result != 122 || count == 0 || count > 128 || length == 0 || length > 131072)
                    return new List<string>();
                IntPtr names = IntPtr.Zero, text = IntPtr.Zero;
                try
                {
                    uint capacity = count;
                    names = Marshal.AllocHGlobal(checked((int)count * IntPtr.Size));
                    text = Marshal.AllocHGlobal(checked((int)length * 2));
                    result = GetPackagesByPackageFamily(CodexPackageFamily, ref count, names, ref length, text);
                    if (result == 122) continue; // A Store update changed the registration during lookup.
                    if (result != 0 || count > capacity) return new List<string>();
                    var packages = new List<string>();
                    for (int index = 0; index < count; index++)
                    {
                        IntPtr name = Marshal.ReadIntPtr(names, index * IntPtr.Size);
                        if (name != IntPtr.Zero) packages.Add(Marshal.PtrToStringUni(name));
                    }
                    return packages;
                }
                finally
                {
                    if (text != IntPtr.Zero) Marshal.FreeHGlobal(text);
                    if (names != IntPtr.Zero) Marshal.FreeHGlobal(names);
                }
            }
            return new List<string>();
        }

        private static Version PackageVersion(string fullName)
        {
            Version version;
            string[] parts = (fullName ?? "").Split('_');
            return parts.Length == 5 && Version.TryParse(parts[1], out version) ? version : new Version(0, 0);
        }

        internal static string InstalledExecutable(IEnumerable<string> packages, Func<string, string> packagePath)
        {
            foreach (string package in packages.OrderByDescending(PackageVersion))
            {
                string[] parts = (package ?? "").Split('_');
                if (parts.Length != 5 || parts[0] != "OpenAI.Codex" || parts[4] != "2p2nqsd0c76g0") continue;
                string root;
                try { root = packagePath(package); }
                catch { continue; } // A Store update may remove one registration during lookup.
                if (String.IsNullOrWhiteSpace(root)) continue;
                foreach (string name in new[] { "ChatGPT.exe", "Codex.exe" })
                {
                    try
                    {
                        string executable = Path.Combine(root, "app", name);
                        if (!File.Exists(executable)) continue;
                        var target = Resolve(executable);
                        if (target.AppUserModelId != null && target.AppUserModelId.StartsWith(CodexPackageFamily + "!", StringComparison.Ordinal))
                            return target.Executable;
                    }
                    catch { /* An obsolete branding candidate must not hide a valid App. */ }
                }
            }
            return null;
        }

        [ComImport, Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IApplicationActivationManager
        {
            [PreserveSig]
            int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
                [MarshalAs(UnmanagedType.LPWStr)] string arguments, uint options, out uint processId);
            [PreserveSig]
            int ActivateForFile([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, IntPtr items,
                [MarshalAs(UnmanagedType.LPWStr)] string verb, out uint processId);
            [PreserveSig]
            int ActivateForProtocol([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, IntPtr items, out uint processId);
        }

        public static DesktopLaunchTarget Resolve(string executable)
        {
            if (String.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
                throw new UserError("找不到 Codex 桌面程式。帳號已套用，可從開始功能表開啟 Codex。");
            string fullPath = Path.GetFullPath(executable);
            var directory = new DirectoryInfo(Path.GetDirectoryName(fullPath));
            while (directory != null)
            {
                string manifestPath = Path.Combine(directory.FullName, "AppxManifest.xml");
                if (File.Exists(manifestPath))
                {
                    // Match the executable to its manifest Application. A package may contain
                    // several applications, so using the first Id can activate a helper instead.
                    var document = new XmlDocument { XmlResolver = null };
                    using (var reader = XmlReader.Create(manifestPath, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })) document.Load(reader);
                    foreach (XmlElement app in document.GetElementsByTagName("Application", document.DocumentElement.NamespaceURI))
                    {
                        string relative = app.GetAttribute("Executable").Replace('/', Path.DirectorySeparatorChar);
                        if (String.IsNullOrEmpty(relative)) continue;
                        string candidate = Path.GetFullPath(Path.Combine(directory.FullName, relative));
                        if (!String.Equals(candidate, fullPath, StringComparison.OrdinalIgnoreCase)) continue;
                        string id = app.GetAttribute("Id");
                        if (String.IsNullOrWhiteSpace(id)) break;
                        uint length = 0;
                        int result = PackageFamilyNameFromFullName(directory.Name, ref length, null);
                        if (result != 122 || length == 0 || length > 1024) break;
                        var family = new StringBuilder((int)length);
                        if (PackageFamilyNameFromFullName(directory.Name, ref length, family) != 0) break;
                        return new DesktopLaunchTarget { Executable = fullPath, AppUserModelId = family + "!" + id };
                    }
                    throw new UserError("無法辨識 Codex 套件的啟動項目。帳號已套用，請從開始功能表開啟 Codex。");
                }
                directory = directory.Parent;
            }
            if (fullPath.IndexOf("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase) >= 0)
                throw new UserError("無法讀取 Codex 套件資訊。帳號已套用，請從開始功能表開啟 Codex。");
            return new DesktopLaunchTarget { Executable = fullPath };
        }

        public static uint Launch(DesktopLaunchTarget target, Func<string, uint> activatePackage, Func<string, uint> startExecutable)
        {
            // Never fall back to executing a packaged .exe directly: it lacks package identity.
            return !String.IsNullOrEmpty(target.AppUserModelId) ? activatePackage(target.AppUserModelId) : startExecutable(target.Executable);
        }

        public static uint ActivatePackage(string appUserModelId)
        {
            object instance = null;
            try
            {
                instance = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C"), true));
                uint processId;
                // AO_NOERRORUI: report an activation failure inside our UI instead of an OS dialog.
                int result = ((IApplicationActivationManager)instance).ActivateApplication(appUserModelId, null, 2, out processId);
                Marshal.ThrowExceptionForHR(result);
                return processId;
            }
            catch (COMException ex)
            { throw new UserError("Windows 未能啟動 Codex 套件（0x" + ex.ErrorCode.ToString("X8") + "）。帳號已套用，可從開始功能表開啟。"); }
            finally { if (instance != null && Marshal.IsComObject(instance)) Marshal.FinalReleaseComObject(instance); }
        }
    }
}
