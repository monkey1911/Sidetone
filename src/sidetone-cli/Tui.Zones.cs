// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks.
*
* The terminal console's tabs: a line of zone tabs under the title (click one, or [ and ] to go
* through them), and the zones box (z), where a zone can be hidden (its channels switched off)
* or shown again. Only drawn when the codeplug has more than one zone.
*/

using System.Globalization;
using Sidetone.Core;
using Chan = Sidetone.Core.ConsoleChannel;

namespace Sidetone.Cli
{
    public sealed partial class Tui
    {
        private sealed class ZoneDialog
        {
            public int Row;
            public string Error;
        }

        private ZoneDialog zoneDialog;

        /// <summary>True while the zones box is open (self-test hook).</summary>
        internal bool ZoneDialogOpen => zoneDialog != null;

        private void OpenZones()
        {
            if (zones.Count < 2)
            {
                AddLog("this codeplug has only one zone");
                return;
            }
            pageDialog = null;
            radioDialog = null;
            keyDialog = null;
            outputsDialog = null;
            announceDialog = null;
            zoneDialog = new ZoneDialog { Row = CurrentZone.Index };
        }

        private void ZoneInput(InputEvent e)
        {
            ZoneDialog d = zoneDialog;
            switch (e.Key)
            {
                case InputKey.Escape:
                    zoneDialog = null;
                    break;
                case InputKey.Up:
                    d.Row = Math.Max(0, d.Row - 1);
                    d.Error = null;
                    break;
                case InputKey.Down:
                case InputKey.Tab:
                    d.Row = Math.Min(zones.Count - 1, d.Row + 1);
                    d.Error = null;
                    break;
                case InputKey.Space:
                case InputKey.Enter:
                    ToggleZone(d.Row);
                    break;
                case InputKey.CtrlC:
                    quit = true;
                    break;
                case InputKey.Char when e.Char == 'z' || e.Char == 'Z' || e.Char == 'q':
                    zoneDialog = null;
                    break;
            }
        }

        private void ToggleZone(int row)
        {
            ZoneDialog d = zoneDialog;
            if (d == null || row < 0 || row >= zones.Count)
                return;
            d.Row = row;
            ConsoleZone z = zones[row];
            d.Error = SetZoneHidden(z, !z.Hidden);
            followCursor = true;
        }

        /// <summary>The tab line: one tab per zone on show, the one on show highlighted, and a mark where there's traffic.</summary>
        private void DrawTabs(int width, int y)
        {
            ConsoleZone current = CurrentZone;
            IReadOnlyList<ConsoleZone> shown = VisibleZones;
            int hiddenCount = zones.Count - shown.Count;
            string more = hiddenCount > 0 ? $" {hiddenCount} hidden · z " : " z: zones ";
            int moreW = ScreenBuffer.TextWidth(more);
            int limit = Math.Max(0, width - moreW - 1);

            int x = 0;
            for (int i = 0; i < shown.Count && x < limit; i++)
            {
                ConsoleZone z = shown[i];
                bool on = z == current;
                ChannelActivity act = ZoneActivity(z);
                string mark = act == ChannelActivity.Transmitting ? " ●" : act == ChannelActivity.Receiving ? " ●" : string.Empty;
                string label = $" {(i < 9 ? $"{i + 1} " : string.Empty)}{z.Name}";

                Ansi colour = NearestAnsi(z.TabColor), text = NearestAnsi(z.TabTextColor);
                Style style = on
                    ? Style.Of(text != Ansi.Default ? text : Ansi.Black, colour != Ansi.Default ? colour : Ansi.BrightCyan, bold: true)
                    : Style.Of(colour != Ansi.Default ? colour : Ansi.White);

                int start = x;
                x += screen.Text(x, y, label, style, limit - x);
                if (mark.Length > 0 && x < limit)
                    x += screen.Text(x, y, mark, on ? style with { Fg = act == ChannelActivity.Transmitting ? Ansi.Red : Ansi.Green }
                                                     : Style.Of(act == ChannelActivity.Transmitting ? Ansi.BrightRed : Ansi.BrightGreen, bold: true), limit - x);
                if (x < limit)
                    x += screen.Text(x, y, " ", style, limit - x);
                ConsoleZone target = z;
                screen.OnClick(new Rect(start, y, x - start, 1), () => { ShowZone(target); followCursor = true; });
                if (x < limit)
                    x += screen.Text(x, y, "│", Style.Of(Ansi.Grey), limit - x);
            }

            int mx = width - moreW;
            screen.Text(mx, y, more, hiddenCount > 0 ? Style.Of(Ansi.BrightYellow) : Style.Of(Ansi.Grey));
            screen.OnClick(new Rect(mx, y, moreW, 1), OpenZones);
        }

