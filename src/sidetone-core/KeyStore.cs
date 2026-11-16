// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks.
*
* In-memory store for traffic keys: the ones the FNE delivers (KMM), and the ones in a local
* key file when one is unlocked, which win. Keys are held only in memory for the life of the
* process and are never logged or written to disk.
*/

using System.Collections.Concurrent;
using fnecore.P25;

namespace Sidetone.Core
{
    /// <summary>
    /// Traffic keys by (algorithm ID, key ID).
    /// </summary>
    public sealed class KeyStore
    {
        private readonly ConcurrentDictionary<(byte alg, ushort kid), byte[]> keys = new();

        /// <summary>Raised when a key is added or replaced.</summary>
        public event Action<byte, ushort> KeyAdded;

        private Func<byte, ushort, byte[]> local;

        /// <summary>Number of keys held (from the FNE, plus the local key file's).</summary>
        public int Count => keys.Count + LocalCount;

        /// <summary>Number of keys in the unlocked local key file.</summary>
        public int LocalCount { get; private set; }

        /// <summary>
        /// Uses a local key file's keys first. <paramref name="lookup"/> returns the key for an
        /// algorithm and key ID, or null. Pass null to stop using local keys.
        /// </summary>
        public void SetLocal(Func<byte, ushort, byte[]> lookup, int count)
        {
            local = lookup;
            LocalCount = lookup == null ? 0 : count;
        }

        /// <summary>The key for this algorithm and key ID comes from the local key file.</summary>
        public bool IsLocal(byte algId, ushort keyId) => local?.Invoke(algId, keyId) != null;

        /// <summary>
        /// Stores a key; returns false if the algorithm is not one P25 voice uses or the key is empty.
        /// </summary>
        public bool Store(byte algId, ushort keyId, byte[] key)
        {
            if (key == null || key.Length == 0 || !IsSupported(algId))
                return false;

            keys[(algId, keyId)] = (byte[])key.Clone();
            KeyAdded?.Invoke(algId, keyId);
            return true;
        }

        /// <summary>
        /// Gets a copy of a key.
        /// </summary>
        public bool TryGet(byte algId, ushort keyId, out byte[] key)
        {
            byte[] mine = local?.Invoke(algId, keyId);
            if (mine != null)
            {
                key = (byte[])mine.Clone();
                return true;
            }
            if (keys.TryGetValue((algId, keyId), out byte[] stored))
            {
                key = (byte[])stored.Clone();
                return true;
            }
            key = null;
            return false;
        }

        /// <summary>
        /// True for the P25 voice algorithms the decoder supports.
        /// </summary>
        public static bool IsSupported(byte algId) =>
            algId == P25Defines.P25_ALGO_AES || algId == P25Defines.P25_ALGO_DES || algId == P25Defines.P25_ALGO_ARC4;

        /// <summary>
        /// Human-readable algorithm name.
        /// </summary>
        public static string AlgorithmName(byte algId) => algId switch
        {
            P25Defines.P25_ALGO_UNENCRYPT => "clear",
            P25Defines.P25_ALGO_AES => "AES-256",
            P25Defines.P25_ALGO_DES => "DES-OFB",
            P25Defines.P25_ALGO_ARC4 => "ADP/ARC4",
            _ => $"0x{algId:X2}"
        };
    } // public sealed class KeyStore
}
