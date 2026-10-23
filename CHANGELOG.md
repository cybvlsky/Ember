# Changelog

## 1.0.0 - 2026-10-23

First public release.

- Sends only the pixels that changed, so small animations run at 20 to 30 fps over a USB link that needs two seconds for a full frame.
- Eight themes: Ember, Aurora, Clock, Cores, Latte, Mono, Neon and Terminal.
- Layout editor with layers, snapping, undo and a live mode that puts every change on the screen as you make it.
- CPU, GPU, RAM, disk and network sensors through LibreHardwareMonitor, CPU temperature through the signed PawnIO driver.
- Starts with Windows with sensor access and no UAC prompt. The screen goes dark with the monitors and when the PC sleeps.
- A start-up card with the logo and version, faded in and out with the backlight.
- English and Polish, for the app and the built-in themes.
- Installer with an optional PawnIO step and a clean uninstall.
