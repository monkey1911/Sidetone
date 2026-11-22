// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks. Graphical front end.
*
* The Settings row for a codeplug with several systems: each is connected unless turned off here
* (AppSettings, SystemSettings.Off). Shown only when the codeplug has more than one system with channels;
* a change reconnects, as the codeplug is opened again.
*/

using Avalonia.Controls;
using Avalonia.Layout;

namespace Sidetone.Gui
{
    public sealed partial class SettingsOverlay
    {
        private readonly WrapPanel systemsRow = new WrapPanel { ItemSpacing = 6, LineSpacing = 6 };
        private Control systemsField;
        private readonly List<(string Name, Tap Tap)> systemTaps = new List<(string, Tap)>();
        private readonly HashSet<string> systemsOff = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The systems on show and whether each is on, and pressing one (self-check hooks).</summary>
        internal IReadOnlyList<string> SystemNames => systemTaps.Select(t => t.Name).ToList();
        internal bool SystemOn(string name) => !systemsOff.Contains(name);
        internal void PressSystem(string name) => ToggleSystem(name);
        internal bool SystemsRowShown => systemsField.IsVisible;

        private Control BuildSystemsRow()
        {
            systemsField = Field("Systems", systemsRow);
            systemsField.IsVisible = false;
            return systemsField;
        }

        private void ShowSystems(Values current)
        {
            systemTaps.Clear();
            systemsRow.Children.Clear();
            systemsOff.Clear();
            foreach ((string name, bool on) in current.Systems ?? new List<(string, bool)>())
            {
                if (!on)
                    systemsOff.Add(name);
                string n = name;
                Tap t = new Tap(name, () => ToggleSystem(n), fontSize: 13, padX: 12, padY: 7);
                systemTaps.Add((name, t));
                systemsRow.Children.Add(t.Root);
            }
            systemsField.IsVisible = systemTaps.Count > 1;
        }

        private void ToggleSystem(string name)
        {
            if (!systemsOff.Remove(name))
            {
                if (systemTaps.Count(t => !systemsOff.Contains(t.Name)) <= 1)
                {
                    message.Text = "At least one system stays on.";
                    return;
                }
                systemsOff.Add(name);
            }
            message.Text = string.Empty;
        }

        private void CollectSystems(Values v) =>
            v.Systems = systemTaps.Count == 0 ? null : systemTaps.Select(t => (t.Name, !systemsOff.Contains(t.Name))).ToList();

        private void RefreshSystems(Palette p)
        {
            foreach ((string name, Tap t) in systemTaps)
            {
                bool on = !systemsOff.Contains(name);
                t.Text = on ? $"● {name}" : $"○ {name} (off)";
                Paint(t, on, p);
            }
        }
    } // public sealed partial class SettingsOverlay
}
