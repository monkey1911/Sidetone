# Sidetone: notes for Claude Code

Sidetone is "an IP dispatch console for DVMProject FNE networks": a Linux port of the DVMProject
Windows dispatch console (DVMConsole), with a terminal UI and an Avalonia GUI. It is used on the air
on the CTRS / Centrunk P25 system. This file is the hand-off from the claude.ai sessions that
built versions 0.4 to 0.23; read it, then `README.md` (user-facing behaviour) and `WISHLIST.md`
(what's next, in order).

## State at hand-off (2026-11-16)

- Version **0.23.0** on `main` (set once, in `src/Directory.Build.props`). The last release the
  owner published is 0.23.0 or about to be; check `gh release list`.
- CI is green on ARM64 and x86-64: 244 self-test checks plus the GUI self-check and screenshots.
- **Tested on the air by the owner:** RX/TX clear and AES, key delivery from the FNE, alias sync,
  alert tones, channel marker, QCII and digital pages, radio check (the radio answers; see the FNE
  limitation below), GUI on x86-64 under Hyprland, keyboard shortcuts, instant recall.
- **Built and self-tested but not yet tried on the air or on real hardware:** patching, recording
  to WAV, page notifications, mic processing (on by default!), local KFDtool key files and the
  keyring, selectable encryption on the air, the Settings screen with real devices, the app
  launcher entry, display scaling on a real HiDPI screen, the Motorola desk mic in the GUI, the GUI
  on ARM64 (Surface Pro 11), `--wayland`.
- Next on the wishlist: item 7 (NXDN/DMR, needs live traffic) or item 10 (a CI test against a real
  dvmfne), plus the "Terminal console" gaps at the end of `WISHLIST.md`.

## Layout

```
src/Directory.Build.props   shared: Version, net10.0, nullable off, unsafe on
src/sidetone-core/          class library, namespace Sidetone.Core: everything that isn't UI
src/sidetone-cli/           terminal front end (TUI), commands, and the self-test (SelfTest.cs)
src/sidetone/               graphical front end (Avalonia 12.1.6), namespace Sidetone.Gui
extern/dvmconsole           submodule: monkey1911/dvmconsole-ARM64, branch dev (the Windows console)
extern/dvmvocoder           submodule: DVMProject/dvmvocoder v0.2, built by CMake into libvocoder.so
packaging/                  icon (SVG + 256 px PNG), dvmcli/dvmgui wrapper scripts for the rename
tools/                      ci-status.sh, ci-screenshots.sh (see Testing)
.github/workflows/build.yml the only CI workflow ("Build (Linux)")
```

### Core (`src/sidetone-core`)

- `DispatchConsole` (partial: `.cs`, `.Patch.cs`, `.Recall.cs`) is the console: channels,
  receive, transmit, tones, pages, radio commands, patching, recall/recording, notifications,
  settings. Both front ends drive the same instance; `Tui` *derives* from it, the GUI *owns* one.
- `ConsoleChannel` is one codeplug channel (talkgroup, encryption, listening, mute, gain, patch,
  record, TxClear). `GetStatus(c)` gives what the cards show.
- `CliPeer` is the FNE peer (fnecore `FneSystemBase`): login, P25 frames, key requests (KMM),
  aliases, TSBKs (call alert, extended function).
- `P25VoiceReceiver` (decrypt + IMBE decode per listening channel; raises `Codeword` with clear
  IMBE frames), `P25Transmitter` (IMBE encode + encrypt; `PushImbe` takes ready codewords;
  `ClearFrame` event exposes what it sends), `P25FrameBuilder`.
- `KeyStore` (keys from the FNE, plus `SetLocal` for a key file), `LocalKeys.cs` (KFDtool `.ekc`
  via the submodule's `EkcKeyStore`; `SecretStore` = `secret-tool` keyring).
- Audio: `AudioBackend.cs` (`IAudioBackend`, `IAudioOutput`, `IAudioInput`; `PipeWireBackend` is
  the only real one; `NoAudioBackend` elsewhere). `AudioSinks.cs` (`PipeWireSink` = `pw-play`,
  `WavSink`), `PipeWireCapture.cs` (`pw-record`), `AudioDevices.cs` (`pw-dump` parsing, device
  matching, startup report). **Nothing outside the PipeWire backend may start `pw-*` tools.**
- `AudioProcessing.cs`: `VoiceProcessor` (low cut, gate, auto level, limiter; 8 kHz, 160-sample
  blocks), `WavFile` (reader + level description). Plain C# on purpose (portability).
- `Tones.cs` (alert tones, QCII pages, marker), `LevelMeter.cs`, `RadioAliases.cs`,
  `ControlSocket.cs` (`$XDG_RUNTIME_DIR/sidetone.sock`, `ptt down|up|toggle|hold`, `status`),
  `PttDevice.cs` (evdev; Motorola desk mic 0cad:1804 known), `Settings.cs`
  (`~/.config/sidetone/settings.json`, per-system channel settings; copies `~/.config/dvmcli` once).

### Threading (important)

Everything in `DispatchConsole` runs on the UI thread that called `Start()` (the TUI loop or
Avalonia's dispatcher). Network, capture and timer threads must not touch console state directly:
they queue work with `PostToUi` / the `uiCalls` queue and `Wake()`. Exceptions with their own
locks: `uiLock` (log, recent calls, channel activity), `patchLock`, `recallLock`, `KeyStore`.
Receive callbacks (`Codeword`, `Audio`, `CallEnded`) arrive on the network thread under the
receiver's lock.

### GUI (`src/sidetone`)

No XAML: everything is built in C#. `Palette` (light/dark) is painted by hand on a 100 ms
`DispatcherTimer` refresh (`MainWindow.RefreshNow`). Boxes are in-window overlays
(`CommandOverlay` for page/radio, `SettingsOverlay`, `KeyFileOverlay`, `PatchOverlay`,
`RecallOverlay`), not separate windows. `Tap` (in `Controls.cs`) is the flat button used
everywhere; set `Released` for hold-to-talk. `ChannelCard` is one card. Keyboard handling is in
`MainWindow.HandleKeyDown/Up` (tunnel handlers; an open box takes keys first). `DemoCodeplug` is a
made-up system for `--demo`, screenshots and the self-check. The GUI uses only the core's public
API (only `sidetone-cli` has `InternalsVisibleTo`).

## Building and testing

Nothing has been built locally so far: the sessions had no .NET SDK, so **CI is the compiler**.
Locally you need the .NET 10 SDK, CMake and a C++ compiler; follow the steps in `build.yml`
(build libvocoder with CMake, then `dotnet publish -r linux-x64` or `linux-arm64`, self-contained).

What CI does on every push to `main` (both architectures):
1. builds libvocoder, publishes `sidetone-cli` and `sidetone` into one folder;
2. `sidetone-cli selftest`: the core's self-test, printing `PASS:`/`FAIL:` lines;
3. `sidetone --selfcheck`: drives the GUI headlessly (Avalonia.Headless), presses keys, opens boxes;
4. renders screenshots (`--screenshot FILE --demo --theme dark|light --show page|radio|settings|keyfile|patch|recall`)
   and prints them base64 into the log between `-----BEGIN PNG name-----` / `-----END PNG name-----`;
5. packages `sidetone-<tag>-linux-<arch>.tar.gz` (with the `dvmcli`/`dvmgui` wrappers), and on a
   published **release** attaches it (there is deliberately no tag trigger: two runs raced and
   clobbered uploads).

The loop that worked: commit, push, `tools/ci-status.sh` (waits, prints errors and failed tests,
saves the x64 log), then `tools/ci-screenshots.sh` and look at the PNGs. Add a self-test or
self-check for every feature; the owner tests on the air afterwards.

### Things that bit before

- A property named `System` or `Console` hides the namespace/type inside that class; use
  `global::System...`.
- C# positional patterns like `is (string a, string b)` don't match when an element is null.
- `--` inside an XML comment breaks a `.csproj`.
- Avalonia headless: `Dispatcher.UIThread.RunJobs()` doesn't run `DispatcherTimer`s; the
  self-check uses `Dispatcher.UIThread.MainLoop(token)` for timed things (Space release delay).
- Names inside the long `SelfTest.Run` method clash easily (CS0136); give test locals distinct names.
- CI log lines carry timestamps and occasionally a byte-order mark; strip both before decoding.
- The level meter only reads "active" within 300 ms of the last block.

## Domain facts worth knowing

- **The FNE version CTRS runs does not pass extended-function replies (radio check acks) back to
  the peer that sent them.** The radio does answer over the air. Don't ask the owner about it again;
  Sidetone already decodes and logs replies if a newer FNE sends them.
- Tones go out as IMBE tone frames (as the Windows console does), so QCII tones are quantised to
  about 31 Hz steps.
- Patching passes clear IMBE codewords between talkgroups (decrypt on receive, re-encrypt per
  target), so patched voice is not decoded and re-encoded. `patchSourceIdPassthrough` (top level of
  the codeplug) is the Windows console's own setting. A secure-to-clear patch sends secure traffic
  in the clear; the log warns.
- The owner's Harris radio can't do QCII over P25; Motorola radios can.
- The Motorola USB-C desk mic: BTN_THUMB (289) is PTT, BTN_TRIGGER (288) is the "mute" button,
  which does nothing on board. Its PTT works; its audio is quiet (mic processing's auto level is
  meant to help; the owner chose flat EQ earlier, i.e. no EQ).
- Codeplugs are the Windows console's YAML. Sidetone adds no codeplug keys of its own.
- Avalonia under Hyprland runs through XWayland by default; `--wayland` is opt-in and early.
  On Hyprland with `xwayland:force_zero_scaling`, Sidetone takes the monitor's scale itself.

## Working with the owner

- Commit straight to `main`; no feature branches or PRs unless asked.
- The owner publishes GitHub releases and pastes notes written for them. After a version, offer
  release notes in this shape (one release can cover several version bumps):
  ```
  ## Sidetone X.Y.Z
  ### New
  ### Changed
  ### Fixed
  ### Notes      (what is untested on the air goes here)
  ```
- Put only one command per code block in answers; never several commands in one paste box.
- Say plainly what was tested where (CI vs on the air) and what wasn't.
- Keep personal names out of the public repo's files (the wishlist refers to "the FNE's
  maintainers").
- **Never print or commit a codeplug**: they contain FNE passwords. `.gitignore` ignores `*.yml`
  outside `.github/workflows/`. Likewise keys, key-file passwords and recordings.
- The owner works on several machines: an x86-64 desktop (Hyprland, Omarchy), a Surface Pro 11
  (ARM64, Omarchy), and has an Intel MacBook on macOS 15 for an eventual macOS port.
