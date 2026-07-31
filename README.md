<div align="center">
  <p><b>DesktopPet — 纯桌宠版</b></p>
  <p><em>DesktopPet is a renamed, modified fork of <a href="https://github.com/ntd4996/agentpet">AgentPet</a>, used under the MIT License. This edition removes all AI-agent monitoring and keeps only the desktop pet.</em></p>
  <p>
    <img src="https://img.shields.io/badge/platform-macOS%2013%2B-black" alt="macOS 13+" />
    <img src="https://img.shields.io/badge/license-MIT-blue" alt="MIT" />
    <img src="https://img.shields.io/badge/Swift-SwiftUI-orange" alt="Swift" />
  </p>
</div>

---

A lightweight desktop pet for everyone: a little pixel companion that lives on your desktop, grows as you interact with it, and can be customized with your own sprites.

No AI agents required. No hooks. No accounts. Runs fully offline.

## Features

- 🐾 **Desktop pet**: floats on your desktop, draggable, click-through optional, reacts with moods and speech bubbles.
- 🎨 **Custom pets**: import a spritesheet, auto-slice it into frames, and instantly get a new pet. Browse and adopt pets from the open Petdex library.
- 🎮 **Raise your companion**: feed it, earn XP, level up through five stages (Hatchling → Companion → Scout → Hero → Legend), unlock achievements.
- 💬 **Chat bubbles**: idle chatter, mood lines, fully customizable messages.
- ⏰ **Break reminder**: nudges you to step away after long stretches (off by default).
- 🍎 **Native**: Swift/SwiftUI, menu bar app, no Dock icon, Sparkle auto-update.
- 🌍 **Localized**: English, 简体中文, 繁體中文, Tiếng Việt.

## Build from source (macOS)

Requires Xcode 16 / Swift 6.

```bash
git clone https://github.com/Imzl-zl/DesktopPet.git
cd DesktopPet
./scripts/build-app.sh release
open build/DesktopPet.app
```

Or run/test with SwiftPM directly:

```bash
swift build
swift test
```

## Verify the core layer on Windows

No Mac? The pure-logic layer (`DesktopPetCore`: pet care, activity model, mood
resolution, break clock) is platform-independent and can be compiled and tested
on Windows. Install the [Swift for Windows toolchain](https://www.swift.org/install/windows/)
(requires VS 2022 Build Tools + Windows SDK), then in git-bash:

```bash
./scripts/verify-core-windows.sh
```

> `swift test` itself currently crashes on Windows (SwiftPM llbuild job bug,
> see swiftlang/swift-package-manager#6605) — the script builds and runs the
> 54 core tests with a plain `swiftc` flow instead.

## Roadmap

- v0.2: Desktop awareness (the pet reacts to which app you're using) + chat with local/cloud multimodal models.
- v0.3: Daily summary — the pet summarizes your day and generates a recap image.

## License

MIT, same as the original AgentPet project. See [LICENSE](LICENSE). The original copyright notice is preserved as required by MIT.
