# dvmcli: DVM console proof of concept for Linux

A headless test of the [DVM Desktop Dispatch Console](https://github.com/DVMProject/dvmconsole) on Linux, for ARM64 (aarch64) and x86-64. It was built for Omarchy on a Surface Pro 11 and runs the same way on x86-64 PCs. It connects to an FNE as a console peer, follows one P25 talkgroup, decodes clear voice with libvocoder, and plays it through PipeWire.

It exists to prove the networking, vocoder and audio work on Linux before any decision about a full desktop port.

**What works:** FNE login using your dvmconsole codeplug, P25 receive and transmit (clear, AES-256, DES, ADP/ARC4 with keys from the FNE), PipeWire audio, a terminal console (channel cards, mouse support, toggle push-to-talk), and WAV recording.

**Not yet:** DMR/NXDN/analog, and a graphical UI.

## Install

Each release on the [Releases](../../releases) page has one download per architecture (CI runs keep the same two as artifacts):

| Machine | Download |
|---|---|
| ARM64 (Surface Pro 11 and other aarch64) | `dvmcli-*-linux-arm64.tar.gz` |
| x86-64 (Intel / AMD PCs) | `dvmcli-*-linux-x64.tar.gz` |

If you're not sure which one a machine needs, this prints `aarch64` or `x86_64`:

```bash
uname -m
```

Extract the ARM64 build:

```bash
tar -xzf ~/Downloads/dvmcli-*-linux-arm64.tar.gz -C ~
```

Or the x86-64 build:

```bash
tar -xzf ~/Downloads/dvmcli-*-linux-x64.tar.gz -C ~
```

The builds are self-contained, so no .NET install is needed. They are built on Ubuntu 24.04, so any distribution of that age or newer runs them, including current Arch / Omarchy and Fedora. Playback uses `pw-play` and transmit uses `pw-record`, which ship with PipeWire.

## Codeplug

`dvmcli` reads the same codeplug YAML as the Windows console. Give it a **separate peer ID** from any console that is online at the same time, because the FNE accepts each peer ID only once. Copy your codeplug and change `peerId`, `identity` and `password` under the system to the second peer's values.

Keep codeplugs out of git; they contain passwords. This repo's `.gitignore` ignores `*.yml` outside the workflows folder.

## Use

Change into the extracted folder (the name matches the release and architecture you downloaded):

```bash
cd ~/dvmcli-*-linux-*
```

Run the offline self-test (vocoder, frame decoding, codeplug parsing):

```bash
./dvmcli selftest
```

List the systems and channels in your codeplug:

```bash
./dvmcli list --codeplug ~/dvm/codeplug-linux.yml
```

Follow a talkgroup by channel name and play it:

```bash
./dvmcli listen --codeplug ~/dvm/codeplug-linux.yml --channel "DVM Calling"
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
./dvmcli tui --codeplug ~/dvm/codeplug-linux.yml
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
| a | Reload the alias file and download aliases from the FNE again |
| m / M | Mute the focused card / mute everything |
| + / - | Volume of the focused card |
| k | Request the focused card's key from the FNE again |
| q or Ctrl+C | Quit |

Alert tones match the Windows console's built-in tones 1–6 (5 seconds each). A digital page is a P25 call alert from the console's RID to one radio (the Windows console's "Page Subscriber"); call alerts and acknowledgements addressed to the console's RID are logged when the FNE passes them on. A QuickCall II page is tone A for 1 s then tone B for 3 s (250–3000 Hz, as the Windows console's manual page), sent on every listening channel; the last page's tones are filled in next time. On P25, tones travel as IMBE tone frames, which carry the frequency in steps of about 31 Hz, so radios regenerate the nearest step. The channel marker is the console's "channel hold" beep: 0.5 s of 650 Hz every 10 seconds on each listening channel that isn't busy; change the interval with `--marker-interval SECONDS`. Tones are encrypted on secure channels and skipped on channels whose key isn't held. The tone buttons are hidden on terminals narrower than 110 columns; the keys still work.

Radio check, inhibit and uninhibit are sent as in the Windows console. Inhibit disables the radio, so it asks you to press SEND a second time. Replies ("radio 1234 answered the radio check", inhibit / uninhibit confirmations) are logged when the FNE passes them on.

The talk permit tone (a short 1200 Hz beep when transmit starts) is on by default; `t` turns it off.

The microphone level meter sits in the bottom edge of the title box. It shows whenever the microphone is open: always while you transmit (labelled `TX MIC`), and when you press `v` or click it to check your level without transmitting. `v` leaves the microphone open until you press it again, but nothing is sent. The bar is green up to -18 dB, yellow to -6 dB and red above, `▌` marks the recent peak, and `CLIP!` means the input hit full scale. For clean audio, keep speech mostly in the green to yellow and never clipping. Adjust the level with your system's input volume, for example `wpctl set-volume @DEFAULT_AUDIO_SOURCE@ 0.8`.

Settings are remembered per system in `~/.config/dvmcli/settings.json`: which channels you're listening to, the focused card, volumes, mutes, global mute, the talk permit tone and the last page / radio values. Use `--settings FILE` for a different file or `--no-settings` to start fresh without saving. The channel marker always starts off. The file holds no passwords or keys.

Transmit uses the system's `rid` as the source ID, and the default microphone unless you start the TUI with `--input` (see [Audio devices](#audio-devices)). A secure channel never transmits in the clear: without its key, transmit is refused. Transmissions stop automatically after 120 seconds; change that with `--tx-limit SECONDS`. Add `--log FILE` to keep a copy of the log.

## Audio devices

Without options, dvmcli plays to the system's default output and transmits from the default input, the ones marked `*` in `wpctl status`. Both `listen` and `tui` log which devices they're using at startup, for example:

```
audio output: Plantronics Savi 8200 Office Series Analog Stereo (system default)
audio input: Plantronics Savi 8200 Office Series Mono (system default)
```

List the outputs and inputs PipeWire knows about:

```bash
./dvmcli devices
```

To use a different device, give `--output` (playback) or `--input` (microphone, `tui` only) any part of its name that matches only that device. Case doesn't matter:

```bash
./dvmcli tui --codeplug ~/dvm/codeplug-linux.yml --output "Savi" --input "Savi"
```

The ID from `dvmcli devices` or `wpctl status` works too, but IDs can change after a reboot or replug, so names are more reliable. If a name matches nothing or more than one device, dvmcli stops and lists the choices. If the chosen device disappears while dvmcli is running, PipeWire moves the audio to the default device.

## PTT from hotkeys and scripts

While the TUI is running, other programs can key it up through a control socket, so you can use a desktop hotkey, a script, or your own helper app. It works the same on any distribution or desktop. The socket is `$XDG_RUNTIME_DIR/dvmcli.sock`, and only your user can open it. The TUI logs its path at startup.

`dvmcli ptt` is the ready-made client. It transmits on the focused channel, the same as Space:

| Command | What it does |
|---|---|
| `dvmcli ptt down` | Start transmitting |
| `dvmcli ptt up` | Stop transmitting |
| `dvmcli ptt toggle` | Start or stop |
| `dvmcli ptt hold` | Transmit until this command is stopped (Ctrl+C, SIGTERM, or the process dies) |
| `dvmcli ptt status` | Say whether the console is transmitting, and on which channel |

Each command prints the console's reply and exits with 0 for `ok` and 1 for `error` (for example `error: not connected to the FNE yet`). The transmit time limit still applies.

**Hold-to-talk in Hyprland**: bind a key's press and release:

```
bind = , F13, exec, dvmcli ptt down
```

```
bindr = , F13, exec, dvmcli ptt up
```

In Sway, use `bindsym` and `bindsym --release`. Desktops whose shortcuts only fire on press, such as GNOME and KDE custom shortcuts, can use `dvmcli ptt toggle`. Give the full path to `dvmcli` if it isn't on your `PATH`.

**`ptt hold`** is the safest choice for helpers: transmit lasts only while the command runs, so if the helper crashes or is killed, the console unkeys. The command exits when the transmission ends some other way, such as Space, `ptt up` or the time limit.

**Writing your own client.** Connect to the socket and send one command per line: `ptt down`, `ptt up`, `ptt toggle`, `ptt hold`, `status` or `help`. Each gets one reply line starting with `ok` or `error`. After `ptt hold`, keep the connection open for as long as you want to transmit; closing it releases PTT. For example, with `socat`:

```bash
echo "ptt toggle" | socat - UNIX-CONNECT:$XDG_RUNTIME_DIR/dvmcli.sock
```

Only one TUI can use a socket at a time. To run a second one, start it with `--socket PATH`, and pass the same `--socket PATH` to `dvmcli ptt`. `--no-socket` turns the socket off.

## Radio aliases

dvmcli shows radio names instead of bare radio IDs, the same way the Windows console does and from the same codeplug settings. Cards and Recent calls show the name; the log shows the name and the ID, for example `RX Kara Portable (9511)`, unless the name already contains the ID (`RX Kara M - 9511`). Radios without an alias show their ID.

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

dvmcli then downloads the FNE's alias list after every login. The download is cached in `~/.config/dvmcli/RadioAliases/`, so names show up straight away the next time, before the FNE answers. FNE names take priority, and radios missing from the FNE's list fall back to the local file. If a download fails, the aliases you already have are kept, and the FNE connection isn't affected.

This needs an FNE with radio alias sync, set up with a `radio_alias` file, and the request goes to the FNE's metadata port (its traffic port plus one), so that UDP port has to be reachable too.

Startup and every download write a line to the log, for example `aliases: 2 from /home/kara/dvm/alias.yml` or `aliases: 812 from the FNE (13520 bytes)`. The TUI's title bar shows how many radios have a name, and `a` reloads the file and downloads again.

## Building from source

CI builds every push on GitHub's ARM64 and x86-64 Linux runners. To build locally you need the .NET 10 SDK, CMake and a C++ compiler. Clone with submodules:

```bash
git clone --recursive https://github.com/monkey1911/dvmconsole-linux.git
```

Then follow the same steps as `.github/workflows/build.yml`, with `linux-arm64` or `linux-x64` as the runtime identifier.

## How it is put together

- `extern/dvmconsole` is a submodule of [monkey1911/dvmconsole-ARM64](https://github.com/monkey1911/dvmconsole-ARM64) (`dev` branch). The CLI compiles its Windows-independent sources directly (codeplug, vocoder interop, logging) and uses its `fnecore` networking library.
- `extern/dvmvocoder` is [DVMProject/dvmvocoder](https://github.com/DVMProject/dvmvocoder), built as `libvocoder.so`.
- `src/dvmcli` is the CLI: the FNE peer, the P25 voice receiver, PipeWire/WAV output, and the self-test.

## License

dvmcli is licensed under the GNU AGPL v3 (see `LICENSE`), as is the dvmconsole code it builds on, which is © DVMProject and contributors. libvocoder is GPL-2.0-only and is built and loaded as a separate shared library, the same way the Windows console uses it.

IMBE and AMBE are trademarks of Digital Voice Systems, Inc. This project is not affiliated with DVSI or DVMProject.
