<p align="center">
  <img src="docs/images/logo.png" width="128" alt="DeskPods logo">
</p>

<h1 align="center">DeskPods</h1>

<p align="center">
  <b>Your AirPods battery on Windows — the way your iPhone shows it.</b><br>
  Open the case, a card pops up with left, right and case charge. That's it.
</p>

<p align="center">
  <a href="https://github.com/Stasieps/DeskPods/releases/latest"><img src="https://img.shields.io/github/v/release/Stasieps/DeskPods?label=download&color=2ea44f" alt="Latest release"></a>
  <img src="https://img.shields.io/badge/Windows-10%20%7C%2011-0078D6" alt="Windows 10 and 11">
  <img src="https://img.shields.io/badge/price-free-brightgreen" alt="Free">
  <a href="LICENSE"><img src="https://img.shields.io/github/license/Stasieps/DeskPods" alt="MIT license"></a>
</p>

<p align="center">
  <a href="https://github.com/Stasieps/DeskPods/releases/latest/download/DeskPods_Setup.exe"><b>⬇️ Download DeskPods for Windows</b></a>
  &nbsp;·&nbsp;
  <a href="README.uk.md">Українською</a>
  &nbsp;·&nbsp;
  <a href="README.ru.md">Русский</a>
</p>

---

## Why I made this

I sit at my PC with AirPods in my ears pretty much all day, and the one thing I
always wanted to know was simple: **how much charge is left?**

Windows doesn't really tell you. So I bought a couple of paid apps that promised
to. They didn't do the one job they had: the card didn't show up when I opened the
case, the numbers were stale, or the window just hung there while the earbuds were
already in my ears.

So I built my own — and made it do exactly one thing, properly.

## What you get

| | |
|---|---|
| 🎧 **Pop-up card when you open the case** | Left, right and case battery, right in the corner of the screen. Close the lid — it goes away. |
| 🔋 **Battery in the tray** | A small case icon next to the clock. It changes colour when charge gets low or the AirPods are gone. |
| 🔔 **One low-battery alert, not ten** | When an earbud gets low, you get one heads-up per discharge. You choose the threshold (10–50 %). |
| 👂 **Doesn't get in the way** | Earbuds in your ears don't keep the card hanging on screen. No big windows over your desktop. |
| ⌨️ **Shortcut** | Press `Ctrl` + `Alt` + `P` any time to see the card. You can change the shortcut. |
| 🎨 **5 looks** | Pick a design that fits your setup — from paper-white E-Ink to dark Glass. |
| 🌍 **English, Українська, Русский** | Switch the language in Settings. |
| 🚀 **Starts with Windows** | Quietly sits in the tray and is ready before you put the earbuds in. |

## Private by design

- **No account, no cloud, no ads, no tracking.** Nothing leaves your PC, except an update check against GitHub Releases (switchable in Settings).
- DeskPods only *listens* to the Bluetooth signal your AirPods already broadcast
  to nearby Apple devices. It doesn't connect to them or change anything.
- If you ever send a bug report, the exported log leaves out raw radio data and
  your personal settings.

## Install

1. **[Download `DeskPods_Setup.exe`](https://github.com/Stasieps/DeskPods/releases/latest/download/DeskPods_Setup.exe)**
2. Run it. No admin rights needed. A new version installs right over the old one, and your settings stay.
3. Open your AirPods case near the PC. The card shows up.

> **Windows says “Windows protected your PC”?**
> That's normal for small free apps that aren't code-signed (a signing certificate
> costs a few hundred dollars a year). Click **More info → Run anyway**.

Don't want to install anything? Grab the **portable ZIP** from the
[releases page](https://github.com/Stasieps/DeskPods/releases/latest), unzip it and run `PodsView.exe`.

### You need

- Windows 10 (version 2004 or newer) or Windows 11, 64-bit
- A PC with Bluetooth
- AirPods paired with this PC at least once (Windows Settings → Bluetooth & devices)

## Supported headphones

| Pop-up card + battery | Battery only |
|---|---|
| AirPods (1st, 2nd, 3rd gen) | AirPods 4 (ANC) |
| AirPods Pro (1st and 2nd gen) | AirPods Pro 2 (USB-C) |
| | AirPods Max, AirPods Max (USB-C) |
| | Beats and Powerbeats models |

“Battery only” means DeskPods recognises them and shows the charge, but the
pop-up-on-open is switched off until it has been tested on that exact hardware.
Have one of these? A [bug report with a log](#something-not-working) helps a lot.

## FAQ

**Why does it show 55 % and not 57 %?**
AirPods broadcast battery in 10 % steps. Every app on Windows gets the same
numbers — DeskPods shows the middle of the step instead of pretending to be more precise.

**The card didn't show up the first time.**
Make sure the AirPods are paired with this PC and Bluetooth is on. If it still
doesn't work, see below.

**Does it drain my AirPods or my laptop?**
No. It only listens to a signal the AirPods send anyway.

## Something not working?

1. Right after it goes wrong, open DeskPods → **Settings → Diagnostics → Export logs**.
2. [Open an issue](https://github.com/Stasieps/DeskPods/issues/new/choose), say what happened, and attach the file.

## Support the project

DeskPods is free and always will be. If it saves you a few “how much battery is
left?” moments, you can buy me a beer 🍺 — there's a button in **Settings → About**,
or go to [ko-fi.com/deskpods](https://ko-fi.com/deskpods).

## For developers

DeskPods is a native .NET 8 WPF app. To build it from source, run `START.cmd` and
choose **1** (it installs the .NET 8 SDK for you with option **5** if needed).
Tests: `tests/PodsView.ParserSmoke`, `tests/PodsView.UiSmoke` and the Python
checks in `tests/portable`. Every branch is checked by CI. A release is built
automatically when a `vX.Y.Z` tag that matches `VERSION` is pushed.
What changed in each version: [CHANGELOG.md](CHANGELOG.md).

---

<sub>DeskPods is an independent project and is not affiliated with, endorsed by or sponsored by Apple Inc.
AirPods and Beats are trademarks of Apple Inc. MIT licensed.</sub>
