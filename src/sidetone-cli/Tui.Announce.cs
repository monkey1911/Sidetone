// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks.
*
* The terminal console's announcements box (n): WAV files to play onto talkgroups. Enter plays the
* highlighted one on the focused card, Space on every listening channel; a adds a file by typing
* its path, r takes the highlighted one off the list. The list is the same as the graphical console's.
*/

using Sidetone.Core;

namespace Sidetone.Cli
{
    public sealed partial class Tui
    {
        private sealed class AnnounceDialog
        {
            public int Row;
            public string Message;
            public System.Text.StringBuilder Path;      // non-null while a path is being typed
        }

        private AnnounceDialog announceDialog;

        /// <summary>True while the announcements box is open, and its message (self-test hooks).</summary>
        internal bool AnnounceDialogOpen => announceDialog != null;
        internal string AnnounceDialogMessage => announceDialog?.Message;

        private void OpenAnnouncements()
        {
            pageDialog = null;
            radioDialog = null;
            zoneDialog = null;
            keyDialog = null;
            outputsDialog = null;
            announceDialog = new AnnounceDialog();
        }

        private void AnnounceInput(InputEvent e)
        {
            AnnounceDialog d = announceDialog;
            int count = Announcements.Count;
            if (d.Path != null)
            {
                // typing a file path to add
                switch (e.Key)
                {
                    case InputKey.Escape: d.Path = null; d.Message = null; break;
                    case InputKey.Backspace: if (d.Path.Length > 0) d.Path.Length--; break;
                    case InputKey.Space: d.Path.Append(' '); break;
                    case InputKey.Char: d.Path.Append(e.Char); break;
                    case InputKey.CtrlC: quit = true; break;
                    case InputKey.Enter:
                        string path = d.Path.ToString().Trim();
                        if (path.StartsWith("~/", StringComparison.Ordinal))
                            path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Substring(2));
                        string why = AddAnnouncement(path);
                        d.Message = why;
                        if (why == null)
                        {
                            d.Path = null;
                            d.Row = Announcements.Count - 1;
                        }
                        break;
                }
                return;
            }

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
                    d.Row = Math.Min(Math.Max(0, count - 1), d.Row + 1);
                    d.Message = null;
                    break;
                case InputKey.Enter:
                case InputKey.Space:
                    if (count == 0)
                        break;
                    string result = PlayAnnouncement(Announcements[d.Row], allListening: e.Key == InputKey.Space);
                    if (result == null)
                        announceDialog = null;
                    else
                        d.Message = result;
                    break;
                case InputKey.CtrlC:
                    quit = true;
                    break;
                case InputKey.Char when e.Char == 'a' || e.Char == 'A':
                    d.Path = new System.Text.StringBuilder();
                    d.Message = null;
                    break;
                case InputKey.Char when (e.Char == 'r' || e.Char == 'R') && count > 0:
                    RemoveAnnouncement(Announcements[d.Row]);
                    d.Row = Math.Clamp(d.Row, 0, Math.Max(0, Announcements.Count - 1));
                    break;
                case InputKey.Char when e.Char == 'n' || e.Char == 'N' || e.Char == 'q':
                    announceDialog = null;
                    break;
            }
        }

        private void DrawAnnounceDialog(int width, int height)
        {
            AnnounceDialog d = announceDialog;
            if (d == null)
                return;

            screen.OnClick(new Rect(0, 0, width, height), () => { });   // swallow clicks outside

            IReadOnlyList<string> files = Announcements;
            int w = Math.Min(72, width - 4);
            int listRows = Math.Max(1, Math.Min(Math.Max(1, files.Count), height - 12));
            int h = listRows + 10;
            Rect r = new Rect((width - w) / 2, Math.Max(0, (height - h) / 2), w, h);
            Style body = Style.Of(Ansi.BrightWhite, Ansi.Black);
            screen.Fill(r, body);
            screen.Box(r, Style.Of(Ansi.BrightCyan, Ansi.Black, bold: true), heavy: true);
            screen.Text(r.X + 2, r.Y, " Announcements ", Style.Of(Ansi.BrightCyan, Ansi.Black, bold: true));
            int ix = r.X + 3, iw = w - 6;
            screen.Text(ix, r.Y + 1, $"WAV files to play onto talkgroups. Focused card: {chans[cursor].Cfg.Name}", Style.Of(Ansi.Grey, Ansi.Black), iw);

            if (files.Count == 0)
                screen.Text(ix, r.Y + 3, "None yet: a adds one by its path.", Style.Of(Ansi.Grey, Ansi.Black), iw);
            int top = Math.Clamp(d.Row - listRows + 1, 0, Math.Max(0, files.Count - listRows));
            for (int i = 0; i < listRows && top + i < files.Count; i++)
            {
                int row = top + i;
                bool sel = row == d.Row;
                Style st = sel ? Style.Of(Ansi.Black, Ansi.BrightCyan, bold: true) : body;
                Rect line = new Rect(ix, r.Y + 3 + i, iw, 1);
                screen.Fill(line, st);
                TimeSpan? length = AnnouncementLength(files[row]);
                screen.Text(ix, line.Y, AnnouncementName(files[row]), st, iw - 12);
                screen.TextRight(ix + iw - 1, line.Y, length == null ? "can't read" : $"{length.Value.TotalSeconds:F0} s", st);
                int captured = row;
                screen.OnClick(line, () => { d.Row = captured; d.Message = null; });
            }

            int y = r.Y + 4 + listRows;
            if (d.Path != null)
            {
                DrawField(ix, y, "Add file", d.Path.ToString(), true, null, () => { });
                y += 2;
            }
            if (d.Message != null)
                screen.Text(ix, y, d.Message, Style.Of(Ansi.BrightRed, Ansi.Black, bold: true), iw);
            screen.Text(ix, r.Y + h - 2, d.Path != null ? "type the WAV file's path  Enter: add  Esc: cancel"
                                                         : "Enter: play on focused  Space: on all listening  a: add  r: remove  Esc: close",
                        Style.Of(Ansi.Grey, Ansi.Black), iw);
        }
    } // public sealed partial class Tui
}
