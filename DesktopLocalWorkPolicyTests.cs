using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace CodexAccountSwitcher
{
    public static class DesktopLocalWorkPolicyTests
    {
        private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        private static bool Rejected(Action operation) { try { operation(); return false; } catch (UserError) { return true; } }
        private static Dictionary<string, string> EnvironmentOf(ProcessStartInfo start)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string key in start.EnvironmentVariables.Keys) result[key] = start.EnvironmentVariables[key];
            return result;
        }
        private static bool SameEnvironment(ProcessStartInfo start, Dictionary<string, string> expected)
        {
            if (start.EnvironmentVariables.Count != expected.Count) return false;
            foreach (var pair in expected) if (start.EnvironmentVariables[pair.Key] != pair.Value) return false;
            return true;
        }
        private sealed class Fixture : IDisposable
        {
            public readonly string Root, Session, App;
            public Fixture()
            {
                Root = Path.Combine(Path.GetTempPath(), "CodexAccountSwitcher-local-work-policy-" + Guid.NewGuid().ToString("N"));
                DesktopSessionFiles.CreatePrivate(Root);
                Session = Path.Combine(Root, "session"); DesktopSessionFiles.CreatePrivate(Session);
                App = Path.Combine(Root, "app", "ChatGPT.exe");
                Directory.CreateDirectory(Path.GetDirectoryName(App)); File.WriteAllText(App, "synthetic App fixture");
                Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(App), "resources"));
            }
            public void Dispose()
            {
                string temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string full = Path.GetFullPath(Root);
                if (!full.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) ||
                    !Path.GetFileName(full).StartsWith("CodexAccountSwitcher-local-work-policy-", StringComparison.Ordinal))
                    throw new IOException("Refusing to remove a path outside this test fixture.");
                ManagedBridge.EnsureNoReparsePoint(full); Directory.Delete(full, true);
            }
        }

        public static void Register(Action<string, Action> test)
        {
            test("Desktop Local Work opt-out is a complete no-op when disabled", delegate
            {
                var start = new ProcessStartInfo { UseShellExecute = false };
                start.EnvironmentVariables[DesktopLocalWorkPolicy.CliOverride] = "existing executor override";
                start.EnvironmentVariables[DesktopLocalWorkPolicy.InitialEnable] = "1";
                var before = EnvironmentOf(start);
                DesktopLocalWorkPolicy.Configure(start, null, null, false);
                Check(SameEnvironment(start, before), "Disabled opt-out changed the launch environment.");
                DesktopLocalWorkPolicy.Configure(null, null, null, false);
            });
            test("Desktop Local Work opt-out replaces only executor launch variables in a private directory", delegate
            {
                using (var fixture = new Fixture())
                {
                    var start = new ProcessStartInfo { UseShellExecute = false, Arguments = "unchanged launch arguments" };
                    start.EnvironmentVariables[DesktopLocalWorkPolicy.CliOverride] = "inherited executor executable";
                    start.EnvironmentVariables[DesktopLocalWorkPolicy.InitialEnable] = "1";
                    start.EnvironmentVariables["CODEX_CLI_PATH"] = "local CLI fixture";
                    start.EnvironmentVariables["CODEX_APP_SERVER_WS_URL"] = "local transport fixture";
                    string processOverride = Environment.GetEnvironmentVariable(DesktopLocalWorkPolicy.CliOverride);
                    string processEnabled = Environment.GetEnvironmentVariable(DesktopLocalWorkPolicy.InitialEnable);
                    var expected = EnvironmentOf(start);
                    expected[DesktopLocalWorkPolicy.CliOverride] = Path.GetFullPath(fixture.Session);
                    expected[DesktopLocalWorkPolicy.InitialEnable] = "0";
                    DesktopLocalWorkPolicy.ApplyEnvironment(start, fixture.Session);
                    Check(SameEnvironment(start, expected), "The block changed unrelated backend/environment fields.");
                    Check(start.Arguments == "unchanged launch arguments", "The App launch arguments changed.");
                    Check(Directory.Exists(start.EnvironmentVariables[DesktopLocalWorkPolicy.CliOverride]) &&
                        !File.Exists(start.EnvironmentVariables[DesktopLocalWorkPolicy.CliOverride]), "The override can resolve to an executable.");
                    Check(Environment.GetEnvironmentVariable(DesktopLocalWorkPolicy.CliOverride) == processOverride &&
                        Environment.GetEnvironmentVariable(DesktopLocalWorkPolicy.InitialEnable) == processEnabled,
                        "The opt-out leaked into the switcher/global environment.");
                }
            });
            test("Desktop Local Work blocker rejects files missing directories relative paths and shell launches without mutation", delegate
            {
                using (var fixture = new Fixture())
                {
                    var start = new ProcessStartInfo { UseShellExecute = false };
                    var before = EnvironmentOf(start);
                    foreach (string path in new[] { fixture.App, Path.Combine(fixture.Root, "missing"), "relative-fixture", null, "" })
                    {
                        Check(Rejected(() => DesktopLocalWorkPolicy.ApplyEnvironment(start, path)), "An invalid blocker path was accepted.");
                        Check(SameEnvironment(start, before), "Invalid blocker validation partially changed the launch environment.");
                    }
                    start.UseShellExecute = true;
                    Check(Rejected(() => DesktopLocalWorkPolicy.ApplyEnvironment(start, fixture.Session)), "A shell launch bypassed environment delivery.");
                }
            });
            test("Desktop Local Work compatibility accepts only the exact reviewed archive digest and size", delegate
            {
                var digest = new byte[32];
                for (int index = 0; index < digest.Length; index++)
                    digest[index] = Convert.ToByte(DesktopLocalWorkPolicy.VerifiedArchiveSha256.Substring(index * 2, 2), 16);
                DesktopLocalWorkPolicy.ValidateArchiveDigest(DesktopLocalWorkPolicy.VerifiedArchiveLength, digest);
                Check(Rejected(() => DesktopLocalWorkPolicy.ValidateArchiveDigest(DesktopLocalWorkPolicy.VerifiedArchiveLength + 1, digest)), "A different archive size was accepted.");
                digest[0] ^= 1;
                Check(Rejected(() => DesktopLocalWorkPolicy.ValidateArchiveDigest(DesktopLocalWorkPolicy.VerifiedArchiveLength, digest)), "An unreviewed archive hash was accepted.");
                Check(Rejected(() => DesktopLocalWorkPolicy.ValidateArchiveDigest(DesktopLocalWorkPolicy.VerifiedArchiveLength, null)), "A missing hash was accepted.");
            });
            test("Current desktop Local Work compatibility rejects same-size changes and cross-version digests", delegate
            {
                var digest = new byte[32];
                for (int i = 0; i < digest.Length; i++) digest[i] = Convert.ToByte(DesktopLocalWorkPolicy.UpdatedArchiveSha256.Substring(i * 2, 2), 16);
                DesktopLocalWorkPolicy.ValidateArchiveDigest(DesktopLocalWorkPolicy.UpdatedArchiveLength, digest);
                Check(Rejected(() => DesktopLocalWorkPolicy.ValidateArchiveDigest(DesktopLocalWorkPolicy.VerifiedArchiveLength, digest)), "A digest from another version was accepted.");
                digest[0] ^= 1;
                Check(Rejected(() => DesktopLocalWorkPolicy.ValidateArchiveDigest(DesktopLocalWorkPolicy.UpdatedArchiveLength, digest)), "A changed current-version archive was accepted.");
            });
            test("Desktop Local Work opt-out refuses missing changed and non-file App archives before environment changes", delegate
            {
                using (var fixture = new Fixture())
                {
                    string archive = Path.Combine(Path.GetDirectoryName(fixture.App), "resources", "app.asar");
                    var target = new DesktopPackageTarget { AppExecutable = fixture.App, CliExecutable = fixture.App };
                    var start = new ProcessStartInfo { UseShellExecute = false };
                    var before = EnvironmentOf(start);
                    Check(Rejected(() => DesktopLocalWorkPolicy.Configure(start, target, fixture.Session, true)), "A missing App archive was accepted.");
                    File.WriteAllText(archive, "unreviewed archive");
                    Check(Rejected(() => DesktopLocalWorkPolicy.Configure(start, target, fixture.Session, true)), "An unreviewed App image was accepted.");
                    File.Delete(archive); Directory.CreateDirectory(archive);
                    Check(Rejected(() => DesktopLocalWorkPolicy.Configure(start, target, fixture.Session, true)), "A directory masquerading as an archive was accepted.");
                    Check(SameEnvironment(start, before), "An unsupported App altered the launch environment.");
                    Check(Rejected(() => DesktopLocalWorkPolicy.ValidateTarget(null)), "A missing App target was accepted.");
                    Check(Rejected(() => DesktopLocalWorkPolicy.ValidateTarget(new DesktopPackageTarget { AppExecutable = "relative-app.exe" })), "A relative App target was accepted.");
                }
            });
        }
    }
}
