// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks.
*
* Windows's own services, handed to the core's platform hooks (as Android and macOS do):
*   Speech: SAPI (the voices in Settings, Time & language, Speech), through its COM objects on a thread of
*     their own, into a 22 kHz 16-bit WAV that is then levelled like any announcement.
*   Key-file passwords: Credential Manager (generic credentials named "Sidetone:" and the key file's path).
* Notifications aren't here: the graphical console flashes its taskbar button instead (MainWindow).
*/

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Sidetone.Core
{
    [SupportedOSPlatform("windows")]
    public static class WindowsPlatform
    {
        public static void Register()
        {
            if (Speech.Platform == null && Type.GetTypeFromProgID("SAPI.SpVoice") != null)
                Speech.Platform = new SapiEngine();
            SecretStore.Platform ??= (Lookup, Store, Clear);
        }

        /*
        ** Speech
        */

        private sealed class SapiEngine : IPlatformSpeech
        {
            private const int SAFT22kHz16BitMono = 22;
            private const int SSFMCreateForWrite = 3;
            private string voice;

            public string Name => "Windows speech";

            public string VoiceName
            {
                get
                {
                    if (voice == null)
                    {
                        try { voice = OnSta(() => { dynamic v = New("SAPI.SpVoice"); return (string)v.Voice.GetDescription(); }); }
                        catch (Exception) { voice = "the system voice"; }
                    }
                    return voice;
                }
            }

            public void SpeakToWav(string text, double rate, string wavPath)
            {
                try
                {
                    OnSta<object>(() =>
                    {
                        dynamic v = New("SAPI.SpVoice");
                        dynamic format = New("SAPI.SpAudioFormat");
                        format.Type = SAFT22kHz16BitMono;
                        dynamic file = New("SAPI.SpFileStream");
                        file.Format = format;
                        file.Open(wavPath, SSFMCreateForWrite, false);
                        try
                        {
                            v.AudioOutputStream = file;
                            v.Rate = Math.Clamp((int)Math.Round((rate - 1) * 10), -10, 10);     // SAPI's -10 to 10; 0 is the voice's own pace
                            v.Speak(text, 0);
                        }
                        finally
                        {
                            file.Close();
                        }
                        return null;
                    });
                }
                catch (Exception ex) when (ex is COMException || ex is InvalidOperationException || ex is Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
                {
                    throw new ArgumentException($"Windows speech didn't say it ({ex.Message})");
                }
                if (!File.Exists(wavPath))
                    throw new ArgumentException("Windows speech didn't say it (no audio)");
            }

            private static dynamic New(string progId) =>
                Activator.CreateInstance(Type.GetTypeFromProgID(progId) ?? throw new InvalidOperationException($"{progId} isn't installed"));

            /// <summary>SAPI's objects want a single-threaded apartment: runs the work on a thread of its own.</summary>
            private static T OnSta<T>(Func<T> work)
            {
                T result = default;
                Exception failed = null;
                Thread t = new Thread(() =>
                {
                    try { result = work(); }
                    catch (Exception ex) { failed = ex; }
                }) { IsBackground = true, Name = "speech" };
                t.SetApartmentState(ApartmentState.STA);
                t.Start();
                if (!t.Join(TimeSpan.FromSeconds(60)))
                    throw new InvalidOperationException("it took too long");
                if (failed != null)
                    throw failed is System.Reflection.TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : failed;
                return result;
            }
        } // private sealed class SapiEngine

        /*
        ** Credential Manager
        */

        private const int CredTypeGeneric = 1, CredPersistLocalMachine = 2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct Credential
        {
            public int Flags;
            public int Type;
            public string TargetName;
            public string Comment;
            public long LastWritten;
            public int CredentialBlobSize;
            public IntPtr CredentialBlob;
            public int Persist;
            public int AttributeCount;
            public IntPtr Attributes;
            public string TargetAlias;
            public string UserName;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);
        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredWrite(ref Credential credential, int flags);
        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredDelete(string target, int type, int flags);
        [DllImport("advapi32.dll")]
        private static extern void CredFree(IntPtr buffer);

        private static string Target(string keyFile) => "Sidetone:" + (keyFile ?? string.Empty);

        private static string Lookup(string keyFile)
        {
            if (!CredRead(Target(keyFile), CredTypeGeneric, 0, out IntPtr p))
                return null;
            try
            {
                Credential c = Marshal.PtrToStructure<Credential>(p);
                byte[] bytes = new byte[c.CredentialBlobSize];
                Marshal.Copy(c.CredentialBlob, bytes, 0, bytes.Length);
                return Encoding.UTF8.GetString(bytes);
            }
            finally
            {
                CredFree(p);
            }
        }

        private static bool Store(string keyFile, string password)
        {
            byte[] secret = Encoding.UTF8.GetBytes(password ?? string.Empty);
            IntPtr blob = Marshal.AllocHGlobal(Math.Max(1, secret.Length));
            try
            {
                Marshal.Copy(secret, 0, blob, secret.Length);
                Credential c = new Credential
                {
                    Type = CredTypeGeneric,
                    TargetName = Target(keyFile),
                    CredentialBlobSize = secret.Length,
                    CredentialBlob = blob,
                    Persist = CredPersistLocalMachine,
                    UserName = Environment.UserName,
                    Comment = "Sidetone: a KFDtool key file's password"
                };
                return CredWrite(ref c, 0);
            }
            finally
            {
                // wipe the copies before letting go of them
                Marshal.Copy(new byte[secret.Length], 0, blob, secret.Length);
                Marshal.FreeHGlobal(blob);
                Array.Clear(secret);
            }
        }

        private static bool Clear(string keyFile) =>
            CredDelete(Target(keyFile), CredTypeGeneric, 0) || Marshal.GetLastWin32Error() == 1168;     // ERROR_NOT_FOUND: nothing to forget
    } // public static class WindowsPlatform
}
