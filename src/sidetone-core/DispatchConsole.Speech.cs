// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks.
*
* Spoken announcements: text said by a text-to-speech engine (Piper or espeak-ng, installed
* separately; see Speech.cs) and sent like a WAV announcement. And the time announcement: words
* before, the time (12- or 24-hour, in a chosen zone), the zone, words after; played when asked or
* by itself every 15, 30 or 60 minutes on the clock. A scheduled one waits for its channels to be
* quiet (up to three minutes; then it's skipped and logged) and never keys over the console's own
* transmission.
*
* The engine runs on a worker thread (Piper takes a fraction of a second, longer the first time);
* the audio is then sent from the UI thread. "Hear it" plays the words through the vocoder on the
* speakers, as a radio would hear them, without transmitting.
*/

using dvmconsole;
using Chan = Sidetone.Core.ConsoleChannel;

namespace Sidetone.Core
{
    public partial class DispatchConsole
    {
        private SpeechEngine speechEngine;
        private string speechWhy;
        private bool speechChecked;
        private volatile bool speechBusy;
        /// <summary>Spoken audio already made, by engine and voice, then text.</summary>
        private readonly Dictionary<string, short[]> spokenCache = new Dictionary<string, short[]>(StringComparer.Ordinal);

        /// <summary>Makes speech instead of the engine, at once on the calling thread (self-test).</summary>
        internal Func<string, short[]> synthesizeForTest;
        /// <summary>Takes what would be sent, instead of sending it (self-test; no FNE needed).</summary>
        internal Action<TonePlan, List<Chan>> sendAnnouncementForTest;
        /// <summary>The time, instead of the clock (self-test).</summary>
        internal Func<DateTime> clockForTest;

        /// <summary>The time now (UTC), or the self-test's.</summary>
        internal DateTime UtcNow => clockForTest?.Invoke() ?? DateTime.UtcNow;

        private bool CanSendAnnouncement => sendAnnouncementForTest != null || (peer != null && peer.Connected);

        private void SendAnnouncement(TonePlan plan, List<Chan> targets)
        {
            if (sendAnnouncementForTest != null)
                sendAnnouncementForTest(plan, targets);
            else
                SendTone(plan, targets, quiet: false);
        }

        /*
        ** The engine
        */

        /// <summary>The engine in use and its voice, or null (with why) when none is installed.</summary>
        public SpeechEngine CurrentSpeechEngine(out string why)
        {
            if (!speechChecked)
            {
                speechEngine = Speech.Choose(settings?.SpeechEngine, settings?.SpeechVoice, out speechWhy);
                speechChecked = true;
            }
            why = speechWhy;
            return speechEngine;
        }

        /// <summary>One line for the boxes: the engine and voice in use, or why there isn't one.</summary>
        public string SpeechStatus
        {
            get
            {
                SpeechEngine e = CurrentSpeechEngine(out string why);
                return e != null ? $"Text to speech: {e.Label}" : $"Text to speech: {why}";
            }
        }

        /// <summary>Looks for the engines again (after one was installed, or a voice downloaded).</summary>
        public void RefreshSpeechEngine() => speechChecked = false;

        /// <summary>Chooses the engine ("piper", "espeak-ng" or null for automatic) and its voice (null for the default).</summary>
        public void SetSpeechEngine(string engine, string voice)
        {
            if (settings == null)
                return;
            engine = string.IsNullOrWhiteSpace(engine) ? null : engine;
            voice = string.IsNullOrWhiteSpace(voice) ? null : voice.Trim();
            if (settings.SpeechEngine == engine && settings.SpeechVoice == voice)
                return;
            settings.SpeechEngine = engine;
            settings.SpeechVoice = voice;
            RefreshSpeechEngine();
            MarkDirty();
            CurrentSpeechEngine(out _);
            AddLog(SpeechStatus.Replace("Text to speech: ", "text to speech: "));
        }

        /*
        ** Spoken announcements
        */

        /// <summary>The remembered spoken announcements (their text), in the order they were added.</summary>
        public IReadOnlyList<string> SpokenAnnouncements => (IReadOnlyList<string>)settings?.SpokenAnnouncements ?? Array.Empty<string>();

        /// <summary>The longest text kept.</summary>
        public const int MaxSpokenLength = 2000;

        /// <summary>A short name for a spoken announcement, for lists and the log.</summary>
        public static string SpokenName(string text)
        {
            text = (text ?? string.Empty).Trim();
            return text.Length <= 48 ? text : text.Substring(0, 47).TrimEnd() + "…";
        }

        /// <summary>Adds a text to the list. Returns null, or why not.</summary>
        public string AddSpokenAnnouncement(string text)
        {
            text = (text ?? string.Empty).Replace('\n', ' ').Replace('\r', ' ').Trim();
            if (text.Length == 0)
                return "type what to say first";
            if (text.Length > MaxSpokenLength)
                return $"that's too long (at most {MaxSpokenLength} characters)";
            if (settings == null)
                return "settings aren't loaded";
            settings.SpokenAnnouncements ??= new List<string>();
            if (!settings.SpokenAnnouncements.Contains(text))
            {
                settings.SpokenAnnouncements.Add(text);
                AddLog($"spoken announcement added: \"{SpokenName(text)}\"");
                MarkDirty();
            }
            return null;
        }

        public void RemoveSpokenAnnouncement(string text)
        {
            if (settings?.SpokenAnnouncements?.Remove(text) == true)
            {
                AddLog($"spoken announcement removed: \"{SpokenName(text)}\"");
                MarkDirty();
            }
        }

        /// <summary>The key cached speech is kept under: the engine and voice (or the self-test's stand-in), then the text.</summary>
        private string SpeechKey(SpeechEngine engine, string text)
        {
            string who = synthesizeForTest != null ? "test" : engine?.CacheKey;
            return who == null ? null : who + "\n" + text;
        }

        /// <summary>How long a spoken announcement lasts, when it has been made already (else null).</summary>
        public TimeSpan? SpokenLength(string text)
        {
            string key = SpeechKey(CurrentSpeechEngine(out _), text);
            if (key == null)
                return null;
            lock (spokenCache)
                return spokenCache.TryGetValue(key, out short[] s) ? TimeSpan.FromSeconds(s.Length / (double)VoiceProcessor.SampleRate) : null;
        }

        /// <summary>True while the engine is making speech for an announcement or a preview.</summary>
        public bool SpeechPreparing => speechBusy;

        /// <summary>Why nothing can be sent right now, or null.</summary>
        private string AnnouncementBlocked()
        {
            if (toneJob != null)
                return $"a tone or announcement is already being sent ({toneJob.Plan.Name}); Esc stops it";
            if (speechBusy)
                return "an announcement is still being made by the text-to-speech engine";
            if (Transmitting)
                return "stop transmitting before sending an announcement";
            return null;
        }

        /// <summary>The focused channel, or every listening channel, when they can take an announcement; else why not.</summary>
        private string AnnouncementTargets(bool allListening, out List<Chan> targets)
        {
            targets = null;
            if (allListening)
            {
                targets = TxCapableListening();
                if (targets.Count == 0)
                    return "no listening channels to send the announcement on (click a card to listen)";
            }
            else
            {
                Chan c = chans[cursor];
                if (!c.IsP25)
                    return $"{c.Cfg.Name}: can't transmit on a {c.Cfg.Mode} channel";
                if (c.Cfg.RxOnly)
                    return $"{c.Cfg.Name}: receive-only channel";
                targets = new List<Chan> { c };
            }
            if (!CanSendAnnouncement)
                return "not connected to the FNE yet";
            return null;
        }

        /// <summary>
        /// Says a text on the focused channel, or every listening channel. Returns null when it has started
        /// (the engine runs first; then it's sent like a WAV announcement), or why not (also logged).
        /// </summary>
        public string SpeakAnnouncement(string text, bool allListening) =>
            StartSpoken($"spoken announcement \"{SpokenName(text)}\"", () => text, allListening, null, cache: true);

        /// <summary>Says the time announcement now, on the focused channel or every listening channel. Null, or why not.</summary>
        public string PlayTimeAnnouncement(bool allListening) =>
            StartSpoken("time announcement", () => Speech.TimeAnnouncementText(TimeAnnouncement, UtcNow), allListening, null, cache: false);

        private string StartSpoken(string name, Func<string> text, bool allListening, List<Chan> fixedTargets, bool cache)
        {
            string Refuse(string why)
            {
                AddLog(why);
                return why;
            }

            string blocked = AnnouncementBlocked();
            if (blocked != null)
                return Refuse(blocked);
            SpeechEngine engine = CurrentSpeechEngine(out string noEngine);
            if (engine == null && synthesizeForTest == null)
                return Refuse($"{name}: {noEngine}");
            List<Chan> targets = fixedTargets;
            if (targets == null)
            {
                string why = AnnouncementTargets(allListening, out targets);
                if (why != null)
                    return Refuse(why);
            }
            string words = text();
            if (string.IsNullOrWhiteSpace(words))
                return Refuse($"{name}: there's nothing to say");

            MakeSpeech(engine, words, cache, (samples, error) =>
            {
                if (error != null)
                {
                    AddLog($"{name}: {error}");
                    return;
                }
                string late = AnnouncementBlocked();
                if (late != null)
                {
                    AddLog($"{name} not sent: {late}");
                    return;
                }
                if (!CanSendAnnouncement)
                {
                    AddLog($"{name} not sent: not connected to the FNE");
                    return;
                }
                TimeSpan length = TimeSpan.FromSeconds(samples.Length / (double)VoiceProcessor.SampleRate);
                if (length > txLimit)
                {
                    AddLog($"{name} is {length.TotalSeconds:F0} s, longer than the {txLimit.TotalSeconds:F0} s transmit time limit");
                    return;
                }
                SendAnnouncement(TonePlan.FromAudio(name, samples), targets);
            });
            return null;
        }

        /// <summary>
        /// Runs the engine (on a worker thread), then calls done on the UI thread with the audio or why
        /// there is none. With the self-test's stand-in, all at once.
        /// </summary>
        private void MakeSpeech(SpeechEngine engine, string words, bool cache, Action<short[], string> done)
        {
            string key = SpeechKey(engine, words);
            if (cache && key != null)
                lock (spokenCache)
                    if (spokenCache.TryGetValue(key, out short[] known))
                    {
                        done(known, null);
                        return;
                    }

            short[] Make() => synthesizeForTest != null ? synthesizeForTest(words) : Speech.Synthesize(engine, words);
            void Keep(short[] samples)
            {
                if (cache && key != null)
                    lock (spokenCache)
                        spokenCache[key] = samples;
            }

            if (synthesizeForTest != null)
            {
                short[] samples = null;
                string error = null;
                try { samples = Make(); Keep(samples); }
                catch (ArgumentException ex) { error = ex.Message; }
                done(samples, error);
                return;
            }

            speechBusy = true;
            Task.Run(() =>
            {
                short[] samples = null;
                string error = null;
                try { samples = Make(); Keep(samples); }
                catch (ArgumentException ex) { error = ex.Message; }
                catch (Exception ex) { error = $"the text-to-speech engine failed ({ex.Message})"; }
                PostToUi(() =>
                {
                    speechBusy = false;
                    done(samples, error);
                });
            });
        }

        /*
        ** Hear it: the words through the vocoder on the speakers, without transmitting
        */

        private CancellationTokenSource previewStop;

        /// <summary>True while a preview plays.</summary>
        public bool Previewing => previewStop != null;

        /// <summary>Plays a text as a radio would hear it, on the focused channel's speaker. Null, or why not.</summary>
        public string PreviewSpeech(string text)
        {
            StopPreview();
            if (speechBusy)
                return "the text-to-speech engine is still busy";
            SpeechEngine engine = CurrentSpeechEngine(out string noEngine);
            if (engine == null && synthesizeForTest == null)
                return noEngine;
            if (string.IsNullOrWhiteSpace(text))
                return "there's nothing to say";
            Chan c = chans[cursor];
            MakeSpeech(engine, text, cache: true, (samples, error) =>
            {
                if (error != null)
                {
                    AddLog($"hear it: {error}");
                    return;
                }
                PlayThroughVocoder(c, samples);
            });
            return null;
        }

        /// <summary>The time announcement as a radio would hear it now. Null, or why not.</summary>
        public string PreviewTimeAnnouncement() => PreviewSpeech(Speech.TimeAnnouncementText(TimeAnnouncement, UtcNow));

        public void StopPreview()
        {
            CancellationTokenSource stop = previewStop;
            previewStop = null;
            stop?.Cancel();
        }

        private void PlayThroughVocoder(Chan c, short[] samples)
        {
            IAudioSink sink;
            try
            {
                if (replaySinkForTest != null)
                    sink = replaySinkForTest();
                else if (NoAudio)
                    sink = null;
                else
                    sink = AudioBackend.Current.OpenOutput(OutputFor(c));
            }
            catch (Exception ex)
            {
                AddLog($"hear it: can't play ({ex.Message})");
                return;
            }
            CancellationTokenSource stop = new CancellationTokenSource();
            previewStop = stop;
            bool fast = replaySinkForTest != null;

            void Play()
            {
                try
                {
                    MBEEncoder encoder = new MBEEncoder(MBE_MODE.IMBE_88BIT);
                    MBEDecoder decoder = new MBEDecoder(MBE_MODE.IMBE_88BIT);
                    System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
                    int frames = (samples.Length + 159) / 160;
                    for (int i = 0; i < frames && !stop.IsCancellationRequested; i++)
                    {
                        if (!fast)
                        {
                            int wait = i * 20 - ReplayLeadMs - (int)clock.ElapsedMilliseconds;
                            if (wait > 0 && stop.Token.WaitHandle.WaitOne(wait))
                                break;
                        }
                        short[] frame = new short[160];
                        Array.Copy(samples, i * 160, frame, 0, Math.Min(160, samples.Length - i * 160));
                        byte[] cw = new byte[P25VoiceReceiver.ImbeLength];
                        encoder.encode(frame, cw);
                        short[] heard = new short[P25VoiceReceiver.FrameSamples];
                        decoder.decode(cw, heard);
                        AudioMath.ApplyGain(heard, c.Gain);
                        sink?.Write(heard);
                    }
                    if (!fast && !stop.IsCancellationRequested)
                        stop.Token.WaitHandle.WaitOne(Math.Max(0, frames * 20 + 150 - (int)clock.ElapsedMilliseconds));
                }
                catch (Exception) { /* the player went away */ }
                finally
                {
                    try { sink?.Dispose(); } catch (Exception) { }
                    if (previewStop == stop)
                        previewStop = null;
                    Wake();
                }
            }

            if (fast)
                Play();
            else
                Task.Run(Play);
        }

        /*
        ** The time announcement
        */

        private static readonly TimeSpan TimeWaitLimit = TimeSpan.FromMinutes(3);
        private DateTime? timeSlotDone;      // the slot (UTC start) already played, skipped or passed over
        private DateTime? timeSlotWaiting;   // a slot waiting for quiet channels
        private string timeWaitReason;       // what it's waiting for, for the log if it gives up

        /// <summary>This system's time announcement (a copy: change it with <see cref="SetTimeAnnouncement"/>).</summary>
        public TimeAnnouncementSettings TimeAnnouncement =>
            (settings?.For(system.Name).TimeAnnouncement ?? new TimeAnnouncementSettings()).Copy();

        /// <summary>Saves this system's time announcement. Returns null, or why it can't be used (and nothing is saved).</summary>
        public string SetTimeAnnouncement(TimeAnnouncementSettings t)
        {
            if (t == null || settings == null)
                return "nothing to save";
            if (!Speech.KnownZone(t.TimeZone))
                return $"unknown time zone \"{t.TimeZone}\" (use a name like America/Chicago, or leave it empty for this computer's)";
            if (!Speech.Intervals.Contains(t.EveryMinutes))
                return $"it can play every {string.Join(", ", Speech.Intervals.Where(i => i > 0))} minutes, or not by itself";
            t = t.Copy();
            t.TimeZone = string.IsNullOrWhiteSpace(t.TimeZone) ? null : t.TimeZone.Trim();
            t.ZoneName = string.IsNullOrWhiteSpace(t.ZoneName) ? null : t.ZoneName.Trim();
            t.Channel = string.IsNullOrWhiteSpace(t.Channel) ? null : t.Channel;
            SystemSettings ss = settings.For(system.Name);
            TimeAnnouncementSettings was = ss.TimeAnnouncement;
            ss.TimeAnnouncement = t;
            MarkDirty();
            bool scheduleChanged = was == null || was.EveryMinutes != t.EveryMinutes || was.Channel != t.Channel || was.TimeZone != t.TimeZone;
            if (scheduleChanged)
            {
                timeSlotDone = null;
                timeSlotWaiting = null;
                AddLog(t.EveryMinutes > 0 ? $"time announcement: every {t.EveryMinutes} minutes on {t.Channel ?? "every listening channel"}" : "time announcement: not played by itself");
            }
            return null;
        }

        /// <summary>The time announcement's words as people read them now ("The time is 2:20 PM PST").</summary>
        public string TimeAnnouncementPreview => Speech.TimeAnnouncementPreview(TimeAnnouncement, UtcNow);

        /// <summary>When the next scheduled time announcement is due (UTC), or null when it isn't scheduled.</summary>
        public DateTime? NextTimeAnnouncement
        {
            get
            {
                TimeAnnouncementSettings t = settings?.For(system.Name).TimeAnnouncement;
                if (t == null || t.EveryMinutes <= 0)
                    return null;
                if (timeSlotWaiting != null)
                    return timeSlotWaiting;
                return SlotStart(t, UtcNow).AddMinutes(t.EveryMinutes);
            }
        }

        /// <summary>The start (UTC) of the schedule's slot that holds this moment, on the zone's clock.</summary>
        private static DateTime SlotStart(TimeAnnouncementSettings t, DateTime utc)
        {
            TimeZoneInfo zone = Speech.Zone(t.TimeZone);
            DateTime local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);
            int minute = local.Hour * 60 + local.Minute;
            DateTime slotLocal = local.Date.AddMinutes(minute - minute % t.EveryMinutes);
            return utc - (local - slotLocal);
        }

        /// <summary>The scheduled announcement's channels, or why there are none.</summary>
        private string ScheduledTargets(TimeAnnouncementSettings t, out List<Chan> targets)
        {
            targets = null;
            if (t.Channel == null)
            {
                targets = TxCapableListening();
                return targets.Count == 0 ? "no listening channels" : null;
            }
            Chan c = chans.FirstOrDefault(x => x.Cfg.Name == t.Channel);
            if (c == null)
                return $"no channel called {t.Channel} in this system";
            if (!c.IsP25 || c.Cfg.RxOnly)
                return $"{c.Cfg.Name} can't transmit";
            if (!c.Selected)
                return $"not listening to {c.Cfg.Name} (it has to be listening, so the console knows when it's quiet)";
            targets = new List<Chan> { c };
            return null;
        }

        private static bool ChannelBusy(Chan c) => (c.Rx?.InCall ?? false) || c.Active;

        private void TimeAnnouncementHousekeeping()
        {
            TimeAnnouncementSettings t = settings?.For(system.Name).TimeAnnouncement;
            if (t == null || t.EveryMinutes <= 0)
            {
                timeSlotWaiting = null;
                return;
            }
            DateTime now = UtcNow;
            DateTime slot = SlotStart(t, now);
            if (timeSlotDone == null)
                timeSlotDone = now - slot > TimeSpan.FromSeconds(30) ? slot : slot.AddMinutes(-t.EveryMinutes);   // started mid-slot: wait for the next
            if (slot > timeSlotDone && timeSlotWaiting == null)
            {
                timeSlotDone = slot;
                timeSlotWaiting = slot;
                timeWaitReason = null;
            }
            if (timeSlotWaiting is not DateTime waiting)
                return;

            if (now - waiting > TimeWaitLimit)
            {
                timeSlotWaiting = null;
                AddLog($"time announcement skipped after waiting {TimeWaitLimit.TotalMinutes:F0} minutes: {timeWaitReason ?? "the console was busy"}");
                return;
            }
            string blocked = AnnouncementBlocked();
            if (blocked != null || replay != null)
            {
                timeWaitReason = blocked ?? "a call was being replayed";
                return;
            }
            if (!CanSendAnnouncement)
            {
                timeWaitReason = "not connected to the FNE";
                return;
            }
            string why = ScheduledTargets(t, out List<Chan> targets);
            if (why != null)
            {
                timeSlotWaiting = null;
                AddLog($"time announcement skipped: {why}");
                return;
            }
            if (targets.Any(ChannelBusy) || targets.Any(c => toneChans.Contains(c)))
            {
                timeWaitReason = $"{string.Join(", ", targets.Where(ChannelBusy).Select(c => c.Cfg.Name))} stayed busy";
                return;    // someone's talking: wait for them
            }

            timeSlotWaiting = null;
            StartSpoken("time announcement", () => Speech.TimeAnnouncementText(t, UtcNow), false, targets, cache: false);
        }
    } // public partial class DispatchConsole
}
