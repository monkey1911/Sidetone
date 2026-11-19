# Wishlist

What we'd like Sidetone to do, in three parts: what hasn't been started, what is done but has
something left, and what is finished. Nothing here is promised or scheduled. When something is
done it is struck through with the version it came in; an entry moves down a section once nothing
is left in it.

## To be implemented

Roughly in the order that makes sense to do them.

1. **An automated network test.** Run a real dvmfne in CI with two console instances and check login, voice in both directions and encryption. Today every on-air check is done by hand (the missing grant encrypt flag fixed in 0.24.2 would have shown up here).

2. **NXDN and DMR.** libvocoder already has the codec both use, and the cards already show modes that aren't supported yet, so the work is a receiver and a transmitter per mode. Needs live traffic to test against.
   - **Analog** too, once we've confirmed how DVM carries it. It may be the simplest of the three.

3. **Windows 10/11 and macOS.** Avalonia and most of the core are already cross-platform, and audio goes through one backend interface (0.23). The Linux-only parts are the audio backend (PipeWire), PTT devices (evdev) and the control socket, so each platform needs its own version of those.
   - macOS is new ground. Windows already has the upstream console, so the gain there is smaller.
   - Test hardware: an Intel MacBook on macOS 15 is available. An Apple silicon tester is still needed.
   - **macOS package: one universal `Sidetone.app`** for Intel and Apple silicon. .NET publishes one architecture at a time, and a self-contained runtime can't be merged with `lipo`, so the app holds both complete builds (`osx-x64` and `osx-arm64`) and a tiny universal launcher (a few lines of C, built for both and joined with `lipo`) starts the right one. About twice the size of one build (roughly 80 to 100 MB zipped), with no .NET install needed. `libvocoder` builds universal directly (`CMAKE_OSX_ARCHITECTURES="x86_64;arm64"`); SkiaSharp and HarfBuzz already ship universal. CI can publish both from one Apple silicon runner and test the Intel half under Rosetta. Rejected: a framework-dependent universal app (smaller, truly merged, but users must install .NET 10 first) and two separate Mac downloads (users must pick).
   - **macOS signing:** a free Apple ID only signs apps for your own Macs; signing for others (a Developer ID certificate) and notarization need the Apple Developer Program ($99 a year). Start without it: builds get the free ad-hoc signature (needed on Apple silicon anyway), and the README explains letting the app in once: on macOS 15 open it, then **System Settings → Privacy & Security → Open Anyway** (right-click → Open no longer works there), or `xattr -dr com.apple.quarantine /Applications/Sidetone.app`. Pay for the program only if the warning becomes a real obstacle; a Developer ID signature can be added to the same `.app` later.
   - **Windows signing:** unsigned programs still run, after SmartScreen's "More info → Run anyway", but on Windows 11 PCs with Smart App Control on they are blocked outright. Options, cheapest first: **SignPath Foundation** (free code signing for open-source projects built in public CI; apply first, they review each project), **Microsoft Trusted Signing** (about $10 a month; check country and identity eligibility), a traditional OV certificate (a few hundred dollars a year, on a hardware token or cloud service, awkward from CI; EV no longer skips SmartScreen's reputation building), or the **Microsoft Store** (MSIX; the Store signs what it distributes). Fallback: unsigned builds with the SmartScreen steps in the README, noting that Smart App Control needs a signed build.

4. **Smaller gaps compared with the Windows console:**
   - more than one FNE system at a time
   - playing a WAV file onto a talkgroup
   - web streams

5. **TMS (text messaging).** Waiting on others: it depends on data support on the FNE side. The question for the FNE's maintainers is what a console peer will be sent and allowed to send, so the GUI can get a messages panel that fits.

## Partially implemented

- ~~**Rename the project.**~~ Done in 0.17: the project is now **Sidetone**, "an IP dispatch console for DVMProject FNE networks". The programs are `sidetone` and `sidetone-cli`, settings carry over, and the old `dvmcli` and `dvmgui` commands keep working for now.
  - Still to do: remove the old command wrappers after a release or two.

- ~~**Selectable encryption and local key files.**~~ Done in 0.19: channels marked `selectable_encryption` can be switched between secure and clear for transmit, and keys can come from a password-protected KFDtool `.ekc` container.
  - ~~The terminal console can only unlock a key file before it starts.~~ Done in 0.25: `K` opens its key file box at any time.
  - Still to do: `sidetone-cli listen` doesn't use a key file yet.
  - Still to do: try a key file made by KFDtool itself (so far only one written by the self-test).

- ~~**Patching.**~~ Done in 0.20, passing voice frames between talkgroups without decoding and re-encoding them.
  - ~~Patched calls come out choppy on a radio site.~~ Fixed in 0.24.1: patched voice is buffered and sent at a steady pace.
  - ~~One-way patches (the Windows console has them).~~ Done in 0.28: each patched channel goes both ways, source only or target only.
  - Still to do: remembering patches between runs, if that turns out to be wanted.
  - Still to do: patching across modes, once there are other modes.

- ~~**Desktop notifications.**~~ Done in 0.21, for pages (call alerts) to the console.
  - Tabled: emergency alarms. Upstream added emergency support in late 2024 and removed it on purpose in March 2025 ("what do you think we are mission critical?"); to be decided later whether Sidetone shows the emergency bit at all.

- ~~**Packaging.**~~ Done in 0.27: an AppImage and an Arch package (`sidetone-bin` PKGBUILD) with each release, next to the tarballs; the PKGBUILD is built, installed and run on Arch in CI.
  - Still to do: publish it on the AUR, once AUR sign-ups open again (or someone with an account publishes it with us as co-maintainers).

- ~~**The terminal console caught up with the graphical one.**~~ Done in 0.25.
  - ~~Use the graphical console's saved audio devices, PTT device, transmit time limit and key file when no option is given.~~ Done in 0.25.
  - ~~Clickable buttons for Replay, Rec, patch, secure/clear and key request.~~ Done in 0.25, on each card's bottom edge.
  - ~~The same transmit time limit range in both.~~ Done in 0.25: 5 to 900 seconds.
  - ~~Unlocking a key file after start-up.~~ Done in 0.25: `K`.
  - Still to do: sending audio to another machine's sound system, for when the TUI is run over SSH (today audio stays on the machine Sidetone runs on).

## Fully implemented

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
