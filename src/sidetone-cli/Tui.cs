// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks. Terminal front end.
*
* Terminal UI: one system's channels as a grid of cards (up to four across), live
* receive on selected channels, toggle push-to-talk, alert tones 1-5 and the channel
* marker on every listening channel, and mouse support: buttons in the title bar,
* click a card to listen, wheel to scroll. The console itself (what the keys and
* clicks do) is the core's DispatchConsole; this class adds input, dialogs and drawing.
*/

using dvmconsole;
using fnecore;
using fnecore.P25;
using Chan = Sidetone.Core.ConsoleChannel;

namespace Sidetone.Cli
{
    /// <summary>
    /// Terminal dispatch console.
    /// </summary>
    public sealed partial class Tui : DispatchConsole
    {
        /// <summary>Grid geometry for a terminal size.</summary>
        public readonly record struct GridGeometry(int Columns, int CardWidth, int VisibleRows, int TotalRows, int GridTop, int LogTop, int LogHeight);

        public const int TitleHeight = 3;
        public const int FooterHeight = 2;
        public const int CardHeight = 7;
        public const int MinCardWidth = 24;
        public const int MaxColumns = 4;
        private const int MinLogHeight = 6;
        private const int MaxBottomHeight = 10;   // log + recent calls: 8 lines plus the border
        private const int MinSplitWidth = 100;    // below this the recent-calls panel is hidden

        private readonly ScreenBuffer screen = new ScreenBuffer();

        private int scrollRow;
        private int logOffset;
        private bool followCursor = true;
        private volatile bool quit;

        /// <summary>Set when the user asks to quit (self-test hook).</summary>
        internal bool QuitRequested => quit;

        /// <summary>
        /// Initializes a new instance of the <see cref="Tui"/> class.
        /// </summary>
        public Tui(Codeplug codeplug, Codeplug.System system, TimeSpan txLimit, string logFile, TimeSpan? markerInterval = null,
                   string settingsPath = null)
            : base(codeplug, system, txLimit, logFile, markerInterval, settingsPath)
        {
        }

        /// <summary>
        /// Runs until the user quits.
        /// </summary>
        public int Run()
        {
            if (!Terminal.IsTerminal())
                throw new InvalidOperationException("the TUI needs an interactive terminal (use 'listen' for scripts)");

            Start();

            Terminal term = new Terminal();
            try
            {
                while (!quit)
                {
                    while (term.TryRead(out InputEvent e))
                        HandleInput(e);

                    Tick();
                    (int w, int h) = term.Size;
                    Render(w, h);
                    term.Write(screen.Flush());
                    wake.WaitOne(50);       // control commands wake the loop at once
                }
            }
            finally
            {
                term.Dispose();
                Shutdown();
            }

            Console.WriteLine("sidetone-cli: stopped");
            return 0;
        }

        /// <summary>Scrolled back in the log: keep looking at the same lines when new ones arrive.</summary>
        protected override void OnLogAdded()
        {
            if (logOffset > 0)
                logOffset++;
        }

        /*
        ** Radio commands: check, inhibit, uninhibit (one box: radio ID, action, send)
        */

        private sealed class RadioDialog
        {
            public string Rid = string.Empty;
            public int Action;          // index into RadioActions
            public bool Confirm;        // inhibit: SEND pressed once
            public string Error;
        }

        private RadioDialog radioDialog;

        /// <summary>True while the radio command box is open (self-test hook).</summary>
        internal bool RadioDialogOpen => radioDialog != null;

        /// <summary>Selected radio action label (self-test hook).</summary>
        internal string RadioDialogAction => radioDialog == null ? null : RadioActions[radioDialog.Action].Label;

        private void OpenRadio()
        {
            pageDialog = null;
            zoneDialog = null;
            radioDialog = new RadioDialog { Rid = lastPageRid };
        }

        private void RadioInput(InputEvent e)
        {
            RadioDialog d = radioDialog;
            switch (e.Key)
            {
                case InputKey.Escape:
                    radioDialog = null;
                    break;
                case InputKey.Tab:
                case InputKey.Right:
                case InputKey.Down:
                    SetRadioAction((d.Action + 1) % RadioActions.Length);
                    break;
                case InputKey.Left:
                case InputKey.Up:
                    SetRadioAction((d.Action + RadioActions.Length - 1) % RadioActions.Length);
                    break;
                case InputKey.Backspace:
                    if (d.Rid.Length > 0)
                        d.Rid = d.Rid.Substring(0, d.Rid.Length - 1);
                    d.Error = null;
                    d.Confirm = false;
                    break;
                case InputKey.Enter:
                    SubmitRadio();
                    break;
                case InputKey.CtrlC:
                    quit = true;
                    break;
                case InputKey.Char when char.IsDigit(e.Char) && d.Rid.Length < 8:
                    d.Rid += e.Char;
                    d.Error = null;
                    d.Confirm = false;
                    break;
            }
        }

        private void SetRadioAction(int action)
        {
            if (radioDialog == null)
                return;
            radioDialog.Action = action;
            radioDialog.Confirm = false;
            radioDialog.Error = null;
        }

        private void SubmitRadio()
        {
            RadioDialog d = radioDialog;
            if (d == null)
                return;
            ExtendedFunction fn = RadioActions[d.Action].Function;

            if (!uint.TryParse(d.Rid, out uint rid) || rid == 0 || rid > 0xFFFFFF)
            {
                d.Error = "enter a radio ID (1-16777215)";
                return;
            }
            if (fn == ExtendedFunction.INHIBIT && !d.Confirm)
            {
                d.Confirm = true;
                d.Error = $"press SEND again to inhibit {rid}";
                return;
            }

            string error = SendRadioCommand(d.Action, rid);
            if (error != null)
            {
                d.Error = error;
                d.Confirm = false;
                return;
            }
            radioDialog = null;
        }

