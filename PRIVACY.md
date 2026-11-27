# Sidetone privacy policy

Effective 26 November 2026. This policy covers Sidetone on every platform: Android, Linux, macOS and Windows.

## The short version

Sidetone has no accounts, no ads, no analytics and no crash reporting. Its developers receive nothing from it. What Sidetone sends goes only to the FNE servers and web streams you name in your codeplug, and everything else stays on your device.

## What Sidetone sends, and where

Sidetone is a dispatch console for DVMProject FNE radio networks. When you load a codeplug, Sidetone connects to the FNE servers it names. Those servers belong to whoever runs that radio network, not to Sidetone's developers. Sidetone sends them:

- **The login details from your codeplug:** the peer ID, its identity text, and a hash of the password (the password itself is never sent).
- **The app's version and platform**, for example `SIDETONE_R00A40_AND` for Android.
- **Your voice**, while you hold push-to-talk, along with the radio ID your codeplug gives the console.
- **Tones, pages, announcements and radio commands** that you send.
- **Requests** for encryption keys and for the network's list of radio names.

What the network operator does with this is up to them. Ask them about their own policy.

If your codeplug lists web streams, Sidetone connects to those addresses when you turn a stream on, using any user name and password the codeplug gives for it.

Sidetone doesn't use your location, contacts, phone number, advertising ID or any other account.

## What Sidetone keeps on your device

- **The codeplug** you open, and your **settings**.
- **Passwords for key files**, if you save one: on Android in the Android Keystore, on Linux in the system keyring, on macOS in the Keychain, and on Windows in Credential Manager.
- **Encryption keys** from the FNE, in memory only, while the console is running.
- **A list of radio names** from the FNE, cached so it is there at the next start.
- **Recent calls and instant recall audio**, kept between runs. Encrypted calls are kept only if you turn that on.
- **Recordings**, if you turn recording on.
- **Log files** (kept 14 days) and **debug logs** (kept 7 days). Debug logs never contain codeplug contents, passwords or keys.

On Android, all of this is in the app's own storage, and Android's cloud backup is turned off for Sidetone. Uninstalling the app deletes it all. On a computer, you can delete these files yourself; the [README](README.md) says where each one is.

## When something leaves your device because you asked

- **Sharing a recording**, or **saving debug logs**, hands the file to the app or place you choose.
- **Text to speech** on Android uses the phone's own speech engine. Some engines work over the internet; that depends on your phone, not on Sidetone. On Linux, macOS and Windows, speech is made on the device.

## Permissions on Android

- **Microphone:** to transmit your voice, only while push-to-talk is held.
- **Notifications:** to show that the console is connected, and to tell you when you're paged.
- **Foreground service** (microphone and media playback): so receive and push-to-talk keep working with the screen off.
- **Nearby devices (Bluetooth):** to use a Bluetooth headset's microphone and speaker.
- **Network access, Wi-Fi state and wake lock:** to stay connected to the FNE.
- **Zebra key events:** to receive the PTT key on Zebra devices.

## Children

Sidetone is a tool for radio operators. It isn't made for children and doesn't knowingly collect anything from anyone.

## Changes

If this policy changes, the new version will be in this file, with a new date at the top. The file's history on GitHub shows every earlier version.

## Contact

Open an issue at <https://github.com/monkey1911/Sidetone/issues>.
