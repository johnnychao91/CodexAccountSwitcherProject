using System;
using System.IO;
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
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int PackageFamilyNameFromFullName(string fullName, ref uint length, StringBuilder familyName);

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
