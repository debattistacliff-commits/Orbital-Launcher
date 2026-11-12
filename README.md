# Orbital Launcher

![Orbital Launcher](docs/images/orbital-launcher-banner.png)

A frosted-glass ring launcher for Windows 10 and 11. Press **Alt+Space** anywhere, or right-click the desktop, and a see-through ring opens at your mouse: your apps, social apps, files, radio, a watch with weather and world times, and quick system controls, all one click away.

![Themes](docs/images/themes.png)

## Install

You need Windows 10 (1809 or newer) or Windows 11.

1. Click the green **Code** button at the top of this page, then **Download ZIP**.
2. Open your Downloads folder, right-click the ZIP and choose **Extract All…**. Put the folder somewhere you'll keep it, such as Documents.
3. Open the folder and double-click **Install.bat**.
   - If Windows shows *"Windows protected your PC"*, click **More info** → **Run anyway**. It shows this for any downloaded file it doesn't know yet.
   - If your PC doesn't have Microsoft .NET 10 yet, the installer offers to download it for you (free, from Microsoft). Press **Y**.
4. Wait about a minute. The **Orbital Launcher** icon appears on your desktop, and the app starts. The first time, the **Customise** window opens so you can make the ring yours.

The app then lives in the tray (bottom right, near the clock). Press **Alt+Space** to open the ring.

**To remove it**, double-click **Uninstall.bat** in the same folder.

<details>
<summary>For technical users</summary>

`Install.bat` runs `scripts\install.ps1`, which builds a Release copy into `publish\win-x64` with the .NET 10 SDK and adds the desktop shortcut. `scripts\uninstall.ps1 [-RemoveSettings]` removes it, along with the right-click and start-up entries.
</details>

## Opening the ring

- **Alt+Space** opens and closes it at the mouse. If another app already uses Alt+Space (for example PowerToys Run), it uses **Ctrl+Alt+Space** instead; the tray tooltip tells you which.
- **Right-click the desktop** → *Show more options* → **Orbital Launcher**.
- **Click the tray icon.**
- **Esc** or a click outside closes it.
- **Drag the watch** in the centre to move the ring. Click the **pin** on its edge to keep it open in that spot.

## Sections

| Section | What you get |
| --- | --- |
| **Apps** | Your pinned apps (up to 14, with a *More* page). Search any app and right-click it to pin or unpin. |
| **Social** | Facebook, Instagram, TikTok, WhatsApp, YouTube, Telegram, Discord and more, whether installed from the Store, Edge, Chrome or as normal programs, plus your Chrome and Edge profiles with their pictures. |
| **Files** | Desktop, Documents, Downloads, Pictures, Music, Videos, This PC, Recycle Bin. |
| **Radio** | Stations from your country and any others you choose, favourites, recent stations and search. Click to play, right-click to save as a favourite. |
| **System** | Screenshot, snip an area, record the screen, volume, mic, brightness, Wi-Fi, Bluetooth, Windows shortcuts, the work timer, world times and power (lock, sleep, restart and shut down need two clicks). |

### Open apps

A **green dot** means the app is already open. Clicking it brings it to the front; clicking it again while it is in front closes it. The small **×** closes all its windows. Apps that hide in the tray when closed (like Telegram) are fully quit.

### Pin your own shortcuts

Drag a shortcut, program or file onto the ring, or copy it and press **Ctrl+V** with the ring open. It is pinned to Apps.

## Search

Just start typing when the ring is open. **Enter** opens the top result, **Up/Down** move, **Esc** clears.

| Type | Finds |
| --- | --- |
| `notepad` | Apps, folders, actions and saved stations |
| `f report` | Files only (from the Windows index). Press **Search all drives** for a full scan. |
| `r jazz` | Radio stations worldwide |
| `t 45m`, `t 2h` | Starts a work timer that rings when time is up |
| `w rome` | Time and weather in a place |

Right-click a file result to show it in its folder.

## The watch

The centre of the ring is a watch. **Click it** or use **‹ ›** to flip between faces:

Digital · Classic 12-hour · Classic 24-hour · Glow · Calendar · Timer · Radio · Volume · World times · Weather (animated sky: sun, clouds, rain, snow, wind, storms)

![Watch faces](docs/images/watch-faces.png)

Use the colour swatch (or right-click the watch) to change its colour, a country flag or a landmark (London, Rome, Paris, Valletta, Sydney).

![Flags and landmarks](docs/images/watch-flags-landmarks.png)
![Weather](docs/images/weather-scenes.png)

When the radio plays, the ring and watch light up with the music.

## Customise

Tray icon → **Customise…**, or *System → More → Customise*.

- **Look:** 10 themes (Glass, Titanium, Midnight, Light frost, Neon, Ocean, Sunset, Forest, Cartoon, High contrast), the watch face and colour.
- **Places:** the cities shown on the World and Weather watches. Search any city.
- **Radio:** which countries' stations to list.
- **Accounts:** which Chrome and Edge profiles appear in Social.
- **Start-up:** hotkey, start with Windows, desktop right-click entry.
- **My setup:** save your whole setup (or just a theme) to a file and load it on another PC, or share it with friends.

**Save and apply** restarts the ring with your changes.

![System controls](docs/images/system-controls.png)

## Privacy

- No account, no tracking, no adverts. Your settings stay on your PC in `%LOCALAPPDATA%\DesktopOrbit\ring.json`.
- App, file and profile lists are read from Windows on your PC and never leave it.
- Radio stations come from the free community directory [Radio Browser](https://www.radio-browser.info/); streams come from each station.
- Weather and city search use [Open-Meteo](https://open-meteo.com/) (free, no key). Only the positions of your chosen places are sent.
- The launcher never sees or stores passwords; logins stay in your browser and apps.

## Self-test

Run `OrbitalLauncher.exe --selftest` to check everything works on your PC. It writes a report to `%LOCALAPPDATA%\DesktopOrbit\selftest.txt`. It plays a few seconds of radio and rings the timer once, puts back anything it changes, and never restarts or shuts down the PC.

## Support this project

Orbital Launcher is free. If you enjoy it and want to say thanks, you can buy me a coffee. It's completely voluntary.

[![Buy Me a Coffee](https://img.shields.io/badge/Buy%20me%20a%20coffee-support-FFDD00?logo=buymeacoffee&logoColor=black)](https://buymeacoffee.com/debattistaj)

## For developers

```text
Ring/              The ring window, watch faces, open-app tracking, drag-and-drop pins
ShellIntegration/  App catalogue, icons, radio, audio, brightness, weather, settings, self-test
Customise/         The Customise window
scripts/           Install and uninstall
```

Build with `dotnet build`. See [docs/ROADMAP.md](docs/ROADMAP.md) for what's next.

## Creator

Created and maintained by [@debattistacliff-commits](https://github.com/debattistacliff-commits). Copyright is retained by the creator; see [LICENSE](LICENSE).
