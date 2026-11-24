// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks. Graphical front end.
*
* The box that asks for a local key file's password. The password goes to the console to unlock
* the file and, only if asked, to the desktop's keyring; it is never logged or saved by Sidetone.
*/

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Sidetone.Gui
{
    public sealed class KeyFileOverlay
    {
        /// <summary>Covers the window below the header while the box is open.</summary>
        public readonly Grid Root = new Grid { IsVisible = false };
        public readonly TextBox Password = new TextBox { Width = 300, FontSize = 15, PasswordChar = '•', HorizontalAlignment = HorizontalAlignment.Left };

        private readonly Func<string, bool, string> unlock;
        private readonly Action closed;

        private readonly Border scrim = new Border { Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)) };
        private readonly Border card = new Border
        {
            Width = 460,
            Padding = new Thickness(22, 18, 22, 18),
            CornerRadius = new CornerRadius(Ui.CornerRadius),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        private readonly TextBlock title = new TextBlock { Text = "Unlock key file", FontSize = 19, FontWeight = FontWeight.Bold };
        private readonly TextBlock file = new TextBlock { FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock label = new TextBlock { Text = "Password", FontSize = 13 };
        private readonly TextBlock note = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock message = new TextBlock { FontSize = 13, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, MinHeight = 18 };
        private readonly Tap rememberTap, skip, unlockTap;
        private bool canRemember;

        public bool IsOpen => Root.IsVisible;
        /// <summary>Keep the password in the desktop's keyring.</summary>
        public bool Remember { get; private set; }
        public string Message => message.Text ?? string.Empty;

        /// <param name="unlock">Tries a password (and whether to remember it); returns null, or why it didn't work.</param>
        public KeyFileOverlay(Func<string, bool, string> unlock, Action closed)
        {
            this.unlock = unlock;
            this.closed = closed;

            rememberTap = new Tap("Remember in the keyring", () => Remember = !Remember, fontSize: 12, padX: 10, padY: 5);
            rememberTap.Root.HorizontalAlignment = HorizontalAlignment.Left;
            skip = new Tap("Not now", Close, fontSize: 13, minWidth: 90, padY: 8);
            unlockTap = new Tap("Unlock", Submit, fontSize: 13, minWidth: 110, padY: 8);
            WrapPanel buttons = new WrapPanel { ItemSpacing = 8, LineSpacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
            buttons.Children.Add(skip.Root);
            buttons.Children.Add(unlockTap.Root);

            Password.TextChanged += (s, e) => message.Text = string.Empty;

            StackPanel stack = new StackPanel { Spacing = 10 };
            stack.Children.Add(title);
            stack.Children.Add(file);
            stack.Children.Add(label);
            stack.Children.Add(Password);
            stack.Children.Add(rememberTap.Root);
            stack.Children.Add(note);
            stack.Children.Add(message);
            stack.Children.Add(buttons);
            card.Child = stack;

            scrim.PointerPressed += (s, e) => e.Handled = true;     // a stray click doesn't dismiss a password box
            card.PointerPressed += (s, e) => e.Handled = true;
            Root.Children.Add(scrim);
            Root.Children.Add(card);
        }

        /// <summary>Opens the box for a key file. <paramref name="keyring"/>: the password can be remembered.</summary>
        public void Show(string keyFile, bool keyring)
        {
            file.Text = keyFile;
            canRemember = keyring;
            Remember = false;
            rememberTap.Root.IsVisible = keyring;
            note.Text = "Until it's unlocked, encrypted channels use keys from the FNE, if it sends them. " +
                        (keyring ? "Without Remember, you're asked each time Sidetone starts."
                                 : "You're asked each time Sidetone starts (remembering needs secret-tool and a desktop keyring).");
            Password.Text = string.Empty;
            message.Text = string.Empty;
            Root.IsVisible = true;
            Password.Focus();
        }

        public void Close()
        {
            if (!Root.IsVisible)
                return;
            Password.Text = string.Empty;
            Root.IsVisible = false;
            closed();
        }

        /// <summary>Tries the typed password; the box stays open, saying why, when it doesn't work.</summary>
        public void Submit()
        {
            string typed = Password.Text ?? string.Empty;
            if (typed.Length == 0)
            {
                message.Text = "Type the key file's password.";
                return;
            }
            string error = unlock(typed, canRemember && Remember);
            if (error != null)
            {
                message.Text = error;
                return;
            }
            Close();
        }

        public void Refresh(Palette p)
        {
            if (!Root.IsVisible)
                return;
            card.Background = p.Panel;
            card.BorderBrush = p.Edge;
            title.Foreground = p.Text;
            file.Foreground = p.Dim;
            label.Foreground = p.Dim;
            note.Foreground = p.Dim;
            message.Foreground = p.Tx;
            if (Remember)
                rememberTap.Paint(p.Accent, p.OnStrong);
            else
                rememberTap.Paint(Brushes.Transparent, p.Dim, p.Edge);
            skip.Paint(Brushes.Transparent, p.Text, p.Edge);
            unlockTap.Paint(p.Accent, p.OnStrong);
        }
    } // public sealed class KeyFileOverlay
}
