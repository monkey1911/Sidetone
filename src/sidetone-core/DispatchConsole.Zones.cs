// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks.
*
* Zones (tabs). Each codeplug zone is a tab, as in the Windows console: its channels are shown
* together, in codeplug order. The tab on show is simply the focused channel's zone, so switching
* tabs moves the focus, and each tab remembers which of its cards was focused last. Channels on
* the other tabs keep working (listening, patches, recording); only what's on screen changes.
*
* A zone can also be hidden, like the Windows console's Tab Manager does: its channels are switched
* off (not listening, so out of patches and recording too), their traffic isn't shown, and they
* can't be listened to until the zone is shown again. Hidden zones are remembered per system.
*/

using dvmconsole;
using Chan = Sidetone.Core.ConsoleChannel;

namespace Sidetone.Core
{
    /// <summary>
    /// One zone of the codeplug, shown as a tab: its name, colours and channels.
    /// </summary>
    public sealed class ConsoleZone
    {
        internal readonly List<Chan> chans = new List<Chan>();
        internal volatile bool hidden;      // read on the network thread

        /// <summary>The zone's name as shown on its tab.</summary>
        public string Name { get; internal set; }

        /// <summary>The tab's colour and text colour from the codeplug (like "#RRGGBB"), or null.</summary>
        public string TabColor { get; internal set; }
        public string TabTextColor { get; internal set; }

        /// <summary>Position in <see cref="DispatchConsole.Zones"/>.</summary>
        public int Index { get; internal set; }

        /// <summary>The zone's channels, in codeplug order.</summary>
        public IReadOnlyList<ConsoleChannel> Channels => chans;

        /// <summary>The audio output for this tab's channels (a device name), or null for the main output.</summary>
        public string Output { get; internal set; }

        /// <summary>Hidden: the zone's channels are switched off and its tab isn't shown.</summary>
        public bool Hidden => hidden;

        /// <summary>The card last focused on this tab (an index into <see cref="DispatchConsole.Channels"/>).</summary>
        internal int lastFocus = -1;

        public override string ToString() => Name;
    } // public sealed class ConsoleZone

    public partial class DispatchConsole
    {
        internal readonly List<ConsoleZone> zones = new List<ConsoleZone>();

        /// <summary>All zones with channels for this system, in codeplug order (hidden ones too).</summary>
        public IReadOnlyList<ConsoleZone> Zones => zones;

        /// <summary>The zones that are shown as tabs.</summary>
        public IReadOnlyList<ConsoleZone> VisibleZones => zones.Where(z => !z.Hidden).ToList();

        /// <summary>The zones that are hidden.</summary>
        public IReadOnlyList<ConsoleZone> HiddenZones => zones.Where(z => z.Hidden).ToList();

        /// <summary>The tab on show: the focused channel's zone.</summary>
        public ConsoleZone CurrentZone => chans[cursor].InZone;

        /// <summary>
        /// Adds a codeplug channel's zone to the zone list (zones with the same name share one tab,
        /// as in the Windows console). A zone without a name is called "Zone N".
        /// </summary>
        private ConsoleZone ZoneFor(Codeplug.Zone zone, int position)
        {
            string name = string.IsNullOrWhiteSpace(zone.Name) ? $"Zone {position + 1}" : zone.Name.Trim();
            ConsoleZone z = zones.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (z != null)
                return z;
            z = new ConsoleZone
            {
                Name = name,
                TabColor = string.IsNullOrWhiteSpace(zone.TabColor) ? null : zone.TabColor.Trim(),
                TabTextColor = string.IsNullOrWhiteSpace(zone.TabTextColor) ? null : zone.TabTextColor.Trim(),
                Index = zones.Count
            };
            zones.Add(z);
            return z;
        }

        /// <summary>
        /// Moves the focus to a channel and remembers it as its tab's focused card. A channel in a
        /// hidden zone can't be focused.
        /// </summary>
        internal void FocusTo(int index)
        {
            index = Math.Clamp(index, 0, chans.Count - 1);
            if (chans[index].Hidden)
                return;
            cursor = index;
            chans[index].InZone.lastFocus = index;
            MarkDirty();
        }

        /// <summary>Shows a zone's tab: the focus goes to the card focused there last, or its first card.</summary>
        public void ShowZone(ConsoleZone z)
        {
            if (z == null || z.Hidden || z.chans.Count == 0 || z == CurrentZone)
                return;
            int target = z.lastFocus >= 0 && z.lastFocus < chans.Count && chans[z.lastFocus].InZone == z ? z.lastFocus : chans.IndexOf(z.chans[0]);
            FocusTo(target);
        }

        /// <summary>Shows the next tab (delta 1) or the previous one (-1), going round at the ends.</summary>
        public void NextZone(int delta)
        {
            IReadOnlyList<ConsoleZone> shown = VisibleZones;
            if (shown.Count < 2)
                return;
            int at = Math.Max(0, IndexOf(shown, CurrentZone));
            ShowZone(shown[((at + delta) % shown.Count + shown.Count) % shown.Count]);
        }

