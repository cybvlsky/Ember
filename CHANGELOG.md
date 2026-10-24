# Changelog

## Unreleased

- Apps: Claude Code and Spotify get their own screens that take turns with the theme while they are in use (theme 15 s, each app 10 s, both adjustable). Each switch fades through black.
- The Claude Code screen reads Claude Code's logs on the PC: cost and tokens in the current five-hour session and when it resets, the month at API prices compared with the plan price, and an estimate of the session limit once a limit has been hit. Nothing is sent anywhere and no credentials are read.
- The Spotify screen shows the cover, title, artist, album and progress from the Windows media controls; the progress bar takes its colour from the cover.
- App screens wear the current theme's background, font and colours.
- Screen colour correction for panels that come out blue: colour temperature, gamma and per-channel gains. Only the panel is corrected.
- Needs Windows 10 version 2004 or newer.

## 1.0.1 - 2026-10-24

- A new look that follows Windows 11: a navigation pane with the categories on the left that folds to icons (by hand, in the editor and in narrow windows), settings in cards, the system font, neutral greys with one accent colour.
- New default theme, Studio: flat grey cards and one orange accent, no glow. Existing setups keep the theme they use.
- A flat logo.
- The window opens at a size that fits smaller screens.
- The release workflow's actions are pinned to commits.

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
