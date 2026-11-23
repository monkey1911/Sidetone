# Wishlist

What we'd like Sidetone to do, in three parts: what hasn't been started, what is done but has
something left, and what is finished. Nothing here is promised or scheduled. When something is
done it is struck through with the version it came in; an entry moves down a section once nothing
is left in it.

## To be implemented

Roughly in the order that makes sense to do them.

1. **An automated network test.** Run a real dvmfne in CI with two console instances and check login, voice in both directions and encryption (key delivery from the FNE, then an AES call decrypted on the other side). Today every on-air check is done by hand.
   - **Waiting** for dvmhost's `r05a06_dev` to be merged into `master` (expected soon, per the FNE's maintainers), so the test is pinned to what CTRS will run rather than a moving dev branch.
   - The CI script writes the FNE's config, talkgroup rules, peer list, RID list, key container and both codeplugs each run, with made-up passwords and a test key; nothing is committed. The RID list must allow both consoles' radio IDs to request keys, or the FNE silently ignores their key requests.
   - An FNE alone would not have caught the missing grant encrypt flag fixed in 0.24.2: the dvmhost site denied that grant, not the FNE. Catching that would need a dvmhost in the test too (not yet checked whether one runs without a modem).

2. **DMR**, ~~**and more than one FNE system at a time.**~~ (Several systems at once: done in 0.33.) libvocoder already has the codec, the cards already show modes that aren't supported yet, and the Windows console in the submodule handles DMR (`MainWindow.DMR.cs`, `FneSystemBase.DMR.cs`, `DMR/DmrTxCall.cs`) to follow, so the work is a receiver and a transmitter. CTRS can carry DMR, but on a second FNE of its own (what setting it up last time required), so for a dispatcher DMR goes with connecting to several FNEs at once, as the Windows console can: one console with the P25 and DMR channels together.
   - ~~Until then, a second Sidetone with a codeplug for the DMR FNE works, in its own window.~~ Done in 0.33: one console connects to every system in the codeplug (each turned off in Settings if wanted), so DMR channels on the DMR FNE will sit beside the P25 ones once DMR itself is done.
   - Testing without radios: two Sidetones through the network test's FNE (item 1). On the air needs someone on CTRS with a DMR radio; until then the release notes say DMR is untested on the air.
   - Ask the FNE's maintainers whether the `r05a06_dev` merge (DMR grant work on 2026-11-10) still needs a separate FNE for DMR.
   - **Analog** too, once we've confirmed how DVM carries it (the Windows console has `MainWindow.Analog.cs`). It may be the simplest.

3. **NXDN**, for networks that carry it (CTRS doesn't). The same work as DMR (libvocoder has the codec; the Windows console's `MainWindow.NXDN.cs` and `FneSystemBase.NXDN.cs` show the framing), but with nobody to test it against a radio.

