<div align="center">

# ⚡ AdvanceClip

### The Ultimate Cross-Device Clipboard Ecosystem

*Copy on one device. Paste anywhere. Instantly.*

[![Windows](https://img.shields.io/badge/Windows-0078D6?style=for-the-badge&logo=windows&logoColor=white)](#-pc-application)
[![Android](https://img.shields.io/badge/Android-3DDC84?style=for-the-badge&logo=android&logoColor=white)](#-android-companion)


---

</div>

## 🎯 What is AdvanceClip?

AdvanceClip unifies your clipboard across **every device you own** — seamlessly, privately, and at network speed. Copy a paragraph on your office PC, paste it on your phone in the elevator, then paste a file on your home PC. No accounts, no cloud storage limits, no friction.

It's not just a clipboard manager — it's a **complete synchronization ecosystem** that connects your Windows PCs and Android devices into one unified workspace.

---

## ✨ Core Features

### 📋 Universal Clipboard Sync
- Copy **text, links, code, images, or files** on any connected device
- Automatically appears on all other devices within seconds
- Works over **LAN** (instant, zero-latency) and **Cloudflare Tunnel** (anywhere in the world)

### 📱 Android Floating Ball
- Persistent **floating clipboard overlay** on your phone
- Tap any synced item to paste instantly — no need to open the app
- **Survives screen lock** — sync continues in the background via native foreground service

### 🌐 Global File Transfer
- Copy a file on your PC → your phone instantly gets a **download link**
- Files transfer via **Cloudflare Tunnel** — works from office to home, across networks
- Large files stream with progress bars — no size limits

### 🖥️ Desktop Power Hub
- **Keyboard shortcut** `Win + Shift + V` opens the clipboard dashboard anywhere
- Full clipboard history with **search, filter, and drag-out**
- Smart content detection — PDFs, images, code, links, all auto-categorized
- **PDF merge**, **table extraction**, **AI-powered OCR** built in

### ⏱️ Beautiful Timer
- Type `/5` to start a 5-minute timer, `timer 30 min`, or `2:30`
- Circular progress ring with **gradient glow effects**
- Color transitions (blue → amber → red) as time runs out
- **Always-on-top** — perfect for presentations, cooking, workouts

### 🔄 One-Click Updates
- Built-in **auto-update system** powered by GitHub Releases
- Check for updates → Download → Restart — all from Settings
- Works on both PC and Android

---

## 🏗️ Architecture

```
┌──────────────────┐                        ┌──────────────────┐
│   Windows PC     │   ◄── Firebase ──►     │   Android Phone  │
│  AdvanceClip.exe │       (signaling)      │  AdvanceClip.apk │
│                  │                        │                  │
│  • Clipboard     │   ◄── Cloudflare ──►   │  • Floating Ball │
│  • File Server   │      (file transfer)   │  • Background    │
│  • Hub Dashboard │                        │    Sync Service  │
│  • Timer         │   ◄── LAN Direct ──►   │  • Settings      │
│  • PDF Tools     │      (same WiFi)       │                  │
└──────────────────┘                        └──────────────────┘
```

**Three sync paths, automatic fallback:**
1. **LAN** — Same WiFi? Files transfer at **full network speed** (~100 Mbps)
2. **Cloudflare Tunnel** — Different networks? Secure tunnel through Cloudflare's edge
3. **Firebase RTDB** — Lightweight clipboard text sync across all devices globally

---

## 📦 Project Structure

```
AdvanceClip/
├── AdvanceClip_PC/              # Windows desktop app (C# / WPF / .NET 10)
│   ├── Classes/                 # Core logic — sync, networking, update engine
│   ├── Windows/                 # UI — HubWindow, Timer, Toast, QuickLook
│   ├── ViewModels/              # MVVM — clipboard items, drag-drop shelf
│   ├── Resources/               # Icons, web client, embedded assets
│   ├── Scripts/                 # Automation scripts
│   └── AdvanceClip.csproj       # Build configuration
│
├── AdvanceClip_Android/         # Android companion (React Native + Kotlin)
│   ├── app/(tabs)/              # RN screens — clipboard feed, settings
│   ├── android/app/src/main/    # Native Kotlin — OverlayService, sync
│   ├── context/                 # Settings persistence
│   └── package.json             # Dependencies
│
├── version.json                 # Update manifest (checked by the app)
└── .gitignore
```

---

## 🚀 Getting Started

### Prerequisites
| Platform | Requirements |
|----------|-------------|
| **PC** | Windows 10/11, .NET 10.0 Desktop Runtime |
| **Android** | Android 8.0+ (API 26), "Install from unknown sources" enabled |

### Quick Start

**PC — Run from source:**
```powershell
cd AdvanceClip_PC
dotnet run
```

**PC — Build release EXE:**
```powershell
cd AdvanceClip_PC
dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true -o FINAL\new_exe
```

**Android — Build APK:**
```powershell
cd AdvanceClip_Android/android
./gradlew assembleRelease
# Output: android/app/build/outputs/apk/release/app-release.apk
```

### First Launch
1. Open `AdvanceClip.exe` → it starts a local server + Cloudflare tunnel automatically
2. Install the APK on your phone → go to **Settings** → enter your PC's local IP
3. Enable **Floating Ball** → enable **Global Sync**
4. Copy something on your PC — it appears on your phone instantly ⚡

---

## 🔑 Configuration

| Setting | Where | Purpose |
|---------|-------|---------|
| **Device Name** | PC Settings tab / Android Settings | Identifies you in the sync feed |
| **Gemini API Key** | PC Settings tab | Powers AI text extraction from images |
| **Floating Ball** | Android Settings | Toggle the overlay clipboard ball |
| **Global Sync** | Both apps' Settings | Enable/disable cloud sync (saves Firebase quota) |

---

## 📥 Releases & Updates

Pre-built binaries are available on the [Releases](../../releases) page:
- **`AdvanceClip.exe`** — Windows desktop app (single-file, self-contained)
- **`AdvanceClip_Mobile.apk`** — Android companion app

Both apps have a built-in **"Check for Updates"** button in Settings that fetches the latest version from this repository.

---

## 🛡️ Privacy

- **No accounts required** — no sign-up, no email, no tracking
- **Firebase** is used only for lightweight clipboard text relay (no files stored)
- **Files transfer peer-to-peer** via Cloudflare Tunnel or LAN — never stored on any server
- **All data stays on your devices** — nothing is retained in the cloud after delivery

---

<div align="center">

Built with ❤️ using C# WPF, React Native, Kotlin, and Firebase

**Copy once. Paste everywhere.**

</div>
