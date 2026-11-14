// SPDX-License-Identifier: AGPL-3.0-only
/**
* dvmcli - headless Linux proof of concept for the DVM Desktop Dispatch Console.
*
* Dedicated PTT devices (desk microphones, footswitches, PTT buttons) read straight from the
* kernel's input layer (/dev/input/event*), which sees both press and release on any desktop,
* or none. Devices are found by name in /proc/bus/input/devices, so the event number can change
* between plugs; the watcher reconnects when the device is unplugged and plugged back in.
*/

using System.Buffers.Binary;
using System.Globalization;

namespace dvmcli
{
    /// <summary>
    /// One input device as /proc/bus/input/devices lists it.
    /// </summary>
    public sealed class InputDeviceInfo
    {
        public string Name { get; init; }
        public ushort Vendor { get; init; }
        public ushort Product { get; init; }
        /// <summary>/dev/input/eventN, or null when the device has no event handler.</summary>
        public string EventPath { get; init; }
        /// <summary>Key and button codes the device reports.</summary>
        public List<int> Keys { get; init; } = new List<int>();
    }

    /// <summary>
    /// Input-device lookup and button names.
    /// </summary>
    public static class InputDevices
    {
        public const string ProcPath = "/proc/bus/input/devices";

        /// <summary>
        /// Devices with known buttons: (vendor, product) → (PTT button, mute button).
        /// </summary>
        public static readonly Dictionary<(ushort, ushort), (int Ptt, int Mute, string Label)> Known = new()
        {
            // Motorola Solutions desktop microphone: the bar is BTN_THUMB, the mute button BTN_TRIGGER
            [(0x0cad, 0x1804)] = (0x121, 0x120, "Motorola desktop microphone")
        };

        private static readonly Dictionary<string, int> names = BuildNames();

        private static Dictionary<string, int> BuildNames()
        {
            Dictionary<string, int> n = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i <= 9; i++) n[$"BTN_{i}"] = 0x100 + i;
            string[] mouse = { "LEFT", "RIGHT", "MIDDLE", "SIDE", "EXTRA", "FORWARD", "BACK", "TASK" };
            for (int i = 0; i < mouse.Length; i++) n[$"BTN_{mouse[i]}"] = 0x110 + i;
            string[] joy = { "TRIGGER", "THUMB", "THUMB2", "TOP", "TOP2", "PINKIE", "BASE", "BASE2", "BASE3", "BASE4", "BASE5", "BASE6" };
            for (int i = 0; i < joy.Length; i++) n[$"BTN_{joy[i]}"] = 0x120 + i;
            n["BTN_DEAD"] = 0x12f;
            string[] pad = { "SOUTH", "EAST", "C", "NORTH", "WEST", "Z", "TL", "TR", "TL2", "TR2", "SELECT", "START", "MODE", "THUMBL", "THUMBR" };
            for (int i = 0; i < pad.Length; i++) n[$"BTN_{pad[i]}"] = 0x130 + i;
            for (int i = 13; i <= 24; i++) n[$"KEY_F{i}"] = 183 + (i - 13);
            n["KEY_SPACE"] = 57;
            n["KEY_PAUSE"] = 119;
            n["KEY_SCROLLLOCK"] = 70;
            return n;
        }

        /// <summary>The kernel name for a code (BTN_THUMB), or "code N".</summary>
        public static string ButtonName(int code)
        {
            foreach (KeyValuePair<string, int> kv in names)
                if (kv.Value == code && (kv.Key.StartsWith("BTN_") || kv.Key.StartsWith("KEY_")))
                    return kv.Key;
            return $"code {code}";
        }

        /// <summary>Parses BTN_THUMB, thumb, KEY_F13 or a number. Returns -1 when unknown.</summary>
        public static int ParseButton(string s)
        {
            s = s?.Trim() ?? string.Empty;
            if (int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n > 0 && n < 0x300)
                return n;
            if (names.TryGetValue(s, out n) || names.TryGetValue("BTN_" + s, out n) || names.TryGetValue("KEY_" + s, out n))
                return n;
            return -1;
        }

        /// <summary>Reads the device list (empty when it can't be read).</summary>
        public static List<InputDeviceInfo> List(string procPath = ProcPath)
        {
            try { return Parse(File.ReadAllText(procPath)); }
            catch (IOException) { return new List<InputDeviceInfo>(); }
            catch (UnauthorizedAccessException) { return new List<InputDeviceInfo>(); }
        }

