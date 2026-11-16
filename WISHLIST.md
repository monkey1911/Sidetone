# Wishlist

Things we'd like to do, roughly in the order that makes sense to do them. Nothing here is promised or scheduled.

## First: the things other items depend on

1. ~~**Rename the project.**~~ Done in 0.17: the project is now **Sidetone**, "an IP dispatch console for DVMProject FNE networks". The programs are `sidetone` and `sidetone-cli`, settings carry over, and the old `dvmcli` and `dvmgui` commands keep working for now.
   - Still to do: remove the old command wrappers after a release or two.

2. ~~**Settings screen in the GUI.**~~ Done in 0.18: audio output and microphone, the PTT device and its buttons, the transmit time limit and what Space does are in a Settings box and are remembered.
   - Still to do: the terminal console (`sidetone-cli`) doesn't read these saved choices yet; it still takes them as options.

3. ~~**Start the GUI from the desktop's launcher.**~~ Done in 0.18: Settings can add Sidetone to the app launcher, with an icon (`sidetone --install-launcher` does the same from a terminal).
   - Still to do: system-wide packaging (see the AUR item below).

## Console features

4. ~~**Hold-to-talk on the on-screen PTT button, and a PTT on each card.**~~ Done in 0.19.

5. ~~**Selectable encryption and local key files.**~~ Done in 0.19: channels marked `selectable_encryption` can be switched between secure and clear for transmit, and keys can come from a password-protected KFDtool `.ekc` container.
   - Still to do: `sidetone-cli listen` doesn't use a key file yet, and a key file has not been tried against one made by KFDtool itself, only one written by the self-test.

6. ~~**Patching.**~~ Done in 0.20, passing voice frames between talkgroups without decoding and re-encoding them.
   - Still to do: one-way patches (the Windows console has them), remembering patches between runs if that turns out to be wanted, and patching across modes once there are other modes.

7. **NXDN and DMR.** libvocoder already has the codec both use, and the cards already show modes that aren't supported yet, so the work is a receiver and a transmitter per mode. Needs live traffic to test against.
   - **Analog** too, once we've confirmed how DVM carries it. It may be the simplest of the three.

8. ~~**Instant replay.**~~ Done in 0.21: the last ten calls per channel, with talkgroup recording to WAV files.
   - Still to do: deleting old recordings automatically after a set number of days, and a compressed format (Opus) if WAV turns out too big.

9. ~~**Desktop notifications.**~~ Done in 0.21, for pages (call alerts) to the console.
   - Still to do: emergency alarms, once the console decodes them.

## Audio

- ~~**Microphone processing.**~~ Done in 0.22: low cut, noise gate, automatic level and limiter in C#, plus optional receive leveling.
- **An audio device layer.** One internal interface for playing and recording, with PipeWire as the first implementation, so Windows (WASAPI) and macOS (Core Audio) can be added without touching the rest. Next.

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
