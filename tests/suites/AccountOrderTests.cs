using System;
using System.IO;
using System.Linq;

namespace CodexAccountSwitcher
{
    public static class AccountOrderTests
    {
        private static void Check(bool condition, string message)
        { if (!condition) throw new Exception(message); }

        private static string Auth(string name, string refresh)
        { return SelfTests.Auth("order-" + name, "order-workspace-" + name, name + "@example.com", "plus", "fake-order-" + refresh); }

        private static string Id(string name)
        { return Account.Parse(Auth(name, "original")).Id; }

        private static void Order(Vault vault, params string[] names)
        { Check(vault.Data.Accounts.Select(account => account.Id).SequenceEqual(names.Select(Id)), "Account order changed unexpectedly."); }

        private static void WithVault(Action<Vault, string> action)
        {
            string temporaryRoot = Path.GetFullPath(Path.GetTempPath());
            string root = Path.Combine(temporaryRoot, "CodexAccountSwitcher-order-" + Guid.NewGuid().ToString("N"));
            try
            {
                var vault = new Vault(Path.Combine(root, "vault"));
                vault.Upsert(Auth("first", "original"), "First");
                vault.Upsert(Auth("second", "original"), "Second").PendingLogin = true;
                vault.Upsert(Auth("third", "original"), "Third");
                vault.Data.PreviousAuth = Auth("first", "original");
                vault.Save();
                string home = Path.Combine(root, "fake-codex-home");
                Directory.CreateDirectory(home);
                File.WriteAllText(Path.Combine(home, "auth.json"), Auth("first", "active"));
                action(vault, root);
            }
            finally
            {
                // Delete only this test's freshly generated child of the temporary directory.
                string expectedParent = temporaryRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!String.Equals(Path.GetDirectoryName(Path.GetFullPath(root)), expectedParent, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The test cleanup path escaped its temporary directory.");
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        public static void Register(Action<string, Action> test)
        {
            test("Manual order moves accounts up and down without changing credentials or profile data", delegate
            {
                WithVault((vault, root) =>
                {
                    var profiles = vault.Data.Accounts.ToDictionary(account => account.Id, account => Json.Encode(account));
                    string previous = vault.Data.PreviousAuth;
                    string authPath = Path.Combine(root, "fake-codex-home", "auth.json");
                    string active = File.ReadAllText(authPath);
                    Check(vault.Move(Id("second"), -1), "Moving up did not succeed.");
                    Order(vault, "second", "first", "third");
                    Check(vault.Move(Id("second"), 1), "Moving down did not succeed.");
                    Order(vault, "first", "second", "third");
                    Check(vault.Move(Id("second"), 1), "Moving to the last position did not succeed.");
                    Order(vault, "first", "third", "second");
                    Check(vault.Data.Accounts.All(account => profiles[account.Id] == Json.Encode(account)), "A move changed profile data.");
                    Check(vault.Data.PreviousAuth == previous, "A move changed the recovery credentials.");
                    Check(File.ReadAllText(authPath) == active, "A move changed the active credentials.");
                });
            });

            test("Manual order boundaries and unknown accounts are no-ops; invalid offsets are rejected without saving", delegate
            {
                WithVault((vault, root) =>
                {
                    string path = Path.Combine(root, "vault", "accounts.dpapi");
                    byte[] original = File.ReadAllBytes(path);
                    using (var locked = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    {
                        Check(!vault.Move(Id("first"), -1), "The first account moved past the boundary.");
                        Check(!vault.Move(Id("third"), 1), "The last account moved past the boundary.");
                        Check(!vault.Move("unknown-test-account", 1), "An unknown account moved.");
                        Check(!vault.Move(null, -1), "A missing account ID moved.");
                        foreach (int offset in new[] { 0, -2, 2, Int32.MinValue, Int32.MaxValue })
                        {
                            bool rejected = false;
                            try { vault.Move(Id("second"), offset); }
                            catch (ArgumentOutOfRangeException) { rejected = true; }
                            Check(rejected, "An invalid offset was accepted.");
                        }
                    }
                    Order(vault, "first", "second", "third");
                    Check(File.ReadAllBytes(path).SequenceEqual(original), "A no-op rewrote the vault.");
                });
            });

            test("Manual account order persists when reopening an existing version-one vault", delegate
            {
                WithVault((vault, root) =>
                {
                    string directory = Path.Combine(root, "vault");
                    var reopened = new Vault(directory);
                    Order(reopened, "first", "second", "third");
                    Check(reopened.Move(Id("third"), -1) && reopened.Move(Id("third"), -1), "Reordering a reopened vault failed.");
                    var loaded = new Vault(directory);
                    Order(loaded, "third", "first", "second");
                    Check(loaded.Data.Version == 1, "Reordering changed the vault format version.");
                    Check(loaded.Data.Accounts.Last().PendingLogin, "Reordering lost a pending login.");
                });
            });

            test("New accounts are appended after the manually ordered accounts", delegate
            {
                WithVault((vault, root) =>
                {
                    vault.Move(Id("third"), -1); vault.Move(Id("third"), -1);
                    vault.Upsert(Auth("fourth", "original"), "Fourth").PendingLogin = true;
                    vault.Save();
                    var loaded = new Vault(Path.Combine(root, "vault"));
                    Order(loaded, "third", "first", "second", "fourth");
                    Check(loaded.Data.Accounts.Last().PendingLogin, "Appending lost the new account's pending login.");
                });
            });

            test("Renaming, refreshing, and signing in again preserve manual order and intended pending-login state", delegate
            {
                WithVault((vault, root) =>
                {
                    vault.Move(Id("third"), -1); vault.Move(Id("third"), -1);
                    var renamed = vault.Data.Accounts.Single(account => account.Id == Id("second"));
                    // Match the UI rename workflow: Upsert replaces the object, then restores its pending flag.
                    bool pending = renamed.PendingLogin;
                    vault.Upsert(renamed.Auth, "Renamed").PendingLogin = pending;
                    vault.Save();
                    Order(vault, "third", "first", "second");
                    string refreshed = Auth("first", "refreshed");
                    vault.Upsert(refreshed, null); vault.Save();
                    Order(vault, "third", "first", "second");
                    string relogin = Auth("third", "signed-in-again");
                    vault.Upsert(relogin, null).PendingLogin = true; vault.Save();
                    var loaded = new Vault(Path.Combine(root, "vault"));
                    Order(loaded, "third", "first", "second");
                    Check(loaded.Data.Accounts[0].Auth == relogin && loaded.Data.Accounts[0].PendingLogin, "Signing in again lost the pending session.");
                    Check(loaded.Data.Accounts[1].Auth == refreshed && !loaded.Data.Accounts[1].PendingLogin, "Refreshing changed the session state.");
                    Check(loaded.Data.Accounts[2].Label == "Renamed" && loaded.Data.Accounts[2].PendingLogin, "Renaming lost its label or pending state.");
                });
            });

            test("Removing an account preserves the relative manual order of the remaining accounts", delegate
            {
                WithVault((vault, root) =>
                {
                    vault.Move(Id("third"), -1); vault.Move(Id("third"), -1);
                    vault.Remove(Id("first"));
                    Order(vault, "third", "second");
                    var loaded = new Vault(Path.Combine(root, "vault"));
                    Order(loaded, "third", "second");
                    Check(loaded.Data.PreviousAuth == null, "Removing an account retained its recovery credentials.");
                    Check(loaded.Data.Accounts[1].PendingLogin, "Removing another account changed pending-login state.");
                });
            });

            test("Failed order persistence restores memory and disk order without changing active credentials", delegate
            {
                WithVault((vault, root) =>
                {
                    vault.Move(Id("third"), -1);
                    string directory = Path.Combine(root, "vault");
                    string path = Path.Combine(directory, "accounts.dpapi");
                    string authPath = Path.Combine(root, "fake-codex-home", "auth.json");
                    byte[] originalFile = File.ReadAllBytes(path);
                    string originalData = Json.Encode(vault.Data);
                    string active = File.ReadAllText(authPath);
                    bool failed = false;
                    using (var locked = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    {
                        try { vault.Move(Id("first"), 1); }
                        catch (IOException) { failed = true; }
                    }
                    Check(failed, "Reordering a locked vault unexpectedly succeeded.");
                    Check(Json.Encode(vault.Data) == originalData, "Failed persistence did not restore the in-memory vault.");
                    Check(File.ReadAllBytes(path).SequenceEqual(originalFile), "Failed persistence changed the vault file.");
                    Order(new Vault(directory), "first", "third", "second");
                    Check(File.ReadAllText(authPath) == active, "Failed persistence changed the active credentials.");
                    Check(Directory.GetFiles(directory, "*.switch-*").Length == 0, "Failed persistence left a temporary vault file.");
                });
            });
        }
    }
}
