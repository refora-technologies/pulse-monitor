<div align="center">
  <img src="Resources/Icons/pulse%20logo.png" alt="Pulse Logo" width="128" height="128" />
  <h1>Pulse Monitor</h1>
  <p><b>A lightweight, precise, and beautiful hardware monitoring overlay for Windows.</b></p>
  <p>
    <img src="https://img.shields.io/badge/Windows-10%20%7C%2011-blue?style=flat-square" alt="Windows 10/11" />
    <img src="https://img.shields.io/github/license/refora-technologies/pulse-monitor?style=flat-square" alt="License" />
    <img src="https://img.shields.io/github/v/release/refora-technologies/pulse-monitor?style=flat-square" alt="Release" />
    <img src="https://img.shields.io/github/downloads/refora-technologies/pulse-monitor/total?style=flat-square" alt="Downloads" />
  </p>
  <p>
    <a href="https://pulse.reforatech.com">🌐 Website</a> &nbsp;·&nbsp;
    <a href="https://github.com/refora-technologies/pulse-monitor/releases/latest">⬇️ Download</a> &nbsp;·&nbsp;
    <a href="https://pulse.reforatech.com/privacy.html">🔒 Privacy</a>
  </p>
</div>

<br/>

## Overview

**Pulse** by Refora Technologies is a highly optimized hardware monitoring overlay designed to deliver real-time telemetry of your system's critical components. Built from the ground up for minimal performance impact, Pulse gives power users, gamers, and developers granular insight into CPU, GPU, Memory, and frame rate vitals—all through a premium, customizable user interface.

## Screenshots

<div align="center">
  <img src="Resources/Screenshots/control-panel.png" alt="Control Panel" width="260" />
  <img src="Resources/Screenshots/overlay-normal.png" alt="Overlay — Normal Mode" width="260" />
  <img src="Resources/Screenshots/overlay-compact.png" alt="Overlay — Compact Mode" width="260" />
</div>

## Key Features

- **Real-Time Telemetry:** Instant readouts for CPU/GPU Temperatures, Clock Speeds, Usage, Power Draw, VRAM/RAM utilization, FPS, Network throughput (upload/download), Disk Activity, and battery charge.
- **FPS Monitoring:** Tracks the frame rate of whatever app or game currently has focus, powered by a bundled PresentMon capture — works with any GPU vendor. An optional **1% Low** tile reports the frame rate at the 99th percentile of recent frame times, measured over the last 2,000 frames or 30 seconds, whichever is shorter. That shows stutter an average frame rate hides.
- **Keyboard Shortcuts:** Show or hide the overlay, open the control panel, or switch compact mode from anywhere, including inside a game. Off until you turn them on, and Pulse refuses combinations that other software needs.
- **GPU Selection:** On laptops and multi-GPU systems, choose exactly which adapter the GPU tiles read from, or leave it on automatic.
- **Dynamic Overlay:** A seamless glassmorphic HUD that sits unobtrusively on your screen, featuring a secondary "Compact Mode" designed specifically for distraction-free in-game monitoring. Right-click the overlay in free-drag mode for quick access to the control panel.
- **Ultra-Fast Polling:** Choose how often readings are taken, from every 5 seconds down to twice a second.
- **Arrange It Your Way:** Drag tiles into any order you like, or move them with the keyboard, and place the overlay by dragging it, snapping it to a corner, or sliding it into position.
- **Resilient Sensor Reading:** Sensors are read in an isolated background process, so a graphics driver fault cannot take Pulse down with it. Pulse also notices when a GPU is switched off or comes back, re-detects your hardware on its own, and keeps trying to open sensors that were not ready rather than giving up for the rest of the session.
- **Verified Updates:** The in-app updater checks a SHA-256 checksum before installing anything and refuses to run an update that doesn't match, and downloads to a location only administrators can write to.
- **Diagnostics:** One click writes a report to your desktop, leading with what went wrong and how often, small enough to attach to a bug report.
- **Refora Design Language:** A customized, violet-accented dark theme powered by the Plus Jakarta Sans font family for crisp, elegant readability.
- **Zero Configuration Setup:** Single-file executable architecture that runs seamlessly without requiring any external .NET runtime installations.

## Technical Stack

- **Framework:** .NET 10.0 (WPF)
- **Architecture:** MVVM Design Pattern (CommunityToolkit.Mvvm)
- **Hardware Integration:** LibreHardwareMonitor
- **Frame Rate Capture:** PresentMon
- **Data Serialization:** Newtonsoft.Json

## Installation

**Requirements:** 64-bit Windows 10 or 11 on an x64 processor. Pulse installs a kernel-level driver to read sensors and that driver is x64 only, so setup will refuse an ARM64 machine rather than install something that cannot work.

Download the latest installer from our official distribution channels. Pulse features a completely self-contained deployment model—no prerequisites required.

1. Run `PulseSetup.exe`.
2. Follow the on-screen instructions.
3. Pulse launches when setup finishes and opens its control panel. It stays in the system tray after that.

> **Note on Administrator Privileges:** Pulse requires elevated administrator privileges upon launch in order to securely read low-level hardware sensors directly from the kernel interface.

## Configuration & Usage

Once launched, right-click the Pulse icon in the Windows system tray and choose **Open Control Panel**, or double-click the icon. From the control panel, you can:
- Toggle the visibility of specific hardware tiles (e.g., *CPU Temp*, *GPU Power*, *FPS*).
- Reorder tiles by dragging the handle on each one, or with **Alt + arrow keys**. The overlay follows the order you set here.
- Choose which GPU the GPU tiles read from on multi-GPU systems.
- Show RAM and VRAM as used / total instead of just used.
- Define overlay opacity and screen position, including which display to use on multi-monitor setups.
- Fade the overlay's background independently of its text, down to just the readings floating on screen, and reset opacity, background and size to the default look in one click.
- Resize the overlay by dragging the corner at its bottom right.
- Place the overlay precisely with the **X and Y** sliders, or drag it where you want it.
- Enable **Compact Mode** for a minimized, text-only HUD.
- Adjust the hardware polling rate (0.5s, 1s, 2s, 5s).
- Show network speeds in megabytes per second or megabits per second.
- Show the battery charge level on a laptop.
- Set keyboard shortcuts for the overlay, the control panel and compact mode.
- Un-dock the overlay to manually drag it to any custom position on your desktop — right-click it in this mode for quick access to the control panel or to hide the overlay.
- Save a diagnostics file from the **About** section if you need to report a problem.

## License

This project is licensed under the **GNU General Public License v3.0 (GPLv3)**.

You are free to use, modify, and distribute this software, provided that any derivative works are also open-source and licensed under the identical terms. See the `LICENSE` file for the complete terms and conditions.

Pulse bundles and links to the following open-source components: LibreHardwareMonitor (MPL-2.0), PawnIO kernel driver (LGPL-2.1), PresentMon (MIT), Newtonsoft.Json (MIT), and CommunityToolkit.Mvvm (MIT). See `THIRD-PARTY-NOTICES.txt` for full attribution.

---

<div align="center">
  <p>Crafted by <b>Refora Technologies</b></p>
  <p><a href="https://reforatech.com">reforatech.com</a></p>
</div>