4. **TMS (text messaging) and Location, over MQTT.** Waiting on others: what the FNE side will publish and accept.
   - ~~**SMPP** (2026-11-22, from the FNE's maintainers): the console will talk to a TMS server over SMPP.~~ Dropped on 2026-11-22: SMPP can't do secure authentication. Both TMS and the new **Location** feature will go over **MQTT** instead.
   - **Client: MQTTnet** (MIT, a .NET Foundation project, 5.2 with a net10.0 build, July 2026). MQTT 3.1.1 and 5.0, TLS including client certificates, user name and password, MQTT 5 enhanced authentication (the extensible part, if the FNE side wants more than a password), WebSockets. Plain C#, so the same on Linux, macOS, Windows and Android. Sidetone is only a client; the broker is on the FNE or server side. MQTTnet's own broker can stand in for it in the self-test, as a local HTTP server does for web streams.
   - Still to learn from the FNE's maintainers: the broker's address and how a console logs in (likely new codeplug keys per system: host, port, TLS, user name, password or certificate); the topic layout and message format (JSON?) for messages, delivery receipts and locations; how radio IDs map to topics; whether location is pushed regularly or asked for.
   - What it would likely take, once known: one MQTT connection per system in the core (connecting and reconnecting with the FNE link, `FneLink`); a messages panel in the GUI and TUI, and notifications for incoming messages (Android's, and the desktop ones); for Location, the last known position per radio in the call list and radio box at first, with a map later if wanted. Secrets (passwords, a client certificate's key) are never logged, as for FNE passwords.

## Partially implemented

- ~~**Windows 10/11 and macOS.**~~ (macOS: done in 0.34; Windows: done in 0.35.) Avalonia and most of the core are already cross-platform, and audio goes through one backend interface (0.23). The Linux-only parts are the audio backend (PipeWire), PTT devices (evdev) and the control socket, so each platform needs its own version of those.
  - ~~macOS is new ground.~~ Done in 0.34: PortAudio over Core Audio, macOS's own speech (`say`), Keychain and notifications, Cmd for Ctrl. Windows already has the upstream console, so the gain there is smaller.
  - ~~**Windows next (0.35):**~~ Done in 0.35: an Inno Setup installer for x64 and ARM64 (per user, no admin), NAudio WASAPI audio (the Windows console's library), SAPI speech, Credential Manager, the TUI through the console's VT mode, CI on `windows-latest` and `windows-11-arm` (both suites natively, then install, the installed self-test and uninstall on both; Windows builds keep .NET's globalization for IANA time zones). The owner tests on Windows 10 and 11 x64 and Windows 11 ARM.
  - Still to do on Windows: PTT devices (USB HID desk mics), toast notifications (need a Start menu identity), signing (see below), and moving to NAudio's WasapiPlayer/WasapiRecorder builders.
  - Test hardware: an Intel MacBook on macOS 15 is available. An Apple silicon tester is still needed (CI runs the tests on Apple silicon, but without sound devices).
  - Still to do on macOS: PTT devices (USB HID desk mics), a `.dmg`, and paid signing and notarization if the Open Anyway step becomes an obstacle.
  - ~~**macOS package: one universal `Sidetone.app`**~~ Done in 0.34 (`packaging/macos`; the launcher is built for both CPUs in one `clang` call, and the Intel half is tested on an Intel runner instead of Rosetta): for Intel and Apple silicon. .NET publishes one architecture at a time, and a self-contained runtime can't be merged with `lipo`, so the app holds both complete builds (`osx-x64` and `osx-arm64`) and a tiny universal launcher (a few lines of C, built for both and joined with `lipo`) starts the right one. About twice the size of one build (roughly 80 to 100 MB zipped), with no .NET install needed. `libvocoder` builds universal directly (`CMAKE_OSX_ARCHITECTURES="x86_64;arm64"`); SkiaSharp and HarfBuzz already ship universal. CI can publish both from one Apple silicon runner and test the Intel half under Rosetta. Rejected: a framework-dependent universal app (smaller, truly merged, but users must install .NET 10 first) and two separate Mac downloads (users must pick).
  - ~~**macOS signing:**~~ Done in 0.34 as planned here (ad-hoc, Open Anyway in the README): a free Apple ID only signs apps for your own Macs; signing for others (a Developer ID certificate) and notarization need the Apple Developer Program ($99 a year). Start without it: builds get the free ad-hoc signature (needed on Apple silicon anyway), and the README explains letting the app in once: on macOS 15 open it, then **System Settings → Privacy & Security → Open Anyway** (right-click → Open no longer works there), or `xattr -dr com.apple.quarantine /Applications/Sidetone.app`. Pay for the program only if the warning becomes a real obstacle; a Developer ID signature can be added to the same `.app` later.
  - **Windows signing:** unsigned programs still run, after SmartScreen's "More info → Run anyway", but on Windows 11 PCs with Smart App Control on they are blocked outright. Options, cheapest first: **SignPath Foundation** (free code signing for open-source projects built in public CI; apply first, they review each project), **Microsoft Trusted Signing** (about $10 a month; check country and identity eligibility), a traditional OV certificate (a few hundred dollars a year, on a hardware token or cloud service, awkward from CI; EV no longer skips SmartScreen's reputation building), or the **Microsoft Store** (MSIX; the Store signs what it distributes). Fallback: unsigned builds with the SmartScreen steps in the README, noting that Smart App Control needs a signed build.

- ~~**Android.**~~ Done in 0.31: Sidetone for Android 11 and newer (built for API 36, as Google Play requires), the graphical console with a phone layout, Android's audio, a foreground service that stays connected with the screen off, the phone's own text to speech, key-file passwords in the Android Keystore, and a hardware PTT key learned by pressing it. CI builds the APK and the Play Store bundle and runs the self-test on an Android 11 emulator.
  - Still to do: publish on Google Play: a developer account, the store listing, the privacy policy, the Data safety form, and the foreground service declaration (with a short video). Maybe separate US and international builds, if the encryption export rules call for it.
  - ~~Still to do: rugged phones whose PTT key reaches only apps that know the maker's own signal (Sonim).~~ Done in 0.31.4 (Sonim's `PTT_KEY_DOWN`/`UP`, Kodiak's and MCPTT's key actions); tested on a Sonim XP10.
  - Still to do: the same for Kyocera and others, once someone with such a phone can say which signal it sends.
  - ~~Still to do: exporting or sharing recordings (they stay in the app's storage).~~ Done in 0.36: Instant recall, Recordings…, Share or Share day through Android's share sheet (a FileProvider that serves only the recordings).
  - ~~Still to do: boxes laid out for a phone (today the desktop boxes are scaled down to fit).~~ Done in 0.36: full-screen sheets on phones (`PhoneSheet`), the usual boxes on tablets.

- ~~**Selectable encryption and local key files.**~~ Done in 0.19: channels marked `selectable_encryption` can be switched between secure and clear for transmit, and keys can come from a password-protected KFDtool `.ekc` container.
  - ~~The terminal console can only unlock a key file before it starts.~~ Done in 0.25: `K` opens its key file box at any time.
  - ~~`sidetone-cli listen` doesn't use a key file yet.~~ Done in 0.29: `--key-file`, Settings' choice or the codeplug's.
  - Still to do: try a key file made by KFDtool itself (so far only one written by the self-test).

- ~~**Patching.**~~ Done in 0.20, passing voice frames between talkgroups without decoding and re-encoding them.
  - ~~Patched calls come out choppy on a radio site.~~ Fixed in 0.24.1: patched voice is buffered and sent at a steady pace.
  - ~~One-way patches (the Windows console has them).~~ Done in 0.28: each patched channel goes both ways, source only or target only.
  - Still to do: remembering patches between runs, if that turns out to be wanted.
  - Still to do: patching across modes, once there are other modes.

- ~~**Desktop notifications.**~~ Done in 0.21, for pages (call alerts) to the console.
  - Tabled: emergency alarms. Upstream added emergency support in late 2024 and removed it on purpose in March 2025 ("what do you think we are mission critical?"); to be decided later whether Sidetone shows the emergency bit at all.

- ~~**Announcements: playing a WAV file onto a talkgroup.**~~ Done in 0.29: a remembered list of WAV files (any common format, converted and levelled), played on the focused channel or every listening channel (the Windows console's custom alert tones).
  - ~~Spoken announcements: text said by a text-to-speech engine.~~ Done in 0.30: Piper or espeak-ng, installed separately; "Hear it" plays them through the vocoder without sending.
  - ~~A time announcement, played when asked or by itself on the clock.~~ Done in 0.30: words before and after, 12- or 24-hour, a time zone (said as its abbreviation or your own words), every 15, 30 or 60 minutes, waiting for quiet channels.
  - Still to do: scheduling WAV files and spoken texts too (once, or repeating), like the Windows console's timed announcements.
  - Still to do: text to speech on macOS (its own `say` would do), with the macOS port.

- ~~**Packaging.**~~ Done in 0.27: an AppImage and an Arch package (`sidetone-bin` PKGBUILD) with each release, next to the tarballs; the PKGBUILD is built, installed and run on Arch in CI.
  - Still to do: publish it on the AUR, once AUR sign-ups open again (or someone with an account publishes it with us as co-maintainers).

- ~~**The terminal console caught up with the graphical one.**~~ Done in 0.25.
  - ~~Use the graphical console's saved audio devices, PTT device, transmit time limit and key file when no option is given.~~ Done in 0.25.
  - ~~Clickable buttons for Replay, Rec, patch, secure/clear and key request.~~ Done in 0.25, on each card's bottom edge.
  - ~~The same transmit time limit range in both.~~ Done in 0.25: 5 to 900 seconds.
  - ~~Unlocking a key file after start-up.~~ Done in 0.25: `K`.
  - Still to do: sending audio to another machine's sound system, for when the TUI is run over SSH (today audio stays on the machine Sidetone runs on).

## Fully implemented

- ~~**Web streams.**~~ Done in 0.32: the codeplug's `web_streams` per zone, as cards after the tab's channels (a Streams box in the TUI), MP3 decoded by NLayer on every platform, played to the tab's output; the Windows console's 3 attempts and RX hold. AAC, Ogg and HLS streams are refused with a reason.

- ~~**Rename the project.**~~ Done in 0.17: the project is now **Sidetone**, "an IP dispatch console for DVMProject FNE networks". The programs are `sidetone` and `sidetone-cli`, settings carry over, and the old `dvmcli` and `dvmgui` commands kept working for a while.
  - ~~Remove the old command wrappers after a release or two.~~ Done in 0.29.

- ~~**Settings screen in the GUI.**~~ Done in 0.18: audio output and microphone, the PTT device and its buttons, the transmit time limit and what Space does are in a Settings box and are remembered.
  - ~~The terminal console doesn't read these saved choices yet.~~ Done in 0.25: options still win, otherwise the TUI uses what Settings saved.

- ~~**Start the GUI from the desktop's launcher.**~~ Done in 0.18: Settings can add Sidetone to the app launcher, with an icon (`sidetone --install-launcher` does the same from a terminal).
  - ~~System-wide packaging.~~ Done in 0.27 (see Packaging above).

- ~~**Hold-to-talk on the on-screen PTT button, and a PTT on each card.**~~ Done in 0.19.

- ~~**Instant replay and talkgroup recording.**~~ Done in 0.21: the last ten calls per channel, with talkgroup recording in both consoles.
  - ~~Deleting old recordings automatically after a set number of days.~~ Done in 0.26: **Delete after** in Settings (7 to 365 days, or keep them).
  - ~~A compressed format (Opus).~~ Done in 0.26: recordings are Opus by default, about an eighth of the size of WAV; WAV can still be chosen.
  - ~~A call history that survives closing the console.~~ Done in 0.28: Recent calls and instant recall are kept between runs (the Windows console keeps its history in memory only).

- ~~**Microphone processing.**~~ Done in 0.22: low cut, noise gate, automatic level and limiter in C#, plus optional receive leveling.

- ~~**An audio device layer.**~~ Done in 0.23: all playing, recording and device listing goes through one interface, with PipeWire as the first backend. A Windows (WASAPI) or macOS (Core Audio) port now needs only its own backend for audio (PTT devices and the control socket still need theirs).

- ~~**Tabs (zones).**~~ Done in 0.24: each codeplug zone is a tab in both consoles, in the codeplug's colours, and a zone can be hidden (its channels switched off) as with the Windows console's Tab Manager.
  - ~~Sending each zone, and each channel, to its own audio output.~~ Done in 0.28.

- ~~**Log files.**~~ Done in 0.25: a log file per day for both consoles, and Copy log in Settings.