        private void DrawRadioDialog(int width, int height)
        {
            RadioDialog d = radioDialog;
            if (d == null)
                return;

            screen.OnClick(new Rect(0, 0, width, height), () => { });   // swallow clicks outside

            int w = Math.Min(54, width - 4), h = 11;
            Rect r = new Rect((width - w) / 2, Math.Max(0, (height - h) / 2), w, h);
            screen.Fill(r, Style.Of(Ansi.BrightWhite, Ansi.Black));
            screen.Box(r, Style.Of(Ansi.BrightCyan, Ansi.Black, bold: true), heavy: true);
            screen.Text(r.X + 2, r.Y, " Radio command ", Style.Of(Ansi.BrightCyan, Ansi.Black, bold: true));
            int ix = r.X + 3, iw = w - 6;

            DrawField(ix, r.Y + 2, "Radio ID", d.Rid, true, null, () => { });

            // action choice
            screen.Text(ix, r.Y + 4, "Action", Style.Of(Ansi.White, Ansi.Black), 12);
            int ax = ix + 13;
            for (int i = 0; i < RadioActions.Length; i++)
            {
                int index = i;
                string label = RadioActions[i].Label;
                bool sel = d.Action == i;
                Style st = sel ? (RadioActions[i].Function == ExtendedFunction.INHIBIT ? Style.Of(Ansi.Black, Ansi.BrightRed, bold: true) : Style.Of(Ansi.Black, Ansi.BrightCyan, bold: true))
                               : Style.Of(Ansi.White, Ansi.Grey);
                Rect b = new Rect(ax, r.Y + 4, label.Length + 2, 1);
                screen.Fill(b, st);
                screen.Text(b.X + 1, b.Y, label, st);
                screen.OnClick(b, () => SetRadioAction(index));
                ax += b.W + 1;
            }

            if (d.Error != null)
                screen.Text(ix, r.Y + 6, d.Error, Style.Of(d.Confirm ? Ansi.BrightYellow : Ansi.BrightRed, Ansi.Black, bold: true), iw);
            else
                screen.Text(ix, r.Y + 6, $"from RID {peer?.ConsoleRid ?? 0}   Tab/arrows: action  Enter: send  Esc: cancel", Style.Of(Ansi.Grey, Ansi.Black), iw);

            Style sendStyle = d.Confirm ? Style.Of(Ansi.Black, Ansi.BrightRed, bold: true) : Style.Of(Ansi.Black, Ansi.BrightCyan, bold: true);
            Rect send = new Rect(ix, r.Y + 8, 10, 1), cancel = new Rect(ix + 12, r.Y + 8, 10, 1);
            screen.Fill(send, sendStyle);
            screen.Text(send.X + 3, send.Y, "SEND", sendStyle);
            screen.OnClick(send, SubmitRadio);
            Style cancelStyle = Style.Of(Ansi.Black, Ansi.White, bold: true);
            screen.Fill(cancel, cancelStyle);
            screen.Text(cancel.X + 2, cancel.Y, "CANCEL", cancelStyle);
            screen.OnClick(cancel, () => radioDialog = null);
        }

        /*
        ** Page dialog: QuickCall II tone page (on listening channels) or digital page (P25 call alert to one radio)
        */

        private sealed class PageDialog
        {
            public bool Digital;
            public string A = string.Empty, B = string.Empty, Rid = string.Empty;
            public int Field;           // tone mode: 0 = A, 1 = B
            public string Error;
        }

        private PageDialog pageDialog;

        /// <summary>True while the page dialog is open (self-test hook).</summary>
        internal bool PageDialogOpen => pageDialog != null;

        /// <summary>True when the open page dialog is in digital (call alert) mode (self-test hook).</summary>
        internal bool PageDialogDigital => pageDialog?.Digital == true;

        private void OpenPage(bool digital = false)
        {
            // the last page's values are filled in
            radioDialog = null;
            zoneDialog = null;
            pageDialog = new PageDialog { Digital = digital, A = lastPageA, B = lastPageB, Rid = lastPageRid };
        }

        private void PageInput(InputEvent e)
        {
            PageDialog d = pageDialog;
            ref string field = ref (d.Digital ? ref d.Rid : ref (d.Field == 0 ? ref d.A : ref d.B));
            switch (e.Key)
            {
                case InputKey.Escape:
                    pageDialog = null;
                    break;
                case InputKey.Left:
                case InputKey.Right:
                    d.Digital = !d.Digital;
                    d.Error = null;
                    break;
                case InputKey.Tab:
                case InputKey.Down:
                case InputKey.Up:
                    if (!d.Digital)
                        d.Field = 1 - d.Field;
                    break;
                case InputKey.Backspace:
                    if (field.Length > 0)
                        field = field.Substring(0, field.Length - 1);
                    d.Error = null;
                    break;
                case InputKey.Enter:
                    if (!d.Digital && d.Field == 0 && string.IsNullOrEmpty(d.B))
                        d.Field = 1;
                    else
                        SubmitPage();
                    break;
                case InputKey.CtrlC:
                    quit = true;
                    break;
                case InputKey.Char when char.IsDigit(e.Char) && field.Length < (d.Digital ? 8 : 7):
                case InputKey.Char when e.Char == '.' && !d.Digital && field.Length < 7:
                    field += e.Char;
                    d.Error = null;
                    break;
            }
        }

