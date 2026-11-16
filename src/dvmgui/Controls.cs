// SPDX-License-Identifier: AGPL-3.0-only
/**
* dvmgui - the graphical front end of the Linux DVM console.
*
* Small building blocks: a flat clickable label (Tap) that is painted from the palette, so
* it looks right in both themes, and the card for one channel.
*/

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace dvmgui
{
    /// <summary>
    /// A flat button: a rounded label that runs an action when clicked. It never takes keyboard
    /// focus, so a held key can't press it.
    /// </summary>
    public sealed class Tap
    {
        public readonly Border Root;
        public readonly TextBlock Label;

        public Tap(string text, Action click, double fontSize = 13, double minWidth = 0, double padX = 12, double padY = 6)
        {
            Label = new TextBlock
            {
                Text = text,
                FontSize = fontSize,
                FontWeight = FontWeight.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Root = new Border
            {
                Child = Label,
                Padding = new Thickness(padX, padY),
                CornerRadius = new CornerRadius(6),
                MinWidth = minWidth,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center
            };
            Root.PointerPressed += (s, e) =>
            {
                if (!e.GetCurrentPoint(Root).Properties.IsLeftButtonPressed)
                    return;
                e.Handled = true;       // not also a click on whatever the button sits on
                click();
            };
            Root.PointerEntered += (s, e) => Root.Opacity = 0.82;
            Root.PointerExited += (s, e) => Root.Opacity = 1.0;
        }

        public string Text
        {
            get => Label.Text;
            set { if (Label.Text != value) Label.Text = value; }
        }

        /// <summary>Colours the button: background, text, and an outline (or none).</summary>
        public void Paint(IBrush background, IBrush foreground, IBrush edge = null)
        {
            Root.Background = background;
            Label.Foreground = foreground;
            Root.BorderBrush = edge ?? background;
        }
    } // public sealed class Tap

    /// <summary>
    /// The card for one channel: name, talkgroup, encryption and key, what it's doing, the last
    /// call, and listen / volume / mute controls. Clicking the card focuses it (PTT then transmits
    /// there) and starts listening.
    /// </summary>
    public sealed class ChannelCard
    {
        public const double CardWidth = 272;

        public readonly Border Root;

        private readonly DispatchConsole console;
        private readonly ConsoleChannel chan;
        private readonly int index;
        private readonly TextBlock name, tg, crypt, keyLine, status, last, vol;
        private readonly Border statusPill;
        private readonly Tap listen, keyTap, quieter, louder, mute;

        public ChannelCard(DispatchConsole console, int index, Action changed)
        {
            this.console = console;
            this.index = index;
            chan = console.Channels[index];

            name = new TextBlock { FontSize = 16, FontWeight = FontWeight.Bold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            listen = new Tap("Listen", () => { console.SetFocus(index); console.ToggleSelect(chan); changed(); }, fontSize: 12, padX: 10, padY: 3);
            Grid top = Row(name, listen.Root);

            tg = new TextBlock { FontSize = 13 };
            crypt = new TextBlock { FontSize = 13, FontWeight = FontWeight.SemiBold };
            Grid line1 = Row(tg, crypt);

            keyLine = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            keyTap = new Tap("Request key", () => { console.RequestKey(chan); changed(); }, fontSize: 11, padX: 8, padY: 2);
            Grid line2 = Row(keyLine, keyTap.Root);
            line2.MinHeight = 24;

            status = new TextBlock { FontSize = 13, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            statusPill = new Border { Child = status, Padding = new Thickness(8, 4), CornerRadius = new CornerRadius(5), Margin = new Thickness(0, 4, 0, 2) };

            last = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, MinHeight = 16 };

            vol = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, MinWidth = 62 };
            quieter = new Tap("-", () => { console.SetFocus(index); console.Quieter(chan); changed(); }, fontSize: 13, minWidth: 30, padX: 6, padY: 1);
            louder = new Tap("+", () => { console.SetFocus(index); console.Louder(chan); changed(); }, fontSize: 13, minWidth: 30, padX: 6, padY: 1);
            mute = new Tap("Mute", () => { console.SetFocus(index); console.ToggleMute(chan); changed(); }, fontSize: 12, padX: 10, padY: 2);
            StackPanel volRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 4, 0, 0) };
            volRow.Children.Add(vol);
            volRow.Children.Add(quieter.Root);
            volRow.Children.Add(louder.Root);
            volRow.Children.Add(mute.Root);

            StackPanel stack = new StackPanel { Spacing = 3 };
            stack.Children.Add(top);
            stack.Children.Add(line1);
            stack.Children.Add(line2);
            stack.Children.Add(statusPill);
            stack.Children.Add(last);
            stack.Children.Add(volRow);

            Root = new Border
            {
                Width = CardWidth,
                Margin = new Thickness(6),
                Padding = new Thickness(13, 10, 13, 11),
                CornerRadius = new CornerRadius(9),
                BorderThickness = new Thickness(2),
                Child = stack
            };
            Root.PointerPressed += (s, e) =>
            {
                if (!e.GetCurrentPoint(Root).Properties.IsLeftButtonPressed)
                    return;
                console.SetFocus(index);
                if (!chan.Selected)
                    console.Select(chan);
                changed();
            };
        }

        private static Grid Row(Control left, Control right)
        {
            Grid g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Grid.SetColumn(left, 0);
            Grid.SetColumn(right, 1);
            right.HorizontalAlignment = HorizontalAlignment.Right;
            g.Children.Add(left);
            g.Children.Add(right);
            return g;
        }

        private static void Set(TextBlock t, string text)
        {
            if (t.Text != text)
                t.Text = text;
        }

        /// <summary>
        /// Brings the card up to date with the channel and paints it for the theme.
        /// </summary>
        public void Refresh(Palette p)
        {
            ChannelStatus st = console.GetStatus(chan);
            bool focused = console.FocusIndex == index;
            bool tx = st.Activity == ChannelActivity.Transmitting || st.Activity == ChannelActivity.SendingTone;
            bool rx = st.Activity == ChannelActivity.Receiving || st.Activity == ChannelActivity.ReceivingNoKey;

            Root.Background = p.Card;
            Root.BorderBrush = tx ? p.Tx : rx ? p.Rx : focused ? p.Accent : p.Edge;

            Set(name, chan.Cfg.Name);
            name.Foreground = chan.IsP25 ? p.Text : p.Dim;

            Set(tg, chan.Tg != 0 ? $"TG {chan.Tg}" : $"TG {chan.Cfg.Tgid}");
            tg.Foreground = p.Text;

            if (!chan.IsP25)
            {
                Set(crypt, string.Empty);
                Set(keyLine, string.Empty);
                keyTap.Root.IsVisible = false;
                listen.Root.IsVisible = false;
            }
            else if (chan.Encrypted)
            {
                bool have = console.HasKey(chan);
                Set(crypt, KeyStore.AlgorithmName(chan.TxAlg));
                crypt.Foreground = p.Warn;
                Set(keyLine, have ? $"key {chan.KeyId:X4} loaded" : $"key {chan.KeyId:X4} not loaded");
                keyLine.Foreground = have ? p.Rx : p.Warn;
                keyTap.Root.IsVisible = !have;
                keyTap.Paint(Brushes.Transparent, p.Warn, p.Warn);
            }
            else
            {
                Set(crypt, "clear");
                crypt.Foreground = p.Dim;
                Set(keyLine, chan.Cfg.RxOnly ? "receive only" : "no encryption");
                keyLine.Foreground = p.Dim;
                keyTap.Root.IsVisible = false;
            }

            // what it's doing
            Set(status, st.Text + (st.Muted && rx ? " (muted)" : string.Empty));
            switch (st.Activity)
            {
                case ChannelActivity.Transmitting:
                case ChannelActivity.SendingTone:
                    statusPill.Background = p.Tx; status.Foreground = p.OnStrong; break;
                case ChannelActivity.Receiving:
                    statusPill.Background = p.Rx; status.Foreground = p.OnStrong; break;
                case ChannelActivity.ReceivingNoKey:
                    statusPill.Background = p.Warn; status.Foreground = p.OnStrong; break;
                case ChannelActivity.Busy:
                    statusPill.Background = p.Track; status.Foreground = p.Busy; break;
                case ChannelActivity.Listening:
                    statusPill.Background = p.Track; status.Foreground = st.Muted ? p.Warn : p.Rx; break;
                default:
                    statusPill.Background = p.Track; status.Foreground = p.Dim; break;
            }

            Set(last, string.IsNullOrEmpty(chan.LastCall) ? string.Empty : $"last: {chan.LastCall}");
            last.Foreground = p.Dim;

            // controls
            listen.Text = chan.Selected ? "Listening" : "Listen";
            if (chan.Selected)
                listen.Paint(p.Rx, p.OnStrong);
            else
                listen.Paint(Brushes.Transparent, p.Text, p.Edge);

            Set(vol, $"vol {chan.Gain:0.##}x");
            vol.Foreground = chan.Selected ? p.Text : p.Dim;
            quieter.Paint(Brushes.Transparent, p.Text, p.Edge);
            louder.Paint(Brushes.Transparent, p.Text, p.Edge);
            mute.Text = chan.Muted ? "Muted" : "Mute";
            if (chan.Muted)
                mute.Paint(p.Warn, p.OnStrong);
            else
                mute.Paint(Brushes.Transparent, p.Text, p.Edge);
        }
    } // public sealed class ChannelCard
}
