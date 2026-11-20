// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks.
*
* The terminal console's key file box: the password for a local KFDtool key file, asked for
* inside the TUI (at start when the keyring doesn't have it, and again with K or by clicking the
* lock in the title), so a key file can be unlocked after the console has started. The password
* is never shown, logged or kept, unless "remember" puts it in the keyring.
*/

using Sidetone.Core;

namespace Sidetone.Cli
{
    public sealed partial class Tui
    {
        private sealed class KeyFileDialog
        {
            public readonly System.Text.StringBuilder Password = new System.Text.StringBuilder();
            public bool Remember;
            public string Error;
        }

        private KeyFileDialog keyDialog;

        /// <summary>Set before <see cref="Run"/>: open the key file box as soon as the TUI starts.</summary>
        internal bool AskForKeyFilePassword { get; set; }

        /// <summary>True while the key file box is open (self-test hook).</summary>
        internal bool KeyFileDialogOpen => keyDialog != null;

        /// <summary>The key file box's message (self-test hook).</summary>
        internal string KeyFileDialogError => keyDialog?.Error;

        /// <summary>There is a key file, and it hasn't been unlocked.</summary>
        internal bool KeyFileLocked => KeyFilePath != null && !KeyFileLoaded && File.Exists(KeyFilePath);

        /// <summary>Opens the key file box (K).</summary>
        internal void OpenKeyFile()
        {
            if (KeyFilePath == null)
            {
                AddLog("no key file: give one with --key-file FILE.ekc, choose one in the graphical console's Settings, or name it in the codeplug");
                return;
            }
            if (!File.Exists(KeyFilePath))
            {
                AddLog($"key file not found: {KeyFilePath}");
                return;
            }
            if (KeyFileLoaded)
            {
                AddLog($"the key file is already unlocked ({keys.LocalCount} keys)");
                return;
            }
            pageDialog = null;
            radioDialog = null;
            zoneDialog = null;
            outputsDialog = null;
            announceDialog = null;
            keyDialog = new KeyFileDialog();
        }

        private void KeyFileInput(InputEvent e)
        {
            KeyFileDialog d = keyDialog;
            switch (e.Key)
            {
                case InputKey.Escape:
                    keyDialog = null;
                    AddLog("key file: not unlocked now (K to unlock it later)");
                    break;
                case InputKey.Enter:
                    SubmitKeyFile();
                    break;
                case InputKey.Tab:
                    d.Remember = !d.Remember && SecretStore.Available;
                    break;
                case InputKey.Backspace:
                    if (d.Password.Length > 0)
                        d.Password.Length--;
                    d.Error = null;
                    break;
                case InputKey.Space:
                    d.Password.Append(' ');
                    d.Error = null;
                    break;
                case InputKey.Char:
                    d.Password.Append(e.Char);
                    d.Error = null;
                    break;
                case InputKey.CtrlC:
                    quit = true;
                    break;
            }
        }

        private void SubmitKeyFile()
        {
            KeyFileDialog d = keyDialog;
            if (d == null)
                return;
            string password = d.Password.ToString();
            if (password.Length == 0)
            {
                d.Error = "Type the key file's password, or Esc to skip.";
                return;
            }
            string error = LoadKeyFile(password, out _);
            if (error != null)
            {
                d.Error = char.ToUpperInvariant(error[0]) + error.Substring(1) + ".";
                d.Password.Clear();
                return;
            }
            if (d.Remember && !SecretStore.Store(KeyFilePath, password))
                AddLog("warning: the keyring didn't take the key file password; you'll be asked again next time");
            d.Password.Clear();
            keyDialog = null;
        }

        private void DrawKeyFileDialog(int width, int height)
        {
            KeyFileDialog d = keyDialog;
            if (d == null)
                return;

            screen.OnClick(new Rect(0, 0, width, height), () => { });   // swallow clicks outside

            int w = Math.Min(60, width - 4), h = 11;
            Rect r = new Rect((width - w) / 2, Math.Max(0, (height - h) / 2), w, h);
            screen.Fill(r, Style.Of(Ansi.BrightWhite, Ansi.Black));
            screen.Box(r, Style.Of(Ansi.BrightCyan, Ansi.Black, bold: true), heavy: true);
            screen.Text(r.X + 2, r.Y, " Key file ", Style.Of(Ansi.BrightCyan, Ansi.Black, bold: true));
            int ix = r.X + 3, iw = w - 6;

            screen.Text(ix, r.Y + 1, Path.GetFileName(KeyFilePath), Style.Of(Ansi.White, Ansi.Black), iw);
            DrawField(ix, r.Y + 3, "Password", new string('•', d.Password.Length), true, null, () => { });

            if (SecretStore.Available)
            {
                string box = d.Remember ? "[x]" : "[ ]";
                Rect remember = new Rect(ix, r.Y + 5, iw, 1);
                screen.Text(ix, remember.Y, $"{box} remember it in the keyring (Tab)", Style.Of(d.Remember ? Ansi.BrightGreen : Ansi.White, Ansi.Black), iw);
                screen.OnClick(remember, () => d.Remember = !d.Remember);
            }

            if (d.Error != null)
                screen.Text(ix, r.Y + 7, d.Error, Style.Of(Ansi.BrightRed, Ansi.Black, bold: true), iw);
            else
                screen.Text(ix, r.Y + 7, "Enter: unlock  Esc: not now", Style.Of(Ansi.Grey, Ansi.Black), iw);

            Style unlockStyle = Style.Of(Ansi.Black, Ansi.BrightCyan, bold: true), skipStyle = Style.Of(Ansi.Black, Ansi.White, bold: true);
            Rect unlock = new Rect(ix, r.Y + 9, 10, 1), skip = new Rect(ix + 12, r.Y + 9, 11, 1);
            screen.Fill(unlock, unlockStyle);
            screen.Text(unlock.X + 2, unlock.Y, "UNLOCK", unlockStyle);
            screen.OnClick(unlock, SubmitKeyFile);
            screen.Fill(skip, skipStyle);
            screen.Text(skip.X + 2, skip.Y, "NOT NOW", skipStyle);
            screen.OnClick(skip, () => KeyFileInput(new InputEvent(InputKind.Key, InputKey.Escape)));
        }
    } // public sealed partial class Tui
}
