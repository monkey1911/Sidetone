# Sidetone: notes for Claude Code

Sidetone is "an IP dispatch console for DVMProject FNE networks": a Linux port of the DVMProject
Windows dispatch console (DVMConsole), with a terminal UI and an Avalonia GUI. It is used on the air
on the CTRS / Centrunk P25 system. This file is the hand-off from the claude.ai sessions that
built versions 0.4 to 0.23; read it, then `README.md` (user-facing behaviour) and `WISHLIST.md`
(what's next, in order).

## State at hand-off (2026-11-16)

- Version **0.40.2** on `main` (set once, in `src/Directory.Build.props`): the phone layout only below 600 wide
  (`NarrowWidth`, was 700), so an unfolded Galaxy Fold (about 690 dp upright) gets the tablet layout; the header's
  buttons are a WrapPanel so Settings stays on screen there (the owner's choice, for foldables). 0.40.1: **Try the
  demo** on the welcome view (`ConsoleView.StartDemo`/`LeaveDemo`: offline, `settingsPath` set aside so nothing is
  saved; the header's Go offline reads Leave demo; Settings' Open codeplug… leaves the demo first), for Google
  Play's reviewers (App access), who have no codeplug. Welcome rows are DockPanels so the text wraps on a phone. 0.40.0: **tones and announcements go out only on
  the focused channel** (the owner's choice), never on every listening channel: alert tones, QCII pages, tone and
  DTMF presets, WAV/spoken/time announcements (the "Play on all listening" buttons and Shift+Enter / the TUI's Space
  are gone), the scheduled time announcement when no channel is chosen (it follows the focus), and the channel
  marker (focused channel, while listening and quiet). One helper, `DispatchConsole.FocusedTarget(out why)`;
  `TxCapableListening` is gone. The channel marker's interval is a setting (`AppSettings.MarkerSeconds`, 5–600,
  null = 60; `--marker-interval` still wins; `MarkerInterval` is read each pass, so a change applies at once;
  Settings row "Channel marker"); it was a fixed 10 s. Windows: `sidetone.exe` and `sidetone-cli.exe` now carry
  the icon (`ApplicationIcon` = `packaging/windows/sidetone.ico` for win RIDs; it was only the setup's icon, so
  Explorer, the Start menu and the taskbar showed the generic one); the `windows` CI job checks both with
  `ExtractIconEx`. 0.39.1: the whole title row folds the bottom
  panels (a transparent background makes the TextBlock take clicks across its width; the owner asked), and a
  folded panel opens on a click anywhere. 0.39.0: tone and DTMF presets, and folding
  the bottom panels. `TonePresets.cs` (`TonePreset`/`ToneStep`, `DtmfPreset`; `ParseSteps`/`FormatSteps` for the
  editor's `Hz:seconds` text; `ImportMidi` through the Windows console's `TonePresetFileImporter`, linked, with a
  two-constant `dvmconsole.SettingsManager` stand-in in the same file since the real one is WPF). Kept in
  `AppSettings.TonePresets`/`DtmfPresets`; sent by `SendTonePreset`/`SendDtmfPreset` → `SendManual` (then every
  listening P25 channel, the focused one since 0.40, like alert tones; refused past the time limit). `TonePlan.Sequence` = tone codewords;
  **P25 full-rate IMBE has no real tone or DTMF codewords**: `VocoderToneLookupTable` is pre-encoded voice
  codewords for single tones, 281–2500 Hz (anything above snaps to 2500, so QCII's allowed 3000 Hz is really
  2500), and `TonePlan.Dtmf` renders the pair as audio through the encoder, as Windows, which measurably
  blurs one of the two tones often (see the README's warning). GUI: `TonePresetOverlay` (Tones…, More → Tones…,
  `--show tones`); TUI `g` (`Tui.Tones.cs`). Folding: `AppSettings.BottomCollapsed`; GUI title ▾/▸
  (`ToggleBottom`, row 4 Auto), TUI `b` (`Layout(..., folded)` gives a one-row bottom, `DrawFolded`).
  0.38.0: three Windows-console features
  (`DispatchConsole.Alerts.cs`). The codeplug's `transmitTimeoutSeconds` is the time limit unless an option
  or Settings say otherwise (`StartupChoices.TxLimit(option, saved, codeplug)`; dvmconsole's Codeplug defaults
  it to 180, so every codeplug without it now gets 180, not 120; its 0 becomes 900); a 1 kHz beep each second
  for the last 10 s and a 1 s one at the cut-off, played locally on the channel's sink (`PlayLocal`,
  `localSoundForTest`); after a cut-off a held PTT (device, key, Android key: `HoldToTalk`/`OnDevicePtt`) is
  refused until let go (`limitLatched`). The talkgroup check (`TalkgroupBlocker`): StartTx and SendTone refuse a
  TG not in the FNE's announced list (`CliPeer.AnnouncedTalkgroups`, fnecore's `AnnouncedTGs`; `Invalid` =
  turned off; DMR matches slot when entries carry 1/2), allow all when none announced, as `PeerSystem.
  IsTalkgroupAvailable`. Connection chimes (`ConnectionHousekeeping`): G5-B5-D6 rising on connect, falling on a
  drop (also logged "lost the connection to the FNE"), one per pass, `AppSettings.ConnectionChimes` (null = on),
  Settings row. Test stand-ins: `FneLink.ConnectedForTest`, `TalkgroupsForTest`. Untested on the air.
  0.37.0: **DMR** receive and clear
  transmit, following the owner's dvmconsole fork (fnecore's `DMRFrame`/`DMRCallData` do the framing;
  libvocoder's `MBE_MODE.DMR_AMBE`, 9-byte codewords, 3 per burst). `ConsoleChannel.IsDmr`, `Slot` (codeplug
  `slot`, 1 when unset), `IsVoice` (P25 or DMR: listen, record, cards; `IsP25` stays where it means P25 only:
  patches, tones, announcements, keys), `DmrSecure` (an `algo` on a DMR channel: TX refused, never clear).
  `IVoiceReceiver` (P25VoiceReceiver, new `DmrVoiceReceiver`: group calls on its TG and slot, a late terminator
  never starts a call, encrypted calls muted and logged once, as Windows) and `IVoiceTransmitter`
  (P25Transmitter, new `DmrTransmitter` around the fork's `DmrTxCall`, linked into the core: 60 ms bursts,
  superframe filled with silence, terminator). `CliPeer.DmrFrame` → `DispatchConsole.OnDmrFrame(link, e)` (fnecore's
  slot is 0/1, the codeplug's 1/2). `RecalledCall.Codec` (Imbe/Ambe) for replay, recordings and the history
  (`SavedCall.Codec` "ambe", left out for IMBE, so old files load). DMR sends through `FneLink.SendDmr`
  (`DmrSendForTest`/`DmrFactoryForTest` in the self-test). Not on DMR yet, each refused with a message: patches,
  tones/pages/marker, announcements, radio commands and digital pages from a focused DMR channel.
  `ConsoleChannel.TgText`/`TxDescription` describe the target for both modes. Untested on the air.
  0.36.2: calls are named
  `SYSTEM:CHANNEL:RADIOID/ALIAS` (the owner's choice; the tab doesn't matter): `RecalledCall.Label` and
  `RecalledCall.Caller` (just the ID without an alias), shown in Recent calls (`DispatchConsole.CallLabel`,
  the whole label in the Chan column, Who null), Instant recall and the Recordings list. Recording names are
  `HHmmss_SYSTEM_CHANNEL_RADIOID[_ALIAS][-n]` with '-' inside parts (`Safe(text, '-')`), so '_' only separates;
  the radio ID part is always there (0 if none); the SYSTEM folder keeps the old '_' form so a system's
  files stay in one folder. `RecordingFile.Parse` reads both that and the old `HHmmss_CHANNEL_TGnnn_RX_WHO`.
  The history file is version 2: calls keyed `SYSTEM:CHANNEL` (`HistoryKey`; it was `ChanKey`,
  zone/name/tg, which mixed up a same-named channel with the same TG on two FNEs); a version 1 file loads
  onto the first system's channels. Channel settings still use `ChanKey`. The self-test checks the transmit
  time limit on a live transmission (`StartTx` checks `FneLink.CanSend` and sends through `FneLink.Sink`,
  the same as `Peer` in real use, so the self-test's stand-in sink can transmit).
  0.36.1: fixes from an audit. Logs are
  UTC (the console's log lines and files, debug logs, `listen`'s output), as the FNE's and dvmhost's are
  (the owner's choice); the transmit clock (`P25Transmitter.StartedAt`, the time limit) and the channel
  marker's timer are UTC too, so a clock change can't stretch the time limit or stop the marker. Cards,
  Recent calls, recordings' names and the time announcement stay local. A web stream with no data for
  30 s (`WebStreamPlayer.QuietTimeout`: a half-open connection after a phone changes networks) is given
  up on and connected again. Open folder (recordings, logs) uses Avalonia's launcher, so it works on
  macOS and Windows (it ran `xdg-open`).
  0.36.0: Android improvements. Sharing
  recordings: Instant recall's button is **Recordings…** on a device (`RecordingsOverlay`: days newest
  first, Share and Share day; `Recordings.List` reads the files back from the names `RecordingPath`
  gives them), handed to `IGuiPlatform.Share`; Android's `AndroidPlatform.Share` uses AndroidX's
  FileProvider (`org.sidetone.app.files`, `Resources/xml/file_paths.xml` exposes only
  files/data/sidetone/recordings) and ACTION_SEND / SEND_MULTIPLE with the read grant. Phone boxes:
  `PhoneSheet.Apply(card, narrow)` makes each box a full-screen sheet (card fills the view, labelled
  two-column rows stacked, fixed-width fields stretched or capped at 320 in wrapping rows), remembering
  every change so wide layouts get the old boxes back; applied on layout switches and each refresh of an
  open box. Button rows in the boxes are WrapPanels. The old scaling (`FitBoxToWidth`) is gone.
  Tested by the owner on a phone: sharing recordings works, and the boxes "look much better". A real
  tablet keeping the centred boxes isn't reported (CI's tablet screenshot only).
  0.35.0: **Windows**. `WasapiBackend`
  (NAudio.Wasapi 3.3.0, MIT; endpoints by ID; shared mode at the device's mix format with
  `WavFile.StreamResampler` both ways; `WasapiOut`/`WasapiCapture` are marked obsolete in 3.3, kept as
  the Windows console's proven classes). `WindowsPlatform`: SAPI speech through COM on an STA thread
  (CI's runners have Microsoft David), Credential Manager for key-file passwords; pages flash the
  taskbar (`MainWindow`). Windows builds keep .NET's globalization (InvariantGlobalization false for win
  RIDs, Windows's ICU) so IANA zones resolve; `DesktopPlatform` sets the culture invariant; zone list
  converted from Windows ids; US/UK abbreviations by table (`Speech.WindowsAbbreviations`). TUI in the
  console's VT mode (`TerminalIO.StartWindows`). `packaging/windows/sidetone.iss` (Inno Setup: one setup
  for x64 and ARM64 picking by `IsArm64`, per user, optional PATH entry). CI: `windows` (windows-latest,
  windows-11-arm: MSVC libvocoder, both suites, devices), `windows-installer` (silent install, installed
  self-test, uninstall), `windows-installer-arm` (the same, checks the ARM64 PE went in). The self-test
  skips Unix-only checks on Windows (file modes count as passed; the stand-in speech scripts and the
  launcher entry print SKIP), and macOS and Windows each speak a sentence with their own voice.
  Tested by the owner on Windows 10 x64 and Windows 11 x64 (a domain-joined Toughbook FZ-55): text to
  speech (SAPI) and the microphone (transmit). The first build showed a blank console window beside the
  GUI on both; the WinExe build (f8a9a03) confirmed without it on both. SmartScreen showed on Windows 11
  as the README says, once the setup was extracted with Windows's own zip tool (7-Zip doesn't carry the
  downloaded-file tag over, so the first time it didn't).
  0.34.0: **macOS**. One universal
  `Sidetone.app` (`packaging/macos`: `launcher.c` built for both CPUs in one `clang` call, which execs
  `Contents/Resources/{arm64,x64}/<its own name>`, so `Contents/MacOS/sidetone-cli` works too;
  `Info.plist` with the microphone description; `make-app.sh` signs every Mach-O ad-hoc, then the
  bundle, which covers its main program: signing that alone signs the bundle and fails while anything
  else is unsigned). Audio `PortAudioBackend` (PortAudioSharp2 1.0.6, Apache-2.0; stereo streams at 8 kHz
  or the device rate with `WavFile.StreamResampler`, writes paced at 0.5 s queued); `MacPlatform`
  (`say`, Keychain through Security.framework, osascript notifications with the text as arguments);
  `DesktopPlatform.Setup()` first in both programs (XDG folders to Application Support; Windows to
  %APPDATA%/%LOCALAPPDATA%). Cmd = Ctrl; Linux-only Settings rows hidden; the TUI's window size from
  .NET on macOS (ioctl is variadic, which P/Invoke gets wrong on Apple silicon). The control socket now
  accepts asynchronously and cancels before closing: on macOS a blocked `Accept` hung `Close` (it
  would have hung the app closing). CI jobs `macos` (macos-15: build, both suites, `devices`, .app)
  and `macos-intel` (macos-15-intel: the Intel half through the launcher); runners have a virtual
  sound device only. The owner reported the 0.34.0 build working on their Intel MacBook (macOS 15),
  including the time announcement spoken by macOS's own voice (`say`).
  A tester's MacBook Pro M1 (macOS 26.6.2) got keys from CTRS's FNE (after the peer-list delay below).
  0.33.0: several FNE systems at
  once. `FneLink` (one per system: peer, `KeyStore`, `RadioAliasBook`, and `SinkForTest`/`RidForTest`
  for the self-test); `ConsoleChannel.Link`. The console's `system`, `peer`, `keys`, `aliases`,
  `sinkForTest` and `consoleRidForTest` now forward to the **primary** (first) link, which also holds
  the codeplug-wide settings (hidden tabs, tab outputs, time announcement, history); a channel's own
  settings are under its system (`SystemSettings.FocusedSystem` says which system the focus is on).
  Frames are matched by system and TG (`OnFrame(link, e)`), so the same TG on two FNEs is two channels.
  Radio commands and digital pages go to `CommandLink` (the focused channel's system). A cross-system
  patch sends as the console's RID on the target's system. `CodeplugLoader.SystemsToConnect`: every
  system with channels, minus `SystemSettings.Off` (Settings, Systems row, `SettingsOverlay.Systems.cs`),
  at least one; `--system NAME` still picks one. `LastSystem` is now only the offline view's label.
  Screenshot mode fills any codeplug with the demo's activity (`DemoCodeplug.Populate`), so a codeplug
  rendered there needs 4 tabs and 4 channels.
  Tested by a tester (reported by the owner): several FNEs at once works, and they like turning
  systems off in Settings.
  0.32.0: web streams, the codeplug's
  `web_streams` per zone (`WebStreamPlayer.cs`, `DispatchConsole.Streams.cs`; cards after the tab's
  channels, `StreamCard`; the TUI's `s` box, `Tui.Streams.cs`). MP3 only, decoded by NLayer 3.0.1 (it
  reads `Position` even on a stream that can't seek, so the body is wrapped in a byte counter), mixed to
  mono and resampled to 8 kHz by `WavFile.StreamResampler` (the announcement filter, in pieces). The
  Windows console's behaviour: off at start, 3 attempts 5 s apart, RX held 1.4 s (measured in audio
  played, since a feed arrives in bursts), Basic auth. Refusals (401/403/404, not MP3) fail at once. The
  log shows a stream's name and host only. Tested by the self-test against a local server
  (`TestData/voice.mp3`, also embedded in the Android app for its on-device check).
  0.31.6: the peer tells the FNE
  its version and platform, `SIDETONE_R00A31_LNX` (`CliPeer.SoftwareId`; R major, A minor, as the
  Windows console's `CONSOLE_R01A02`; then `LNX`, `AND`, `WIN` or `MAC`, the owner's choice); it was a
  fixed `SIDETONE_R00A01`. Confirmed: the FNE shows `SIDETONE_R00A31_AND` for a phone. 0.31.5, fixes from an audit:
  `CliPeer.Connected` reads fnecore's state (`Information.State == RUNNING`; it was a flag set at
  login and never cleared, so a lost connection still showed connected); a call whose TDU is lost
  ends after 3 s without frames (`P25VoiceReceiver.EndIfIdle`, `DispatchConsole.CallHousekeeping`,
  also for busy channels via `ActiveLast`); Android takes PTT key broadcasts only when Settings'
  "PTT when locked" allows them (`AppSettings.PttFromBroadcasts`, off by default: any app could send
  one and key the transmitter); an unreadable settings file is kept as `settings.json.bad` and the
  reason logged. 0.31.4: touch taps act on lift
  (`TouchTap` in Controls.cs: cards and buttons ignore a finger that scrolled; hold-to-talk still
  acts on press), and Android opens a Bluetooth headset's call link (SCO, `BluetoothCallLink`)
  while its microphone is used (tester: "no microphone audio on Bluetooth"); the service listens for
  PTT key broadcasts with the screen off or locked (Zebra RKES `com.symbol.button.L1/L2/R1/R2` with
  the KeyEvent, and `org.sidetone.app.PTT_DOWN`/`PTT_UP`), after a Zebra TC57x (Android 13) tester's PTT
  key only worked in the foreground; `PttKeyReceiver` (declared in the manifest) takes Sonim's
  `PTT_KEY_DOWN/UP`, Kodiak's and MCPTT's key intents (Sonim's firmware lists apps declaring them for
  its PTT key). Target PTT devices: Zebra TC57/TC58, Motorola LEX L11 (no public interface known),
  Sonim XP10 and XP Pro. `PttIntents.cs` handles them all. The Sonim XP10's PTT key was tested and works (reported by the owner). 0.31.3: an FNE address that can't be
  looked up (`CliPeer.ResolveAddress`, `FneAddressException`) gets a plain reason instead of a crash
  (0.31.0) or a raw SocketException (0.31.1); found from an Android tester's debug logs. 0.31.2: Go offline / Go online
  (`ConsoleView.GoOffline`: closes the console, keeps the codeplug, remembered as
  `AppSettings.Offline`, so start-up stays offline; Android's notification action is the same).
  0.31.1: debug logs (`DebugLog`:
  written line by line from start-up, crashes, UI freezes, Android's last exit reasons; Save debug
  logs… zips them), Android wakes the screen
  for incoming calls (Settings, Incoming calls; on by default), and libvocoder is upstream's own
  (the m_autoGain fix was merged). 0.31.0: Sidetone for Android
  (src/sidetone-android, CI-built APK/AAB, Android 11+), the GUI's console as `ConsoleView` with a
  phone layout below 700 px, and core platform hooks. 0.30.3: announcements levelled
  like the mic (speech at -20 dBFS) and a Level setting; 0.30.2 fixed the time
  announcement playing again when its schedule was turned on just after the hour; 0.30.1 added a
  speed setting for text to speech; 0.30.0 brought spoken announcements (Piper or espeak-ng) and the time
  announcement; 0.29 brought WAV
  announcements, `listen --key-file`, the dvmcli/dvmgui wrappers removed. Check
  `gh release list` for what was last published.
- Over 400 checks in `sidetone-cli selftest` and the GUI's `--selfcheck` (some run only where they
  apply: a speech engine installed, the system's own voice on macOS and Windows; Unix-only ones SKIP on
  Windows) plus screenshots, and the Android job's on-device
  self-test (`DeviceSelfTest.cs`, on an API 30 emulator).
- **Tested on the air by the owner:** RX/TX clear and AES, key delivery from the FNE, alias sync,
  alert tones, channel marker, QCII and digital pages, radio check (the radio answers; see the FNE
  limitation below), GUI on x86-64 under Hyprland, GUI on ARM64 (Surface Pro 11), display
  scaling on the Surface's HiDPI screen, keyboard shortcuts, instant recall, recording to WAV,
  selectable encryption, the app launcher entry, tabs and hiding zones (0.24), encrypted grant
  demands accepted by a site strapped to encryption (0.24.2: the channel marker on TG 500 was
  granted and heard through the owner's hotspot), text to speech with Piper at a slower speed
  (0.30.1: "sounds much better" than the voice's own pace), the Android app (0.31.0) receiving and
  transmitting both clear and AES on a real device, spoken announcements with the phone's own
  text to speech, instant recall, tones, and receive with the screen off for several minutes (the
  foreground service keeps it connected; the screen stays off, by design), and (0.31.5) the hardware
  PTT key on a Zebra TC57x (Android 13) transmitting with the screen off and locked, through Zebra's key broadcasts with
  PTT when locked allowed, and receiving with the screen locked: an incoming call turns the screen on
  (0.31.1's wake on call, twice in a row) and Sidetone shows over the lock screen, in the
  phone layout once undocked; in Zebra's desktop dock mode Android reports a wide, low-scale display,
  so the tablet layout there is expected, not a bug), and (0.31.4) a Bluetooth headset's microphone
  on a Galaxy S23 Ultra (the SCO call link).
- The owner tests every release on the air and on their hardware after publishing it. The list
  below is only what the earlier sessions never heard results for, not what is known to be
  untested. Ask the owner before calling something untested, and move items up once confirmed.
- **Patching (0.24.2):** works across the network: other consoles hear the source TG's traffic on
  the patch target (10503 of 10503 frames sent, source gaps up to 266 ms). Not yet heard on a
  radio through the owner's hotspot, now thought to be FNE and radio programming (being worked on
  with the system's operators, after `affiliated: true` on both TGs didn't fix it). Earlier: the owner's
  hotspot has one voice channel, and the FNE sent the source TG (505, the hourly system update) to
  it although nothing there was affiliated (the owner's radio was on the target TG, not scanning),
  so the patch target's grant was denied ("no channels available"). Not a Sidetone fault. Retest
  with a source TG the FNE doesn't send to the hotspot.
- **No results reported yet:** page
  notifications, mic processing (on by default!), local KFDtool key files and the keyring, the
  Settings screen with real devices, the Motorola desk mic in the GUI, `--wayland`, and from 0.25:
  log files and Copy log, the TUI using the GUI's saved settings, the TUI's card buttons and key
  file box; from 0.26: Opus recordings and Delete after; from 0.28: one-way patches, outputs per
  tab and channel, the call history between runs; from 0.29: announcements, `listen --key-file`; from 0.30: the scheduled time announcement, the new announcement level (0.30.3); from 0.31:
  on Android, hardware PTT keys on the Sonim XP Pro; the LEX L11's PTT key was reported not working (how far it got not known yet); from 0.32: web streams (a real feed, and
  one with a password); from 0.37: DMR receive and transmit on the air (CTRS's DMR FNE); from 0.38: the time limit's beeps and
  latch, the talkgroup check against CTRS's real talkgroup list, and the chimes on a real drop; from 0.39:
  tone presets heard on a radio, DTMF presets reaching a decoder (doubtful through IMBE), MIDI import of a
  real file, and folding; from 0.40: the Windows icon on a real install (CI checks the exe), the marker's
  interval, and tones/announcements on the focused channel only; from 0.34: macOS on Apple silicon (only CI so far), and on the
  owner's Intel Mac whatever their "it worked" didn't cover (ask before listing parts as tested); from
  0.36: the boxes on a real tablet; from
  0.35: on Windows 10 and 11 x64 the rest (receive audio, Credential Manager, the taskbar flash, the
  TUI), and all of it on Windows 11 ARM; and the
  desktop's phone layout
  (Piper is installed on the owner's Surface, through `uv tool install piper-tts`, with the
  en_US-lessac-medium voice).
- Next on the wishlist ("To be implemented"): a CI test against a real dvmfne (no longer waiting: dvmhost's
  `r05a06_dev` merged into `master` as R06A00, eb2f9ae, on 2026-11-27); analog (`mode: analog`, G.711 mu-law; for any DVM network, though CTRS doesn't carry it);
  NXDN; TMS and Location over MQTT (waiting on details); then Windows-console parity items
  (extra clocks, settings export, hiding panels). Sidetone is for any DVM
  network, not only CTRS (the owner's word). DMR's leftovers (encryption, patches, tones and
  announcements, radio commands) are under "Partially implemented". The TUI's only remaining gap is audio over SSH.

## Layout

```
src/Directory.Build.props   shared: Version, net10.0, nullable off, unsafe on
src/sidetone-core/          class library, namespace Sidetone.Core: everything that isn't UI
src/sidetone-cli/           terminal front end (TUI; `Tui` is partial: Tui.Zones.cs tabs, Tui.KeyFile.cs key file box), commands, self-test
src/sidetone/               graphical front end (Avalonia 12.1.6), namespace Sidetone.Gui; ConsoleView is the console,
                            MainWindow only its desktop frame
src/sidetone-android/       Sidetone for Android (net10.0-android, API 30+, targets 36, org.sidetone.app): compiles
                            src/sidetone's sources (all but Program.cs) and adds the activity, audio, service, TTS, keystore
extern/dvmconsole           submodule: monkey1911/dvmconsole-ARM64, branch dev (the Windows console)
extern/dvmvocoder           submodule: DVMProject/dvmvocoder main at e7ab96e (v0.2 plus the m_autoGain fix), built by CMake into libvocoder.so
packaging/                  icons (sidetone-1024.png for macOS), macos/ (universal .app), windows/ (Inno Setup), sidetone.desktop (for packages; the dvmcli/dvmgui wrappers went in 0.29),
                            appimage/ (AppRun, build-appimage.sh: pinned appimagetool 1.9.1 + runtime 20251108,
                            SHA-256 checked), arch/ (PKGBUILD.in template + make-pkgbuild.sh, sidetone.install)
tools/                      ci-status.sh, ci-screenshots.sh (see Testing), android-check.sh (the emulator self-test)
.github/workflows/build.yml the only CI workflow ("Build"; Linux, Android, macOS and Windows)
```

### Core (`src/sidetone-core`)

- `DispatchConsole` (partial: `.cs`, `.Patch.cs`, `.Recall.cs`, `.Zones.cs`, `.Outputs.cs`, `.History.cs`, `.Announce.cs`, `.Speech.cs`) is the console: channels,
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
- Audio: `AudioBackend.cs` (`IAudioBackend`, `IAudioOutput`, `IAudioInput`; `AudioBackend.ForPlatform`
  picks `PipeWireBackend` on Linux, `PortAudioBackend` on macOS, `WasapiBackend` on Windows; Android sets
  its own `AndroidAudioBackend`; `NoAudioBackend` where none works). `AudioSinks.cs` (`PipeWireSink` = `pw-play`,
  `WavSink`), `PipeWireCapture.cs` (`pw-record`), `AudioDevices.cs` (`pw-dump` parsing, device
  matching, startup report). **Nothing outside the PipeWire backend may start `pw-*` tools.**
- Recordings: `OpusFileSink` (in `AudioSinks.cs`; Concentus 2.2.2 + Concentus.OggFile, pure C#,
  `AttemptToUseNativeLibrary = false`; it mends Concentus.OggFile's ending: pre-skip, an empty
  last page dropped, EOS and the exact end granule, CRCs recomputed, checked with ffmpeg) or
  `WavSink`; `Recordings.Prune` deletes only `.opus/.wav/.part` in `FOLDER/SYSTEM/YYYY-MM-DD`.
  ffmpeg warns "non monotonically increasing dts" when decoding any trimmed Opus file, its own too.
- `AudioProcessing.cs`: `VoiceProcessor` (low cut, gate, auto level, limiter; 8 kHz, 160-sample
  blocks), `WavFile` (reader + level description). Plain C# on purpose (portability).
- `StartupChoices.cs`: what both consoles start with (option, else the GUI's saved setting, else
  the default) for audio devices, PTT device, key file and time limit (5 to 900 s); and `LogFiles`
  (a log file per day in `~/.local/state/sidetone/logs`, 14 days kept; `DispatchConsole.LogFolder`,
  `--log FILE` wins, `--no-log` none; never set in demo, screenshots or tests).
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

`ConsoleView` (a `UserControl`, partial: `ConsoleView.Narrow.cs` is the phone layout) is the whole
console; `MainWindow` is a thin desktop `Window` around it (title, icon, keys, closing). On Android,
`App` hands the same view to `IActivityApplicationLifetime.MainViewFactory` (made once, kept).
Below 600 px wide (700 until 0.40.2; 600 so an unfolded Galaxy Fold, about 690 dp, gets the tablet layout) the view switches to the phone layout (one column, big PTT, Channels/Calls/Log/More
pages; on a phone each box is a full-screen sheet, `PhoneSheet`, while tablets keep the usual boxes). `IGuiPlatform` (`GuiPlatform.Current`,
null on a desktop) is what a device adds: the hardware PTT key, keep screen on, the foreground
service, battery settings; `SettingsOverlay.Device.cs` holds its Settings rows.

No XAML: everything is built in C#. `Palette` (light/dark) is painted by hand on a 100 ms
`DispatcherTimer` refresh (`ConsoleView.RefreshNow`). Boxes are in-window overlays
(`CommandOverlay` for page/radio, `SettingsOverlay`, `KeyFileOverlay`, `PatchOverlay`,
`RecallOverlay`, `ZoneOverlay` for the Tabs box), not separate windows. `ZoneTabs` is the tab strip;
all cards are built once and only the current zone's are made visible. `Tap` (in `Controls.cs`) is the flat button used
everywhere; set `Released` for hold-to-talk. `ChannelCard` is one card. Keyboard handling is in
`ConsoleView.HandleKeyDown/Up` (tunnel handlers on the window or top level; an open box takes keys first). Corners
come from one shared `Ui.CornerRadius` (2 px, the owner's squarer look): use it for any new button, card,
pill or box instead of a number of its own. `DemoCodeplug` is a
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

**macOS and Windows builds happen in CI only** (jobs `macos`, `macos-intel`, `windows`, `windows-installer`,
`windows-installer-arm`). For macOS: the managed code cross-publishes from
Linux (`dotnet publish -r osx-arm64` compiles and catches errors), but libvocoder, signing and the .app
need a Mac. PortAudio also runs on Linux (ALSA), so `PortAudioBackend` can be exercised here from a
scratch program (play silence only).

**Android builds happen in CI only** (job `android`): the NDK that builds libvocoder for Android runs on
x86-64 hosts only, and this Surface is ARM64 with little disk. The job builds libvocoder for arm64-v8a
and x86_64 (`-Wl,-z,max-page-size=16384`), publishes the APK and AAB, checks every `.so` for 16 KB
page alignment, and runs `tools/android-check.sh` on an API 30 emulator (the on-device self-test,
`DeviceSelfTest.cs`, logs to logcat tag `sidetone`, then phone and tablet screenshots, uploaded as the
`sidetone-android` artifact). Signing uses repo secrets `ANDROID_KEYSTORE_BASE64`,
`ANDROID_KEYSTORE_PASSWORD`, `ANDROID_KEY_ALIAS` when set, else a throwaway debug key. The desktop
GUI's phone layout can be checked locally: `--size 412x915` (and `--show calls|log|more`).
Debug logs (`DebugLog.cs`, 0.31.1, asked for when an Android tester's codeplug wouldn't load): one
file a day in `$XDG_STATE_HOME/sidetone/debug`, each line appended at once (survives crashes);
`DebugLog.Mark` records the current step, which the UI watchdog (`WatchUi`, started in `App`) reports
on a freeze; Android adds `ApplicationExitInfo` (last exits, ANR thread dumps). Never put codeplug
contents, passwords or keys in it. Not started for the demo, screenshots, self-checks or `--no-log`.
Platform hooks in the core for Android: `Speech.Platform`, `SecretStore.Platform`,
`DesktopNotify.Platform`; files go under the XDG variables, which the Android app points into its
own storage.

The build should have no NuGet warnings. `System.Security.Cryptography.Xml` (a direct reference in
`sidetone-core`, used only for `.ekc` key files) was raised to 10.0.15 in 0.23.1 for advisories in
10.0.0. Check with `dotnet list src/sidetone-core/sidetone-core.csproj package --vulnerable --include-transitive`.

What CI does on every push to `main` (both architectures):
1. builds libvocoder, publishes `sidetone-cli` and `sidetone` into one folder;
2. `sidetone-cli selftest`: the core's self-test, printing `PASS:`/`FAIL:` lines;
3. `sidetone --selfcheck`: drives the GUI headlessly (Avalonia.Headless), presses keys, opens boxes;
4. renders screenshots (`--screenshot FILE --demo --theme dark|light --show page|radio|settings|keyfile|patch|recall|tabs`)
   and prints them base64 into the log between `-----BEGIN PNG name-----` / `-----END PNG name-----`;
5. packages `sidetone-<tag>-linux-<arch>.tar.gz` (with a `desktop/` folder) and `Sidetone-<tag>-<x86_64|aarch64>.AppImage` (tested with
   `APPIMAGE_EXTRACT_AND_RUN=1`: versions and `--selfcheck`), and on a published **release**
   attaches them (there is deliberately no tag trigger: two runs raced and clobbered uploads);
6. job `arch-package` (after both): makes the PKGBUILD from the two tarballs, builds it with
   makepkg in an `archlinux:latest` container, installs it with pacman and runs `sidetone-cli
   selftest` from `/usr/bin` (x86-64 only; tested on ARM64 Omarchy by hand), and on a release
   attaches `sidetone-bin-<tag>.tar.gz` (PKGBUILD + sidetone.install). The owner can't publish to
   the AUR yet (sign-ups closed). Inside an AppImage, `AppLauncher.ProgramPath` is `$APPIMAGE`.

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

- **Upstream DVMConsole is `main` again** since 2026-11-27: the R02A00 release (547832a) merged
  `r01a02_dev` into `main`; the new dev branch is `r02a02_dev`. Before that, `main` was abandoned
  (net8.0, last commit 2026-03) and `r01a02_dev` was the live branch. The owner's fork
  (monkey1911/dvmconsole-ARM64, our submodule, at 7082da1 from 2026-11-02 on its `dev` branch) is
  behind R02A00. Compare against upstream `main` (or `r02a02_dev`) when checking what the Windows
  console does. dvmhost merged `r05a06_dev` into `master` the same day (R06A00, eb2f9ae; its dev
  branch is now `r06a02_dev`), and fnecore's `master` is 70e9d60, which R02A00 pins. None of them
  set the grant encrypt flag below (checked 2026-11-27), so `CliPeer.BuildTdu` stays.

- **Grant demands for encrypted calls must carry the grant encrypt flag** (control byte 14:
  `0x80` grant demand, `0x08` grant encrypt; dvmhost's `NET_CTRL_*` in `BaseNetwork.h`). A dvmhost
  site with the talkgroup strapped to encryption otherwise denies the grant ("TGID enc. strapping
  rejection", `DENY_TGT_GROUP_NOT_VALID`). fnecore's `SendP25TDU` never sets it (nor does the Windows
  console), so `CliPeer.BuildTdu` builds the TDU itself. Found on the owner's hotspot site (0.24.2).
- A one-voice-channel site (the owner's hotspot) can't carry a patch's source and target at once:
  whichever talkgroup is granted first holds the channel, and the other's grant is denied. dvmhost
  grants a network grant demand without checking affiliations (the FNE decides what reaches a
  site). The FNE doesn't tell the peer about a denied grant.
- **A new peer ID gets no keys until CTRS's FNE has pulled its peer list from myCTRS.** Until then the
  FNE ignores the peer's key requests without a reply (Sidetone logs "requested … key" and nothing comes
  back). Found 2026-11-23 with a tester's Mac (peer 63007, RID 99508): the next try got its keys. Ask how
  new the peer is before suspecting Sidetone. **Per-peer passwords have the same delay:** after a password
  change in myCTRS, the FNE keeps the old one until its next sync. Until then the login stalls without a NAK:
  "login ACK received" then "Sending login request" again every 5 s (the hash isn't answered), or no ACK at
  all. Found 2026-11-27 with peers 113007 and 113017; both connected after the sync.
- **The FNE version CTRS runs does not pass extended-function replies (radio check acks) back to
  the peer that sent them.** The radio does answer over the air. Don't ask the owner about it again;
  Sidetone already decodes and logs replies if a newer FNE sends them.
- Text to speech (`Speech.cs`, `DispatchConsole.Speech.cs`): Piper (`piper`/`piper-tts`, voices
  are `.onnx` + `.onnx.json` in `~/.local/share/piper-voices` or `/usr/share/piper-voices`) or
  espeak-ng, run with the text on stdin (`-m MODEL -f OUT` / `--stdin -v VOICE -w OUT`), never a
  shell; programs are also looked for in `~/.local/bin` (app launchers' PATH lacks it). The engine
  runs on a worker thread, then `PostToUi`. The time is put into words by Sidetone (not the
  engine). The scheduler is in `Housekeeping`; tests drive it with `clockForTest`,
  `synthesizeForTest` and `sendAnnouncementForTest`, and stand-in engine scripts via
  `Speech.programFoldersForTest`. Under `InvariantGlobalization`, `TimeZoneInfo.StandardName` is
  the tzdata abbreviation (PST, or "+04" where there are no letters). Speed (`SpeechRate`, one of
  `Speech.Rates`) is passed only when not 1: Piper `--length_scale` = the voice's own (from its
  `.onnx.json`) / speed, espeak-ng `-s` = 175 × speed. Piper's output length varies a few percent
  from run to run (its noise), so compare speeds over several runs. The AUR's `piper-tts` is
  x86-64 only and its onnxruntime pulls in CUDA/TensorRT: don't suggest it.
- Announcements: `WavFile.ReadAnnouncement` reads any PCM/float WAV, resamples through a
  windowed-sinc low-pass (`WavFile.Resample`) and levels the speech (`WavFile.SpeechLevel`: RMS of
  the 20 ms blocks within 25 dB of the loudest) to the mic's -20 dBFS target, +12 dB at most, peaks
  under -1 dBFS (0.30.3; it was -3 dBFS peak, which put speech about 3 dB above the mic and was
  reported hot on RF). `AnnouncementLevel` (-9 to +3 dB) is applied at send time and in Hear it;
  `TonePlan.FromAudio` sends it through the voice encoder like a tone job. IMBE mangles pure sines,
  so test announcements with speech-like audio (`VoicedFrame`), compared with the vocoder alone.
- **libvocoder's `MBEDecoder` didn't initialise `m_autoGain`** until upstream commit e7ab96e, so a
  decoder made in reused heap memory turned on its automatic gain and decoded about six times too
  loud, at random. It showed up as an Opus recording check failing now and then in CI (it looked like
  an AVX-512 problem at first: it isn't). Reported as DVMProject/dvmvocoder#2 and fixed by the
  owner's pull request DVMProject/dvmvocoder#3, merged 2026-11-21; the submodule is at that commit
  and Sidetone's own patch (patches/, gone since) was dropped. The self-test still makes decoders in
  dirtied memory, so a submodule move that loses the fix fails it. Keep the submodule at or past
  e7ab96e.
- Tones go out as IMBE tone frames (as the Windows console does), so QCII tones are quantised to
  about 31 Hz steps.
- Patch members have a `PatchRole` (Both, SourceOnly, TargetOnly); the Windows console's one-way
  mode is "only the first member is a source". Outputs: `OutputFor(c)` = channel's own, else its
  zone's, else `Audio.OutputTarget`; saved by node.name in `ChannelSettings.Output` and
  `SystemSettings.ZoneOutputs`. History: `~/.local/state/sidetone/history/SYSTEM.json` (Recent
  calls with dates, recall frames base64), secure audio only with `HistoryKeepSecure`; the
  Windows console keeps call history in memory only. Emergency alarms are tabled (upstream removed
  its emergency support on purpose in March 2025).
- Patching passes clear IMBE codewords between talkgroups (decrypt on receive, re-encrypt per
  target), so patched voice is not decoded and re-encoded. Since 0.24.1 it is buffered and sent
  one LDU per 180 ms (2-LDU prebuffer, a 10 ms `Timer`; tests drive `PatchPump` by hand), because
  forwarding frames as they arrived came out choppy on a hotspot site. `patchSourceIdPassthrough` (top level of
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
- **Reading the owner's logs and screens.** When this session runs on the machine with the
  console, read its log files (`~/.local/state/sidetone/logs/`) and the owner's screenshots
  (`~/Pictures/Screenshots/`, newest first) directly instead of asking for pastes. Both the GUI and
  TUI log to file since 0.25. Site logs (dvmhost) are on the owner's hotspot, so those get pasted.
- Say plainly what was tested where (CI vs on the air) and what wasn't.
- **README.md has a Contents list** at the top (every `##` and `###` heading, linked by GitHub's
  anchors: lower case, punctuation dropped, spaces to hyphens). Update it when a section is added,
  renamed or removed.
- **WISHLIST.md** has three sections, top to bottom: To be implemented, Partially implemented,
  Fully implemented. When something is done, strike it through (`~~...~~`) with "Done in X.Y"
  rather than deleting or rewording it, including each "Still to do" sub-item (one per line), and
  move the entry down once nothing in it is left.
- Keep personal names out of the public repo's files (the wishlist refers to "the FNE's
  maintainers").
- **Never print or commit a codeplug**: they contain FNE passwords. `.gitignore` ignores `*.yml`
  outside `.github/workflows/`. Likewise keys, key-file passwords and recordings.
- The owner works on several machines: an x86-64 desktop (Hyprland, Omarchy), a Surface Pro 11
  (ARM64, Omarchy), and has an Intel MacBook on macOS 15 for an eventual macOS port.