        /// <summary>Shows the n-th tab (1 is the first); nothing happens if there's no such tab.</summary>
        public void ShowZoneNumber(int n)
        {
            IReadOnlyList<ConsoleZone> shown = VisibleZones;
            if (n >= 1 && n <= shown.Count)
                ShowZone(shown[n - 1]);
        }

        private static int IndexOf(IReadOnlyList<ConsoleZone> list, ConsoleZone z)
        {
            for (int i = 0; i < list.Count; i++)
                if (list[i] == z)
                    return i;
            return -1;
        }

        /// <summary>
        /// Something is happening on a zone's channels: Transmitting (the console is transmitting or
        /// sending tones there), Receiving (a call on one of them, listened to or not), or Idle.
        /// Used for the mark on a tab, so traffic on another tab can be seen.
        /// </summary>
        public ChannelActivity ZoneActivity(ConsoleZone z)
        {
            if (z == null || z.Hidden)
                return ChannelActivity.Idle;
            bool rx = false;
            foreach (Chan c in z.chans)
            {
                if ((Transmitting && txChan == c) || toneChans.Contains(c))
                    return ChannelActivity.Transmitting;
                if (c.Active || (c.Selected && c.Rx?.InCall == true))
                    rx = true;
            }
            return rx ? ChannelActivity.Receiving : ChannelActivity.Idle;
        }

        /// <summary>
        /// Hides a zone (its channels are switched off) or shows it again. Returns null when done, or
        /// why not: the last zone on show can't be hidden, nor one the console is transmitting on.
        /// </summary>
        public string SetZoneHidden(ConsoleZone z, bool hide)
        {
            if (z == null || z.Hidden == hide)
                return null;

            if (!hide)
            {
                lock (uiLock)
                    z.hidden = false;
                AddLog($"zone {z.Name}: shown again ({Count(z.chans.Count, "channel")}, not listening yet)");
                MarkDirty();
                return null;
            }

            string why = zones.Count(x => !x.Hidden) <= 1 ? $"zone {z.Name}: it's the only zone on show, so it can't be hidden"
                       : z.chans.Any(c => (Transmitting && txChan == c) || toneChans.Contains(c)) ? $"zone {z.Name}: can't hide it while transmitting on it"
                       : null;
            if (why != null)
            {
                AddLog(why);
                return why;
            }

            bool wasShowing = CurrentZone == z;
            int listening = z.chans.Count(c => c.Selected);
            if (ReplayChannel != null && ReplayChannel.InZone == z)
                StopReplay();
            foreach (Chan c in z.chans)
                Deselect(c, quiet: true);       // ends its patch and recording too
            lock (uiLock)
            {
                z.hidden = true;
                foreach (Chan c in z.chans)
                    c.Active = false;           // no "busy" left showing from before
            }
            if (wasShowing)
            {
                // to the next tab on show (or the one before, at the end)
                ConsoleZone next = zones.Skip(z.Index + 1).FirstOrDefault(x => !x.Hidden) ?? zones.Take(z.Index).LastOrDefault(x => !x.Hidden);
                if (next != null)
                {
                    int target = next.lastFocus >= 0 && next.lastFocus < chans.Count && chans[next.lastFocus].InZone == next ? next.lastFocus : chans.IndexOf(next.chans[0]);
                    FocusTo(target);
                }
            }
            AddLog($"zone {z.Name}: hidden, {Count(z.chans.Count, "channel")} switched off" +
                   (listening > 0 ? $" (stopped listening on {listening})" : string.Empty));
            MarkDirty();
            return null;
        }

        private static string Count(int n, string what) => $"{n} {what}{(n == 1 ? string.Empty : "s")}";

        /// <summary>
        /// Applies the remembered hidden zones before channels are restored, so a hidden zone's
        /// channels don't start listening. Never leaves every zone hidden (as the Windows console
        /// doesn't): then the first one is shown.
        /// </summary>
        private void RestoreHiddenZones(SystemSettings ss)
        {
            HashSet<string> hide = new HashSet<string>((ss.HiddenZones ?? new List<string>()).Select(n => n?.Trim() ?? string.Empty), StringComparer.OrdinalIgnoreCase);
            lock (uiLock)
            {
                foreach (ConsoleZone z in zones)
                    z.hidden = hide.Contains(z.Name);
                if (zones.Count > 0 && zones.All(z => z.Hidden))
                    zones[0].hidden = false;
            }
            List<string> hidden = zones.Where(z => z.Hidden).Select(z => z.Name).ToList();
            if (hidden.Count > 0)
                AddLog($"hidden zones (switched off): {string.Join(", ", hidden)}");
        }

        /// <summary>After restoring: the focus is on a channel that's on show, and every tab knows its focused card.</summary>
        private void SettleFocus()
        {
            if (chans[cursor].Hidden)
            {
                int first = chans.FindIndex(c => !c.Hidden);
                cursor = Math.Max(0, first);
            }
            chans[cursor].InZone.lastFocus = cursor;
        }
    } // public partial class DispatchConsole
}
