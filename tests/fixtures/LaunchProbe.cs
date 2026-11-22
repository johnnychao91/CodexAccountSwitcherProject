using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using CodexAccountSwitcher;

class LaunchProbe
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern int GetPackageFamilyName(IntPtr process, ref uint length, StringBuilder name);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            string exe = Installation.DesktopExe();
            var target = DesktopLauncher.Resolve(exe);
            Console.WriteLine("Resolved AppUserModelId: " + target.AppUserModelId);
            if (args.Length == 1 && args[0] == "--activate")
            {
                uint pid = Installation.Launch(exe);
                IntPtr handle = OpenProcess(0x1000, false, pid);
                if (handle == IntPtr.Zero) throw new Exception("Could not inspect activated process");
                try
                {
                    uint length = 0;
                    if (GetPackageFamilyName(handle, ref length, null) != 122) throw new Exception("Activated process has no package identity");
                    var family = new StringBuilder((int)length);
                    if (GetPackageFamilyName(handle, ref length, family) != 0) throw new Exception("Could not read package identity");
                    if (!target.AppUserModelId.StartsWith(family + "!", StringComparison.Ordinal)) throw new Exception("Unexpected package identity");
                    Console.WriteLine("PASS: Windows activated PID " + pid + " with package identity " + family);
                }
                finally { CloseHandle(handle); }
            }
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("FAIL: " + ex.Message); return 1; }
    }
}
