# SlateCapture

**See and record your Android tablet's screen on your computer, over Wi-Fi.**

Open SlateCapture on your computer and on your tablet. They find each other on your home network, and the tablet's screen appears on your computer in real time. Press one button to record it as an MP4 video. No cables, no IP addresses, no technical setup.

<!-- Screenshot: desktop app showing the live tablet screen -->
<!-- ![SlateCapture on the desktop](docs/screenshots/desktop.png) -->

## Features

- **Connects by itself** – the tablet finds your computer automatically on the same Wi-Fi network.
- **Light on old tablets** – uses the tablet's built-in hardware video encoder. Works on Android 5.0 and newer.
- **Smooth 60 FPS** – the tablet picks the best resolution that still runs at 60 frames per second.
- **Full-quality recording** – the video is saved directly as MP4, without being re-compressed.
- **Tablet audio** – on Android 10 and newer, the tablet's sound can be recorded too. The option appears only when your tablet supports it.
- **English and Turkish** – choose the language in the settings.

SlateCapture only shows and records the screen. It does not control the tablet.

## Screenshots

<!-- Add your images to docs/screenshots/ and remove the comment marks below -->
<!-- ![Desktop app](docs/screenshots/desktop.png) -->
<!-- ![Tablet app](docs/screenshots/tablet.png) -->
<!-- ![Settings](docs/screenshots/settings.png) -->

*Screenshots coming soon.*

## Installation

Download the files from the [latest release](https://github.com/harun-mutlu/SlateCapture/releases/latest).

### On your computer (Windows)

Choose one:

- **Installer** – run `SlateCapture-x.y.z-setup.exe` and follow the steps. The installer also allows SlateCapture through the Windows Firewall.
- **Portable** – extract `SlateCapture-x.y.z-win-x64-portable.zip` to any folder and run `SlateCapture.exe`. Nothing is installed. The first time, Windows asks whether to allow network access: choose **Allow**.

> macOS and Linux builds can be made from the source code, but **they have not been tested yet**. Feedback is welcome.

### On your tablet (Android)

1. Copy `SlateCapture-x.y.z.apk` to the tablet (it is also included next to the desktop app), or download it on the tablet from the release page.
2. Open the file. If Android asks, allow installing apps from this source.
3. Install it.

## How to use

1. Connect the tablet and the computer to the **same Wi-Fi network**.
2. Open SlateCapture on the computer.
3. Open SlateCapture on the tablet. It shows *Looking for your computer*, then *Connected*.
4. Tap **Start streaming** on the tablet and allow screen capture when Android asks.
5. The tablet's screen now appears on your computer.
6. Press the red **record** button on the computer to start recording, and press it again to stop.

Recordings are saved as MP4 files in your **Videos** folder. You can change the folder in the settings.

## Troubleshooting

- **The tablet can't find the computer** – make sure both are on the same Wi-Fi network (not a guest network) and that SlateCapture is allowed through the firewall. SlateCapture uses UDP port 47900 and TCP port 47901.
- **No audio option** – recording tablet audio needs Android 10 or newer.
- **Choppy video** – a weak Wi-Fi signal is the usual cause. Move closer to the router or use the 5 GHz band.

## Feedback

Found a bug or have an idea? Click **Feedback** at the top of the desktop app, write your message and press **Send**. It goes straight to the developer, no account needed. You can choose whether to include technical details (app version, operating system, tablet model and recent connection events). No files or recordings are ever sent.

You can also open an [issue](https://github.com/harun-mutlu/SlateCapture/issues) on GitHub.

## Building from source (Windows)

Everything the build needs is downloaded into `tools/`. Nothing is installed system-wide.

1. `setup.cmd` – downloads JDK 17, Android SDK, .NET SDK, FFmpeg (LGPL shared build) and Inno Setup, and creates an Android signing key in `signing/`.
2. `build.cmd` – builds the APK, the Windows installer and the portable zip into `dist/`. Use `build.cmd -All` for the Linux and macOS builds as well.
3. `install-apk.cmd` – installs the APK on a tablet connected over USB (optional).

Keep the `signing/` folder private and backed up. Android only accepts updates signed with the same key.

On Linux and macOS the desktop app uses the system FFmpeg 7 libraries (`apt install ffmpeg` / `brew install ffmpeg`).

### How it works

The tablet broadcasts a discovery message on UDP port `47900`. The desktop app answers with its stream port (`47901`, TCP). The tablet connects, reports its encoder capabilities and streams H.264 (and AAC) packets. The desktop app decodes them with FFmpeg for the live preview and writes the original packets into an MP4 file when recording.

## License

[MIT](LICENSE) © 2026 Harun Mutlu

The Windows builds include FFmpeg libraries under the LGPL 2.1.