        private void SubmitPage()
        {
            PageDialog d = pageDialog;
            if (d == null)
                return;

            if (d.Digital)
            {
                if (!uint.TryParse(d.Rid, out uint rid) || rid == 0 || rid > 0xFFFFFF)
                {
                    d.Error = "enter a radio ID (1-16777215)";
                    return;
                }
                string error = SendDigitalPage(rid);
                if (error != null)
                {
                    d.Error = error;
                    return;
                }
                pageDialog = null;
                return;
            }

            bool okA = double.TryParse(d.A, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double a);
            bool okB = double.TryParse(d.B, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double b);
            if (!okA || a < TonePlan.MinPageHz || a > TonePlan.MaxPageHz)
            {
                d.Field = 0;
                d.Error = $"tone A must be {TonePlan.MinPageHz:F0}-{TonePlan.MaxPageHz:F0} Hz";
                return;
            }
            if (!okB || b < TonePlan.MinPageHz || b > TonePlan.MaxPageHz)
            {
                d.Field = 1;
                d.Error = $"tone B must be {TonePlan.MinPageHz:F0}-{TonePlan.MaxPageHz:F0} Hz";
                return;
            }

            lastPageA = d.A;
            lastPageB = d.B;
            MarkDirty();
            pageDialog = null;
            SendPage(a, b);
        }

        private void DrawPageDialog(int width, int height)
        {
            PageDialog d = pageDialog;
            if (d == null)
                return;

            // swallow clicks outside the dialog
            screen.OnClick(new Rect(0, 0, width, height), () => { });

            int w = Math.Min(54, width - 4), h = 12;
            Rect r = new Rect((width - w) / 2, Math.Max(0, (height - h) / 2), w, h);
            Style bg = Style.Of(Ansi.BrightWhite, Ansi.Black);
            screen.Fill(r, bg);
            screen.Box(r, Style.Of(Ansi.BrightYellow, Ansi.Black, bold: true), heavy: true);
            screen.Text(r.X + 2, r.Y, " Page ", Style.Of(Ansi.BrightYellow, Ansi.Black, bold: true));
            int ix = r.X + 3, iw = w - 6;

            // mode tabs
            Style on = Style.Of(Ansi.Black, Ansi.BrightYellow, bold: true), off = Style.Of(Ansi.White, Ansi.Grey);
            Rect toneTab = new Rect(ix, r.Y + 2, 16, 1), digTab = new Rect(ix + 17, r.Y + 2, 11, 1);
            screen.Fill(toneTab, d.Digital ? off : on);
            screen.Text(toneTab.X + 1, toneTab.Y, "Tone (QC II)", d.Digital ? off : on);
            screen.OnClick(toneTab, () => { pageDialog.Digital = false; pageDialog.Error = null; });
            screen.Fill(digTab, d.Digital ? on : off);
            screen.Text(digTab.X + 1, digTab.Y, "Digital", d.Digital ? on : off);
            screen.OnClick(digTab, () => { pageDialog.Digital = true; pageDialog.Error = null; });

            Style info = Style.Of(Ansi.White, Ansi.Black);
            if (d.Digital)
            {
                screen.Text(ix, r.Y + 4, $"P25 call alert from RID {peer?.ConsoleRid ?? 0} to one radio", info, iw);
                DrawField(ix, r.Y + 6, "Radio ID", d.Rid, true, null, () => { });
            }
            else
            {
                List<string> names = TxCapableListening().Select(c => c.Cfg.Name).ToList();
                screen.Text(ix, r.Y + 4, names.Count > 0 ? $"on: {string.Join(", ", names)}" : "no listening channels: click a card first",
                            names.Count > 0 ? info : Style.Of(Ansi.Yellow, Ansi.Black), iw);
                DrawField(ix, r.Y + 5, "Tone A (Hz)", d.A, d.Field == 0, "1 s", () => pageDialog.Field = 0);
                DrawField(ix, r.Y + 6, "Tone B (Hz)", d.B, d.Field == 1, "3 s", () => pageDialog.Field = 1);
            }

            if (d.Error != null)
                screen.Text(ix, r.Y + 8, d.Error, Style.Of(Ansi.BrightRed, Ansi.Black, bold: true), iw);
            else
                screen.Text(ix, r.Y + 8, d.Digital ? "←/→: mode  Enter: send  Esc: cancel" : "←/→: mode  Tab: A/B  Enter: send  Esc: cancel",
                            Style.Of(Ansi.Grey, Ansi.Black), iw);

            Rect send = new Rect(ix, r.Y + 9, 10, 1), cancel = new Rect(ix + 12, r.Y + 9, 10, 1);
            screen.Fill(send, on);
            screen.Text(send.X + 3, send.Y, "SEND", on);
            screen.OnClick(send, SubmitPage);
            Style cancelStyle = Style.Of(Ansi.Black, Ansi.White, bold: true);
            screen.Fill(cancel, cancelStyle);
            screen.Text(cancel.X + 2, cancel.Y, "CANCEL", cancelStyle);
            screen.OnClick(cancel, () => pageDialog = null);
        }

        private void DrawField(int x, int y, string label, string value, bool active, string suffix, Action focus)
        {
            screen.Text(x, y, label, Style.Of(active ? Ansi.BrightWhite : Ansi.White, Ansi.Black, bold: active), 12);
            Rect box = new Rect(x + 13, y, 12, 1);
            Style boxStyle = active ? Style.Of(Ansi.Black, Ansi.BrightWhite, bold: true) : Style.Of(Ansi.White, Ansi.Grey);
            screen.Fill(box, boxStyle);
            screen.Text(box.X + 1, y, value + (active ? "▏" : string.Empty), boxStyle, box.W - 1);
            if (suffix != null)
                screen.Text(box.X + box.W + 2, y, suffix, Style.Of(Ansi.Grey, Ansi.Black));
            screen.OnClick(new Rect(x, y, 30, 1), focus);
        }

        /*
        ** Input
        */