        private void DrawZoneDialog(int width, int height)
        {
            ZoneDialog d = zoneDialog;
            if (d == null)
                return;

            screen.OnClick(new Rect(0, 0, width, height), () => { });   // swallow clicks outside

            int w = Math.Min(64, width - 4);
            int listRows = Math.Max(1, Math.Min(zones.Count, height - 10));
            int h = listRows + 8;
            Rect r = new Rect((width - w) / 2, Math.Max(0, (height - h) / 2), w, h);
            Style body = Style.Of(Ansi.BrightWhite, Ansi.Black);
            screen.Fill(r, body);
            screen.Box(r, Style.Of(Ansi.BrightCyan, Ansi.Black, bold: true), heavy: true);
            screen.Text(r.X + 2, r.Y, " Zones ", Style.Of(Ansi.BrightCyan, Ansi.Black, bold: true));
            int ix = r.X + 3, iw = w - 6;
            screen.Text(ix, r.Y + 1, "A hidden zone's channels are switched off.", Style.Of(Ansi.Grey, Ansi.Black), iw);

            int top = Math.Clamp(d.Row - listRows + 1, 0, Math.Max(0, zones.Count - listRows));
            for (int i = 0; i < listRows && top + i < zones.Count; i++)
            {
                int row = top + i;
                ConsoleZone z = zones[row];
                bool sel = row == d.Row;
                int listening = z.chans.Count(c => c.Selected);
                string state = z.Hidden ? "hidden" : listening > 0 ? $"{listening} of {z.chans.Count} listening" : $"{z.chans.Count} channel{(z.chans.Count == 1 ? string.Empty : "s")}";
                Style st = sel ? Style.Of(Ansi.Black, Ansi.BrightCyan, bold: true) : z.Hidden ? Style.Of(Ansi.Grey, Ansi.Black) : body;
                Rect line = new Rect(ix, r.Y + 3 + i, iw, 1);
                screen.Fill(line, st);
                int used = screen.Text(ix, line.Y, z.Hidden ? "[ ] " : "[✓] ", st, iw);
                used += screen.Text(ix + used, line.Y, z.Name, st, iw - used - 24);
                screen.TextRight(ix + iw - 1, line.Y, state, st);
                screen.OnClick(line, () => ToggleZone(row));
            }

            int msgY = r.Y + 4 + listRows;
            if (d.Error != null)
                screen.Text(ix, msgY, d.Error, Style.Of(Ansi.BrightRed, Ansi.Black, bold: true), iw);
            else
                screen.Text(ix, msgY, "↑/↓: zone  Space/Enter: hide or show  Esc: close", Style.Of(Ansi.Grey, Ansi.Black), iw);

            Style closeStyle = Style.Of(Ansi.Black, Ansi.BrightCyan, bold: true);
            Rect close = new Rect(ix, r.Y + h - 2, 9, 1);
            screen.Fill(close, closeStyle);
            screen.Text(close.X + 2, close.Y, "CLOSE", closeStyle);
            screen.OnClick(close, () => zoneDialog = null);
        }

        /// <summary>The terminal colour closest to a codeplug tab colour ("#RRGGBB"), or Default for none.</summary>
        internal static Ansi NearestAnsi(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex))
                return Ansi.Default;
            string h = hex.Trim().TrimStart('#');
            if (h.Length == 8)
                h = h.Substring(2);     // #AARRGGBB
            if (h.Length != 6 || !int.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
                return Ansi.Default;
            int r = (rgb >> 16) & 0xFF, g = (rgb >> 8) & 0xFF, b = rgb & 0xFF;

            // by hue, so the pale tab colours codeplugs often use still read as red, green, blue...
            int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            double value = max / 255.0, saturation = max == 0 ? 0 : (max - min) / (double)max;
            if (saturation < 0.25)
                return value < 0.2 ? Ansi.Black : value < 0.6 ? Ansi.Grey : value < 0.85 ? Ansi.White : Ansi.BrightWhite;

            double hue = max == r ? 60.0 * (g - b) / (max - min)
                       : max == g ? 60.0 * (b - r) / (max - min) + 120
                       : 60.0 * (r - g) / (max - min) + 240;
            hue = (hue + 360) % 360;
            int sector = (int)Math.Round(hue / 60) % 6;     // red, yellow, green, cyan, blue, magenta
            bool bright = value > 0.75;
            return sector switch
            {
                0 => bright ? Ansi.BrightRed : Ansi.Red,
                1 => bright ? Ansi.BrightYellow : Ansi.Yellow,
                2 => bright ? Ansi.BrightGreen : Ansi.Green,
                3 => bright ? Ansi.BrightCyan : Ansi.Cyan,
                4 => bright ? Ansi.BrightBlue : Ansi.Blue,
                _ => bright ? Ansi.BrightMagenta : Ansi.Magenta
            };
        }
    } // public sealed partial class Tui
}
