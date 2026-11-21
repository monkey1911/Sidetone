// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks. Graphical front end.
*
* The Settings rows only a device (Android) has: a hardware PTT key, learned by pressing it;
* keeping the screen on while connected; and a way to the battery settings, where Sidetone can be
* let run unrestricted so it stays connected with the screen off.
*/

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Sidetone.Gui
{
    public sealed partial class SettingsOverlay
    {
        private readonly TextBlock pttKeyText = new TextBlock { FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock deviceNote = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        private Tap pttLearn, pttNone, screenOn, screenOff, wakeOn, wakeOff, battery;
        private int? pttKey;
        private string pttKeyName;
        private bool keepScreenOn, wakeOnCall = true, learning;

        /// <summary>Whether the screen is woken for incoming calls, and pressing its buttons (self-check hooks).</summary>
        internal bool WakeOnCall => wakeOnCall;
        internal void PressWake(bool on) => wakeOnCall = on;

        /// <summary>The hardware PTT key chosen so far, and whether a key is being waited for (self-check hooks).</summary>
        internal int? PttKey => pttKey;
        internal bool Learning => learning;

        /// <summary>Presses Learn… (self-check hook).</summary>
        internal void PressLearn() => StartLearning();

        /// <summary>Presses Save (self-check hook).</summary>
        internal void PressSave() => Save();

        private Control[] BuildDeviceRows()
        {
            pttLearn = new Tap("Learn…", StartLearning, fontSize: 13, padX: 12, padY: 7);
            pttNone = new Tap("None", () => { StopLearning(); pttKey = null; pttKeyName = null; message.Text = string.Empty; }, fontSize: 13, padX: 12, padY: 7);
            StackPanel keyButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            keyButtons.Children.Add(pttLearn.Root);
            keyButtons.Children.Add(pttNone.Root);
            StackPanel keyRow = new StackPanel { Spacing = 6 };
            keyRow.Children.Add(pttKeyText);
            keyRow.Children.Add(keyButtons);

            screenOn = new Tap("Keep it on", () => keepScreenOn = true, fontSize: 13, padX: 12, padY: 7);
            screenOff = new Tap("Let it sleep", () => keepScreenOn = false, fontSize: 13, padX: 12, padY: 7);
            StackPanel screenRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            screenRow.Children.Add(screenOn.Root);
            screenRow.Children.Add(screenOff.Root);

            wakeOn = new Tap("Wake the screen", () => wakeOnCall = true, fontSize: 13, padX: 12, padY: 7);
            wakeOff = new Tap("Leave it off", () => wakeOnCall = false, fontSize: 13, padX: 12, padY: 7);
            StackPanel wakeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            wakeRow.Children.Add(wakeOn.Root);
            wakeRow.Children.Add(wakeOff.Root);

            battery = new Tap("Battery settings…", () => GuiPlatform.Current?.OpenBatterySettings(), fontSize: 13, padX: 12, padY: 7);
            battery.Root.HorizontalAlignment = HorizontalAlignment.Left;

            deviceNote.Text = "Incoming calls: when the screen is off, a call on a listening channel turns it on for a moment (muted channels don't). " +
                              "A hardware PTT key works while Sidetone is on screen; Bluetooth and headset buttons also with the screen off. " +
                              "To stay connected with the screen off, let Sidetone run without battery restrictions.";
            return new Control[] { Field("PTT key", keyRow), Field("Screen", screenRow), Field("Incoming calls", wakeRow), Field("Battery", battery.Root), deviceNote };
        }

        private void ShowDeviceValues(Values current)
        {
            StopLearning();
            pttKey = current.HardwarePttKey;
            pttKeyName = current.HardwarePttKeyName;
            keepScreenOn = current.KeepScreenOn;
            wakeOnCall = current.WakeOnCall;
        }

        private void CollectDeviceValues(Values v)
        {
            v.HardwarePttKey = pttKey;
            v.HardwarePttKeyName = pttKeyName;
            v.KeepScreenOn = keepScreenOn;
            v.WakeOnCall = wakeOnCall;
        }

        private void StartLearning()
        {
            if (GuiPlatform.Current == null)
                return;
            learning = true;
            message.Text = string.Empty;
            GuiPlatform.Current.LearnPttKey((code, name) =>
            {
                learning = false;
                pttKey = code;
                pttKeyName = name;
            });
        }

        private void StopLearning()
        {
            if (!learning)
                return;
            learning = false;
            GuiPlatform.Current?.CancelLearn();
        }

        /// <summary>A key's name as people know it: KEYCODE_VOLUME_DOWN → Volume down.</summary>
        internal static string KeyLabel(string name, int code)
        {
            if (string.IsNullOrWhiteSpace(name))
                return $"key {code}";
            string n = name.StartsWith("KEYCODE_", StringComparison.Ordinal) ? name.Substring(8) : name;
            if (int.TryParse(n, out _))
                return $"key {code}";
            n = n.Replace('_', ' ').ToLowerInvariant();
            return char.ToUpperInvariant(n[0]) + n.Substring(1);
        }

        private void RefreshDevice(Palette p)
        {
            if (GuiPlatform.Current == null)
                return;
            pttKeyText.Text = learning ? "Press the button you want to use for PTT…"
                            : pttKey == null ? "None: only the on-screen PTT."
                            : $"{KeyLabel(pttKeyName, pttKey.Value)} (key code {pttKey.Value})";
            pttKeyText.Foreground = learning ? p.Accent : p.Text;
            if (learning)
                pttLearn.Paint(p.Accent, p.OnStrong);
            else
                pttLearn.Paint(Brushes.Transparent, p.Text, p.Edge);
            pttLearn.Text = learning ? "Waiting…" : "Learn…";
            pttNone.Paint(Brushes.Transparent, p.Text, p.Edge);
            pttNone.Root.IsVisible = pttKey != null || learning;
            Paint(screenOn, keepScreenOn, p);
            Paint(screenOff, !keepScreenOn, p);
            Paint(wakeOn, wakeOnCall, p);
            Paint(wakeOff, !wakeOnCall, p);
            battery.Paint(Brushes.Transparent, p.Text, p.Edge);
            deviceNote.Foreground = p.Dim;
        }
    } // public sealed partial class SettingsOverlay
}