        /// <summary>
        /// Handles one key or mouse event (internal for the self-test).
        /// </summary>
        internal void HandleInput(InputEvent e)
        {
            if (zoneDialog != null && e.Kind == InputKind.Key)
            {
                ZoneInput(e);
                return;
            }
            if (pageDialog != null && e.Kind == InputKind.Key)
            {
                PageInput(e);
                return;
            }
            if (radioDialog != null && e.Kind == InputKind.Key)
            {
                RadioInput(e);
                return;
            }

            GridGeometry g = Layout(screen.Width, screen.Height, CurrentZone.chans.Count, TabRows);
            switch (e.Kind)
            {
                case InputKind.Click:
                    screen.HitTest(e.X, e.Y)?.Invoke();
                    return;

                case InputKind.WheelUp:
                case InputKind.WheelDown:
                {
                    int dir = e.Kind == InputKind.WheelUp ? -1 : 1;
                    if (g.LogHeight > 0 && e.Y >= g.LogTop)
                        logOffset = Math.Max(0, logOffset - dir * 3);
                    else
                    {
                        scrollRow += dir;
                        followCursor = false;
                    }
                    return;
                }
            }

            Chan c = chans[cursor];
            int page = Math.Max(1, g.VisibleRows) * g.Columns;
            int onTab = CurrentZone.chans.Count;
            switch (e.Key)
            {
                case InputKey.Left: MoveCursor(-1); break;
                case InputKey.Right: MoveCursor(1); break;
                case InputKey.Tab: MoveCursor(1); break;
                case InputKey.Up: MoveCursor(-g.Columns); break;
                case InputKey.Down: MoveCursor(g.Columns); break;
                case InputKey.PageUp: MoveCursor(-page); break;
                case InputKey.PageDown: MoveCursor(page); break;
                case InputKey.Home: MoveCursor(-onTab); break;
                case InputKey.End: MoveCursor(onTab); break;
                case InputKey.Enter: ToggleSelect(c); break;
                case InputKey.Space: ToggleTx(); break;
                case InputKey.Escape: StopTx(null); CancelTones(); break;
                case InputKey.CtrlC: quit = true; break;
                case InputKey.Char:
                    switch (e.Char)
                    {
                        case 'q': case 'Q': quit = true; break;
                        case 'm': ToggleMute(c); break;
                        case 'M': ToggleGlobalMute(); break;
                        case '+': case '=': Louder(c); break;
                        case '-': case '_': Quieter(c); break;
                        case 'k': case 'K': RequestKey(c); break;
                        case 'e': case 'E': ToggleTxSecure(c); break;
                        case 'x': case 'X': CyclePatch(c); break;
                        case 'l': ReplayLast(c); break;
                        case 'L': ReplayOlder(c); break;
                        case 'w': case 'W': ToggleRecord(c); break;
                        case >= '1' and <= '6': SendAlert(e.Char - '0'); break;
                        case 'r': case 'R': OpenRadio(); break;
                        case 't': case 'T': ToggleTalkPermit(); break;
                        case 'c': case 'C': ToggleMarker(); break;
                        case 'p': case 'P': OpenPage(); break;
                        case 'd': case 'D': OpenPage(digital: true); break;
                        case 'v': case 'V': ToggleMicMonitor(); break;
                        case 'u': case 'U': ToggleMicMute(); break;
                        case 'a': case 'A': RefreshAliases(); break;
                        case '[': NextZone(-1); followCursor = true; break;
                        case ']': NextZone(1); followCursor = true; break;
                        case 'z': case 'Z': OpenZones(); break;
                    }
                    break;
            }
        }

        /// <summary>Moves the focus among the cards on the tab on show.</summary>
        private void MoveCursor(int delta)
        {
            List<Chan> shown = CurrentZone.chans;
            int at = Math.Max(0, shown.IndexOf(chans[cursor]));
            FocusTo(chans.IndexOf(shown[Math.Clamp(at + delta, 0, shown.Count - 1)]));
            followCursor = true;
        }

        private void Focus(int index)
        {
            FocusTo(index);
            followCursor = true;
        }

        /// <summary>A line of tabs under the title, when the codeplug has more than one zone.</summary>
        internal int TabRows => zones.Count > 1 ? 1 : 0;

        /*
        ** Layout and rendering
        */

        /// <summary>
        /// Works out the card grid for a terminal size: up to four cards across, at least <see cref="MinCardWidth"/>
        /// wide, and as many card rows as fit while leaving room for the log (capped at <see cref="MaxBottomHeight"/> rows).
        /// </summary>
        public static GridGeometry Layout(int width, int height, int cardCount, int tabRows = 0)
        {
            int cols = Math.Clamp(width / MinCardWidth, 1, MaxColumns);
            int cardW = width / cols;
            int totalRows = (cardCount + cols - 1) / cols;
            int avail = Math.Max(0, height - TitleHeight - tabRows - FooterHeight);
            int visible = Math.Clamp((avail - MinLogHeight) / CardHeight, 1, Math.Max(1, totalRows));
            int gridH = Math.Min(visible * CardHeight, avail);
            int logH = Math.Min(avail - gridH, MaxBottomHeight);
            if (logH < 3)
                logH = 0;
            // the log sits just above the footer; any spare rows stay between it and the cards
            return new GridGeometry(cols, cardW, visible, totalRows, TitleHeight + tabRows, TitleHeight + tabRows + avail - logH, logH);
        }

