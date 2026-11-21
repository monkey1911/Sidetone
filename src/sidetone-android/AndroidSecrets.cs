// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone for Android. Remembered key-file passwords (SecretStore.Platform): kept in the app's
* private preferences, encrypted with an AES key that lives in the Android Keystore and never
* leaves it. A key file's password is found by a hash of the key file's path.
*/

using System.Security.Cryptography;
using System.Text;
using Android.Content;
using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;

namespace Sidetone.Droid
{
    public static class AndroidSecrets
    {
        private const string Alias = "sidetone-key-file-passwords";
        private const string Preferences = "secrets";

        private static ISharedPreferences Store_ => AndroidPlatform.Instance.AppContext.GetSharedPreferences(Preferences, FileCreationMode.Private);

        private static string Slot(string keyFile) => "keyfile-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(keyFile)));

        private static IKey Key()
        {
            Java.Security.KeyStore ks = Java.Security.KeyStore.GetInstance("AndroidKeyStore");
            ks.Load(null);
            if (ks.GetKey(Alias, null) is IKey existing)
                return existing;
            KeyGenerator gen = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, "AndroidKeyStore");
            gen.Init(new KeyGenParameterSpec.Builder(Alias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
                .SetBlockModes(KeyProperties.BlockModeGcm)
                .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)
                .Build());
            return gen.GenerateKey();
        }

        public static string Lookup(string keyFile)
        {
            try
            {
                string saved = Store_.GetString(Slot(keyFile), null);
                if (saved == null)
                    return null;
                byte[] all = Convert.FromBase64String(saved);
                byte[] iv = all[..12];
                Cipher c = Cipher.GetInstance("AES/GCM/NoPadding");
                c.Init(Javax.Crypto.CipherMode.DecryptMode, Key(), new GCMParameterSpec(128, iv));
                return Encoding.UTF8.GetString(c.DoFinal(all[12..]));
            }
            catch (Exception ex) when (ex is Java.Lang.Exception || ex is FormatException)
            {
                return null;    // forgotten, or the keystore lost its key: the password is asked for again
            }
        }

        public static bool Store(string keyFile, string password)
        {
            try
            {
                Cipher c = Cipher.GetInstance("AES/GCM/NoPadding");
                c.Init(Javax.Crypto.CipherMode.EncryptMode, Key());
                byte[] iv = c.GetIV();
                byte[] sealedBytes = c.DoFinal(Encoding.UTF8.GetBytes(password));
                if (iv == null || iv.Length != 12)
                    return false;
                string value = Convert.ToBase64String(iv.Concat(sealedBytes).ToArray());
                return Store_.Edit().PutString(Slot(keyFile), value).Commit();
            }
            catch (Java.Lang.Exception)
            {
                return false;
            }
        }

        public static bool Clear(string keyFile) => Store_.Edit().Remove(Slot(keyFile)).Commit();
    } // public static class AndroidSecrets
}
