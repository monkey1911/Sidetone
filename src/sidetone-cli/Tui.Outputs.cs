// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks.
*
* The terminal console's outputs box (o): which audio output each tab and each channel plays to.
* Up and down pick a row, left and right go through the outputs; a change takes effect at once.
*/

using Sidetone.Core;

namespace Sidetone.Cli
{
    public sealed partial class Tui
    {
        private sealed class OutputRow
        {
            public ConsoleZone Zone;        // a tab's row, or the tab a channel is on
            public ConsoleChannel Chan;     // a channel's row, or null
        }

        private sealed class OutputsDialog
        {
            public readonly List<OutputRow> Rows = new List<OutputRow>();
            public List<AudioDevice> Devices = new List<AudioDevice>();
            public int Row;
            public string Note;
        }

        private OutputsDialog outputsDialog;

        /// <summary>True while the outputs box is open (self-test hook).</summary>
        internal bool OutputsDialogOpen => outputsDialog != null;

        private void OpenOutputs()
        {
            pageDialog = null;
            radioDialog = null;
            zoneDialog = null;
            keyDialog = null;
            announceDialog = null;
            streamsDialog = null;
            tonesDialog = null;
            OutputsDialog d = new OutputsDialog();
            try { d.Devices = (deviceQueryForTest ?? (() => AudioDeviceList.Query()))().Outputs; }
            catch (Exception ex) { d.Note = $"can't list the outputs ({ex.Message}); only saved ones are shown"; }
            foreach (ConsoleZone z in VisibleZones)
            {
                if (zones.Count > 1)
                    d.Rows.Add(new OutputRow { Zone = z });
                foreach (ConsoleChannel c in z.chans)
                    d.Rows.Add(new OutputRow { Zone = z, Chan = c });
            }
            d.Row = Math.Max(0, d.Rows.FindIndex(r => r.Chan == chans[cursor]));
            outputsDialog = d;
        }

        /// <summary>The choices on a row: follow (null), then each connected output, then the saved one if it isn't connected.</summary>
        private static List<string> OutputChoices(OutputsDialog d, OutputRow row)
        {
            List<string> choices = new List<string> { null };
            choices.AddRange(d.Devices.Select(x => x.Name));
            string current = row.Chan != null ? row.Chan.Output : row.Zone.Output;
            if (current != null && !choices.Contains(current))
                choices.Add(current);
            return choices;
        }

        private void StepOutput(int row, int delta)
        {
            OutputsDialog d = outputsDialog;
            if (d == null || row < 0 || row >= d.Rows.Count)
                return;
            d.Row = row;
            OutputRow r = d.Rows[row];
            List<string> choices = OutputChoices(d, r);
            string current = r.Chan != null ? r.Chan.Output : r.Zone.Output;
            int at = Math.Max(0, choices.IndexOf(current));
            string next = choices[((at + delta) % choices.Count + choices.Count) % choices.Count];
            if (r.Chan != null)
                SetChannelOutput(r.Chan, next);
            else
                SetZoneOutput(r.Zone, next);
        }

        private void OutputsInput(InputEvent e)
        {
            OutputsDialog d = outputsDialog;
            switch (e.Key)
            {
                case InputKey.Escape:
                case InputKey.Enter:
                    outputsDialog = null;
                    break;
                case InputKey.Up:
                    d.Row = Math.Max(0, d.Row - 1);
                    break;
                case InputKey.Down:
                case InputKey.Tab:
                    d.Row = Math.Min(d.Rows.Count - 1, d.Row + 1);
                    break;
                case InputKey.Right:
                case InputKey.Space:
                    StepOutput(d.Row, 1);
                    break;
                case InputKey.Left:
                    StepOutput(d.Row, -1);
                    break;
                case InputKey.CtrlC:
                    quit = true;
                    break;
                case InputKey.Char when e.Char == 'o' || e.Char == 'O' || e.Char == 'q':
                    outputsDialog = null;
                    break;
            }
        }