        /// <summary>
        /// Draws one frame into the screen buffer (and registers the clickable regions).
        /// </summary>
        internal ScreenBuffer Render(int width, int height)
        {
            screen.Begin(width, height);
            if (width < 40 || height < TitleHeight + TabRows + FooterHeight + CardHeight)
            {
                screen.Text(0, 0, "Sidetone: terminal too small", Style.Of(Ansi.Yellow, bold: true));
                screen.Text(0, 1, "make the window bigger, or press q to quit", Style.Plain);
                return screen;
            }

            List<Chan> shown = CurrentZone.chans;
            GridGeometry g = Layout(width, height, shown.Count, TabRows);
            DrawTitle(width);
            if (TabRows > 0)
                DrawTabs(width, TitleHeight);

            // scrolling: keep the focused card in view after keyboard moves; the wheel scrolls freely
            int maxScroll = Math.Max(0, g.TotalRows - g.VisibleRows);
            if (followCursor)
            {
                int row = Math.Max(0, shown.IndexOf(chans[cursor])) / g.Columns;
                if (row < scrollRow) scrollRow = row;
                if (row >= scrollRow + g.VisibleRows) scrollRow = row - g.VisibleRows + 1;
            }
            scrollRow = Math.Clamp(scrollRow, 0, maxScroll);

            for (int vr = 0; vr < g.VisibleRows; vr++)
                for (int col = 0; col < g.Columns; col++)
                {
                    int pos = (scrollRow + vr) * g.Columns + col;
                    if (pos >= shown.Count)
                        break;
                    DrawCard(chans.IndexOf(shown[pos]), new Rect(col * g.CardWidth, g.GridTop + vr * CardHeight, g.CardWidth, CardHeight));
                }

            if (maxScroll > 0)
            {
                string more = $" {scrollRow + 1}-{Math.Min(g.TotalRows, scrollRow + g.VisibleRows)} of {g.TotalRows} rows ";
                screen.TextRight(width - 1, g.LogTop - 1, more, Style.Of(Ansi.Grey));
            }

            if (g.LogHeight > 0)
            {
                if (width >= MinSplitWidth)
                {
                    int callsW = Math.Min(width / 2, 64);
                    DrawRecent(new Rect(0, g.LogTop, callsW, g.LogHeight));
                    DrawLog(new Rect(callsW, g.LogTop, width - callsW, g.LogHeight));
                }
                else
                    DrawLog(new Rect(0, g.LogTop, width, g.LogHeight));
            }
            DrawFooter(width, height);
            DrawPageDialog(width, height);
            DrawRadioDialog(width, height);
            DrawZoneDialog(width, height);
            return screen;
        }

        private void DrawTitle(int width)
        {
            // buttons: three rows tall so they're easy to hit
            const int pttW = 10, muteW = 9, exitW = 8, toneW = 4, markW = 8, radioW = 9;
            bool tones = width >= 116;
            const int pageW = 8;
            int toneBlock = tones ? 6 * (toneW + 1) + pageW + 1 + radioW + 1 + markW + 2 : 0;
            int x = width - (pttW + muteW + exitW + 2) - toneBlock;

            if (tones)
            {
                ToneJob job = toneJob;
                for (int n = 1; n <= 6; n++)
                {
                    int number = n;
                    bool playing = job != null && job.Plan.Name == $"alert {n}";
                    Button(new Rect(x, 0, toneW, TitleHeight), $"T{n}",
                           playing ? Style.Of(Ansi.Black, Ansi.BrightRed, bold: true) : Style.Of(Ansi.Black, Ansi.BrightCyan, bold: true),
                           () => { if (playing) CancelTones(); else SendAlert(number); });
                    x += toneW + 1;
                }
                bool paging = job != null && job.Plan.Name.StartsWith("page ", StringComparison.Ordinal);
                Button(new Rect(x, 0, pageW, TitleHeight), "PAGE",
                       paging ? Style.Of(Ansi.Black, Ansi.BrightRed, bold: true)
                       : pageDialog != null ? Style.Of(Ansi.Black, Ansi.BrightYellow, bold: true) : Style.Of(Ansi.Black, Ansi.BrightGreen, bold: true),
                       () => { if (paging) CancelTones(); else OpenPage(); });
                x += pageW + 1;
                Button(new Rect(x, 0, radioW, TitleHeight), "RADIO",
                       radioDialog != null ? Style.Of(Ansi.Black, Ansi.BrightYellow, bold: true) : Style.Of(Ansi.Black, Ansi.BrightCyan, bold: true), OpenRadio);
                x += radioW + 1;
                bool beeping = job != null && job.Plan.Name == "marker";
                Button(new Rect(x, 0, markW, TitleHeight), markerOn ? "● MARK" : "MARK",
                       beeping ? Style.Of(Ansi.Black, Ansi.BrightRed, bold: true)
                       : markerOn ? Style.Of(Ansi.Black, Ansi.BrightYellow, bold: true) : Style.Of(Ansi.Black, Ansi.White, bold: true), ToggleMarker);
                x += markW + 2;
            }

            bool txOn = Transmitting;
            Button(new Rect(x, 0, pttW, TitleHeight), txOn ? "● TX" : "PTT",
                   txOn ? Style.Of(Ansi.Black, Ansi.BrightRed, bold: true) : Style.Of(Ansi.Black, Ansi.BrightBlue, bold: true), ToggleTx);
            x += pttW + 1;
            Button(new Rect(x, 0, muteW, TitleHeight), globalMute ? "MUTED" : "MUTE",
                   globalMute ? Style.Of(Ansi.Black, Ansi.BrightYellow, bold: true) : Style.Of(Ansi.Black, Ansi.White, bold: true), ToggleGlobalMute);
            x += muteW + 1;
            Button(new Rect(x, 0, exitW, TitleHeight), "EXIT", Style.Of(Ansi.Black, Ansi.BrightMagenta, bold: true), () => quit = true);

            // system box
            int boxW = width - (pttW + muteW + exitW + 3) - toneBlock;
            Style frame = Style.Of(Ansi.Cyan);
            screen.Box(new Rect(0, 0, boxW, TitleHeight), frame);
            screen.Text(2, 0, " Sidetone ", Style.Of(Ansi.BrightCyan, bold: true));

            int tx0 = 2, inner = boxW - 4;
            bool connected = peer?.Connected == true;
            int used = screen.Text(tx0, 1, system.Name, Style.Of(Ansi.BrightWhite, bold: true), inner);
            used += screen.Text(tx0 + used, 1, connected ? "  ● connected" : "  ○ connecting…",
                                connected ? Style.Of(Ansi.BrightGreen) : Style.Of(Ansi.Yellow), inner - used);
            screen.Text(tx0 + used, 1, $"  peer {system.PeerId}  RID {peer?.ConsoleRid ?? 0}  keys {keys.Count}  {aliases.Summary}", Style.Of(Ansi.White), inner - used);

            DrawMicMeter(boxW);
        }

