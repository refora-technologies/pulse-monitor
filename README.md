<div align="center">
  <img src="Resources/Icons/pulse%20logo.png" alt="Pulse Logo" width="128" height="128" />
  <h1>Pulse Monitor</h1>
  <p><b>A lightweight, precise, and beautiful hardware monitoring overlay for Windows.</b></p>
  <p>
    <a href="https://pulse.reforatech.com">🌐 Website</a> &nbsp;·&nbsp;
    <a href="https://github.com/refora-technologies/pulse-monitor/releases/latest">⬇️ Download</a> &nbsp;·&nbsp;
    <a href="https://pulse.reforatech.com/privacy.html">🔒 Privacy</a>
  </p>
</div>

<br/>

## Overview

**Pulse** by Refora Technologies is a highly optimized hardware monitoring overlay designed to deliver real-time telemetry of your system's critical components. Built from the ground up for minimal performance impact, Pulse gives power users, gamers, and developers granular insight into CPU, GPU, and Memory vitals—all through a premium, customizable user interface.

## Key Features

- **Real-Time Telemetry:** Instant readouts for CPU/GPU Temperatures, Clock Speeds, Usage, TDP/Power Draw, and VRAM/RAM utilization.
- **Dynamic Overlay:** A seamless glassmorphic HUD that sits unobtrusively on your screen, featuring a secondary "Compact Mode" designed specifically for distraction-free in-game monitoring.
- **Ultra-Fast Polling:** Customizable interval polling directly integrated with LibreHardwareMonitor, down to 0.5 seconds for instantaneous tracking.
- **Refora Design Language:** A customized, violet-accented dark theme powered by the Ubuntu font family for crisp, elegant readability.
- **Zero Configuration Setup:** Single-file executable architecture that runs seamlessly without requiring any external .NET runtime installations.

## Technical Stack

- **Framework:** .NET 8.0 (WPF)
- **Architecture:** MVVM Design Pattern (CommunityToolkit.Mvvm)
- **Hardware Integration:** LibreHardwareMonitor
- **Data Serialization:** Newtonsoft.Json

## Installation

Download the latest installer from our official distribution channels. Pulse features a completely self-contained deployment model—no prerequisites required.

1. Run `PulseSetup_v1.X.X.exe`.
2. Follow the on-screen instructions.
3. Pulse will automatically launch and minimize to the system tray.

> **Note on Administrator Privileges:** Pulse requires elevated administrator privileges upon launch in order to securely read low-level hardware sensors directly from the kernel interface.

## Configuration & Usage

Once launched, right-click the Pulse icon in the Windows system tray and select **Settings**. From the control panel, you can:
- Toggle the visibility of specific hardware tiles (e.g., *CPU Temp*, *GPU Power*).
- Define overlay opacity and screen position.
- Enable **Compact Mode** for a minimized, text-only HUD.
- Adjust the hardware polling rate (0.5s, 1s, 2s, 5s).
- Un-dock the overlay to manually drag it to any custom position on your desktop.

## License

This project is licensed under the **GNU General Public License v3.0 (GPLv3)**.

You are free to use, modify, and distribute this software, provided that any derivative works are also open-source and licensed under the identical terms. See the `LICENSE` file for the complete terms and conditions.

Pulse links dynamically to open-source components such as LibreHardwareMonitor (MPL-2.0), Newtonsoft.Json (MIT), and CommunityToolkit.Mvvm (MIT).

---

<div align="center">
  <p>Crafted by <b>Refora Technologies</b></p>
  <p><a href="https://reforatech.com">reforatech.com</a></p>
</div>
