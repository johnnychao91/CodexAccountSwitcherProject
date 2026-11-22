using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace CodexAccountSwitcher
{
    public static class DesktopNativeTests
    {
        private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        private static DesktopProcessIdentity Copy(DesktopProcessIdentity value)
        { return new DesktopProcessIdentity { Pid = value.Pid, StartedUtc = value.StartedUtc, ExecutablePath = value.ExecutablePath, ParentPid = value.ParentPid, SessionId = value.SessionId }; }
        public static void Register(Action<string, Action> test)
        {
            test("Desktop native identity rejects stale times paths parents and sessions before shutdown", delegate
            {
                using (var self = Process.GetCurrentProcess())
                {
                    var identity = DesktopNative.Identity(self.Id);
                    Check(identity != null && DesktopNative.Same(identity), "The current user's native process identity was unavailable.");
                    Check(identity.SessionId == self.SessionId, "The native session differs from the current Windows session.");
                    var offset = Copy(identity); offset.StartedUtc = DateTimeOffset.Parse(identity.StartedUtc, CultureInfo.InvariantCulture).ToOffset(TimeSpan.FromHours(8)).ToString("o");
                    Check(DesktopNative.Same(offset), "Equivalent UTC creation identities were rejected.");
                    foreach (string field in new[] { "time", "path", "parent", "session" })
                    {
                        var stale = Copy(identity);
                        if (field == "time") stale.StartedUtc = DateTimeOffset.Parse(identity.StartedUtc, CultureInfo.InvariantCulture).AddTicks(1).ToString("o");
                        if (field == "path") stale.ExecutablePath += ".not-the-process";
                        if (field == "parent") stale.ParentPid++;
                        if (field == "session") stale.SessionId++;
                        Check(!DesktopNative.Same(stale), "Tampered native identity was accepted: " + field);
                        Check(!DesktopNative.Shutdown(stale).Success && !self.HasExited, "A stale identity reached shutdown: " + field);
                    }
                }
            });
            test("Desktop native ancestry binds the actual parent and stops accepting an exited child", delegate
            {
                string shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
                using (var self = Process.GetCurrentProcess())
                using (var child = Process.Start(new ProcessStartInfo(shell, "-NoLogo -NoProfile -NonInteractive -Command \"[Console]::ReadLine() | Out-Null\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true }))
                {
                    try
                    {
                        var parent = DesktopNative.Identity(self.Id); var identity = DesktopNative.Identity(child.Id);
                        Check(identity != null && DesktopNative.IsDescendant(identity, parent), "The owned child was not bound to its actual parent.");
                        var fake = Copy(identity); fake.ParentPid++;
                        Check(!DesktopNative.IsDescendant(fake, parent), "A supplied parent PID replaced native ancestry evidence.");
                        child.StandardInput.Close(); Check(child.WaitForExit(5000) && child.ExitCode == 0, "The synthetic child did not exit normally.");
                        Check(!DesktopNative.Same(identity) && !DesktopNative.IsDescendant(identity, parent), "An exited process was treated as a live descendant.");
                    }
                    finally { if (!child.HasExited) { child.StandardInput.Close(); child.WaitForExit(5000); } }
                }
            });
            test("Desktop managed target resolution rejects an ordinary unpackaged executable", delegate
            {
                using (var self = Process.GetCurrentProcess())
                {
                    bool rejected = false;
                    try { DesktopPackageTarget.Resolve(self.MainModule.FileName); } catch (UserError) { rejected = true; }
                    Check(rejected, "An unpackaged helper was accepted as an installed Desktop package.");
                }
            });
        }
    }
}
