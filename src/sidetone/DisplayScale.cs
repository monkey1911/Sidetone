// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks. Graphical front end.
*
* Display scale for high-DPI screens. Under XWayland the window often gets no scale from the
* desktop and comes out tiny, so the scale is worked out here before the window system starts:
* the command line's, else the one saved in Settings, else one already in the environment, else
* (on Hyprland with unscaled XWayland windows) the monitor's own scale. It is handed to Avalonia
* through AVALONIA_GLOBAL_SCALE_FACTOR, which is why a change needs a restart. The native Wayland
* backend scales by itself and is left alone.
*/

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Sidetone.Gui
{
    public static class DisplayScale
    {
        public const double Min = 0.5, Max = 4.0;
        private const string GlobalVar = "AVALONIA_GLOBAL_SCALE_FACTOR", PerScreenVar = "AVALONIA_SCREEN_SCALE_FACTORS";

        /// <summary>What was decided at startup, for Settings to show (like "244%, from Hyprland").</summary>
        public static string InEffect { get; private set; } = "set by the desktop";

        /// <summary>
        /// Decides the scale and sets it for the window system. Call before the application starts.
        /// </summary>
        /// <param name="option">--scale, or null.</param>
        /// <param name="saved">The scale saved in Settings, or null for automatic.</param>
        /// <param name="wayland">The native Wayland backend is in use.</param>
        public static void Apply(double? option, double? saved, bool wayland)
        {
            if (wayland)
            {
                InEffect = "set by the desktop (native Wayland)";
                return;
            }

            bool fromEnvironment = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(GlobalVar)) ||
                                   !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PerScreenVar));
            double? scale = null;
            string source = null;
            if (option != null) { scale = option; source = "from --scale"; }
            else if (saved != null) { scale = saved; source = "chosen here"; }
            else if (fromEnvironment)
            {
                InEffect = "set by the environment";
                return;
            }
            else
            {
                scale = DetectHyprland();
                source = "from Hyprland";
            }

            if (scale == null)
                return;
            double value = Math.Clamp(scale.Value, Min, Max);
            Environment.SetEnvironmentVariable(PerScreenVar, null);     // the single factor is the one that counts
            Environment.SetEnvironmentVariable(GlobalVar, value.ToString("0.###", CultureInfo.InvariantCulture));
            InEffect = $"{Percent(value)}, {source}";
        }

        public static string Percent(double scale) => $"{Math.Round(scale * 100):0}%";

        /// <summary>
        /// On Hyprland, when XWayland windows are left unscaled (xwayland:force_zero_scaling), the
        /// focused monitor's scale. Null anywhere else, or when hyprctl can't be asked.
        /// </summary>
        private static double? DetectHyprland()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HYPRLAND_INSTANCE_SIGNATURE")))
                return null;
            string option = Run("hyprctl", "getoption", "xwayland:force_zero_scaling", "-j");
            string monitors = option == null ? null : Run("hyprctl", "monitors", "-j");
            return FromHyprland(option, monitors);
        }

        /// <summary>Works the scale out from hyprctl's answers (see <see cref="DetectHyprland"/>).</summary>
        public static double? FromHyprland(string optionJson, string monitorsJson)
        {
            if (string.IsNullOrWhiteSpace(optionJson) || string.IsNullOrWhiteSpace(monitorsJson))
                return null;
            try
            {
                using JsonDocument opt = JsonDocument.Parse(optionJson);
                bool unscaled = false;
                if (opt.RootElement.ValueKind == JsonValueKind.Object)
                {
                    if (opt.RootElement.TryGetProperty("int", out JsonElement i) && i.ValueKind == JsonValueKind.Number)
                        unscaled = i.GetInt32() != 0;
                    else if (opt.RootElement.TryGetProperty("bool", out JsonElement b))
                        unscaled = b.ValueKind == JsonValueKind.True;
                }
                if (!unscaled)
                    return null;    // the compositor is already enlarging XWayland windows

                using JsonDocument mons = JsonDocument.Parse(monitorsJson);
                if (mons.RootElement.ValueKind != JsonValueKind.Array)
                    return null;
                double? focused = null, largest = null;
                foreach (JsonElement m in mons.RootElement.EnumerateArray())
                {
                    if (!m.TryGetProperty("scale", out JsonElement s) || s.ValueKind != JsonValueKind.Number)
                        continue;
                    double scale = s.GetDouble();
                    if (scale < Min || scale > Max)
                        continue;
                    if (m.TryGetProperty("focused", out JsonElement f) && f.ValueKind == JsonValueKind.True)
                        focused = scale;
                    largest = largest == null ? scale : Math.Max(largest.Value, scale);
                }
                double? result = focused ?? largest;
                return result != null && Math.Abs(result.Value - 1.0) < 0.01 ? null : result;
            }
            catch (Exception ex) when (ex is JsonException || ex is InvalidOperationException || ex is FormatException)
            {
                return null;
            }
        }

        private static string Run(string program, params string[] args)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                foreach (string a in args)
                    psi.ArgumentList.Add(a);
                using Process p = Process.Start(psi);
                if (p == null)
                    return null;
                Task<string> output = p.StandardOutput.ReadToEndAsync();
                _ = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(2000))
                {
                    try { p.Kill(true); } catch (Exception) { }
                    return null;
                }
                return p.ExitCode == 0 && output.Wait(500) ? output.Result : null;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException || ex is IOException)
            {
                return null;
            }
        }
    } // public static class DisplayScale
}