        /// <summary>Width of the meter's bar for a title box this wide (0 = no room).</summary>
        internal static int MeterCells(int boxW)
        {
            int cells = Math.Min(boxW - 18, 24);
            return cells < 6 ? 0 : cells;
        }

        /// <summary>
        /// Draws the microphone level meter in the bottom edge of the title box:
        /// " MIC ▕██████▌·····▏ -14 dB ". Green up to -18 dBFS, yellow to -6, red above;
        /// ▌ marks the recent peak. Click it (or press v) to open or close the mic for the meter.
        /// </summary>
        private void DrawMicMeter(int boxW)
        {
            int cells = MeterCells(boxW);
            if (cells == 0)
                return;

            int y = TitleHeight - 1;
            bool txOn = Transmitting;
            bool open = txOn || micMonitor;

            if (micMuted)
            {
                string muted = txOn ? " TX · MIC MUTED " : " MIC MUTED ";
                int mx = Math.Max(1, boxW - 1 - ScreenBuffer.TextWidth(muted));
                int mw = screen.Text(mx, y, muted, Style.Of(Ansi.BrightWhite, Ansi.Red, bold: true), boxW - 1 - mx);
                screen.OnClick(new Rect(mx, y, mw, 1), ToggleMicMute);
                return;
            }

            if (!open)
            {
                string off = " MIC off · v to check level ";
                int ox = Math.Max(1, boxW - 1 - ScreenBuffer.TextWidth(off));
                int w = screen.Text(ox, y, off, Style.Of(Ansi.Grey), boxW - 1 - ox);
                screen.OnClick(new Rect(ox, y, w, 1), ToggleMicMonitor);
                return;
            }

            LevelMeter.Reading m = micLevel.Read();
            const double span = -LevelMeter.FloorDb;
            int filled = m.Active ? (int)Math.Round((m.LevelDb - LevelMeter.FloorDb) / span * cells) : 0;
            int peakCell = m.Active ? (int)Math.Round((m.PeakDb - LevelMeter.FloorDb) / span * cells) - 1 : -1;

            string label = txOn ? " TX MIC " : " MIC ";
            string value = !m.Active ? "  --   " : m.Clipping ? " CLIP! " : $" {Math.Round(m.LevelDb),3:F0} dB";
            int total = ScreenBuffer.TextWidth(label) + cells + 2 + 8;
            int x = Math.Max(1, boxW - 1 - total);
            int start = x;

            x += screen.Text(x, y, label, txOn ? Style.Of(Ansi.BrightRed, bold: true) : Style.Of(Ansi.BrightWhite, bold: true));
            x += screen.Text(x, y, "▕", Style.Of(Ansi.Grey));
            for (int i = 0; i < cells; i++)
            {
                double cellDb = LevelMeter.FloorDb + span * (i + 1) / cells;
                Ansi colour = cellDb <= -18 ? Ansi.BrightGreen : cellDb <= -6 ? Ansi.BrightYellow : Ansi.BrightRed;
                if (i < filled)
                    screen.Text(x + i, y, "█", Style.Of(colour));
                else if (i == peakCell)
                    screen.Text(x + i, y, "▌", Style.Of(colour));
                else
                    screen.Text(x + i, y, "·", Style.Of(Ansi.Grey));
            }
            x += cells;
            x += screen.Text(x, y, "▏", Style.Of(Ansi.Grey));
            x += screen.Text(x, y, value + " ", m.Clipping ? Style.Of(Ansi.BrightWhite, Ansi.Red, bold: true) : Style.Of(Ansi.White));

            // clicking closes the meter's mic (never ends a transmission)
            if (!txOn)
                screen.OnClick(new Rect(start, y, x - start, 1), ToggleMicMonitor);
        }

        private void Button(Rect r, string label, Style style, Action action)
        {
            screen.Fill(r, style);
            int lw = ScreenBuffer.TextWidth(label);
            screen.Text(r.X + Math.Max(0, (r.W - lw) / 2), r.Y + r.H / 2, label, style, r.W);
            screen.OnClick(r, action);
        }