        /// <summary>Parses /proc/bus/input/devices.</summary>
        public static List<InputDeviceInfo> Parse(string text)
        {
            List<InputDeviceInfo> list = new List<InputDeviceInfo>();
            foreach (string block in text.Replace("\r", string.Empty).Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
            {
                string name = null, evt = null;
                ushort vendor = 0, product = 0;
                List<int> keys = new List<int>();
                foreach (string raw in block.Split('\n'))
                {
                    string line = raw.Trim();
                    if (line.StartsWith("N: Name="))
                        name = line.Substring(8).Trim('"');
                    else if (line.StartsWith("I: "))
                    {
                        foreach (string part in line.Substring(3).Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        {
                            string[] kv = part.Split('=');
                            if (kv.Length != 2) continue;
                            if (kv[0] == "Vendor") ushort.TryParse(kv[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out vendor);
                            if (kv[0] == "Product") ushort.TryParse(kv[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out product);
                        }
                    }
                    else if (line.StartsWith("H: Handlers="))
                    {
                        string e = line.Substring(12).Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(h => h.StartsWith("event"));
                        if (e != null) evt = "/dev/input/" + e;
                    }
                    else if (line.StartsWith("B: KEY="))
                    {
                        // bitmap words of the kernel's long size, most significant first
                        string[] words = line.Substring(7).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        int bits = IntPtr.Size * 8;
                        for (int w = 0; w < words.Length; w++)
                        {
                            if (!ulong.TryParse(words[words.Length - 1 - w], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong v))
                                continue;
                            for (int b = 0; b < bits && v != 0; b++, v >>= 1)
                                if ((v & 1) != 0)
                                    keys.Add(w * bits + b);
                        }
                    }
                }
                if (name != null)
                    list.Add(new InputDeviceInfo { Name = name, Vendor = vendor, Product = product, EventPath = evt, Keys = keys });
            }
            return list;
        }

        /// <summary>
        /// Finds a device by /dev/input path or by part of its name (case ignored). With several matches,
        /// prefers ones that have <paramref name="needKey"/>.
        /// </summary>
        public static InputDeviceInfo Find(List<InputDeviceInfo> devices, string spec, int needKey = -1)
        {
            if (string.IsNullOrWhiteSpace(spec))
                return null;
            spec = spec.Trim();
            List<InputDeviceInfo> hits = spec.StartsWith("/dev/")
                ? devices.Where(d => d.EventPath == spec).ToList()
                : devices.Where(d => d.EventPath != null && d.Name.Contains(spec, StringComparison.OrdinalIgnoreCase)).ToList();
            if (needKey >= 0 && hits.Any(d => d.Keys.Contains(needKey)))
                hits = hits.Where(d => d.Keys.Contains(needKey)).ToList();
            return hits.FirstOrDefault();
        }

        /// <summary>Short description of a device's buttons.</summary>
        public static string DescribeKeys(InputDeviceInfo d) =>
            d.Keys.Count == 0 ? "no buttons" :
            d.Keys.Count > 12 ? $"{d.Keys.Count} keys" :
            string.Join(", ", d.Keys.Select(ButtonName));
    } // public static class InputDevices

    /// <summary>
    /// Watches one PTT device: raises PTT press/release and mute presses, reconnects after unplugging.
    /// </summary>
    public sealed class PttDeviceWatcher : IDisposable
    {
        public const ushort EV_KEY = 1;

        private readonly string spec;
        private readonly string pttSpec, muteSpec;
        private readonly string procPath;
        private volatile bool stopping;
        private FileStream current;
        private bool pttDown;

        /// <summary>PTT pressed (true) or released (false).</summary>
        public event Action<bool> Ptt;
        /// <summary>Mute button pressed.</summary>
        public event Action Mute;
        /// <summary>Messages for the log.</summary>
        public event Action<string> Status;

        /// <summary>The PTT button code in use (after the device was found).</summary>
        public int PttCode { get; private set; } = -1;
        /// <summary>The mute button code in use, or -1.</summary>
        public int MuteCode { get; private set; } = -1;
        /// <summary>Whether the device is open.</summary>
        public bool Present => current != null;

        /// <param name="device">Part of the device name, or a /dev/input/eventN path.</param>
        /// <param name="pttButton">PTT button, or null for the device's known button or its only one.</param>
        /// <param name="muteButton">Mute button, "none", or null for the device's known one.</param>
        public PttDeviceWatcher(string device, string pttButton, string muteButton, string procPath = InputDevices.ProcPath)
        {
            spec = device;
            pttSpec = pttButton;
            muteSpec = muteButton;
            this.procPath = procPath;
            if (pttButton != null && InputDevices.ParseButton(pttButton) < 0)
                throw new ArgumentException($"--ptt-button '{pttButton}' isn't a button name (like BTN_THUMB) or code");
            if (muteButton != null && !muteButton.Equals("none", StringComparison.OrdinalIgnoreCase) && InputDevices.ParseButton(muteButton) < 0)
                throw new ArgumentException($"--mute-button '{muteButton}' isn't a button name (like BTN_TRIGGER), a code, or none");
        }

        public void Start() => new Thread(Run) { IsBackground = true, Name = "dvmcli ptt device" }.Start();

        /// <summary>
        /// Chooses the PTT and mute buttons for a device. Returns null on success, or why not.
        /// </summary>
        public string ChooseButtons(InputDeviceInfo d)
        {
            InputDevices.Known.TryGetValue((d.Vendor, d.Product), out var known);
            bool isKnown = known.Label != null;

            int ptt = pttSpec != null ? InputDevices.ParseButton(pttSpec)
                    : isKnown ? known.Ptt
                    : d.Keys.Count == 1 ? d.Keys[0] : -1;
            if (ptt < 0)
                return $"it has several buttons ({InputDevices.DescribeKeys(d)}); choose one with --ptt-button";
            if (!d.Keys.Contains(ptt))
                return $"it has no {InputDevices.ButtonName(ptt)} (it has {InputDevices.DescribeKeys(d)})";

            int mute = muteSpec == null ? (isKnown ? known.Mute : -1)
                     : muteSpec.Equals("none", StringComparison.OrdinalIgnoreCase) ? -1 : InputDevices.ParseButton(muteSpec);
            if (mute == ptt || (mute >= 0 && !d.Keys.Contains(mute)))
                mute = -1;

            PttCode = ptt;
            MuteCode = mute;
            return null;
        }

        private void Run()
        {
            bool announcedWait = false, announcedFail = false;
            while (!stopping)
            {
                InputDeviceInfo d = InputDevices.Find(InputDevices.List(procPath), spec,
                                                      pttSpec != null ? InputDevices.ParseButton(pttSpec) : -1);
                if (d == null)
                {
                    if (!announcedWait)
                        Status?.Invoke($"PTT device: '{spec}' isn't plugged in; waiting for it");
                    announcedWait = true;
                    Sleep(2000);
                    continue;
                }

                string why = ChooseButtons(d);
                if (why != null)
                {
                    Status?.Invoke($"PTT device: can't use {d.Name}: {why}");
                    return;
                }

                try
                {
                    current = new FileStream(d.EventPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.None);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
                {
                    if (!announcedFail)
                        Status?.Invoke($"PTT device: can't open {d.EventPath} ({ex.Message}); see the README on permissions");
                    announcedFail = true;
                    Sleep(2000);
                    continue;
                }

                announcedWait = announcedFail = false;
                Status?.Invoke($"PTT device: {d.Name} ({d.EventPath}): PTT {InputDevices.ButtonName(PttCode)}" +
                               (MuteCode >= 0 ? $", mute {InputDevices.ButtonName(MuteCode)}" : string.Empty));
                try
                {
                    Pump(current);
                }
                finally
                {
                    try { current?.Dispose(); } catch { /* ignore */ }
                    current = null;
                }
                if (pttDown)
                {
                    pttDown = false;
                    Ptt?.Invoke(false);     // unplugged mid-transmission: release
                }
                if (!stopping)
                    Status?.Invoke($"PTT device: {d.Name} was unplugged; waiting for it to come back");
                announcedWait = true;
                Sleep(1000);
            }
        }

        /// <summary>
        /// Reads input events until the stream ends or fails (the device was unplugged).
        /// </summary>
        public void Pump(Stream stream)
        {
            // struct input_event: struct timeval (two longs), __u16 type, __u16 code, __s32 value
            int timeSize = IntPtr.Size * 2;
            int size = timeSize + 8;
            byte[] buf = new byte[size * 16];
            int have = 0;
            try
            {
                while (!stopping)
                {
                    int n = stream.Read(buf, have, buf.Length - have);
                    if (n <= 0)
                        return;
                    have += n;
                    int off = 0;
                    for (; have - off >= size; off += size)
                    {
                        ushort type = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(off + timeSize));
                        ushort code = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(off + timeSize + 2));
                        int value = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(off + timeSize + 4));
                        OnEvent(type, code, value);
                    }
                    Buffer.BlockCopy(buf, off, buf, 0, have - off);
                    have -= off;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
            {
                // ENODEV after unplugging, or closed on shutdown
            }
        }

        /// <summary>Handles one event (value 1 press, 0 release, 2 auto-repeat).</summary>
        public void OnEvent(ushort type, ushort code, int value)
        {
            if (type != EV_KEY || value == 2)
                return;
            if (code == PttCode)
            {
                bool down = value != 0;
                if (down == pttDown)
                    return;
                pttDown = down;
                Ptt?.Invoke(down);
            }
            else if (code == MuteCode && value == 1)
                Mute?.Invoke();
        }

        /// <summary>Builds one input_event as the kernel writes it (self-test).</summary>
        public static byte[] EncodeEvent(ushort type, ushort code, int value)
        {
            int timeSize = IntPtr.Size * 2;
            byte[] b = new byte[timeSize + 8];
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(timeSize), type);
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(timeSize + 2), code);
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(timeSize + 4), value);
            return b;
        }

        private void Sleep(int ms)
        {
            for (int t = 0; t < ms && !stopping; t += 100)
                Thread.Sleep(100);
        }

        public void Dispose()
        {
            stopping = true;
            try { current?.Dispose(); } catch { /* ignore */ }
        }
    } // public sealed class PttDeviceWatcher
}
