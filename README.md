# Emulate

A simple Android emulator for Windows. Install it, pick an Android OS, pick (or create) a profile, and press **Launch**.
Every device has the **Google Play Store**, your **real location**, and can **pick files straight from your PC**.

## Install

1. Open the [latest release](https://github.com/trendlinepros-afk/Emulate/releases/latest).
2. Download **Emulate-Setup.exe** and run it. No admin rights are needed.
   (Or download `Emulate-win-x64.zip`, unzip it anywhere and run `Emulate.exe`.)
3. The first time you launch a device, Emulate downloads Google's Android Emulator and the system image for that OS
   (about 1.5–2 GB per OS, only once).

**Requirements:** Windows 10/11 64-bit, 8 GB+ RAM (16 GB recommended), and virtualization turned on.
If devices won't start, open **Settings → Check virtualization**, then **Fix virtualization** and restart Windows.
If it still fails, turn on Intel VT-x / AMD SVM in your PC's BIOS.

## Updating

Open **Settings → Check for updates**. If there's a newer version, click **Update & restart**. Emulate downloads it,
closes, swaps the files and opens again. Your profiles are kept, and running devices keep running.
Emulate also checks on startup and shows a banner when an update is available.

Every push to `main` builds a new release automatically (`.github/workflows/release.yml`, version `1.0.<build number>`),
so a code change reaches every install through that button.

## The 5 operating systems

All of them are official Google Play images, so the Play Store and Google sign-in work.

| OS | Type | What it is |
|---|---|---|
| **Android 16 Flagship** | Mainstream | Newest Android on a large 1440p flagship-class screen (Galaxy S Ultra–like size) |
| **Android 15 Everyday** | Mainstream | What most current phones run; the best all-round choice |
| **Android 14 Stable** | Mainstream | Mature release for apps that are picky about new Android versions |
| **Android 15 Hardened** | Secure | Encrypted Private DNS (Quad9), no background Wi-Fi/Bluetooth scanning, Play Protect forced on (including sideloaded apps), lock-screen notification content hidden, Google extras removed |
| **Android 13 Lite** | Stripped-down | Debloated to the essentials, faster animations, 2 GB RAM, so you can run several at once |

The look is stock Android (Pixel-style). Samsung's One UI can't be emulated, but the Flagship screen matches a
current Galaxy S Ultra.

## Launching

Press **Launch** on a profile and choose:
- **Launch in window**: the device's screen opens in a tab inside Emulate, with a toolbar for Back, Home,
  Recent apps, rotate, volume, power, send files, get photos, screenshot, install APK and location.
- **Launch in new window**: the device opens in its own window, like the standard Android Emulator.

You can switch a running device between the two with the pop-out button in its toolbar. Emulate remembers your
choice per profile, and double-clicking a profile launches it the way you used last time.

## Web access (use a device from another phone, tablet or PC)

Open a running device's toolbar and click the globe (or use **⋯ → Web access…** on a profile) and turn it on. You get
a link and a QR code. Open it in a browser on any device on the same network to see the live screen and control it:
tap, swipe, scroll, type, and use Back, Home, Recent apps, volume, rotate and power.

- Every device has its own port and a 6-digit access code. Anyone with the link and code can control it, so only
  share it with people you trust. **New code** shuts out old links.
- It turns on automatically whenever that device runs, until you switch it off.
- Streaming and touch use the server part of [scrcpy](https://github.com/Genymobile/scrcpy) (Apache-2.0, bundled
  in `third_party/scrcpy`). Video has low latency and your fingers are tracked live, including two-finger pinch.
  If scrcpy can't start, Emulate falls back to Android's screen recorder and `adb input`, which is slower.
- Live video needs a browser that supports WebCodecs H.264 (Chrome, Edge, Safari 16.4+, recent Firefox). Other
  browsers get "basic mode", which updates the screen a few times a second.
- **Fit to screen** (the corners button, on by default on phones and tablets) reshapes the Android display to
  your browser's shape so it fills the page. The device returns to its normal shape when the last browser
  disconnects.
- Press and hold works like a real finger: long-press menus open while you hold, and you can drag (e.g. move app icons).
- If another device can't connect, click **Allow through Windows Firewall** in the Web access window.
- To reach it from outside your home network, use a VPN such as Tailscale and open the PC's VPN address.

## Profiles

A profile is one device: its own apps, accounts, photos and settings. You can have as many as you want per OS,
for example a "Work WhatsApp" and a "Personal" phone. For each one you choose:
- device type (flagship, standard, compact phone or tablet), RAM and storage
- camera (your PC webcam, a simulated camera, or none)
- location (live from Windows, approximate from your IP, a fixed spot, or off)

Use the **⋯** menu on a profile to:
- **Copy profile (apps & data)**: an exact, independent clone with the same apps, accounts, photos and settings.
  Stop the device first. The copy's first start is a normal (full) boot.
- **New profile with same settings**: same hardware settings, fresh device.
- **Factory reset** or **Delete**.

## Files between your PC and the device

When a device starts, Emulate installs a small companion app on it called **PC Bridge**.

- **Attach from your PC in any app.** In WhatsApp tap 📎 → *Document* (or *Gallery* → *Browse*), or use any app's
  "choose file" button, then pick **"Pick from PC"**. **File Explorer opens on your computer.** Choose a photo and it's
  sent straight into the chat. You can also open **"Windows PC"** in Android's file browser to browse your PC's
  folders and drives.
- **Shared folder.** Anything you put in `Pictures\Emulate Shared` on the PC appears in every device's gallery
  (`Pictures/Emulate Shared`), so it also shows up in WhatsApp's built-in gallery.
- **Send files** button (in *Running devices*): picks files on the PC and copies them to the device. Photos and
  videos go to the gallery, everything else to Downloads.
- **Save to PC**: share anything on the device to *Save to PC* and it lands in `Downloads\Emulate`.
- **Get photos** button: copies the device's camera roll, screenshots and downloads to `Downloads\Emulate\<profile>`.
- Dragging files from Explorer onto the device window also works.

## Location

With *My real location*, Emulate reads your PC's position from Windows location services
(Settings → Privacy & security → Location → allow desktop apps) and keeps the device's GPS updated.
If that's off, it falls back to your IP address's approximate location.

## Phone features

Devices work like a normal phone or tablet: Play Store, Chrome, camera (your webcam), microphone, sensors and
clipboard sharing with the PC. The device has a simulated SIM, so it can't make real calls or receive real SMS.
To register WhatsApp, use your real phone number and type in the code you get on your real phone.

## How it works (for developers)

- `src/Emulate`: the WPF app (.NET 10). It downloads the Android SDK pieces straight from Google's SDK repository
  (no Android Studio or Java needed), writes AVD definitions per profile, launches `emulator.exe`, and talks to devices
  over `adb`.
  - `Services/UpdateService.cs`: GitHub Releases self-updater
  - `Services/SdkInstaller.cs`: SDK and system image downloads
  - `Services/DeviceManager.cs`: launching, post-boot setup, files, location
  - `Services/BridgeServer.cs`: localhost HTTP server used by PC Bridge (token-protected)
  - `Services/Presets.cs`: Hardened and Lite setup
- `android/pcbridge`: the PC Bridge companion app (plain Java, built without Gradle by `build.sh`).
- `installer/Emulate.iss`: the Inno Setup installer.
- Data lives in `%LOCALAPPDATA%\Emulate` (settings, profiles, SDK, devices, logs). The app installs to
  `%LOCALAPPDATA%\Programs\Emulate`.

Local build: `dotnet build src/Emulate` (needs the .NET 10 SDK).
