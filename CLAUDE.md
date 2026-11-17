# Sidetone: notes for Claude Code

Sidetone is "an IP dispatch console for DVMProject FNE networks": a Linux port of the DVMProject
Windows dispatch console (DVMConsole), with a terminal UI and an Avalonia GUI. It is used on the air
on the CTRS / Centrunk P25 system. This file is the hand-off from the claude.ai sessions that
built versions 0.4 to 0.23; read it, then `README.md` (user-facing behaviour) and `WISHLIST.md`
(what's next, in order).

## State at hand-off (2026-11-16)

- Version **0.24.0** on `main` (set once, in `src/Directory.Build.props`): zone tabs. The last
  release the owner published is 0.23.1; check `gh release list`.
- 267 checks (216 in `sidetone-cli selftest`, 51 in the GUI's `--selfcheck`) plus screenshots,
  passing locally on ARM64; check CI for both architectures.
- **Tested on the air by the owner:** RX/TX clear and AES, key delivery from the FNE, alias sync,
  alert tones, channel marker, QCII and digital pages, radio check (the radio answers; see the FNE
  limitation below), GUI on x86-64 under Hyprland, keyboard shortcuts, instant recall.
- The owner tests every release on the air and on their hardware after publishing it. The list
  below is only what the earlier sessions never heard results for, not what is known to be
  untested. Ask the owner before calling something untested, and move items up once confirmed.
- **No results reported to the earlier sessions yet:** patching, recording
  to WAV, page notifications, mic processing (on by default!), local KFDtool key files and the
  keyring, selectable encryption on the air, the Settings screen with real devices, the app
  launcher entry, display scaling on a real HiDPI screen, the Motorola desk mic in the GUI, the GUI
  on ARM64 (Surface Pro 11), `--wayland`, tabs and hiding zones (0.24, both consoles).
- Next on the wishlist: item 7 (NXDN/DMR, needs live traffic) or item 10 (a CI test against a real
  dvmfne), plus the "Terminal console" gaps at the end of `WISHLIST.md`.

## Layout

```
src/Directory.Build.props   shared: Version, net10.0, nullable off, unsafe on
src/sidetone-core/          class library, namespace Sidetone.Core: everything that isn't UI
src/sidetone-cli/           terminal front end (TUI; `Tui` is partial, tabs in Tui.Zones.cs), commands, self-test
src/sidetone/               graphical front end (Avalonia 12.1.6), namespace Sidetone.Gui
extern/dvmconsole           submodule: monkey1911/dvmconsole-ARM64, branch dev (the Windows console)
extern/dvmvocoder           submodule: DVMProject/dvmvocoder v0.2, built by CMake into libvocoder.so
packaging/                  icon (SVG + 256 px PNG), dvmcli/dvmgui wrapper scripts for the rename
tools/                      ci-status.sh, ci-screenshots.sh (see Testing)
.github/workflows/build.yml the only CI workflow ("Build (Linux)")
```

### Core (`src/sidetone-core`)

- `DispatchConsole` (partial: `.cs`, `.Patch.cs`, `.Recall.cs`, `.Zones.cs`) is the console: channels,
  receive, transmit, tones, pages, radio commands, patching, recall/recording, zones (tabs),
  notifications, settings. Both front ends drive the same instance; `Tui` *derives* from it, the GUI *owns* one.
- `ConsoleChannel` is one codeplug channel (talkgroup, encryption, listening, mute, gain, patch,
  record, TxClear, `InZone`). `GetStatus(c)` gives what the cards show.
- `ConsoleZone` is one tab (codeplug zones with the same name merge; nameless ones are "Zone N";
  zones with no channels for the system are left out). **The tab on show is the focused channel's
  zone** (`CurrentZone`), so there is no separate tab state; each zone remembers its last focused
  card. A hidden zone's channels are deselected and refused by `Select`/`FocusTo`, and `OnFrame`
  ignores them. `SystemSettings.HiddenZones` keeps hidden zones per system.
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
`RecallOverlay`, `ZoneOverlay` for the Tabs box), not separate windows. `ZoneTabs` is the tab strip;
all cards are built once and only the current zone's are made visible. `Tap` (in `Controls.cs`) is the flat button used
everywhere; set `Released` for hold-to-talk. `ChannelCard` is one card. Keyboard handling is in
`MainWindow.HandleKeyDown/Up` (tunnel handlers; an open box takes keys first). `DemoCodeplug` is a
made-up system for `--demo`, screenshots and the self-check. The GUI uses only the core's public
API (only `sidetone-cli` has `InternalsVisibleTo`).

## Building and testing

The claude.ai sessions had no .NET SDK, so CI was their compiler. Claude Code sessions can build
and test locally (first done 2026-11-16 on ARM64: SDK 10.0.404, the system CMake and g++). Build
and run the self-tests locally before pushing; CI then covers the other architecture and the
packaging.

**Build for the machine you're on.** Check `uname -m` first: `aarch64` → `linux-arm64`, `x86_64` →
`linux-x64`. An x64 build on ARM64 tries to run through qemu-user and fails with
`Could not open '/lib64/ld-linux-x86-64.so.2'`. The owner has machines of both kinds.

Local build, the same steps as `build.yml` (run from the repository root; `build/` and `out/` are
git-ignored):

```
cmake -S extern/dvmvocoder -B build/vocoder -DCMAKE_BUILD_TYPE=Release
cmake --build build/vocoder -j"$(nproc)"
mkdir -p build/native && cp "$(find build/vocoder -name libvocoder.so | head -1)" build/native/
R=linux-arm64   # or linux-x64
dotnet publish src/sidetone-cli/sidetone-cli.csproj -c Release -r $R --self-contained true -o out/sidetone-$R
dotnet publish src/sidetone/sidetone.csproj -c Release -r $R --self-contained true -o out/sidetone-$R
out/sidetone-$R/sidetone-cli selftest     # ends with ALL TESTS PASSED
out/sidetone-$R/sidetone --selfcheck      # ends with ALL GUI CHECKS PASSED
```

libvocoder only needs rebuilding when the `extern/dvmvocoder` submodule changes. A screenshot works
locally too: `out/sidetone-$R/sidetone --screenshot FILE --demo --size 1180x760 --theme dark`
(add `--show page|radio|settings|keyfile|patch|recall|tabs` for a box). Save these to the scratchpad and
read the PNG directly, with no CI round trip.

The build should have no NuGet warnings. `System.Security.Cryptography.Xml` (a direct reference in
`sidetone-core`, used only for `.ekc` key files) was raised to 10.0.15 in 0.23.1 for advisories in
10.0.0. Check with `dotnet list src/sidetone-core/sidetone-core.csproj package --vulnerable --include-transitive`.

What CI does on every push to `main` (both architectures):
1. builds libvocoder, publishes `sidetone-cli` and `sidetone` into one folder;
2. `sidetone-cli selftest`: the core's self-test, printing `PASS:`/`FAIL:` lines;
3. `sidetone --selfcheck`: drives the GUI headlessly (Avalonia.Headless), presses keys, opens boxes;
4. renders screenshots (`--screenshot FILE --demo --theme dark|light --show page|radio|settings|keyfile|patch|recall|tabs`)
   and prints them base64 into the log between `-----BEGIN PNG name-----` / `-----END PNG name-----`;
5. packages `sidetone-<tag>-linux-<arch>.tar.gz` (with the `dvmcli`/`dvmgui` wrappers), and on a
   published **release** attaches it (there is deliberately no tag trigger: two runs raced and
   clobbered uploads).

The loop now: build and test locally, commit, push, then `tools/ci-status.sh` (waits, prints
errors and failed tests, saves the x64 log) to confirm the other architecture. `tools/ci-screenshots.sh`
still pulls the CI PNGs if wanted. Add a self-test or self-check for every feature. The owner tests
every release on the air and on their hardware after publishing it.

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

- **Upstream DVMConsole's live branch is `r01a02_dev`.** `main` on DVMProject/dvmconsole is
  essentially abandoned (still net8.0, last commit 2026-03). The owner's fork
  (monkey1911/dvmconsole-ARM64, our submodule) follows `r01a02_dev` on its `dev` branch. Compare
  against `r01a02_dev` when checking what the Windows console does, never `main`.

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
- **Releases are gated on CI.** Only offer release notes, or say a version is ready, once
  `tools/ci-status.sh` shows both architectures green for the commit being released. Local tests
  run on one architecture only. CI runs natively on both (ubuntu-24.04 and ubuntu-24.04-arm), so it
  is the check for the architecture you aren't on. If either job fails, fix it first. (An emulated
  x86-64 run in Docker on the ARM64 machine is possible but slow, and adds nothing CI doesn't do.)
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