        private string OutputLabel(OutputsDialog d, string name, string inherit) =>
            name == null ? inherit : d.Devices.FirstOrDefault(x => x.Name == name)?.Label ?? $"{name} (not connected)";

        private void DrawOutputsDialog(int width, int height)
        {
            OutputsDialog d = outputsDialog;
            if (d == null)
                return;

            screen.OnClick(new Rect(0, 0, width, height), () => { });   // swallow clicks outside

            int w = Math.Min(78, width - 4);
            int listRows = Math.Max(1, Math.Min(d.Rows.Count, height - 11));
            int h = listRows + 9;
            Rect r = new Rect((width - w) / 2, Math.Max(0, (height - h) / 2), w, h);
            Style body = Style.Of(Ansi.BrightWhite, Ansi.Black);
            screen.Fill(r, body);
            screen.Box(r, Style.Of(Ansi.BrightCyan, Ansi.Black, bold: true), heavy: true);
            screen.Text(r.X + 2, r.Y, " Audio outputs ", Style.Of(Ansi.BrightCyan, Ansi.Black, bold: true));
            int ix = r.X + 3, iw = w - 6;
            string mainName = Audio?.OutputTarget;
            screen.Text(ix, r.Y + 1, $"Main output: {OutputLabel(d, mainName, "the system default")}", Style.Of(Ansi.White, Ansi.Black), iw);
            screen.Text(ix, r.Y + 2, "A channel plays to its own output, else its tab's, else the main one.", Style.Of(Ansi.Grey, Ansi.Black), iw);

            int top = Math.Clamp(d.Row - listRows + 1, 0, Math.Max(0, d.Rows.Count - listRows));
            int nameW = Math.Min(28, iw / 2);
            for (int i = 0; i < listRows && top + i < d.Rows.Count; i++)
            {
                int row = top + i;
                OutputRow o = d.Rows[row];
                bool sel = row == d.Row;
                Style st = sel ? Style.Of(Ansi.Black, Ansi.BrightCyan, bold: true) : o.Chan == null ? Style.Of(Ansi.BrightWhite, Ansi.Black, bold: true) : body;
                Rect line = new Rect(ix, r.Y + 4 + i, iw, 1);
                screen.Fill(line, st);
                string label = o.Chan == null ? $"Tab {o.Zone.Name}" : (zones.Count > 1 ? "  " : string.Empty) + o.Chan.Cfg.Name;
                screen.Text(ix, line.Y, label, st, nameW);
                string inherit = o.Chan == null || zones.Count == 1 ? "main output" : "same as tab";
                string value = OutputLabel(d, o.Chan != null ? o.Chan.Output : o.Zone.Output, inherit);
                screen.Text(ix + nameW + 1, line.Y, $"◀ {value} ▶", st, iw - nameW - 1);
                int captured = row;
                screen.OnClick(new Rect(ix + nameW + 1, line.Y, 2, 1), () => StepOutput(captured, -1));
                screen.OnClick(new Rect(ix + nameW + 3, line.Y, iw - nameW - 3, 1), () => StepOutput(captured, 1));
            }

            int msgY = r.Y + 5 + listRows;
            screen.Text(ix, msgY, d.Note ?? "↑/↓: row  ←/→ or click: output  Esc: close", Style.Of(d.Note != null ? Ansi.BrightYellow : Ansi.Grey, Ansi.Black), iw);
            Style closeStyle = Style.Of(Ansi.Black, Ansi.BrightCyan, bold: true);
            Rect close = new Rect(ix, r.Y + h - 2, 9, 1);
            screen.Fill(close, closeStyle);
            screen.Text(close.X + 2, close.Y, "CLOSE", closeStyle);
            screen.OnClick(close, () => outputsDialog = null);
        }
    } // public sealed partial class Tui
}
