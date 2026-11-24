// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks.
*
* The terminal console's web streams box (s): the tab's web streams, what each is doing and its volume.
* Up and down pick one, Enter or Space starts or stops it, + and - change its volume.
*/

using Sidetone.Core;

namespace Sidetone.Cli
{
    public sealed partial class Tui
    {
        private sealed class StreamsDialog
        {
            public int Row;
        }

        private StreamsDialog streamsDialog;

        /// <summary>True while the web streams box is open (self-test hook).</summary>
        internal bool StreamsDialogOpen => streamsDialog != null;

        private void OpenStreams()
        {
            pageDialog = null;
            radioDialog = null;
            zoneDialog = null;
            keyDialog = null;
            outputsDialog = null;
            announceDialog = null;
            tonesDialog = null;
            streamsDialog = new StreamsDialog();
        }

        private void StreamsInput(InputEvent e)
        {
            StreamsDialog d = streamsDialog;
            IReadOnlyList<ConsoleStream> list = StreamsIn(CurrentZone);
            ConsoleStream s = d.Row < list.Count ? list[d.Row] : null;
            switch (e.Key)
            {
                case InputKey.Escape:
                    streamsDialog = null;
                    break;
                case InputKey.Up:
                    d.Row = Math.Max(0, d.Row - 1);
                    break;
                case InputKey.Down:
                case InputKey.Tab:
                    d.Row = Math.Max(0, Math.Min(list.Count - 1, d.Row + 1));
                    break;
                case InputKey.Enter:
                case InputKey.Space:
                    ToggleStream(s);
                    break;
                case InputKey.CtrlC:
                    quit = true;
                    break;
                case InputKey.Char when e.Char == '+' || e.Char == '=':
                    if (s != null) StreamLouder(s);
                    break;
                case InputKey.Char when e.Char == '-' || e.Char == '_':
                    if (s != null) StreamQuieter(s);
                    break;
                case InputKey.Char when e.Char == 's' || e.Char == 'S' || e.Char == 'q':
                    streamsDialog = null;
                    break;
            }
        }

        private void DrawStreamsDialog(int width, int height)
        {
            StreamsDialog d = streamsDialog;
            if (d == null)
                return;

            screen.OnClick(new Rect(0, 0, width, height), () => { });   // swallow clicks outside

            IReadOnlyList<ConsoleStream> list = StreamsIn(CurrentZone);
            d.Row = Math.Clamp(d.Row, 0, Math.Max(0, list.Count - 1));
            int w = Math.Min(78, width - 4);
            int listRows = Math.Max(1, Math.Min(Math.Max(1, list.Count), height - 9));
            int h = listRows * 2 + 6;
            Rect r = new Rect((width - w) / 2, Math.Max(0, (height - h) / 2), w, h);
            Style body = Style.Of(Ansi.BrightWhite, Ansi.Black);
            screen.Fill(r, body);
            screen.Box(r, Style.Of(Ansi.BrightCyan, Ansi.Black, bold: true), heavy: true);
            screen.Text(r.X + 2, r.Y, $" Web streams: {CurrentZone.Name} ", Style.Of(Ansi.BrightCyan, Ansi.Black, bold: true));
            int ix = r.X + 3, iw = w - 6;
            if (list.Count == 0)
                screen.Text(ix, r.Y + 2, "This tab has no web streams (the codeplug's web_streams).", Style.Of(Ansi.Grey, Ansi.Black), iw);

            int top = Math.Clamp(d.Row - listRows + 1, 0, Math.Max(0, list.Count - listRows));
            for (int i = 0; i < listRows && top + i < list.Count; i++)
            {
                int row = top + i;
                ConsoleStream s = list[row];
                bool sel = row == d.Row;
                Style st = sel ? Style.Of(Ansi.Black, Ansi.BrightCyan, bold: true) : body;
                Rect line = new Rect(ix, r.Y + 2 + i * 2, iw, 1);
                screen.Fill(line, st);
                int used = screen.Text(ix, line.Y, $"{s.Name}  ", st, iw / 2);
                Style state = sel ? st : s.State switch
                {
                    StreamState.Receiving => Style.Of(Ansi.BrightGreen, Ansi.Black, bold: true),
                    StreamState.Connecting => Style.Of(Ansi.BrightYellow, Ansi.Black, bold: true),
                    StreamState.Failed => Style.Of(Ansi.BrightRed, Ansi.Black, bold: true),
                    StreamState.Idle => Style.Of(Ansi.Green, Ansi.Black),
                    _ => Style.Of(Ansi.Grey, Ansi.Black)
                };
                used += screen.Text(ix + used, line.Y, s.StateText, state, iw - used);
                screen.Text(ix + used, line.Y, $"   vol {s.Volume:0.##}x", st, iw - used);
                screen.Text(ix + 2, line.Y + 1, s.Reason ?? s.Host, Style.Of(s.Reason != null ? Ansi.BrightRed : Ansi.Grey, Ansi.Black), iw - 2);
                int captured = row;
                screen.OnClick(line, () => { d.Row = captured; ToggleStream(StreamsIn(CurrentZone)[captured]); });
            }

            screen.Text(ix, r.Y + h - 3, "↑/↓: stream  Enter or click: play/stop  +/-: volume  Esc: close", Style.Of(Ansi.Grey, Ansi.Black), iw);
            Style closeStyle = Style.Of(Ansi.Black, Ansi.BrightCyan, bold: true);
            Rect close = new Rect(ix, r.Y + h - 2, 9, 1);
            screen.Fill(close, closeStyle);
            screen.Text(close.X + 2, close.Y, "CLOSE", closeStyle);
            screen.OnClick(close, () => streamsDialog = null);
        }
    } // public sealed partial class Tui
}
