// SPDX-License-Identifier: AGPL-3.0-only
/**
* dvmcli - headless Linux proof of concept for the DVM Desktop Dispatch Console.
*
* Radio (RID) aliases for one system, the same way the Windows console does them: a local
* alias file (aliasPath, default alias.yml beside the codeplug) and, with syncRadioAliases,
* a list downloaded from the FNE after every login, cached per system. Downloaded names win;
* RIDs missing from the download fall back to the local file, then to the bare number.
*/

using dvmconsole;
using fnecore;

namespace dvmcli
{
    /// <summary>
    /// Resolves radio IDs to names for one system.
    /// </summary>
    public sealed class RadioAliasBook
    {
        private readonly Codeplug codeplug;
        private readonly Codeplug.System system;
        private readonly string cacheDirectory;
        private int syncing;

        /// <summary>Messages for the log.</summary>
        public event Action<string> Status;

        /// <summary>The system these aliases belong to.</summary>
        public Codeplug.System System => system;
        /// <summary>Whether this system downloads aliases from the FNE.</summary>
        public bool SyncEnabled => system.SyncRadioAliases;
        /// <summary>Number of aliases from the local file.</summary>
        public int LocalCount => system.RidAlias?.Count ?? 0;
        /// <summary>Number of aliases downloaded from the FNE (or loaded from its cache).</summary>
        public int NetworkCount => system.NetworkRidAliases?.Count ?? 0;
        /// <summary>A download is running.</summary>
        public bool Syncing => Volatile.Read(ref syncing) != 0;

        /// <summary>
        /// Creates the alias book for a system.
        /// </summary>
        /// <param name="cacheDirectory">Folder that holds the RadioAliases cache (dvmcli's settings folder), or null for no cache.</param>
        public RadioAliasBook(Codeplug codeplug, Codeplug.System system, string cacheDirectory)
        {
            this.codeplug = codeplug;
            this.system = system;
            this.cacheDirectory = cacheDirectory;
        }

        /// <summary>The local alias file's full path, or null when none is configured.</summary>
        public string LocalPath =>
            string.IsNullOrWhiteSpace(system.AliasPath) ? null :
            codeplug?.SourcePath != null ? codeplug.ResolveFilePath(system.AliasPath) : Path.GetFullPath(system.AliasPath);

        /// <summary>
        /// Loads the local alias file and, when FNE sync is on, the cached download. Reports what it found.
        /// </summary>
        public void Load()
        {
            LoadLocal();
            if (!SyncEnabled)
                return;
            if (cacheDirectory == null)
                return;
            try
            {
                IReadOnlyDictionary<int, string> cached = RadioAliasCache.Load(cacheDirectory, system);
                if (cached != null)
                {
                    system.NetworkRidAliases = cached;
                    Status?.Invoke($"aliases: {cached.Count} cached from the FNE (refreshed after login)");
                }
            }
            catch (Exception ex)
            {
                Status?.Invoke($"aliases: the FNE alias cache couldn't be read, using the local file: {ex.Message}");
            }
        }

        /// <summary>
        /// (Re)loads the local alias file. A file that can't be read keeps the aliases already loaded.
        /// </summary>
        public void LoadLocal()
        {
            string path = LocalPath;
            if (path == null || !File.Exists(path))
            {
                Status?.Invoke(SyncEnabled
                    ? $"aliases: no local file ({path ?? "aliasPath not set"}); using names from the FNE"
                    : $"aliases: none (no {path ?? "alias file"}, and syncRadioAliases is off); showing radio IDs");
                return;
            }
            try
            {
                List<RadioAlias> list = AliasTools.LoadAliases(path) ?? new List<RadioAlias>();
                system.RidAlias = list.Where(a => a != null && a.Rid > 0 && !string.IsNullOrWhiteSpace(a.Alias)).ToList();
                Status?.Invoke($"aliases: {system.RidAlias.Count} from {path}");
            }
            catch (Exception ex)
            {
                Status?.Invoke($"aliases: {path} couldn't be read, keeping the aliases already loaded: {ex.Message}");
            }
        }

        /// <summary>
        /// Downloads the alias list from the FNE (when syncRadioAliases is on). Failures keep the
        /// current aliases and never affect the FNE connection.
        /// </summary>
        public async Task SyncAsync(FnePeer peer, bool manual = false)
        {
            if (!SyncEnabled)
            {
                if (manual)
                    Status?.Invoke("aliases: FNE sync is off for this system (set syncRadioAliases: true in the codeplug)");
                return;
            }
            if (Interlocked.Exchange(ref syncing, 1) != 0)
            {
                if (manual)
                    Status?.Invoke("aliases: a download from the FNE is already running");
                return;
            }
            try
            {
                Status?.Invoke("aliases: requesting the alias list from the FNE");
                byte[] data = await peer.RequestRadioAliasesAsync().ConfigureAwait(false);
                IReadOnlyDictionary<int, string> aliases = AliasTools.ParseNetworkAliases(data);
                system.NetworkRidAliases = aliases;
                Status?.Invoke($"aliases: {aliases.Count} from the FNE ({data.Length} bytes)");
                if (cacheDirectory != null)
                {
                    try { RadioAliasCache.Save(cacheDirectory, system, data); }
                    catch (Exception ex) { Status?.Invoke($"aliases: loaded, but the cache couldn't be saved: {ex.Message}"); }
                }
            }
            catch (Exception ex)
            {
                Status?.Invoke($"aliases: download from the FNE failed, keeping the current aliases: {ex.Message}");
            }
            finally
            {
                Volatile.Write(ref syncing, 0);
            }
        }

        /// <summary>The alias for a radio ID, or null.</summary>
        public string Name(uint rid)
        {
            if (rid == 0 || rid > int.MaxValue)
                return null;
            string alias = AliasTools.ResolveAlias(system, (int)rid);
            return string.IsNullOrWhiteSpace(alias) ? null : alias;
        }

        /// <summary>The alias, or the radio ID when there is none (for narrow places such as cards).</summary>
        public string Label(uint rid) => Name(rid) ?? rid.ToString();

        /// <summary>
        /// "Alias (RID)", or the radio ID when there is no alias (for the log). Aliases that already
        /// contain the RID as a number of its own (Centrunk's "Kara M - 9511") are shown as they are.
        /// </summary>
        public string Describe(uint rid)
        {
            if (Name(rid) is not string a)
                return rid.ToString();
            return ContainsRid(a, rid) ? a : $"{a} ({rid})";
        }

        /// <summary>Whether the alias contains the RID as a whole number (not part of a longer one).</summary>
        public static bool ContainsRid(string alias, uint rid) =>
            System.Text.RegularExpressions.Regex.IsMatch(alias ?? string.Empty, $@"(?<!\d){rid}(?!\d)");

        /// <summary>Number of radio IDs that have a name (downloaded and local together).</summary>
        public int Count
        {
            get
            {
                HashSet<int> rids = new HashSet<int>();
                if (SyncEnabled && system.NetworkRidAliases is IReadOnlyDictionary<int, string> net)
                    rids.UnionWith(net.Keys);
                if (system.RidAlias is List<RadioAlias> local)
                    rids.UnionWith(local.Select(x => x.Rid));
                return rids.Count;
            }
        }

        /// <summary>Short summary for the title bar.</summary>
        public string Summary
        {
            get
            {
                if (Syncing)
                    return "aliases syncing…";
                return $"aliases {Count}";
            }
        }
    } // public sealed class RadioAliasBook
}