        private void DrawCard(int idx, Rect r)
        {
            Chan c = chans[idx];
            bool focused = idx == cursor;
            bool txHere = (Transmitting && txChan == c) || toneChans.Contains(c);
            P25VoiceReceiver rx = c.Rx;
            bool rxHere = c.Selected && rx != null && rx.InCall;

            Style border = txHere ? Style.Of(Ansi.BrightRed, bold: true)
                : rxHere ? Style.Of(Ansi.BrightGreen, bold: true)
                : focused ? Style.Of(Ansi.BrightCyan, bold: true)
                : c.Selected ? Style.Of(Ansi.Green)
                : Style.Of(Ansi.Grey);
            Style dim = Style.Of(Ansi.Grey);

            // whole card: focus it and start listening
            screen.OnClick(r, () =>
            {
                Focus(idx);
                if (!c.Selected) Select(c);
            });

            Rect box = new Rect(r.X, r.Y, r.W - 1, r.H);    // one column gap between cards
            screen.Box(box, border, heavy: focused);
            int ix = box.X + 2, iw = box.W - 4, right = box.X + box.W - 2;

            // title and listen toggle
            string toggle = c.Selected ? "[●]" : "[ ]";
            int toggleX = right - 3;
            Style nameStyle = !c.IsP25 ? dim : focused ? Style.Of(Ansi.Black, Ansi.BrightCyan, bold: true)
                : c.Selected ? Style.Of(Ansi.BrightWhite, bold: true) : Style.Of(Ansi.White);
            screen.Text(ix, box.Y, focused ? $" ▶ {c.Cfg.Name} " : $" {c.Cfg.Name} ", nameStyle, toggleX - ix - 1);
            screen.Text(toggleX, box.Y, toggle, c.Selected ? Style.Of(Ansi.BrightGreen, bold: true) : dim);
            screen.OnClick(new Rect(toggleX - 1, box.Y, 5, 1), () =>
            {
                Focus(idx);
                ToggleSelect(c);
            });

            int y = box.Y + 1;
            if (!c.IsP25)
            {
                screen.Text(ix, y, c.Tg != 0 ? $"TG {c.Tg}" : $"TG {c.Cfg.Tgid}", dim, iw);
                screen.Text(ix, y + 1, $"{c.Cfg.Mode} · not supported yet", dim, iw);
                return;
            }

            // line 1: talkgroup and crypto
            string patch = PatchName(c);
            screen.Text(ix, y, $"TG {c.Tg}{(patch != null ? $" ⇄ {patch}" : string.Empty)}", patch != null ? Style.Of(Ansi.BrightCyan, bold: true) : Style.Of(Ansi.BrightWhite), iw);
            // a selectable channel switched to clear still decrypts what it receives, so say which half is clear
            string crypt = !c.Encrypted ? "clear" : c.TxSecure ? $"{KeyStore.AlgorithmName(c.TxAlg)} 🔒" : "TX CLEAR 🔓";
            screen.TextRight(right, y, crypt, !c.Encrypted ? dim : c.TxSecure ? Style.Of(Ansi.BrightYellow) : Style.Of(Ansi.BrightRed, bold: true));

            // line 2: key
            if (c.Encrypted)
            {
                bool have = keys.TryGet(c.KeyRequestAlg, c.KeyId, out _);
                screen.Text(ix, y + 1, have ? $"key ✓ {c.KeyId:X4}{(keys.IsLocal(c.KeyRequestAlg, c.KeyId) ? " (file)" : string.Empty)}{(c.Selectable ? "  e: secure/clear" : string.Empty)}"
                                            : $"key – {c.KeyId:X4}  (k: request)",
                            have ? Style.Of(Ansi.Green) : Style.Of(Ansi.Yellow), iw);
            }
            else
                screen.Text(ix, y + 1, c.Cfg.RxOnly ? "receive only" : "no encryption", dim, iw);

            // line 3: status
            DrawStatus(c, rx, txHere, rxHere, ix, y + 2, iw);

            // line 4: last call, or the replay; recording on the right
            int lastWidth = iw;
            if (c.Record)
            {
                const string rec = "● REC";
                screen.TextRight(ix + iw, y + 3, rec, Style.Of(Ansi.BrightRed, bold: true));
                lastWidth -= ScreenBuffer.TextWidth(rec) + 1;
            }
            if (ReplayChannel == c && Replaying is RecalledCall playing)
                screen.Text(ix, y + 3, $"▶ replay {playing.Who} {ReplayPosition.TotalSeconds:F0}/{playing.Length.TotalSeconds:F0} s (l: stop)", Style.Of(Ansi.BrightCyan, bold: true), lastWidth);
            else if (!string.IsNullOrEmpty(c.LastCall))
                screen.Text(ix, y + 3, $"last {c.LastCall}", dim, lastWidth);

            // line 5: volume and mute, clickable
            int vx = ix;
            vx += screen.Text(vx, y + 4, $"vol {c.Gain:0.##}x ", c.Selected ? Style.Of(Ansi.White) : dim, iw);
            vx = SmallButton(vx, y + 4, "[-]", dim, () => { Focus(idx); Quieter(c); }, ix + iw);
            vx = SmallButton(vx, y + 4, "[+]", dim, () => { Focus(idx); Louder(c); }, ix + iw);
            SmallButton(vx + 1, y + 4, c.Muted ? "[MUTED]" : "[mute]",
                        c.Muted ? Style.Of(Ansi.Black, Ansi.Yellow, bold: true) : dim, () => { Focus(idx); ToggleMute(c); }, ix + iw);
        }

        private int SmallButton(int x, int y, string label, Style style, Action action, int limit)
        {
            int w = ScreenBuffer.TextWidth(label);
            if (x + w > limit)
                return x;
            screen.Text(x, y, label, style);
            screen.OnClick(new Rect(x, y, w, 1), action);
            return x + w;
        }

        private void DrawStatus(Chan c, P25VoiceReceiver rx, bool txHere, bool rxHere, int x, int y, int w)
        {
            ToneJob job = toneJob;
            if (txHere && job != null && toneChans.Contains(c))
            {
                Style ts = Style.Of(Ansi.BrightWhite, Ansi.Red, bold: true);
                screen.Fill(new Rect(x, y, w, 1), ts);
                screen.Text(x, y, $" ♪ TX {job.Plan.Name}", ts, w);
                return;
            }
            if (txHere)
            {
                TimeSpan held = DateTime.Now - tx.StartedAt;
                Style s = Style.Of(Ansi.BrightWhite, Ansi.Red, bold: true);
                screen.Fill(new Rect(x, y, w, 1), s);
                screen.Text(x, y, $" ● TX {peer?.ConsoleRid}  {held:mm\\:ss}", s, w);
                return;
            }

            if (rxHere)
            {
                byte alg = rx.CurrentAlgorithm;
                bool enc = alg != 0 && alg != P25Defines.P25_ALGO_UNENCRYPT;
                bool noKey = enc && rx.CallHasMuted && !keys.TryGet(alg, rx.CurrentKeyId, out _);
                Style s = noKey ? Style.Of(Ansi.Black, Ansi.Yellow, bold: true) : Style.Of(Ansi.Black, Ansi.BrightGreen, bold: true);
                screen.Fill(new Rect(x, y, w, 1), s);
                string text = $" RX {rx.CurrentSource}{(enc ? " 🔒" : string.Empty)}{(noKey ? " no key" : string.Empty)}{(c.Muted || globalMute ? " (muted)" : string.Empty)}";
                screen.Text(x, y, text, s, w);
                return;
            }

            if (!c.Selected && c.Active)
                screen.Text(x, y, $"busy {aliases.Label(c.ActiveSrc)}", Style.Of(Ansi.Cyan), w);
            else if (c.Selected)
                screen.Text(x, y, c.Muted || globalMute ? "listening (muted)" : "listening", c.Muted || globalMute ? Style.Of(Ansi.Yellow) : Style.Of(Ansi.Green), w);
            else
                screen.Text(x, y, "click to listen", Style.Of(Ansi.Grey), w);
        }

