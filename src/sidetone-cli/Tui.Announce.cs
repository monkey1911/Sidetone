// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks.
*
* The terminal console's announcements box (n): WAV files and spoken texts to play onto talkgroups,
* and the time announcement. Enter plays the highlighted one on the focused card, Space on every
* listening channel, h hears a spoken one (through the vocoder, on the speakers, not sent); a adds
* a file by its path, t a text to say, r takes one off the list; e opens the speech and time
* settings (engine, voice, the time announcement's words, clock, zone and schedule). The lists and
* settings are the same as the graphical console's.
*/

using Sidetone.Core;

namespace Sidetone.Cli
{
    public sealed partial class Tui
    {
        private enum AnnounceKind { File, Spoken, Time }

        private sealed class AnnounceDialog
        {
            public int Row;
            public string Message;
            public bool MessageIsError;
            public System.Text.StringBuilder Path;      // non-null while a file path is being typed
            public System.Text.StringBuilder Words;     // non-null while a text to say is being typed
            public TimeForm Form;                       // non-null while the speech and time settings are open
        }

        /// <summary>The speech and time settings, being edited (saved when the form closes).</summary>
        private sealed class TimeForm
        {
            public int Field;
            public string Engine;           // null (automatic), "piper", "espeak-ng"
            public string Voice;            // a Piper model, or null for the first found
            public List<string> Voices = new List<string>();
            public TimeAnnouncementSettings Time;
            public List<string> Channels = new List<string>();   // names that can take it (null first: every listening channel)
            public string Error;
        }

        private const int FormFields = 10;

        private AnnounceDialog announceDialog;

        /// <summary>True while the announcements box is open, and its message (self-test hooks).</summary>
        internal bool AnnounceDialogOpen => announceDialog != null;
        internal string AnnounceDialogMessage => announceDialog?.Message;
        internal bool AnnounceFormOpen => announceDialog?.Form != null;
        internal string AnnounceFormError => announceDialog?.Form?.Error;
        /// <summary>The last frame drawn, as plain text (self-test).</summary>
        internal string ScreenText => screen.PlainText();

        private List<(AnnounceKind Kind, string Value)> AnnounceRows()
        {
            List<(AnnounceKind, string)> rows = Announcements.Select(f => (AnnounceKind.File, f)).ToList();
            rows.AddRange(SpokenAnnouncements.Select(t => (AnnounceKind.Spoken, t)));
            rows.Add((AnnounceKind.Time, (string)null));
            return rows;
        }

        private void OpenAnnouncements()
        {
            pageDialog = null;
            radioDialog = null;
            zoneDialog = null;
            keyDialog = null;
            outputsDialog = null;
            RefreshSpeechEngine();   // something may have been installed since
            announceDialog = new AnnounceDialog();
        }

        private void Say(AnnounceDialog d, string message, bool error = true)
        {
            d.Message = message;
            d.MessageIsError = error;
        }

        private void AnnounceInput(InputEvent e)
        {
            AnnounceDialog d = announceDialog;
            if (d.Form != null)
            {
                FormInput(d, e);
                return;
            }
            if (d.Path != null || d.Words != null)
            {
                TypingInput(d, e);
                return;
            }

            List<(AnnounceKind Kind, string Value)> rows = AnnounceRows();
            d.Row = Math.Clamp(d.Row, 0, rows.Count - 1);
            (AnnounceKind kind, string value) = rows[d.Row];
            switch (e.Key)
            {
                case InputKey.Escape:
                    announceDialog = null;
                    break;
                case InputKey.Up:
                    d.Row = Math.Max(0, d.Row - 1);
                    d.Message = null;
                    break;
                case InputKey.Down:
                case InputKey.Tab:
                    d.Row = Math.Min(rows.Count - 1, d.Row + 1);
                    d.Message = null;
                    break;
                case InputKey.Enter:
                case InputKey.Space:
                    bool all = e.Key == InputKey.Space;
                    string result = kind == AnnounceKind.File ? PlayAnnouncement(value, all)
                                  : kind == AnnounceKind.Spoken ? SpeakAnnouncement(value, all)
                                  : PlayTimeAnnouncement(all);
                    if (result == null)
                        announceDialog = null;
                    else
                        Say(d, result);
                    break;
                case InputKey.CtrlC:
                    quit = true;
                    break;
                case InputKey.Char when e.Char == 'a' || e.Char == 'A':
                    d.Path = new System.Text.StringBuilder();
                    d.Message = null;
                    break;
                case InputKey.Char when e.Char == 't' || e.Char == 'T':
                    d.Words = new System.Text.StringBuilder();
                    d.Message = null;
                    break;
                case InputKey.Char when e.Char == 'h' || e.Char == 'H':
                    if (kind == AnnounceKind.File)
                    {
                        Say(d, "h hears spoken announcements and the time; a file plays as it is");
                        break;
                    }
                    string hear = kind == AnnounceKind.Spoken ? PreviewSpeech(value) : PreviewTimeAnnouncement();
                    Say(d, hear ?? "playing it on the speakers as a radio would hear it (Esc in the main screen stops it)", hear != null);
                    break;
                case InputKey.Char when (e.Char == 'r' || e.Char == 'R') && kind != AnnounceKind.Time:
                    if (kind == AnnounceKind.File)
                        RemoveAnnouncement(value);
                    else
                        RemoveSpokenAnnouncement(value);
                    d.Row = Math.Clamp(d.Row, 0, AnnounceRows().Count - 1);
                    break;
                case InputKey.Char when e.Char == 'e' || e.Char == 'E' || e.Char == 's' || e.Char == 'S':
                    d.Form = NewForm();
                    d.Message = null;
                    break;
                case InputKey.Char when e.Char == 'n' || e.Char == 'N' || e.Char == 'q':
                    announceDialog = null;
                    break;
            }
        }

        private void TypingInput(AnnounceDialog d, InputEvent e)
        {
            System.Text.StringBuilder typed = d.Path ?? d.Words;
            switch (e.Key)
            {
                case InputKey.Escape: d.Path = null; d.Words = null; d.Message = null; break;
                case InputKey.Backspace: if (typed.Length > 0) typed.Length--; break;
                case InputKey.Space: typed.Append(' '); break;
                case InputKey.Char: typed.Append(e.Char); break;
                case InputKey.CtrlC: quit = true; break;
                case InputKey.Enter:
                    if (d.Path != null)
                    {
                        string path = d.Path.ToString().Trim();
                        if (path.StartsWith("~/", StringComparison.Ordinal))
                            path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Substring(2));
                        string why = AddAnnouncement(path);
                        Say(d, why);
                        if (why == null)
                        {
                            d.Path = null;
                            d.Row = Announcements.Count - 1;
                        }
                    }
                    else
                    {
                        string text = d.Words.ToString().Trim();
                        string why = AddSpokenAnnouncement(text);
                        Say(d, why);
                        if (why == null)
                        {
                            d.Words = null;
                            d.Row = Announcements.Count + SpokenAnnouncements.ToList().IndexOf(text);
                        }
                    }
                    break;
            }
        }

        /*
        ** The speech and time settings
        */

        private TimeForm NewForm()
        {
            TimeForm f = new TimeForm
            {
                Engine = Settings?.SpeechEngine,
                Voice = Settings?.SpeechVoice,
                Voices = Speech.PiperVoices(),
                Time = TimeAnnouncement
            };
            f.Channels.Add(null);
            f.Channels.AddRange(chans.Where(c => c.IsP25 && !c.Cfg.RxOnly).Select(c => c.Cfg.Name).Distinct());
            if (f.Time.Channel != null && !f.Channels.Contains(f.Time.Channel))
                f.Channels.Add(f.Time.Channel);
            return f;
        }

        private static T Step<T>(IList<T> choices, T current, int delta)
        {
            int at = Math.Max(0, choices.IndexOf(current));
            return choices[((at + delta) % choices.Count + choices.Count) % choices.Count];
        }

        /// <summary>The text being typed in a field, or null when the field is a choice.</summary>
        private static string FieldText(TimeForm f, int field) => field switch
        {
            2 => f.Time.Before ?? string.Empty,
            3 => f.Time.After ?? string.Empty,
            5 => f.Time.TimeZone ?? string.Empty,
            7 => f.Time.ZoneName ?? string.Empty,
            _ => null
        };

        private static void SetFieldText(TimeForm f, int field, string text)
        {
            switch (field)
            {
                case 2: f.Time.Before = text; break;
                case 3: f.Time.After = text; break;
                case 5: f.Time.TimeZone = text; break;
                case 7: f.Time.ZoneName = text; break;
            }
        }

        private static void StepField(TimeForm f, int delta)
        {
            switch (f.Field)
            {
                case 0: f.Engine = Step(new List<string> { null, Speech.PiperSetting, Speech.EspeakSetting }, f.Engine, delta); break;
                case 1:
                    List<string> voices = new List<string> { null };
                    voices.AddRange(f.Voices);
                    f.Voice = Step(voices, f.Voices.Contains(f.Voice) ? f.Voice : null, delta);
                    break;
                case 4: f.Time.Hour24 = !f.Time.Hour24; break;
                case 6: f.Time.SayZone = !f.Time.SayZone; break;
                case 8: f.Time.EveryMinutes = Step(Speech.Intervals.ToList(), f.Time.EveryMinutes, delta); break;
                case 9: f.Time.Channel = Step(f.Channels, f.Time.Channel, delta); break;
            }
            f.Error = null;
        }

        /// <summary>Saves the form; false (with the reason shown) when something in it can't be used.</summary>
        private bool SaveForm(TimeForm f)
        {
            string why = SetTimeAnnouncement(f.Time);
            if (why != null)
            {
                f.Error = char.ToUpperInvariant(why[0]) + why.Substring(1) + ".";
                f.Field = 5;
                return false;
            }
            SetSpeechEngine(f.Engine, f.Voices.Contains(f.Voice) ? f.Voice : null);
            return true;
        }

        private void FormInput(AnnounceDialog d, InputEvent e)
        {
            TimeForm f = d.Form;
            string text = FieldText(f, f.Field);
            switch (e.Key)
            {
                case InputKey.Escape:
                case InputKey.Enter:
                    if (SaveForm(f))
                    {
                        d.Form = null;
                        Say(d, "saved", error: false);
                    }
                    break;
                case InputKey.Up:
                    f.Field = (f.Field + FormFields - 1) % FormFields;
                    break;
                case InputKey.Down:
                case InputKey.Tab:
                    f.Field = (f.Field + 1) % FormFields;
                    break;
                case InputKey.Left:
                    if (text == null) StepField(f, -1);
                    break;
                case InputKey.Right:
                    if (text == null) StepField(f, 1);
                    break;
                case InputKey.Space:
                    if (text == null) StepField(f, 1);
                    else SetFieldText(f, f.Field, text + " ");
                    break;
                case InputKey.Backspace:
                    if (!string.IsNullOrEmpty(text)) SetFieldText(f, f.Field, text.Substring(0, text.Length - 1));
                    f.Error = null;
                    break;
                case InputKey.Char:
                    if (text != null) SetFieldText(f, f.Field, text + e.Char);
                    f.Error = null;
                    break;
                case InputKey.CtrlC:
                    quit = true;
                    break;
            }
        }

        private string FormValue(TimeForm f, int field) => field switch
        {
            0 => f.Engine == null ? "Automatic (Piper if it has a voice, else espeak-ng)" : f.Engine == Speech.PiperSetting ? "Piper" : "espeak-ng",
            1 => f.Voices.Count == 0 ? "(no Piper voices found)" : f.Voices.Contains(f.Voice) ? Speech.VoiceName(f.Voice) : $"First found ({Speech.VoiceName(f.Voices[0])})",
            4 => f.Time.Hour24 ? "24-hour (fourteen twenty)" : "12-hour (two twenty P M)",
            6 => f.Time.SayZone ? "Yes" : "No",
            7 => f.Time.ZoneName ?? string.Empty,
            8 => f.Time.EveryMinutes == 0 ? "Off (only when played)" : $"Every {f.Time.EveryMinutes} minutes, on the clock",
            9 => f.Time.Channel ?? "Every listening channel",
            _ => FieldText(f, field)
        };

        private static readonly string[] FormLabels =
        {
            "Engine", "Piper voice", "Before time", "After time", "Clock", "Time zone", "Say zone", "Zone said as", "By itself", "On channel"
        };

        private void DrawTimeForm(TimeForm f, int ix, int y, int iw)
        {
            for (int i = 0; i < FormFields; i++)
            {
                bool active = i == f.Field;
                bool typed = FieldText(f, i) != null;
                Style label = Style.Of(active ? Ansi.BrightWhite : Ansi.White, Ansi.Black, bold: active);
                Style box = active ? Style.Of(Ansi.Black, Ansi.BrightWhite, bold: true) : Style.Of(Ansi.White, Ansi.Grey);
                screen.Text(ix, y + i, FormLabels[i], label, 13);
                Rect r = new Rect(ix + 14, y + i, iw - 14, 1);
                screen.Fill(r, box);
                string value = FormValue(f, i);
                string hint = i == 5 && string.IsNullOrEmpty(value) ? $"(this computer's: {TimeZoneInfo.Local.Id})"
                            : i == 7 && string.IsNullOrEmpty(value) ? "(its abbreviation, letter by letter)"
                            : null;
                string shown = typed ? value + (active ? "▏" : string.Empty) : $"◀ {value} ▶";
                screen.Text(r.X + 1, r.Y, hint != null && !active ? hint : shown, box, r.W - 1);
                int captured = i;
                screen.OnClick(r, () => { f.Field = captured; if (!typed) StepField(f, 1); });
            }
            string saying = Speech.TimeAnnouncementPreview(f.Time, UtcNow);
            screen.Text(ix, y + FormFields + 1, $"Will say: {saying}", Style.Of(Ansi.BrightGreen, Ansi.Black), iw);
            if (f.Error != null)
                screen.Text(ix, y + FormFields + 2, f.Error, Style.Of(Ansi.BrightRed, Ansi.Black, bold: true), iw);
        }

        /*
        ** Drawing
        */

        private string RowName((AnnounceKind Kind, string Value) row) => row.Kind switch
        {
            AnnounceKind.File => AnnouncementName(row.Value),
            AnnounceKind.Spoken => $"\"{SpokenName(row.Value)}\"",
            _ => $"Time: {TimeAnnouncementPreview}"
        };

        private string RowDetail((AnnounceKind Kind, string Value) row)
        {
            switch (row.Kind)
            {
                case AnnounceKind.File:
                    TimeSpan? length = AnnouncementLength(row.Value);
                    return length == null ? "can't read" : $"{length.Value.TotalSeconds:F0} s";
                case AnnounceKind.Spoken:
                    TimeSpan? said = SpokenLength(row.Value);
                    return said == null ? "spoken" : $"spoken, {said.Value.TotalSeconds:F0} s";
                default:
                    DateTime? next = NextTimeAnnouncement;
                    return next == null ? "when played" : $"next {TimeZoneInfo.ConvertTimeFromUtc(next.Value, Speech.Zone(TimeAnnouncement.TimeZone)):HH:mm}";
            }
        }

        private void DrawAnnounceDialog(int width, int height)
        {
            AnnounceDialog d = announceDialog;
            if (d == null)
                return;

            screen.OnClick(new Rect(0, 0, width, height), () => { });   // swallow clicks outside

            List<(AnnounceKind Kind, string Value)> rows = AnnounceRows();
            int w = Math.Min(84, width - 4);
            int listRows = d.Form != null ? FormFields + 3 : Math.Max(1, Math.Min(rows.Count, height - 14));
            int h = d.Form != null ? FormFields + 9 : listRows + 11;
            Rect r = new Rect((width - w) / 2, Math.Max(0, (height - h) / 2), w, h);
            Style body = Style.Of(Ansi.BrightWhite, Ansi.Black);
            screen.Fill(r, body);
            screen.Box(r, Style.Of(Ansi.BrightCyan, Ansi.Black, bold: true), heavy: true);
            screen.Text(r.X + 2, r.Y, d.Form != null ? " Speech and time announcement " : " Announcements ", Style.Of(Ansi.BrightCyan, Ansi.Black, bold: true));
            int ix = r.X + 3, iw = w - 6;
            CurrentSpeechEngine(out string noEngine);
            screen.Text(ix, r.Y + 1, SpeechStatus, Style.Of(noEngine == null ? Ansi.Grey : Ansi.BrightYellow, Ansi.Black), iw);
            screen.Text(ix, r.Y + 2, $"Focused card: {chans[cursor].Cfg.Name}", Style.Of(Ansi.Grey, Ansi.Black), iw);

            if (d.Form != null)
            {
                DrawTimeForm(d.Form, ix, r.Y + 4, iw);
                screen.Text(ix, r.Y + h - 2, "↑/↓: field  ←/→: choose  type in text fields  Enter or Esc: save and go back", Style.Of(Ansi.Grey, Ansi.Black), iw);
                return;
            }

            int top = Math.Clamp(d.Row - listRows + 1, 0, Math.Max(0, rows.Count - listRows));
            for (int i = 0; i < listRows && top + i < rows.Count; i++)
            {
                int row = top + i;
                bool sel = row == d.Row;
                Style st = sel ? Style.Of(Ansi.Black, Ansi.BrightCyan, bold: true) : rows[row].Kind == AnnounceKind.Time ? Style.Of(Ansi.BrightGreen, Ansi.Black) : body;
                Rect line = new Rect(ix, r.Y + 4 + i, iw, 1);
                screen.Fill(line, st);
                screen.Text(ix, line.Y, RowName(rows[row]), st, iw - 18);
                screen.TextRight(ix + iw - 1, line.Y, RowDetail(rows[row]), st);
                int captured = row;
                screen.OnClick(line, () => { d.Row = captured; d.Message = null; });
            }

            int y = r.Y + 5 + listRows;
            if (d.Path != null || d.Words != null)
            {
                string label = d.Path != null ? "Add file" : "Say";
                screen.Text(ix, y, label, Style.Of(Ansi.BrightWhite, Ansi.Black, bold: true), 9);
                Rect box = new Rect(ix + 10, y, iw - 10, 1);
                Style boxStyle = Style.Of(Ansi.Black, Ansi.BrightWhite, bold: true);
                screen.Fill(box, boxStyle);
                string typed = (d.Path ?? d.Words).ToString();
                int room = box.W - 2;
                screen.Text(box.X + 1, y, (typed.Length > room ? typed.Substring(typed.Length - room) : typed) + "▏", boxStyle, box.W - 1);
                y += 2;
            }
            if (d.Message != null)
                screen.Text(ix, y, d.Message, Style.Of(d.MessageIsError ? Ansi.BrightRed : Ansi.BrightGreen, Ansi.Black, bold: true), iw);
            Style helpStyle = Style.Of(Ansi.Grey, Ansi.Black);
            if (d.Path != null || d.Words != null)
                screen.Text(ix, r.Y + h - 2, d.Path != null ? "type the WAV file's path  Enter: add  Esc: cancel" : "type what to say  Enter: add  Esc: cancel", helpStyle, iw);
            else
            {
                screen.Text(ix, r.Y + h - 3, "Enter: play on focused card  Space: on all listening  h: hear it (not sent)", helpStyle, iw);
                screen.Text(ix, r.Y + h - 2, "a: add WAV file  t: add text  r: remove  e: speech and time settings  Esc: close", helpStyle, iw);
            }
        }
    } // public sealed partial class Tui
}
