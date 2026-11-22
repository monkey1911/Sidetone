// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks.
*
* Web streams on the console (WebStreamPlayer plays them): the codeplug's web_streams per zone, each
* off until it's started (as the codeplug's own example says, "streams load disabled"). A stream plays
* to its tab's output, follows Mute all, and stops when its tab is hidden. Volumes are saved by name.
*/

using dvmconsole;

namespace Sidetone.Core
{
    /// <summary>One web stream of a tab.</summary>
    public sealed class ConsoleStream
    {
        internal Codeplug.WebStream Cfg;
        internal WebStreamPlayer Player;

        public string Name => Cfg.Name;
        public ConsoleZone Zone { get; internal set; }
        /// <summary>The codeplug's idleColor (#RRGGBB or #AARRGGBB), or null.</summary>
        public string IdleColor => string.IsNullOrWhiteSpace(Cfg.IdleColor) ? null : Cfg.IdleColor.Trim();
        /// <summary>The address's host, the only part of it shown or logged.</summary>
        public string Host => WebStreamPlayer.HostOf(Cfg.Url);
        /// <summary>Volume (gain, 1 is as sent).</summary>
        public double Volume { get; internal set; } = 1.0;
        public bool On => Player != null && Player.State != StreamState.Failed;
        public StreamState State
        {
            get
            {
                WebStreamPlayer p = Player;
                if (p == null)
                    return StreamState.Off;
                StreamState st = p.State;
                return st == StreamState.Receiving && DateTime.UtcNow - p.LastBlockUtc > WebStreamPlayer.ActivityHold ? StreamState.Idle : st;
            }
        }
        /// <summary>The connection attempt under way (1 to 3), or 0 once playing.</summary>
        public int Attempt => Player?.Attempt ?? 0;
        /// <summary>Why it failed, in words (Failed only).</summary>
        public string Reason => Player?.State == StreamState.Failed ? Player.Reason : null;

        /// <summary>The state in words, as the card shows it.</summary>
        public string StateText => State switch
        {
            StreamState.Connecting => Attempt > 1 ? $"Retry {Attempt}/{WebStreamPlayer.Attempts}" : "Connecting",
            StreamState.Idle => "Idle",
            StreamState.Receiving => "RX",
            StreamState.Failed => "Failed",
            _ => "Off"
        };

        public override string ToString() => Name;
    } // public sealed class ConsoleStream

    public partial class DispatchConsole
    {
        private readonly List<ConsoleStream> streams = new List<ConsoleStream>();

        /// <summary>Opens a stream's output in the self-test (instead of the sound system).</summary>
        internal Func<ConsoleStream, IAudioOutput> streamOutputForTest;

        /// <summary>Every web stream, in codeplug order.</summary>
        public IReadOnlyList<ConsoleStream> Streams => streams;

        /// <summary>A tab's web streams.</summary>
        public IReadOnlyList<ConsoleStream> StreamsIn(ConsoleZone z) => streams.Where(s => s.Zone == z).ToList();

        /// <summary>
        /// Picks up the codeplug's web streams for the tabs this console has. shortcut: a zone with streams
        /// but no channels for this system has no tab, so its streams are left out; give it a channel.
        /// </summary>
        private void SetUpStreams(Codeplug codeplug)
        {
            List<Codeplug.Zone> cpZones = codeplug.Zones ?? new List<Codeplug.Zone>();
            for (int zi = 0; zi < cpZones.Count; zi++)
            {
                Codeplug.Zone zone = cpZones[zi];
                if (zone?.WebStreams == null)
                    continue;
                string name = string.IsNullOrWhiteSpace(zone.Name) ? $"Zone {zi + 1}" : zone.Name.Trim();
                ConsoleZone z = zones.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                if (z == null)
                    continue;
                foreach (Codeplug.WebStream ws in zone.WebStreams)
                    if (ws != null && !string.IsNullOrWhiteSpace(ws.Name) && !streams.Any(s => string.Equals(s.Name, ws.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
                    {
                        ws.Name = ws.Name.Trim();
                        streams.Add(new ConsoleStream { Cfg = ws, Zone = z });
                    }
            }
        }

        /// <summary>Starts a stream, or stops it if it's on (a failed one starts again).</summary>
        public void ToggleStream(ConsoleStream s)
        {
            if (s == null)
                return;
            if (s.On)
            {
                StopStream(s);
                AddLog($"stream {s.Name}: off");
                return;
            }
            if (s.Zone.Hidden)
            {
                AddLog($"stream {s.Name}: zone {s.Zone.Name} is hidden; show it again to play it");
                return;
            }
            s.Player?.Dispose();
            s.Player = new WebStreamPlayer(s.Cfg, () => OpenStreamOutput(s), () => globalMute ? 0 : s.Volume, AddLog);
            AddLog($"stream {s.Name}: connecting to {s.Host}");
            s.Player.Start();
        }

        private IAudioOutput OpenStreamOutput(ConsoleStream s)
        {
            if (streamOutputForTest != null)
                return streamOutputForTest(s);
            if (NoAudio)
                return null;
            string chosen = s.Zone.Output;
            return AudioBackend.Current.OpenOutput(chosen != null && !missingOutputs.Contains(chosen) ? chosen : Audio?.OutputTarget);
        }

        private static void StopStream(ConsoleStream s)
        {
            WebStreamPlayer p = s.Player;
            s.Player = null;
            p?.Dispose();
        }

        /// <summary>Stops every stream (closing the console, going offline).</summary>
        public void StopAllStreams()
        {
            foreach (ConsoleStream s in streams)
                StopStream(s);
        }

        /// <summary>Stops a hidden tab's streams.</summary>
        private void StopStreamsIn(ConsoleZone z)
        {
            foreach (ConsoleStream s in streams.Where(x => x.Zone == z && x.Player != null))
            {
                StopStream(s);
                AddLog($"stream {s.Name}: off (its zone is hidden)");
            }
        }

        /// <summary>A tab's output changed: its streams move to it.</summary>
        private void MoveStreamsToOutput(ConsoleZone z)
        {
            foreach (ConsoleStream s in streams.Where(x => x.Zone == z))
                s.Player?.ReopenOutput();
        }

        public void StreamLouder(ConsoleStream s) => SetStreamVolume(s, s.Volume * 1.25);
        public void StreamQuieter(ConsoleStream s) => SetStreamVolume(s, s.Volume / 1.25);

        /// <summary>Sets a stream's volume (0.25 to 8, as a channel's).</summary>
        public void SetStreamVolume(ConsoleStream s, double volume)
        {
            if (s == null)
                return;
            s.Volume = Math.Clamp(volume, 0.25, 8.0);
            MarkDirty();
        }

        private void RestoreStreamVolumes(SystemSettings ss)
        {
            foreach (ConsoleStream s in streams)
                if (ss.StreamVolumes != null && ss.StreamVolumes.TryGetValue(s.Name, out double v))
                    s.Volume = Math.Clamp(v, 0.25, 8.0);
        }

        private void SaveStreamVolumes(SystemSettings ss)
        {
            List<ConsoleStream> changed = streams.Where(s => Math.Abs(s.Volume - 1.0) > 1e-6).ToList();
            ss.StreamVolumes = changed.Count == 0 ? null : changed.ToDictionary(s => s.Name, s => Math.Round(s.Volume, 3), StringComparer.OrdinalIgnoreCase);
        }
    } // public partial class DispatchConsole
}
