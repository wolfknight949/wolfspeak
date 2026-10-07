# 🐺 WolfSpeak

Super-light, ultra-low-latency voice calls between two people on the same network.
No servers, no accounts, no compression — your voice goes straight from your PC to your friend's.

|                  | Discord / TeamSpeak         | WolfSpeak                         |
|------------------|-----------------------------|-----------------------------------|
| Route            | You → server → friend       | You → friend (direct LAN)         |
| Codec            | Opus (adds 20–60 ms)        | None — raw 48 kHz PCM             |
| Typical delay    | 80–200 ms                   | **~40–60 ms** mouth-to-ear        |

## Install

Download **`WolfSpeak-win-Setup.exe`** from the
[latest release](https://github.com/wolfknight949/wolfspeak/releases/latest) and run it — no admin needed,
and it installs the .NET 10 Desktop Runtime too if you don't have it. That's it: WolfSpeak **updates itself**
from then on (in the background, never during a call).

Calls only connect when both of you run the **same version** — a friend on a different version shows an
amber "Different version" note in your pack. Auto-update keeps everyone in sync; it checks sooner when it
sees a friend who is already on a newer version.

## How to use

1. Both of you open **WolfSpeak** (same Wi-Fi / router).
2. Your friend appears under **Your pack** — press **Call**.
3. They press **Accept**. Talk. 🎧

Friend doesn't appear (different subnet, ZeroTier / Radmin / Tailscale VPN)? Type their IP in
**"Call their IP directly"** at the bottom of the home screen. Your own IP is shown in the top card.

**First launch:** Windows Firewall will ask about network access — allow **Private networks**.
WolfSpeak uses UDP port **50505**.

### In a call
- 🎙 **Mute** your mic · ☎ **Hang up** · 🎧 **Deafen** (stop hearing your friend)
- Live ping + packet loss with a quality dot (green / amber / red), and a volume slider for your friend.
- Global hotkeys that work in game: **Ctrl+Shift+M** mute · **Ctrl+Shift+D** deafen.

### Tray
- Closing or minimizing keeps WolfSpeak in the tray so your pack can still call you.
  Left-click the wolf to open, right-click for Mute / Hang up / Quit.
- The tray wolf shows a dot: 🟢 in call · 🔴 muted · 🟡 ringing.
- Incoming call while hidden → the window pops up, rings, and Windows shows a notification.
- Opening WolfSpeak again just brings the running one back.
- Optional: **Start with Windows** (starts silently in the tray).

### Settings (⚙)
- **Microphone / Headphones** — pick your headset.
- **Voice activated / Push-to-talk** — PTT works globally, even while a game has focus
  (Mouse 4/5, Caps Lock, Alt, V, …).
- **Mic sensitivity** — drag the white line so your voice passes it but background noise doesn't.
- **Hear myself** — mic test loopback.
- **Delay buffer** — 20 ms is great on cable; raise to 40–60 ms if the voice crackles on Wi-Fi.

## Build

Requires the .NET 10 SDK.

```
WolfSpeak.slnx
src/WolfSpeak/          the app (C# / WPF)
  assets/               icon + artwork
scripts/
  build.ps1             build (-Run to launch)
  release.ps1           installer + auto-update packages (Velopack) → publish/releases
  publish.ps1           loose single-file exe → publish/<runtime>
  make-icon.ps1         regenerates assets/wolfspeak.ico from wolfspeak-icon.png
.github/workflows/      CI release on tag push
```

```bash
.\scripts\build.ps1 -Run
```

### Releasing a new version

1. Bump `<Version>` in `src/WolfSpeak/WolfSpeak.csproj` (e.g. `1.3.0`).
2. Commit, then tag and push:

```bash
git tag v1.3.0
```

```bash
git push origin main --tags
```

The **Release** GitHub Action builds the installer and publishes the GitHub Release. Every installed
WolfSpeak picks it up within the hour (within 5 minutes when it sees a friend already on 1.3.0).

To build a release locally instead: `.\scripts\release.ps1` (add `-Upload` with `$env:GITHUB_TOKEN` set to publish it).
Updates come from GitHub Releases, so the repository must be **public** (or teammates' builds can't fetch them).

### Loose exe (no installer, no auto-update)

```bash
.\scripts\publish.ps1 -All -Zip
```

Produces `publish\win-x64\WolfSpeak.exe` (needs the .NET 10 Desktop Runtime) and
`publish\win-x64-standalone\WolfSpeak.exe` (runs anywhere). Or double-click `publish.cmd`.

## How it works

- **Discovery:** UDP broadcast "hello" every second on port 50505 (plus unicast to saved IPs).
- **Versions:** every hello carries the app version; calls between different versions are refused.
- **Encryption:** each call starts with a handshake of fresh P-256 keys, signed by each PC's identity key
  (kept DPAPI-protected in `%APPDATA%\WolfSpeak\identity.key`). Every packet in the call — audio, ping,
  hang-up — is AES-256-GCM encrypted and authenticated (~1 µs per packet, no added delay), with replay
  protection. Spoofed packets can't listen in, inject audio, redirect or end your call.
- **Safety code:** the 🔒 6-digit code in the call screen is the same on both PCs unless someone is relaying
  the call in between — read it to each other once. WolfSpeak also remembers each friend's key and warns if
  it changes (a reinstall, or an impostor).
- **Discovery is open:** anyone on the network can see your WolfSpeak name and ring you; nobody can connect
  without you pressing Accept.
- **Calls:** tiny signaling packets (request / accept / decline / end), repeated until answered since UDP can drop packets.
- **Audio:** WASAPI capture in 10 ms frames → 16-bit PCM → one UDP packet per frame (~770 kbit/s).
  The receiver keeps a small jitter buffer (default 20 ms) and drops old audio if it ever lags, so delay can't creep up.
- **Ping:** measured every 500 ms during a call.

### Sound quality
- **Mic chain:** 80 Hz high-pass (removes DC offset, rumble, desk thumps, mains hum) → soft limiter
  (boost never hard-clips).
- **Voice gate done right:** 20 ms pre-roll so first syllables aren't cut, fade-in/fade-out at the
  edges of speech (no clicks), 300 ms hangover so word endings aren't chopped.
- **Packet loss concealment:** a lost packet is covered by repeating the last 10 ms with a fast fade,
  then playback fades back in. The sender marks "end of speech" so real silence isn't treated as loss.
- **Clock-drift compensation:** two sound cards never run at exactly the same speed. Instead of letting
  delay grow (or skipping audio), playback runs 1% faster while the buffer is over target — inaudible.
- **Real-time priority:** capture and playback threads are registered with Windows MMCSS "Pro Audio",
  so a busy game doesn't cause dropouts. Packets are tagged DSCP EF (voice) for QoS-aware routers.
- **Hot-plug:** unplugging/replugging a headset or changing the Windows default device is detected and
  audio reconnects automatically (falls back to the default device if yours is gone).
- Use headphones: there is no echo canceller (it would add delay), so speakers would echo back to your friend.

Source files (in `src/WolfSpeak/`):

| File | What |
|------|------|
| `VoiceEngine.cs` | network, discovery, call signaling (+ version check), audio capture/playback |
| `JitterBuffer.cs` | playout buffer: jitter, drift compensation, loss concealment |
| `AudioDsp.cs` | high-pass filter, soft limiter, fades, MMCSS thread priority |
| `DeviceWatcher.cs` | audio device plug/unplug notifications |
| `TrayIcon.cs` | tray icon, status dot, dark menu |
| `Autostart.cs` | "Start with Windows" (per-user Run key) |
| `SoundPlayer.cs` | synthesized ring / connect / hang-up sounds |
| `MainWindow.xaml(.cs)` | UI: home, ringing, in-call, settings |
| `App.xaml` | theme: colors, wolf logo, control styles |
| `Program.cs` | entry point; runs Velopack install/update hooks first |
| `Updater.cs` | background auto-update from GitHub Releases |
| `CallCrypto.cs` | identity key, signed call handshake, AES-GCM packet encryption |