        private void DrawRecent(Rect r)
        {
            screen.Box(r, Style.Of(Ansi.Grey));
            screen.Text(r.X + 2, r.Y, " Recent calls ", Style.Of(Ansi.White, bold: true));

            List<(string Time, string Chan, string Who, string Detail, bool Tx)> rows;
            lock (uiLock)
                rows = recent.Take(r.H - 2).ToList();    // newest first
            if (rows.Count == 0)
                screen.Text(r.X + 2, r.Y + 1, "none yet", Style.Of(Ansi.Grey), r.W - 4);

            int x = r.X + 2, w = r.W - 4;
            for (int i = 0; i < rows.Count; i++)
            {
                var call = rows[i];
                int y = r.Y + 1 + i, used = 0;
                used += screen.Text(x, y, $"{call.Time} ", Style.Of(Ansi.Grey), w);
                used += screen.Text(x + used, y, call.Tx ? "TX " : "RX ", call.Tx ? Style.Of(Ansi.BrightRed, bold: true) : Style.Of(Ansi.BrightGreen, bold: true), w - used);
                used += screen.Text(x + used, y, $"{call.Chan,-10} ", Style.Of(Ansi.BrightWhite), w - used);
                used += screen.Text(x + used, y, $"{call.Who,-8} ", Style.Plain, w - used);
                screen.Text(x + used, y, call.Detail, Style.Of(Ansi.Grey), w - used);
            }
        }

        private void DrawLog(Rect r)
        {
            screen.Box(r, Style.Of(Ansi.Grey));
            screen.Text(r.X + 2, r.Y, " Log ", Style.Of(Ansi.White, bold: true));

            int rows = r.H - 2;
            List<string> lines;
            lock (uiLock)
            {
                logOffset = Math.Clamp(logOffset, 0, Math.Max(0, log.Count - rows));
                lines = log.Reverse().Skip(logOffset).Take(rows).Reverse().ToList();
            }
            if (logOffset > 0)
                screen.TextRight(r.X + r.W - 2, r.Y, $" ↑ {logOffset} newer below ", Style.Of(Ansi.Yellow));

            for (int i = 0; i < lines.Count; i++)
            {
                string l = lines[i];
                Style s = l.Contains("TX ") ? Style.Of(Ansi.BrightRed)
                    : l.Contains(": RX ") ? Style.Of(Ansi.BrightGreen)
                    : l.Contains("error", StringComparison.OrdinalIgnoreCase) || l.Contains("not transmitting") ? Style.Of(Ansi.Yellow)
                    : Style.Plain;
                screen.Text(r.X + 2, r.Y + 1 + i, l, s, r.W - 4);
            }
        }

        private void DrawFooter(int width, int height)
        {
            int y = height - FooterHeight;
            ToneJob job = toneJob;
            if (job != null)
            {
                Style s = Style.Of(Ansi.BrightWhite, Ansi.Red, bold: true);
                screen.Fill(new Rect(0, y, width, 1), s);
                screen.Text(1, y, $"♪ SENDING {job.Plan.Name.ToUpperInvariant()} on {string.Join(", ", job.Channels)}   (Esc, Space or the button to stop)", s, width - 2);
            }
            else if (Transmitting)
            {
                TimeSpan held = DateTime.Now - tx.StartedAt;
                Style s = Style.Of(Ansi.BrightWhite, Ansi.Red, bold: true);
                screen.Fill(new Rect(0, y, width, 1), s);
                string warn = micMuted ? "   ■ MIC MUTED (u to unmute)" : noAudioWarned ? "   ■ NO AUDIO FROM THE MICROPHONE" : string.Empty;
                screen.Text(1, y, $"● TRANSMITTING  {txChan?.Cfg.Name}  tg {tx.Talkgroup}  {(tx.Encrypted ? KeyStore.AlgorithmName(tx.AlgorithmId) : "clear")}  " +
                                  $"{held:mm\\:ss} / {txLimit:mm\\:ss}   (Space, Esc or PTT to stop){warn}", s, width - 2);
            }
            else
            {
                Chan c = chans[cursor];
                string target = c.IsP25 ? $"PTT → {c.Cfg.Name} (tg {c.Tg}, {(c.TxSecure ? KeyStore.AlgorithmName(c.TxAlg) : "clear")})" : $"{c.Cfg.Name}: no transmit on {c.Cfg.Mode}";
                int used = screen.Text(1, y, target, Style.Of(Ansi.White), width - 2);
                if (micMuted)
                    screen.Text(1 + used, y, "   MIC MUTED (u to unmute)", Style.Of(Ansi.BrightRed, bold: true), width - 2 - used);
            }

            screen.Text(1, y + 1, (TabRows > 0 ? "[ ]: tab  z: zones  " : string.Empty) + "click card: listen  [●]: stop  Space: PTT  1-6: alert  p/d: page  r: radio cmd  c: marker  t: talk permit  v: mic meter  u: mic mute  a: aliases  m/M: mute card/all  +/-: vol  k: key  e: secure/clear  x: patch  l/L: replay  w: rec  q: quit",
                        Style.Of(Ansi.Grey), width - 2);
        }
    } // public sealed class Tui
}
