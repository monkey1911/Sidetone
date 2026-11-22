// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks.
*
* Audio outputs per tab and per channel. A channel plays to its own output if it has one, else to
* its tab's (zone's), else to the main output (Settings' Output, or --output). The Windows console
* has the main output and one per channel; tabs are Sidetone's addition. Replays go the same way.
*
* Outputs are saved per system by the sound system's device name (node.name, which doesn't change
* across reboots). One that isn't connected when the console starts is skipped with a warning (the
* main output plays instead) but stays saved, so it's used again once it's back. A change takes effect
* at once: listening channels move to the new output.
*/

using Chan = Sidetone.Core.ConsoleChannel;

namespace Sidetone.Core
{
    public partial class DispatchConsole
    {
        /// <summary>Saved outputs that weren't connected when the console started (or was last checked).</summary>
        private readonly HashSet<string> missingOutputs = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Stands in for the sound system's device list in the self-test.</summary>
        internal Func<AudioDeviceList> deviceQueryForTest;

        /// <summary>The output a channel plays to: its own, its tab's, or the main one (null: the system default).</summary>
        public string OutputFor(ConsoleChannel c)
        {
            string chosen = c.Output ?? c.InZone?.Output;
            return chosen != null && !missingOutputs.Contains(chosen) ? chosen : Audio?.OutputTarget;
        }

        /// <summary>Where a channel's output comes from, in words: "own", "tab" or "main".</summary>
        public string OutputSource(ConsoleChannel c) => c.Output != null ? "own" : c.InZone?.Output != null ? "tab" : "main";

        /// <summary>Gives a channel its own output (a device name), or null to follow its tab.</summary>
        public void SetChannelOutput(ConsoleChannel c, string device)
        {
            device = string.IsNullOrWhiteSpace(device) ? null : device;
            if (c.Output == device)
                return;
            c.Output = device;
            missingOutputs.Remove(device ?? string.Empty);
            AddLog($"{c.Cfg.Name}: plays to {(device ?? (c.InZone?.Output != null ? $"its tab's output ({c.InZone.Output})" : "the main output"))}");
            MoveToOutput(c);
            MarkDirty();
        }

        /// <summary>Gives a tab (zone) an output for its channels (a device name), or null for the main output.</summary>
        public void SetZoneOutput(ConsoleZone z, string device)
        {
            device = string.IsNullOrWhiteSpace(device) ? null : device;
            if (z == null || z.Output == device)
                return;
            z.Output = device;
            missingOutputs.Remove(device ?? string.Empty);
            AddLog($"tab {z.Name}: plays to {device ?? "the main output"}{(z.chans.Any(c => c.Output != null) ? " (channels with their own output keep it)" : string.Empty)}");
            foreach (Chan c in z.chans)
                MoveToOutput(c);
            MoveStreamsToOutput(z);
            MarkDirty();
        }

        /// <summary>A listening channel's audio moves to the output it should now play to.</summary>
        private void MoveToOutput(Chan c)
        {
            if (!c.Selected || NoAudio)
                return;
            IAudioOutput fresh;
            try
            {
                fresh = AudioBackend.Current.OpenOutput(OutputFor(c));
                fresh.Failed += msg => AddLog($"{c.Cfg.Name}: {msg}");
            }
            catch (Exception ex)
            {
                AddLog($"{c.Cfg.Name}: couldn't open that output ({ex.Message}); it keeps playing where it was");
                return;
            }
            IAudioOutput old = c.Sink;
            c.Sink = fresh;     // the receive thread picks it up with its next block
            try { old?.Dispose(); }
            catch { /* it's being replaced anyway */ }
        }

        /// <summary>
        /// Checks the saved outputs against the sound system (at start): the ones that aren't connected
        /// are skipped, with a warning, until the next start or until they are chosen again.
        /// </summary>
        private void CheckSavedOutputs()
        {
            missingOutputs.Clear();
            List<string> saved = chans.Select(c => c.Output).Concat(zones.Select(z => z.Output)).Where(o => o != null).Distinct().ToList();
            if (saved.Count == 0 || NoAudio && deviceQueryForTest == null)
                return;
            AudioDeviceList list;
            try { list = (deviceQueryForTest ?? (() => AudioDeviceList.Query()))(); }
            catch (Exception) { return; }      // can't check: the names are used as saved
            foreach (string name in saved)
            {
                if (list.Outputs.Any(d => d.Name == name))
                    continue;
                missingOutputs.Add(name);
                List<string> users = zones.Where(z => z.Output == name).Select(z => $"tab {z.Name}")
                    .Concat(chans.Where(c => c.Output == name).Select(c => c.Cfg.Name)).ToList();
                AddLog($"warning: the output '{name}' ({string.Join(", ", users)}) isn't connected; the main output plays instead");
            }
        }
    } // public partial class DispatchConsole
}
