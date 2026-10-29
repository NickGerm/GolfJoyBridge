# GolfJoyBridge

Connects a **GolfJoy launch monitor** to golf simulators that support the **GSPro Open Connect API** Only tested on the Spica 3
— GSPro, Muni Golf Sim, OpenGolfSim, ShotForge, and others. Works over a network cable or
Wi-Fi. Also shows the unit's impact-camera video on screen after every shot.

> **Unofficial project.** Not made by or affiliated with the launch monitor's manufacturer, or with
> GSPro. Use it at your own risk. See [Disclaimer](#disclaimer) below.

Licensed under the **GNU GPLv3** — see [`LICENSE`](LICENSE). Free to use, share, and modify; anyone
who distributes it or a modified version has to keep it open too. See [License](#license) below.

## Contents

- [What you need](#what-you-need)
- [Install (prebuilt binary)](#install-prebuilt-binary)
- [Build from source](#build-from-source)
- [Connecting your launch monitor](#connecting-your-launch-monitor)
- [Using it](#using-it)
- [Settings window](#settings-window)
- [Advanced settings](#advanced-settings)
- [Troubleshooting](#troubleshooting)
- [Project layout](#project-layout)
- [License](#license)
- [Disclaimer](#disclaimer)

---

## What you need

- **Windows 10 or 11**, 64-bit.
- A **Spica 3** launch monitor, cabled to your PC or connected to your Wi-Fi.
- **`GRS.dll`**, copied from your launch monitor's own official software. This file is not included
  here — it belongs to the manufacturer, so you need to grab your own copy from wherever you installed
  their app.
- A sim that has a **GSPro / Open Connect / Open API** launch-monitor option (GSPro, Muni, OpenGolfSim,
  ShotForge, and others).

---

## Install (prebuilt binary)

Most people should start here — you don't need to install anything else to run it.

1. Download the latest zip from the **Releases** page (right-hand side of this repo) and extract it
   anywhere, e.g. your Desktop.
2. Find `GRS.dll` in your launch monitor's official app folder and copy it into the same folder as
   `GolfJoyBridge.exe`.
3. Close the official app — it uses the same connection as this program, so only one can run at a time.
4. Run `GolfJoyBridge.exe`. A golf-ball icon appears in your system tray (bottom-right, near the clock —
   click the little arrow there if you don't see it).

The first time it runs, it creates a `GolfJoyBridge.ini` file and a `logs` folder next to the exe.
You shouldn't need to touch either unless something needs troubleshooting.

---

## Build from source

If you'd rather build it yourself, or want to change something:

1. Install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).
2. Clone this repo.
3. Run `build.bat` from the repo root. It publishes a self-contained single-file exe to `publish\GolfJoyBridge.exe`
   — whoever runs it won't need .NET installed themselves.
4. Copy your own `GRS.dll` next to it, as in the install steps above.

To run the automated tests (logic only — no launch monitor needed):

```
run-tests.bat
```

This covers the settings file, the shot-data mapping (checked against real logged shots), the
Open Connect client, and the wireless relay, all against stand-ins — no hardware required. A GitHub
Actions workflow (`.github/workflows/build.yml`) runs the same tests and builds the exe on every push.

---

## Connecting your launch monitor

### Wired (the default)

Follow the manufacturer's instructions for a hardwired ethernet connection. If your monitor
Connects to GSPro or the Golfjoy app on your pc it is configured correctly for this connector.

### Wireless

The wireless connector allows for laptops without dual Nic cards to be used, and also easier switching 
Between right and left handed players. 

1. Connect the unit to your Wi-Fi using the manufacturer's phone app, if you haven't already.
2. On your router, give the unit a **reserved / static IP address** (sometimes called a "DHCP
   reservation") so its address never changes. Check your router's admin page for how to do this.
3. Right-click the tray icon → **Settings...**
4. Choose **Wireless**, type in the unit's IP address, and click **Restart app to apply**.

A small built-in relay lets `GRS.dll` (which only knows how to talk to a wired unit) reach the unit
over Wi-Fi. Shot and camera data are unaffected either way.

---

## Using it

Right-click the tray icon for the menu:

| Item | What it does |
|---|---|
| **Replay last shot** | Shows your most recent shot's camera video again |
| **Settings...** | Opens the settings window (see below) |
| **Open settings file (advanced)** | Opens the raw `.ini` file for less common settings |
| **Open log folder** | Opens the folder with the log file, useful if something's not working |
| **Restart (reload settings)** | Restarts the program |
| **Quit** | Closes it |

The tray icon itself tells you what's going on at a glance:

| Icon colour | Meaning |
|---|---|
| ⚪ Grey | Looking for the launch monitor |
| 🟡 Yellow | Launch monitor connected, waiting on the sim |
| 🟢 Green | Everything connected — ready to play |
| 🟠 Orange | Launch monitor was connected but the connection dropped (check the cable / power / Wi-Fi) |
| 🔴 Red | Could not start — see the log folder for why |

---

## Settings window

Everything most people need is here — right-click the tray icon → **Settings...**

**Connection**
- **Wired / Wireless** — which kind of connection you're using.
- **Unit's Wi-Fi IP address** — only shown in Wireless mode.

Changing either of these needs a restart to take effect — the **Restart app to apply** button turns
green as a reminder once you've changed one.

**Shot camera**
- **Off / Loop / Popup** — how the swing-camera video is shown (see below).
- **Monitor** — which screen it appears on, if you have more than one.
- **Position / Size sliders** — drag them to move and resize the video window. A live preview appears
  on screen the moment you open Settings, so you can see exactly where it will land as you adjust the
  sliders — no guessing and restarting to check.
- **Time after shot before it shows** / **Times it replays** — only for Popup mode, see below.

Everything in this window saves and applies instantly — camera changes show up immediately, no need to
close the window or restart (except for the connection settings, as above).

### Loop vs. Popup

- **Loop** — a small window stays on screen at all times, quietly replaying your most recent shot on a
  loop. It never takes focus away from your sim, so you can glance over whenever you like. Good for a
  second monitor.
- **Popup** — after you hit a shot, nothing happens for a few seconds (you choose how long), then a
  window pops up, plays your swing a few times (you choose how many), and closes itself automatically.
  Good for a single-monitor setup, so the video doesn't sit on screen blocking your view of the sim —
  it just shows up right after your swing, then gets out of the way.

---

## Advanced settings

A few less common options — connection ports, specific club numbers, timing, log file locations — live
only in the `.ini` file, reachable from the tray menu's **Open settings file (advanced)**. Every option
in there has a comment explaining what it does. Most people will never need to open it.

---

## Troubleshooting

**Sim shows the launch monitor as not connected.**
Make sure `GolfJoyBridge.exe` is running (check the tray icon) and that it's not grey/orange/red.
Grey means it hasn't found the unit yet — double check the cable, the unit's power, and your PC's IP
settings (wired) or the reserved IP you set (wireless).

**No camera video shows up.**
Open the `.ini` file (tray menu → advanced) and make sure `SdkLogFolder` isn't left blank. Also confirm
`GRS.dll` is sitting right next to `GolfJoyBridge.exe`.

**Windows or antivirus flags the program when you run it.**
This happens sometimes with small, independently published apps that aren't digitally signed. Since the
source is public, you're welcome to check exactly what it does — or build it yourself from source if
you'd rather not run a downloaded binary at all.

**Still stuck?**
Right-click the tray icon → **Open log folder**, and open `connector.log`. It records what the program
is doing step by step and usually makes the problem obvious — worth checking before anything else.

---

## Project layout

```
src/GolfJoyBridge.Core   logic: settings, Open Connect client, shot mapping, GRS.dll wrapper, wireless relay (no UI)
src/GolfJoyBridge.App    Windows UI: tray icon, settings window, camera windows
tests/                      automated logic tests (no hardware needed)
```

Pull requests welcome — see [License](#license) for the terms contributions are released under.

---

## License

This project is licensed under the **GNU General Public License v3.0** — see [`LICENSE`](LICENSE) for
the full text. In short: you're free to use, study, modify, and share this program, including
commercially, but if you distribute it or a modified version, you must keep the source available under
the same license. You can't take this code and ship a closed-source version of it.

`GRS.dll` is **not** part of this project and is **not** covered by this license — it belongs to the
launch monitor's manufacturer, isn't distributed here, and you supply your own copy under whatever
terms came with it.

---

## Disclaimer

This is an independent, unofficial tool. It is not made by, endorsed by, or affiliated with the
launch monitor's manufacturer or with GSPro. `GRS.dll` is their software, not this project's — you
provide your own copy, and you're bound by whatever terms came with it. Use this program at your own
risk; it comes with **no warranty of any kind**, as described in the [License](#license) above.
