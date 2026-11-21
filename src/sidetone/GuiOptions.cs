// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks. Graphical front end.
*
* The options the console view starts with: from the command line on a desktop, set by the app
* on Android.
*/

namespace Sidetone.Gui
{
    /// <summary>
    /// Command line options.
    /// </summary>
    public sealed class GuiOptions
    {
        public string Codeplug, SystemName, Output, Input, PttDevice, PttButton, MuteButton, Socket, LogFile, Theme, Screenshot, SettingsFile, Show, KeyFile;
        public bool NoSocket, Wayland, Demo, NoSettings, SelfCheck, NoNotify, NoProcessing, NoLog;
        public string RecordDir;
        /// <summary>Given on the command line; null leaves it to the saved settings.</summary>
        public bool? SpaceToggle;
        public int? TxLimit;
        public double? Scale;
        public int Width = 1280, Height = 800;

        public static GuiOptions Parse(string[] args)
        {
            GuiOptions o = new GuiOptions();
            for (int i = 0; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
                switch (args[i])
                {
                    case "--codeplug": o.Codeplug = Next(); break;
                    case "--system": o.SystemName = Next(); break;
                    case "--output": o.Output = Next(); break;
                    case "--input": o.Input = Next(); break;
                    case "--ptt-device": o.PttDevice = Next(); break;
                    case "--key-file": o.KeyFile = Next(); break;
                    case "--record-dir": o.RecordDir = Next(); break;
                    case "--no-notify": o.NoNotify = true; break;
                    case "--no-processing": o.NoProcessing = true; break;
                    case "--scale":
                    {
                        string text = Next().TrimEnd('%');
                        if (!double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double scale))
                            throw new ArgumentException("--scale must be a number, like 2 or 1.5 (or a percentage, like 150%)");
                        if (scale > DisplayScale.Max)
                            scale /= 100;       // 150 means 150%
                        if (scale < DisplayScale.Min || scale > DisplayScale.Max)
                            throw new ArgumentException("--scale must be 0.5 to 4 (50% to 400%)");
                        o.Scale = scale;
                        break;
                    }
                    case "--ptt-button": o.PttButton = Next(); break;
                    case "--mute-button": o.MuteButton = Next(); break;
                    case "--socket": o.Socket = Next(); break;
                    case "--no-socket": o.NoSocket = true; break;
                    case "--log": o.LogFile = Next(); break;
                    case "--no-log": o.NoLog = true; break;
                    case "--settings": o.SettingsFile = Next(); break;
                    case "--no-settings": o.NoSettings = true; break;
                    case "--wayland": o.Wayland = true; break;
                    case "--demo": o.Demo = true; break;
                    case "--screenshot": o.Screenshot = Next(); break;
                    case "--selfcheck": o.SelfCheck = true; break;
                    case "--show":
                        o.Show = Next().ToLowerInvariant();
                        if (o.Show is not ("page" or "digital" or "radio" or "settings" or "keyfile" or "patch" or "recall" or "tabs" or "outputs" or "announce" or "calls" or "log" or "more"))
                            throw new ArgumentException("--show must be page, digital, radio, settings, keyfile, patch, recall, tabs, outputs, announce, calls, log or more");
                        break;
                    case "--space":
                    {
                        string mode = Next().ToLowerInvariant();
                        if (mode is not ("hold" or "toggle"))
                            throw new ArgumentException("--space must be hold or toggle");
                        o.SpaceToggle = mode == "toggle";
                        break;
                    }
                    case "--theme":
                        o.Theme = Next().ToLowerInvariant();
                        if (o.Theme is not ("system" or "light" or "dark"))
                            throw new ArgumentException("--theme must be system, light or dark");
                        break;
                    case "--tx-limit":
                    {
                        if (!int.TryParse(Next(), out int limit) || limit < StartupChoices.MinTxLimit || limit > StartupChoices.MaxTxLimit)
                            throw new ArgumentException($"--tx-limit must be {StartupChoices.MinTxLimit}-{StartupChoices.MaxTxLimit} seconds");
                        o.TxLimit = limit;
                        break;
                    }
                    case "--size":
                    {
                        string[] wh = Next().ToLowerInvariant().Split('x');
                        if (wh.Length != 2 || !int.TryParse(wh[0], out o.Width) || !int.TryParse(wh[1], out o.Height) ||
                            o.Width < 400 || o.Height < 300 || o.Width > 4000 || o.Height > 3000)
                            throw new ArgumentException("--size must look like 1280x800");
                        break;
                    }
                    default:
                        throw new ArgumentException($"unknown option '{args[i]}'");
                }
            }

            if (o.PttDevice == null && (o.PttButton != null || o.MuteButton != null))
                throw new ArgumentException("--ptt-button and --mute-button need --ptt-device");
            return o;
        }
    } // public sealed class GuiOptions
}
