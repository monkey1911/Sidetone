# Sidetone: an IP dispatch console for DVMProject FNE networks

Sidetone is a dispatch console for [DVMProject](https://github.com/DVMProject) FNE networks. It runs on Linux, on ARM64 (aarch64) and x86-64, and is built on the [DVM Desktop Dispatch Console](https://github.com/DVMProject/dvmconsole). It was made for Omarchy on a Surface Pro 11 and runs the same way on x86-64 PCs. It connects to an FNE as a console peer using your dvmconsole codeplug, receives and transmits P25 voice with libvocoder, and plays it through PipeWire.

Up to version 0.16 this project was called dvmconsole-linux, and its programs were `dvmcli` and `dvmgui`. See [Coming from dvmcli and dvmgui](#coming-from-dvmcli-and-dvmgui).

There are two front ends over the same console:

- **`sidetone-cli`**: a terminal console (TUI) that works anywhere, including over SSH, plus the `listen`, `ptt` and `devices` commands.
- **`sidetone`**: a graphical console (Avalonia), with light and dark themes. See [Graphical console (sidetone)](#graphical-console-sidetone).

**What works:** FNE login, P25 receive and transmit (clear, AES-256, DES, ADP/ARC4 with keys from the FNE), PipeWire audio, channel cards, push-to-talk from the keyboard, a hotkey or a PTT device, alert tones, pages, radio commands, the channel marker, radio aliases, and WAV recording.

**Not yet:** DMR/NXDN/analog. What we'd like to add next is in [WISHLIST.md](WISHLIST.md).

## Install

Each release on the [Releases](../../releases) page has one download per architecture (CI runs keep the same two as artifacts):

| Machine | Download |
|---|---|
| ARM64 (Surface Pro 11 and other aarch64) | `sidetone-*-linux-arm64.tar.gz` |
| x86-64 (Intel / AMD PCs) | `sidetone-*-linux-x64.tar.gz` |

If you're not sure which one a machine needs, this prints `aarch64` or `x86_64`:

```bash
uname -m
```

Extract the ARM64 build:

```bash
tar -xzf ~/Downloads/sidetone-*-linux-arm64.tar.gz -C ~
```

Or the x86-64 build:

```bash
tar -xzf ~/Downloads/sidetone-*-linux-x64.tar.gz -C ~
```

Each download has both programs, `sidetone-cli` and `sidetone`. The builds are self-contained, so no .NET install is needed. They are built on Ubuntu 24.04, so any distribution of that age or newer runs them, including current Arch / Omarchy and Fedora. Playback uses `pw-play` and transmit uses `pw-record`, which ship with PipeWire. `sidetone` also needs the usual desktop libraries (X11 and fontconfig), which any graphical install already has.

## Codeplug

Sidetone reads the same codeplug YAML as the Windows console. Give it a **separate peer ID** from any console that is online at the same time, because the FNE accepts each peer ID only once. Copy your codeplug and change `peerId`, `identity` and `password` under the system to the second peer's values.

Keep codeplugs out of git; they contain passwords. This repo's `.gitignore` ignores `*.yml` outside the workflows folder.

## Use

Change into the extracted folder (the name matches the release and architecture you downloaded):

```bash
cd ~/sidetone-*-linux-*
```

Run the offline self-test (vocoder, frame decoding, codeplug parsing):

```bash
./sidetone-cli selftest
```

List the systems and channels in your codeplug:

```bash
./sidetone-cli list --codeplug ~/dvm/codeplug-linux.yml
```

Follow a talkgroup by channel name and play it:

```bash
./sidetone-cli listen --codeplug ~/dvm/codeplug-linux.yml --channel "DVM Calling"
```

Other `listen` options:

| Option | What it does |
|---|---|
| `--tg TGID` | Follow a talkgroup by number instead of channel name |
| `--system NAME` | Pick the system (default: the channel's, or the first one) |
| `--wav FILE` | Also record decoded audio to a WAV file |
| `--no-play` | Don't play audio (useful with `--wav`, or to just watch activity) |
| `--gain N` | Multiply output volume, for example `2` |
| `--output DEVICE` | Play to this audio output instead of the system default (see [Audio devices](#audio-devices)) |
| `--player CMD` | Use a different player command than `pw-play` |
| `--verbose` | Log every network frame and fnecore debug messages |

Press Ctrl+C to stop.

## Terminal console (TUI)

Start the terminal console for the codeplug's first system (or pick one with `--system NAME`):

```bash
./sidetone-cli tui --codeplug ~/dvm/codeplug-linux.yml
```

Channels are shown as cards, up to four across. The mouse works in any terminal with xterm mouse reporting (Alacritty, Ghostty, Kitty, foot and most others):

| Click | Action |
|---|---|
| **PTT** (title bar) | Toggle transmit on the focused card (click again to stop) |
| **MUTE** (title bar) | Mute or unmute all receive audio |
| **T1**–**T6** (title bar) | Send alert tone 1–6 on every listening channel (click again to stop) |
| **PAGE** (title bar) | Page: a QuickCall II tone page on the listening channels, or a digital page (P25 call alert) to one radio ID |
| **RADIO** (title bar) | Radio command: enter a radio ID, pick Check, Inhibit or Uninhibit, then SEND |
| **MARK** (title bar) | Turn the channel marker on or off |
| **EXIT** (title bar) | Quit |
| **MIC** meter (bottom edge of the title box) | Open or close the microphone for the level meter |
| A card | Focus it and start listening |
| `[●]` on a card | Stop listening |
| `[-]` `[+]` `[mute]` on a card | That card's volume and mute |
| Wheel | Scroll the cards, or the log when the pointer is over it |

| Key | Action |
|---|---|
| Arrows, Tab, PgUp / PgDn, Home / End | Move between cards |
| Enter | Start or stop listening on the focused card |
| Space | Toggle transmit on the focused card (press again to stop) |
| Esc | Stop transmitting or stop a tone |
| 1 – 6 | Send alert tone 1–6 on every listening channel |
| r | Radio command box (Tab or arrows pick the action, Enter sends) |
| t | Talk permit tone on or off |
| p / d | Open the page box in tone or digital mode (←/→ switch modes, Tab switches tone A / B, Enter sends, Esc cancels) |
| c | Turn the channel marker on or off |
| v | Microphone level meter on or off (without transmitting) |
| u | Mute or unmute the microphone |
| a | Reload the alias file and download aliases from the FNE again |
| m / M | Mute the focused card / mute everything |
| + / - | Volume of the focused card |
| k | Request the focused card's key from the FNE again |
| e | Switch the focused card's transmit between secure and clear, where the codeplug allows it |
| x | Move the focused card to the next patch (none, then each patch in turn) |
| l | Replay the focused card's last call (again to stop) |
| L | Replay the call before the one playing, stepping back through the last ten |
| w | Turn recording on the focused card on or off |
| q or Ctrl+C | Quit |

Alert tones match the Windows console's built-in tones 1–6 (5 seconds each). A digital page is a P25 call alert from the console's RID to one radio (the Windows console's "Page Subscriber"); call alerts and acknowledgements addressed to the console's RID are logged when the FNE passes them on. A QuickCall II page is tone A for 1 s then tone B for 3 s (250–3000 Hz, as the Windows console's manual page), sent on every listening channel; the last page's tones are filled in next time. On P25, tones travel as IMBE tone frames, which carry the frequency in steps of about 31 Hz, so radios regenerate the nearest step. The channel marker is the console's "channel hold" beep: 0.5 s of 650 Hz every 10 seconds on each listening channel that isn't busy; change the interval with `--marker-interval SECONDS`. Tones are encrypted on secure channels and skipped on channels whose key isn't held. The tone buttons are hidden on terminals narrower than 110 columns; the keys still work.

Radio check, inhibit and uninhibit are sent as in the Windows console. Inhibit disables the radio, so it asks you to press SEND a second time. Replies ("radio 1234 answered the radio check", inhibit / uninhibit confirmations) are logged when the FNE passes them back to the console, which not every FNE version does. Without that, the command still reaches the radio; the console just doesn't hear the answer.

The talk permit tone (a short 1200 Hz beep when transmit starts) is on by default; `t` turns it off.

The microphone level meter sits in the bottom edge of the title box. It shows whenever the microphone is open: always while you transmit (labelled `TX MIC`), and when you press `v` or click it to check your level without transmitting. `v` leaves the microphone open until you press it again, but nothing is sent. The bar is green up to -18 dB, yellow to -6 dB and red above, `▌` marks the recent peak, and `CLIP!` means the input hit full scale. For clean audio, keep speech mostly in the green to yellow and never clipping. Adjust the level with your system's input volume, for example `wpctl set-volume @DEFAULT_AUDIO_SOURCE@ 0.8`.

Settings are remembered per system in `~/.config/sidetone/settings.json`: which channels you're listening to, the focused card, volumes, mutes, global mute, the talk permit tone and the last page / radio values. Use `--settings FILE` for a different file or `--no-settings` to start fresh without saving. The channel marker always starts off. The file holds no passwords or keys.

Transmit uses the system's `rid` as the source ID, and the default microphone unless you start the TUI with `--input` (see [Audio devices](#audio-devices)). A secure channel never transmits in the clear by accident: without its key, transmit is refused. The one exception is a channel the codeplug marks as selectable, which you can switch to clear yourself (see [Encryption keys](#encryption-keys)). Transmissions stop automatically after 120 seconds; change that with `--tx-limit SECONDS`. Add `--log FILE` to keep a copy of the log.

## Graphical console (sidetone)

`sidetone` is in the same download as `sidetone-cli` and uses the same codeplug, settings and options. Start it with a codeplug:

```bash
./sidetone --codeplug ~/dvm/codeplug-linux.yml
```

After the first time, `./sidetone` on its own reopens the last codeplug. With no codeplug at all it shows an **Open codeplug…** button.

What it has so far:

- a card per channel: talkgroup, encryption and key, what the channel is doing, the last call, and Listen, volume and Mute. Clicking a card focuses it (PTT transmits there) and starts listening, as in the TUI.
- **PTT**: hold it to talk, let go to stop. Each card also has its own small **PTT**, which keys that channel without moving the focus to it.
- **MUTE** for all receive audio, **Mute mic**, and **Mic check** with a level meter
- **Settings** and **Quit** in the top right (Quit is there for desktops without window buttons)
- a tool bar under the header:
  - **Alert tones 1 to 6**, sent on every listening channel. While a tone is going out, the PTT button reads **STOP TONE**; click it or press Esc to stop.
  - **Page…** opens a box for a two-tone (QuickCall II) page on every listening channel, or a digital page (P25 call alert) to one radio. The last values are filled in.
  - **Radio command…** sends a check, inhibit or uninhibit to one radio. Inhibit has to be sent twice to confirm. The radio's alias shows as you type its ID. Replies appear in the log when the FNE passes them back to the console, which not every FNE version does.
  - **Channel marker** turns the periodic beep on idle listening channels on and off
  - **Patch…** opens the Patches box (see [Patching](#patching))
  - **Recall…** opens Instant recall (see [Instant recall and recording](#instant-recall-and-recording))
- a transmit bar, recent calls and the log
- **Auto / Light / Dark** in the top right. Auto follows your desktop's light or dark setting. The choice is remembered. `--theme light|dark|system` sets it from the command line.

### Settings

**Settings** in the top right, or Ctrl+comma, opens a box for the things that were command-line options only:

| Setting | What it does |
|---|---|
| Scale | The size of everything in the window, for high-DPI screens. Automatic, or 100% to 300%. Takes effect the next time Sidetone starts. |
| Output, Microphone | The audio devices to use, or the system default |
| Mic processing | Low cut, Auto level, Noise gate and Limiter on your transmit audio (see [Audio processing](#audio-processing)) |
| Receive | Even out the level of received audio between talkers |
| PTT device | A desk microphone, footswitch or PTT button that keys the console (see [PTT devices](#ptt-devices)) |
| PTT button, Mute button | Which of that device's buttons do what. Automatic uses the buttons Sidetone knows for the device, or its only button. |
| Space and PTT | Hold to talk, or press to start and press again to stop. Applies to Space and the on-screen PTT buttons. |
| Recordings | Where recordings go, and a button to open that folder |
| When paged | Desktop notifications when the console is paged, on or off |
| Key file | A local key file to use, and its unlock state (see [Encryption keys](#encryption-keys)) |
| Time limit | How long a transmission can last, 5 to 600 seconds |
| Codeplug | Shows the open codeplug, and opens another one |
| App launcher | Adds Sidetone to, or removes it from, your desktop's app launcher |

**Save** remembers them in the settings file. Changing audio, the PTT device or the time limit reconnects the console, which takes a moment. You can't save while transmitting.

If a saved audio device isn't connected when Sidetone starts, it uses the system default and says so in the log. The saved choice is kept for next time.

A command-line option still wins for the run it was given on, and Settings says so when one is in effect.

### App launcher

To start Sidetone from your desktop's launcher instead of a terminal, open **Settings** and click **Add to app launcher**. This writes a launcher entry and the icon into your own data folder (`~/.local/share`), which GNOME, KDE and the launchers used with Hyprland and Sway all read. Nothing is installed system-wide.

It can also be done from a terminal:

```bash
./sidetone --install-launcher
```

To take it out again:

```bash
./sidetone --remove-launcher
```

The entry points at the folder Sidetone is in. When you extract a newer version to a different folder and start it once, the entry follows it.

Started from the launcher, Sidetone reopens the last codeplug, or shows **Open codeplug…** the first time.

### Command-line options

The audio, PTT device and PTT socket options are the same as `sidetone-cli tui`: `--output`, `--input`, `--ptt-device`, `--ptt-button`, `--mute-button`, `--socket`, `--no-socket`, `--tx-limit`, `--log`, `--system`, `--settings`, `--no-settings`. `sidetone-cli ptt down` and friends control whichever console is running.

Keyboard shortcuts (the same keys as the TUI where both have the feature):

| Key | What it does |
|---|---|
| Space | Push-to-talk on the focused channel: hold to talk, let go to stop |
| Esc | Stop transmitting |
| Arrow keys | Move the focus between cards |
| Enter | Start or stop listening on the focused card |
| M / Shift+M | Mute the focused channel / mute all receive audio |
| + / - | Volume of the focused channel |
| U | Mute or unmute the microphone |
| V | Mic check (level meter) on or off |
| K | Request the focused channel's key from the FNE |
| E | Switch the focused channel's transmit between secure and clear, where the codeplug allows it |
| X | Move the focused channel to the next patch (none, then each patch in turn) |
| L | Replay the focused channel's last call (again to stop) |
| Shift+L | Open Instant recall for the focused channel |
| W | Turn recording on the focused channel on or off |
| A | Reload the alias file and download aliases again |
| T | Talk permit tone on or off |
| 1 to 6 | Send an alert tone on every listening channel |
| P / D | Open the page box: two-tone / digital |
| R | Open the radio command box |
| C | Channel marker on or off |
| Ctrl+, | Open Settings |
| Ctrl+Q | Quit |

In the page and radio command boxes, Enter sends, Esc closes, and the other shortcut keys are just typing.

Space and the on-screen PTT buttons are hold-to-talk here because a window, unlike a terminal, can tell when a key is let go. If the window loses the keyboard while you're holding it, the transmission ends. To make them start and stop instead, as in the TUI, choose that in Settings or start with `--space toggle`.

**Wayland.** On Wayland desktops (Hyprland, GNOME, KDE) the window runs through XWayland by default. `--wayland` uses Avalonia's native Wayland backend instead, which is newer and still missing some features. 

**High-DPI screens.** Under XWayland a window often isn't told the screen's scale, and everything comes out tiny. Sidetone picks the scale in this order:

1. `--scale 2` (or `--scale 150%`) on the command line
2. **Scale** in Settings, when it isn't Automatic
3. `AVALONIA_GLOBAL_SCALE_FACTOR`, if it's already set in the environment
4. on Hyprland, the focused monitor's scale, when Hyprland is set to leave XWayland windows unscaled (`xwayland { force_zero_scaling = true }`, which Omarchy uses)
5. otherwise, whatever the desktop reports

Settings shows which one is in effect. A change needs a restart. With `--wayland` the desktop scales the window itself and none of this applies.

To look around without connecting to anything, `./sidetone --demo` shows a made-up system.

## Audio devices

Without options, Sidetone plays to the system's default output and transmits from the default input, the ones marked `*` in `wpctl status`. Both `listen` and `tui` log which devices they're using at startup, for example:

```
audio output: Plantronics Savi 8200 Office Series Analog Stereo (system default)
audio input: Plantronics Savi 8200 Office Series Mono (system default)
```

List the outputs and inputs PipeWire knows about:

```bash
./sidetone-cli devices
```

To use a different device, give `--output` (playback) or `--input` (microphone, `tui` only) any part of its name that matches only that device. Case doesn't matter:

```bash
./sidetone-cli tui --codeplug ~/dvm/codeplug-linux.yml --output "Savi" --input "Savi"
```

The ID from `sidetone-cli devices` or `wpctl status` works too, but IDs can change after a reboot or replug, so names are more reliable. If a name matches nothing or more than one device, Sidetone stops and lists the choices. If the chosen device disappears while Sidetone is running, PipeWire moves the audio to the default device.

### Microphone mute and the no-audio warning

`u`, or a PTT device's mute button, mutes the microphone. While it's muted, the title bar shows `MIC MUTED` in red where the level meter is, and the bottom line says so too. PTT still keys up, but sends silence, and the transmit bar shows `MIC MUTED`. Press `u` or the mute button again, or click `MIC MUTED`, to unmute. The mute isn't remembered between runs, so Sidetone always starts unmuted.

If you transmit unmuted and the microphone stays near-silent for two seconds, the log and the transmit bar show `NO AUDIO FROM THE MICROPHONE`. That usually means the wrong input, a mic turned down or unplugged, or a mic with its own mute switch.

## PTT from hotkeys and scripts

While a console is running (the TUI or the graphical one), other programs can key it up through a control socket, so you can use a desktop hotkey, a script, or your own helper app. It works the same on any distribution or desktop. The socket is `$XDG_RUNTIME_DIR/sidetone.sock`, and only your user can open it. The console logs its path at startup.

`sidetone-cli ptt` is the ready-made client. It transmits on the focused channel, the same as Space:

| Command | What it does |
|---|---|
| `sidetone-cli ptt down` | Start transmitting |
| `sidetone-cli ptt up` | Stop transmitting |
| `sidetone-cli ptt toggle` | Start or stop |
| `sidetone-cli ptt hold` | Transmit until this command is stopped (Ctrl+C, SIGTERM, or the process dies) |
| `sidetone-cli ptt status` | Say whether the console is transmitting, and on which channel |

Each command prints the console's reply and exits with 0 for `ok` and 1 for `error` (for example `error: not connected to the FNE yet`). The transmit time limit still applies.

**Hold-to-talk in Hyprland**: bind a key's press and release:

```
bind = , F13, exec, sidetone-cli ptt down
```

```
bindr = , F13, exec, sidetone-cli ptt up
```

In Sway, use `bindsym` and `bindsym --release`. Desktops whose shortcuts only fire on press, such as GNOME and KDE custom shortcuts, can use `sidetone-cli ptt toggle`. Give the full path to `sidetone-cli` if it isn't on your `PATH`.

**`ptt hold`** is the safest choice for helpers: transmit lasts only while the command runs, so if the helper crashes or is killed, the console unkeys. The command exits when the transmission ends some other way, such as Space, `ptt up` or the time limit.

**Writing your own client.** Connect to the socket and send one command per line: `ptt down`, `ptt up`, `ptt toggle`, `ptt hold`, `status` or `help`. Each gets one reply line starting with `ok` or `error`. After `ptt hold`, keep the connection open for as long as you want to transmit; closing it releases PTT. For example, with `socat`:

```bash
echo "ptt toggle" | socat - UNIX-CONNECT:$XDG_RUNTIME_DIR/sidetone.sock
```

Only one console can use a socket at a time. To run a second one, start it with `--socket PATH`, and pass the same `--socket PATH` to `sidetone-cli ptt`. `--no-socket` turns the socket off.

## PTT devices (desk mics, footswitches, PTT buttons)

A USB desk microphone, footswitch or PTT button can key the console directly. Sidetone reads the device itself, so it sees both press and release on any desktop, even ones whose hotkeys can't bind it. That includes devices that show up as joysticks, which most desktops ignore. Hold the button to transmit; let go to stop. If the device is unplugged mid-transmission, the console unkeys. The device can be unplugged and plugged back in while the TUI runs.

Find your device's name and buttons:

```bash
./sidetone-cli devices
```

Then start the TUI with `--ptt-device` and any part of that name. For the Motorola Solutions desktop microphone, Sidetone already knows the buttons: the bar is PTT and the other button mutes the microphone (see below). Its audio shows up under the same name, so `--input` makes it the transmit microphone too:

```bash
./sidetone-cli tui --codeplug ~/dvm/codeplug-linux.yml --ptt-device "Motorola Desktop Microphone" --input "Motorola Desktop Microphone"
```

For other devices:
- A device with one button needs nothing more.
- With several buttons, name the PTT one with `--ptt-button`, for example `--ptt-button BTN_THUMB`, `--ptt-button KEY_F13` or a number. `evtest` shows what each button sends.
- `--mute-button BUTTON` makes another button toggle the microphone mute, and `--mute-button none` turns that off.

The TUI logs the device and buttons it's using, and when the device is unplugged or comes back. It transmits on the focused channel, the same as Space, and the transmit time limit still applies.

**Permissions.** Your user needs read access to the device's `/dev/input/event*` file. Devices that show up as joysticks, like the Motorola mic, usually get that automatically for the logged-in user. Check with `getfacl /dev/input/eventN` (look for `user:yourname:rw-`). For a footswitch that shows up as a keyboard, add a udev rule for just that device, using the vendor and product IDs from `sidetone-cli devices`. For example, `/etc/udev/rules.d/70-Sidetone-ptt.rules`:

```
SUBSYSTEM=="input", ATTRS{idVendor}=="1234", ATTRS{idProduct}=="5678", TAG+="uaccess"
```

Avoid adding yourself to the `input` group instead: that lets every program you run read every keystroke.

Sidetone doesn't take the device away from other programs, so a footswitch that types a key still types it in the focused window. Pick a key nothing else uses, such as F13 to F24.

## Audio processing

Your microphone audio is cleaned up before it reaches the vocoder, so it codes well whatever microphone you use:

| Stage | What it does | Default |
|---|---|---|
| Low cut | Removes rumble, hum and handling noise below about 200 Hz | on |
| Noise gate | Turns background noise down 24 dB between words | off |
| Auto level | Brings speech to a steady level: up to 18 dB louder for a quiet microphone, up to 10 dB quieter for a loud one. It only reacts to speech, so it doesn't raise the noise in pauses. | on |
| Limiter | Keeps peaks just below full scale, so loud syllables don't clip | on |

**Even out talkers' levels** does the same levelling (without the low cut or gate) on received audio, so quiet and loud radios come out closer together. It's off by default. Recordings are always saved as they came over the air.

Turn stages on and off under **Mic processing** and **Receive** in Settings; changes take effect immediately. The terminal console uses the same saved choices. `--no-processing` turns everything off for one run, in either program.

**Hearing the difference.** `sidetone-cli process` runs a recording through the same processing, and with `--vocoder` through IMBE and back, so you hear roughly what goes on the air:

```bash
./sidetone-cli process mic-test.wav processed.wav --vocoder
```

It prints the speech and peak levels before and after. The same switches as Settings are available: `--gate`, `--no-low-cut`, `--no-auto-level` and `--no-limiter`. The input must be 8 kHz mono 16-bit WAV; the error message says how to convert other files with ffmpeg.

All of this is plain C# inside Sidetone, independent of PipeWire, so it will behave the same on other platforms.

**Sound system.** Sidetone plays and records through one internal audio interface. On Linux that is PipeWire (`pw-play`, `pw-record` and `pw-dump`); other platforms will get their own (WASAPI on Windows, Core Audio on macOS) without changes elsewhere.

## Instant recall and recording

**Instant recall.** The last ten calls on each listening channel are kept, including your own transmissions, and can be played again:

- **Replay** on a card plays its last call; press it again to stop. While a call is replaying, the card shows who and how far in.
- **Recall…** in the tool bar (or `Shift+L`) lists a channel's last ten calls with a **Play** button each, and buttons across the top to switch channels.
- `L` replays the focused channel's last call in the graphical console. In the TUI, `l` does the same and `L` steps back to older calls.

Replays play at the channel's volume, even when the channel or the console is muted, alongside any live audio. Only calls on listening channels are kept, because only those are received; a secure call is kept only if its key was loaded. Calls are kept in memory and are gone when Sidetone closes.

**Recording.** A channel set to record also saves every call to a WAV file when it ends:

- **Rec** on a card, **Record** in Instant recall, `W` (GUI) or `w` (TUI) turns it on and off. Recording a channel starts listening to it, and a channel that stops listening stops recording.
- Files go to `~/.local/share/sidetone/recordings/SYSTEM/YYYY-MM-DD/`, named like `143107_CT-OPS_TG500_RX_Kara-M.wav`. Choose another folder in Settings or with `--record-dir DIR` (both programs).
- Recording is remembered per channel, so it carries on after a restart.
- Files are 8 kHz mono WAV, about 1 MB a minute of speech. Nothing is deleted automatically.

**Secure calls are saved decrypted.** A recording of a secure channel is plain audio anyone with the file can play. The folders and files are made readable by your user only, and the log says so when you start recording a secure channel.

**Audio quality.** Calls are kept as the voice frames that came over the network (about 0.5 kB a second) and only turned into audio when played or saved, so both sound the same as the live call.

## Desktop notifications

When a radio pages the console (a call alert to the console's radio ID), Sidetone shows a desktop notification as well as logging it. It uses `notify-send`, or `gdbus` when that isn't installed, and needs a notification service such as mako, dunst, swaync, GNOME or KDE. Repeats from the same radio within ten seconds show one notification.

Turn it off under **When paged** in Settings, or for one run with `--no-notify` (both programs).

## Patching

Channels in the same patch hear each other. When a call comes in on one member, the console sends it out on the others and ends it there when it ends. When you transmit on a member, the rest of the patch gets your voice too.

- **Graphical console:** **Patch…** in the tool bar opens a box with a row per channel: **Off**, or one of the patches. A click takes effect at once. `X` moves the focused channel to the next patch.
- **TUI:** `x` moves the focused card to the next patch.
- Patched cards show `⇄` and the patch name next to the talkgroup, and `TX patch ← name` while a call is being passed on to them.

How it behaves:

- **One talker at a time.** While one member's call is being passed on, calls on the other members are not; the next call starts fresh.
- **A patched channel is always listening.** Joining a patch starts listening, and a channel that stops listening leaves its patch.
- **Receive-only channels** can be heard into a patch but are never sent to.
- **Busy channels are skipped** for that call, and so are secure channels whose key isn't loaded. The log says which.
- **Source ID.** Passed-on calls carry the console's radio ID, or the original radio's when the codeplug has `patchSourceIdPassthrough: true`.
- **Names.** The patches are the codeplug's patch groups (`groups:` entries), up to six. Without any, they are Patch 1 to 3.
- **Not remembered.** Patches are empty when Sidetone starts, so nothing is retransmitted by itself at launch.

**Secure and clear channels in one patch.** Each member's traffic goes out with that member's own encryption. Patching a secure channel to a clear one therefore puts the secure channel's traffic on the air in the clear, and the log warns you when a patch is like that.

**Audio quality.** The Windows console decodes a received call to audio and encodes it again for each patched channel. Sidetone passes the voice frames themselves across (decrypting and re-encrypting them as needed), so a patched call goes through the vocoder once, at the radio that spoke, and not a second time in the console.

## Encryption keys

Keys normally come from the FNE: the console asks for each encrypted channel's key after it logs in. Two things add to that.

### Local key file

If the FNE doesn't deliver keys to your console, Sidetone can read them from a password-protected **KFDtool key container** (an `.ekc` file), the same kind the Windows console uses. Clear-text key files are not supported.

Sidetone uses the first of these that is set:

1. `--key-file FILE.ekc` on the command line (both programs)
2. the file chosen under **Key file** in the graphical console's Settings
3. the codeplug's `keyFile:` entry (a path relative to the codeplug works)

The graphical console asks for the password in a box when it opens the codeplug. The terminal console asks before the TUI starts. Skipping it is fine: the console runs with whatever keys the FNE sends, and you can unlock later from Settings.

**Remembering the password.** If your desktop has a keyring and the `secret-tool` command (GNOME Keyring, KWallet and others provide the keyring; `secret-tool` comes with libsecret), Sidetone offers to remember the password there. Otherwise it asks each time. Sidetone never writes the password or any key to its own files or its log, and keys are wiped from memory when the console closes.

A key from the key file wins over one the FNE sends for the same key ID. Cards show `from key file` next to such a key. Only traffic keys for AES-256, DES and ADP are read; key-wrapping keys and other algorithms are skipped, and the log says how many.

### Selectable encryption

A channel with `selectable_encryption: true` in the codeplug can have its **transmit** switched between secure and clear. In the graphical console, the card's encryption label becomes a button: `TX AES-256` or, in red, `TX CLEAR`. `E` does the same for the focused card, and `e` in the TUI. The choice is remembered per channel.

Receive is not affected: the channel still decrypts what it hears. Channels without that codeplug setting can't be switched, and it can't be changed in the middle of a transmission.

## Radio aliases

Sidetone shows radio names instead of bare radio IDs, the same way the Windows console does and from the same codeplug settings. Cards and Recent calls show the name; the log shows the name and the ID, for example `RX Kara Portable (9511)`, unless the name already contains the ID (`RX Kara M - 9511`). Radios without an alias show their ID.

**From a file.** Put an `alias.yml` beside the codeplug, or point `aliasPath` under the system at another file (relative paths are relative to the codeplug). It's the Windows console's format, so you can copy that file over:

```yaml
- alias: "Kara Portable"
  rid: 9511
- alias: "Dispatch"
  rid: 1234
```

**From the FNE.** Add `syncRadioAliases: true` to the system in the codeplug:

```yaml
systems:
  - name: "Centrunk"
    syncRadioAliases: true
```

Sidetone then downloads the FNE's alias list after every login. The download is cached in `~/.config/sidetone/RadioAliases/`, so names show up straight away the next time, before the FNE answers. FNE names take priority, and radios missing from the FNE's list fall back to the local file. If a download fails, the aliases you already have are kept, and the FNE connection isn't affected.

This needs an FNE with radio alias sync, set up with a `radio_alias` file, and the request goes to the FNE's metadata port (its traffic port plus one), so that UDP port has to be reachable too.

Startup and every download write a line to the log, for example `aliases: 2 from /home/kara/dvm/alias.yml` or `aliases: 812 from the FNE (13520 bytes)`. The TUI's title bar shows how many radios have a name, and `a` reloads the file and downloads again.

## Coming from dvmcli and dvmgui

Version 0.17 renamed the project to Sidetone. What changed, and what carries over:

| Before | Now |
|---|---|
| `dvmgui` | `sidetone` |
| `dvmcli` | `sidetone-cli` |
| `~/.config/dvmcli/` | `~/.config/sidetone/` |
| `$XDG_RUNTIME_DIR/dvmcli.sock` | `$XDG_RUNTIME_DIR/sidetone.sock` |
| `dvmcli-*-linux-*.tar.gz` | `sidetone-*-linux-*.tar.gz` |

- **The old commands still work.** Each download includes `dvmcli` and `dvmgui` as small wrappers around the new programs, so hotkeys such as `dvmcli ptt down` carry on. They will be removed in a later release, so switch to the new names when convenient.
- **Settings carry over.** The first time a new version starts, it copies `~/.config/dvmcli` (settings and the alias cache) to `~/.config/sidetone`. The old folder is left in place, so an older version still works.
- **The socket has a new name.** Anything that talks to the socket directly, such as a `socat` line, needs `sidetone.sock`. `--socket PATH` still picks any path you like.
- **On the network** the console now identifies its software as `SIDETONE` instead of `DVMCLI`. Peer IDs and codeplugs are unchanged.

## Building from source

CI builds every push on GitHub's ARM64 and x86-64 Linux runners. To build locally you need the .NET 10 SDK, CMake and a C++ compiler. Clone with submodules:

```bash
git clone --recursive https://github.com/monkey1911/sidetone.git
```

Then follow the same steps as `.github/workflows/build.yml`, with `linux-arm64` or `linux-x64` as the runtime identifier. `sidetone --screenshot FILE.png --demo --theme dark` renders the window to a PNG without a display, which is how CI checks both themes.

## How it is put together

- `extern/dvmconsole` is a submodule of [monkey1911/dvmconsole-ARM64](https://github.com/monkey1911/dvmconsole-ARM64) (`dev` branch). `sidetone-core` compiles its Windows-independent sources directly (codeplug, vocoder interop, logging) and uses its `fnecore` networking library.
- `extern/dvmvocoder` is [DVMProject/dvmvocoder](https://github.com/DVMProject/dvmvocoder), built as `libvocoder.so`.
- `src/sidetone-core` is everything that isn't user interface: the FNE peer, P25 receive and transmit, keys, PipeWire audio, tones, aliases, the PTT socket and PTT devices, settings, and `DispatchConsole`, the console itself.
- `src/sidetone-cli` is the terminal front end: the TUI (which derives from `DispatchConsole`), the commands and the self-test.
- `src/sidetone` is the graphical front end (Avalonia): windows, cards and themes over a `DispatchConsole`.

## License

Sidetone is licensed under the GNU AGPL v3 (see `LICENSE`), as is the dvmconsole code it builds on, which is © DVMProject and contributors. libvocoder is GPL-2.0-only and is built and loaded as a separate shared library, the same way the Windows console uses it.

IMBE and AMBE are trademarks of Digital Voice Systems, Inc. Sidetone is an independent project built on DVMProject's open-source code. It is not affiliated with or endorsed by DVMProject or DVSI.
