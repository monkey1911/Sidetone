// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks.
*
* macOS's own services, handed to the core's platform hooks (as Android does with its own):
*   Speech: /usr/bin/say, the text on stdin, into a 16-bit WAV (then levelled like any announcement).
*   Key-file passwords: the login Keychain, through Security.framework's generic-password calls (service
*     "Sidetone", account = the key file's path), so the password is never on a command line.
*   Notifications: osascript's "display notification", the title and text passed as arguments (never
*     put into the script itself).
*/

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Sidetone.Core
{
    [SupportedOSPlatform("macos")]
    public static class MacPlatform
    {
        private const string Service = "Sidetone";

        public static void Register()
        {
            if (Speech.Platform == null && File.Exists(SayEngine.Program))
                Speech.Platform = new SayEngine();
            SecretStore.Platform ??= (Lookup, Store, Clear);
            DesktopNotify.Platform ??= Notify;
        }

        /*
        ** Speech
        */

        private sealed class SayEngine : IPlatformSpeech
        {
            public const string Program = "/usr/bin/say";
            public string Name => "macOS speech";
            public string VoiceName => "the system voice";

            public void SpeakToWav(string text, double rate, string wavPath)
            {
                List<string> args = new List<string> { "-o", wavPath, "--data-format=LEI16@22050", "-f", "-" };
                if (Math.Abs(rate - 1) > 0.001)
                    args.AddRange(new[] { "-r", Math.Round(175 * rate).ToString(System.Globalization.CultureInfo.InvariantCulture) });   // words a minute
                string error = Run(Program, args, stdin: text, TimeSpan.FromSeconds(60));
                if (error != null || !File.Exists(wavPath))
                    throw new ArgumentException($"macOS speech didn't say it ({error ?? "no audio"})");
            }
        } // private sealed class SayEngine

        /// <summary>Runs a program with arguments (never a shell). Returns null on success, else why not.</summary>
        private static string Run(string program, IEnumerable<string> args, string stdin, TimeSpan limit)
        {
            ProcessStartInfo psi = new ProcessStartInfo(program)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string a in args)
                psi.ArgumentList.Add(a);
            try
            {
                using Process p = Process.Start(psi);
                if (p == null)
                    return $"{Path.GetFileName(program)} didn't start";
                p.StandardInput.Write(stdin ?? string.Empty);
                p.StandardInput.Close();
                Task<string> err = p.StandardError.ReadToEndAsync();
                p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit((int)limit.TotalMilliseconds))
                {
                    try { p.Kill(); } catch (InvalidOperationException) { }
                    return "it took too long";
                }
                return p.ExitCode == 0 ? null : err.Result.Trim() is { Length: > 0 } e ? e : $"exit code {p.ExitCode}";
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is IOException || ex is InvalidOperationException)
            {
                return ex.Message;
            }
        }

        /*
        ** Notifications
        */

        private static bool Notify(string title, string body, bool urgent) =>
            Run("/usr/bin/osascript", new[] { "-e", "on run argv", "-e", "display notification (item 2 of argv) with title (item 1 of argv)", "-e", "end run", title ?? "Sidetone", body ?? string.Empty },
                stdin: null, TimeSpan.FromSeconds(10)) == null;

        /*
        ** Keychain
        */

        private const string SecurityLib = "/System/Library/Frameworks/Security.framework/Security";
        private const string CoreFoundationLib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        [DllImport(SecurityLib)]
        private static extern int SecKeychainFindGenericPassword(IntPtr keychain, uint serviceLength, byte[] service, uint accountLength, byte[] account,
                                                                 out uint passwordLength, out IntPtr passwordData, out IntPtr item);
        [DllImport(SecurityLib)]
        private static extern int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceLength, byte[] service, uint accountLength, byte[] account,
                                                                uint passwordLength, byte[] passwordData, out IntPtr item);
        [DllImport(SecurityLib)]
        private static extern int SecKeychainItemModifyAttributesAndData(IntPtr item, IntPtr attributes, uint length, byte[] data);
        [DllImport(SecurityLib)]
        private static extern int SecKeychainItemDelete(IntPtr item);
        [DllImport(SecurityLib)]
        private static extern int SecKeychainItemFreeContent(IntPtr attributes, IntPtr data);
        [DllImport(CoreFoundationLib)]
        private static extern void CFRelease(IntPtr cf);

        /// <summary>Finds the item for a key file: its password (null when there isn't one) and the item, to release.</summary>
        private static string Find(string keyFile, out IntPtr item)
        {
            byte[] service = Encoding.UTF8.GetBytes(Service), account = Encoding.UTF8.GetBytes(keyFile ?? string.Empty);
            int status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)service.Length, service, (uint)account.Length, account, out uint length, out IntPtr data, out item);
            if (status != 0)
            {
                item = IntPtr.Zero;
                return null;
            }
            try
            {
                byte[] bytes = new byte[length];
                Marshal.Copy(data, bytes, 0, (int)length);
                return Encoding.UTF8.GetString(bytes);
            }
            finally
            {
                SecKeychainItemFreeContent(IntPtr.Zero, data);
            }
        }

        private static string Lookup(string keyFile)
        {
            string password = Find(keyFile, out IntPtr item);
            if (item != IntPtr.Zero)
                CFRelease(item);
            return password;
        }

        private static bool Store(string keyFile, string password)
        {
            byte[] secret = Encoding.UTF8.GetBytes(password ?? string.Empty);
            Find(keyFile, out IntPtr item);
            try
            {
                if (item != IntPtr.Zero)
                    return SecKeychainItemModifyAttributesAndData(item, IntPtr.Zero, (uint)secret.Length, secret) == 0;
                byte[] service = Encoding.UTF8.GetBytes(Service), account = Encoding.UTF8.GetBytes(keyFile ?? string.Empty);
                int status = SecKeychainAddGenericPassword(IntPtr.Zero, (uint)service.Length, service, (uint)account.Length, account, (uint)secret.Length, secret, out IntPtr added);
                if (added != IntPtr.Zero)
                    CFRelease(added);
                return status == 0;
            }
            finally
            {
                Array.Clear(secret);
                if (item != IntPtr.Zero)
                    CFRelease(item);
            }
        }

        private static bool Clear(string keyFile)
        {
            Find(keyFile, out IntPtr item);
            if (item == IntPtr.Zero)
                return true;    // nothing to forget
            try { return SecKeychainItemDelete(item) == 0; }
            finally { CFRelease(item); }
        }
    } // public static class MacPlatform
}
