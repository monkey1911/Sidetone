// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks.
*
* The terminal console's tone presets box (g): the tone and DTMF presets saved in Settings (made and edited
* in the graphical console's Tones box, or settings.json). Up and down pick one, Enter sends it on every
* listening channel, as an alert tone.
*/

using Sidetone.Core;

namespace Sidetone.Cli
{
    public sealed partial class Tui
    {
        private sealed class TonesDialog
        {
            public int Row;
            public string Message = string.Empty;
        }

        private TonesDialog tonesDialog;

        /// <summary>True while the tone presets box is open (self-test hook).</summary>
        internal bool TonesDialogOpen => tonesDialog != null;

        /// <summary>Both kinds, tone presets first: (name, summary, send).</summary>
        private List<(string Name, string Summary, Func<string> Send)> PresetRows() =>
            TonePresetList.Select(t => (t.Name, t.Summary, (Func<string>)(() => SendTonePreset(t))))
                .Concat(DtmfPresetList.Select(d => (d.Name, $"DTMF {d.Digits}", (Func<string>)(() => SendDtmfPreset(d)))))
                .ToList();

        private void OpenTones()
        {
            pageDialog = null;
            radioDialog = null;
            zoneDialog = null;
            keyDialog = null;
            outputsDialog = null;
            announceDialog = null;
            streamsDialog = null;
            tonesDialog = new TonesDialog();
        }

        private void TonesInput(InputEvent e)
        {
            TonesDialog d = tonesDialog;
            var rows = PresetRows();
            switch (e.Key)
            {
                case InputKey.Escape:
                    tonesDialog = null;
                    break;
                case InputKey.Up:
                    d.Row = Math.Max(0, d.Row - 1);
                    break;
                case InputKey.Down:
                case InputKey.Tab:
                    d.Row = Math.Max(0, Math.Min(rows.Count - 1, d.Row + 1));
                    break;
                case InputKey.Enter:
                case InputKey.Space:
                    if (d.Row < rows.Count)
                    {
                        string why = rows[d.Row].Send();
                        if (why == null)
                            tonesDialog = null;     // sending: Esc stops it, as an alert tone
                        else
                            d.Message = why;
                    }
                    break;
                case InputKey.CtrlC:
                    quit = true;
                    break;
                case InputKey.Char when e.Char == 'g' || e.Char == 'G' || e.Char == 'q':
                    tonesDialog = null;
                    break;
            }
        }

        private void DrawTonesDialog(int width, int height)
        {
            TonesDialog d = tonesDialog;
            if (d == null)
                return;

            screen.OnClick(new Rect(0, 0, width, height), () => { });   // swallow clicks outside

            var rows = PresetRows();
            d.Row = Math.Clamp(d.Row, 0, Math.Max(0, rows.Count - 1));
            int w = Math.Min(78, width - 4);
            int listRows = Math.Max(1, Math.Min(Math.Max(1, rows.Count), height - 10));
            int h = listRows + 7;
            Rect r = new Rect((width - w) / 2, Math.Max(0, (height - h) / 2), w, h);
            Style body = Style.Of(Ansi.BrightWhite, Ansi.Black);
            screen.Fill(r, body);
            screen.Box(r, Style.Of(Ansi.BrightCyan, Ansi.Black, bold: true), heavy: true);
            screen.Text(r.X + 2, r.Y, " Tone presets ", Style.Of(Ansi.BrightCyan, Ansi.Black, bold: true));
            int ix = r.X + 3, iw = w - 6;
            if (rows.Count == 0)
                screen.Text(ix, r.Y + 2, "No presets yet: make them in the graphical console's Tones… box.", Style.Of(Ansi.Grey, Ansi.Black), iw);

            int top = Math.Clamp(d.Row - listRows + 1, 0, Math.Max(0, rows.Count - listRows));
            for (int i = 0; i < listRows && top + i < rows.Count; i++)
            {
                int row = top + i;
                bool sel = row == d.Row;
                Style st = sel ? Style.Of(Ansi.Black, Ansi.BrightCyan, bold: true) : body;
                Rect line = new Rect(ix, r.Y + 2 + i, iw, 1);
                screen.Fill(line, st);
                int used = screen.Text(ix, line.Y, $"{rows[row].Name}  ", st, iw / 2);
                screen.Text(ix + used, line.Y, rows[row].Summary, sel ? st : Style.Of(Ansi.Grey, Ansi.Black), iw - used);
                int captured = row;
                screen.OnClick(line, () => { d.Row = captured; TonesInput(new InputEvent(InputKind.Key, InputKey.Enter)); });
            }

            if (d.Message.Length > 0)
                screen.Text(ix, r.Y + h - 4, d.Message, Style.Of(Ansi.BrightRed, Ansi.Black), iw);
            screen.Text(ix, r.Y + h - 3, "↑/↓: preset  Enter or click: send on the focused channel  Esc: close", Style.Of(Ansi.Grey, Ansi.Black), iw);
            Style closeStyle = Style.Of(Ansi.Black, Ansi.BrightCyan, bold: true);
            Rect close = new Rect(ix, r.Y + h - 2, 9, 1);
            screen.Fill(close, closeStyle);
            screen.Text(close.X + 2, close.Y, "CLOSE", closeStyle);
            screen.OnClick(close, () => tonesDialog = null);
        }
    } // public sealed partial class Tui
}
