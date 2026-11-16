// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks.
*
* Local key files: traffic keys from a password-protected KFDtool key container (.ekc), for systems
* where the FNE doesn't deliver keys to the console. The container format is read by dvmconsole's
* own EkcKeyStore; this wraps it with plain error messages, and SecretStore optionally remembers
* the password in the desktop's keyring. Neither passwords nor keys are ever logged or written
* to Sidetone's own files.
*/

using System.Diagnostics;
using System.Security.Cryptography;
using dvmconsole;

namespace Sidetone.Core
{
    /// <summary>
    /// An unlocked local key file.
    /// </summary>
    public sealed class LocalKeyFile : IDisposable
    {
        private readonly EkcKeyStore store;

        private LocalKeyFile(EkcKeyStore store)
        {
            this.store = store;
        }

        /// <summary>Traffic keys in the file.</summary>
        public int Count => store.Count;
        /// <summary>Entries that were left out: key-wrapping keys, and algorithms P25 voice doesn't use here.</summary>
        public int Skipped => store.SkippedKeys;

        /// <summary>The key for an algorithm and key ID, or null. Not a copy: don't change it.</summary>
        public byte[] Lookup(byte algId, ushort keyId) => store.TryGetKey(algId, keyId, out byte[] key) ? key : null;

        /// <summary>
        /// The key file to use: the one given (a command line option or a saved setting), or else the
        /// one the codeplug names (relative to the codeplug). Null when there is none.
        /// </summary>
        public static string Resolve(Codeplug codeplug, string chosen)
        {
            if (!string.IsNullOrWhiteSpace(chosen))
                return Path.GetFullPath(chosen);
            return string.IsNullOrWhiteSpace(codeplug?.KeyFile) ? null : codeplug.ResolveFilePath(codeplug.KeyFile);
        }

        /// <summary>
        /// Opens and unlocks a key file. Returns null and says why when it can't;
        /// <paramref name="wrongPassword"/> is true when another password might work.
        /// </summary>
        public static LocalKeyFile Open(string path, string password, out string error, out bool wrongPassword)
        {
            error = null;
            wrongPassword = false;
            try
            {
                if (!File.Exists(path))
                {
                    error = $"the key file wasn't found: {path}";
                    return null;
                }
                return new LocalKeyFile(EkcKeyStore.Open(path, password ?? string.Empty));
            }
            catch (CryptographicException)
            {
                wrongPassword = true;
                error = "that password doesn't unlock the key file";
            }
            catch (InvalidDataException ex)
            {
                error = ex.Message.Replace("KFDEKC", "KFDtool");    // EkcKeyStore's own messages; they never include file contents
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                error = $"the key file couldn't be read: {ex.Message}";
            }
            catch (Exception)
            {
                // anything else could quote decrypted text; say nothing specific
                error = "the key file couldn't be read; check it in KFDtool";
            }
            return null;
        }

        public void Dispose() => store.Dispose();
    } // public sealed class LocalKeyFile

    /// <summary>
    /// Remembers a key file's password in the desktop's keyring (GNOME Keyring, KWallet and the like),
    /// through the freedesktop Secret Service and its secret-tool command. When secret-tool isn't
    /// installed, nothing is remembered and the password is asked for each time.
    /// </summary>
    public static class SecretStore
    {
        private const int TimeoutMs = 8000;

        private static string Tool => AudioDeviceList.FindOnPath("secret-tool");

        /// <summary>The keyring can be used on this machine.</summary>
        public static bool Available => Tool != null;

        /// <summary>The remembered password for a key file, or null.</summary>
        public static string Lookup(string keyFile)
        {
            string output = Run(null, "lookup", "application", "sidetone", "keyfile", Path.GetFullPath(keyFile));
            return string.IsNullOrEmpty(output) ? null : output.TrimEnd('\n');
        }

        /// <summary>Remembers the password for a key file. Returns false when it couldn't.</summary>
        public static bool Store(string keyFile, string password) =>
            Run(password, "store", "--label", "Sidetone key file password", "application", "sidetone", "keyfile", Path.GetFullPath(keyFile)) != null;

        /// <summary>Forgets the password for a key file.</summary>
        public static bool Clear(string keyFile) =>
            Run(null, "clear", "application", "sidetone", "keyfile", Path.GetFullPath(keyFile)) != null;

        /// <summary>Runs secret-tool; returns what it printed, or null when it isn't there, failed or took too long.</summary>
        private static string Run(string input, params string[] args)
        {
            string tool = Tool;
            if (tool == null)
                return null;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(tool)
                {
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                };
                foreach (string a in args)
                    psi.ArgumentList.Add(a);

                using Process p = Process.Start(psi);
                if (p == null)
                    return null;
                if (input != null)
                    p.StandardInput.Write(input);
                p.StandardInput.Close();
                Task<string> output = p.StandardOutput.ReadToEndAsync();
                _ = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(TimeoutMs))
                {
                    try { p.Kill(true); } catch (Exception) { }
                    return null;
                }
                return p.ExitCode == 0 && output.Wait(1000) ? output.Result : null;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException || ex is IOException)
            {
                return null;
            }
        }
    } // public static class SecretStore
}
