# Wishlist

Things we'd like to do, roughly in the order that makes sense to do them. Nothing here is promised or scheduled.

## First: the things other items depend on

1. ~~**Rename the project.**~~ Done in 0.17: the project is now **Sidetone**, "an IP dispatch console for DVMProject FNE networks". The programs are `sidetone` and `sidetone-cli`, settings carry over, and the old `dvmcli` and `dvmgui` commands keep working for now.
   - Still to do: remove the old command wrappers after a release or two.

2. **Settings screen in the GUI.** Audio output and input, the PTT device and its buttons, the transmit time limit and what Space does are command-line options today. A launcher has no command line, so they need a screen and need to be remembered.

3. **Start the GUI from the desktop's launcher.** A `.desktop` file and an icon, so it doesn't have to be started from a terminal. Needs items 1 and 2.

## Console features

4. **Hold-to-talk on the on-screen PTT button, and a PTT on each card,** so a channel can be keyed without moving the focus to it.

5. **Selectable encryption and local key files.** Switch a channel between secure and clear for transmit (the codeplug's `selectable_encryption`), and load keys from a key file or key container so encrypted talkgroups work when the FNE doesn't deliver keys.

6. **Patching.** Start by reading how the Windows console does it. Worth looking into: passing voice frames between talkgroups without decoding and re-encoding them, which would avoid a second pass through the vocoder.

7. **NXDN and DMR.** libvocoder already has the codec both use, and the cards already show modes that aren't supported yet, so the work is a receiver and a transmitter per mode. Needs live traffic to test against.
   - **Analog** too, once we've confirmed how DVM carries it. It may be the simplest of the three.

8. **Instant replay** of the last call on a channel.

9. **Desktop notifications** when the console is paged.

## Project plumbing

10. **An automated network test.** Run a real dvmfne in CI with two console instances and check login, voice in both directions and encryption. Today every on-air check is done by hand.

11. **An AUR package,** once the name is settled.

## Other platforms

12. **Windows 10/11 and macOS.** Avalonia and most of the core are already cross-platform. The Linux-only parts are audio (PipeWire) and PTT devices (evdev), so each platform needs its own version of those two.
    - macOS is new ground. Windows already has the upstream console, so the gain there is smaller.
    - Test hardware: an Intel MacBook on macOS 15 is available. An Apple silicon tester is still needed.

## Waiting on others

13. **TMS (text messaging).** Depends on data support on the FNE side. The question for the FNE's maintainers is what a console peer will be sent and allowed to send, so the GUI can get a messages panel that fits.

## Smaller gaps compared with the Windows console

From the feature comparison, not yet placed in the order above:

- more than one FNE system at a time
- microphone processing (automatic gain control)
- sending different channels to different audio outputs
- playing a WAV file onto a talkgroup
- talkgroup recording in the consoles (`sidetone-cli listen` can record one talkgroup to WAV)
- web streams
- a call history that survives closing the console
