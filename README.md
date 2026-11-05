# WifiBssidTool

**English** | [Русский](README.ru.md)

A Windows tray app that **pins your Wi-Fi connection to a specific access
point or repeater** on a network with several access points sharing one name
(SSID).

## Why

If a network has several access points (router + repeaters, mesh) with the
same SSID, Windows decides on its own which one to connect to, and often
stubbornly sticks to a distant, weak one. Windows has no built-in way to say
"connect to this particular repeater".

WifiBssidTool watches which BSSID (the access point's MAC address) you are
connected to:

- while you are on the chosen access point, it **does nothing** and does not
  scan the air;
- if Windows moved you to another access point while the chosen one is
  available, it asks Windows to go back;
- if the chosen access point is unavailable (powered off, out of range), it
  **keeps scanning and looks for it**, and connects as soon as it appears.

## Features

- **Tray icon**: the colour shows the state (green: on the chosen access
  point; yellow: connecting; red: chosen access point unavailable; grey:
  paused or no access point selected). Left click opens the window, right
  click shows the menu: open window, change access point, pause, autostart,
  update, **exit**.
- **Settings window**:
  - list of access points of the selected network: name, MAC, signal (% and
    dBm), channel and band (2.4 / 5 / 6 GHz);
  - change the target access point in one click (double click or button);
  - enter a MAC manually if the access point is not visible right now;
  - **custom names** ("Bedroom repeater") so you don't have to remember MACs;
  - event log.
- **Closing the window**: by default the app keeps running in the tray. This
  can be turned off in the settings, then closing the window exits the app.
- **Autostart** with Windows, minimized to the tray. Enable it in the tray
  menu or in the settings.
- **Notifications** when the chosen access point disappears and when the
  connection is restored (can be turned off).
- **Automatic updates** from GitHub Releases, like Discord.
- **13 interface languages**, switchable on the fly.
- Uses the native Windows Wi-Fi API (`wlanapi.dll`) instead of parsing `netsh`
  output, so it does not depend on the system language.
- Verifies the result of every connection attempt. If a normal request does
  not work, it falls back to a hard reconnect (disconnect, then connect to the
  desired BSSID).
- Single instance. Launching it again just shows the window of the running
  copy.

## Installation and updates

1. Download `WifiBssidTool-win-Setup.exe` from
   [Releases](https://github.com/skezze/WifiBssidTool/releases/latest) and run
   it. No administrator rights are needed: the app installs to
   `%LocalAppData%\WifiBssidTool`, creates shortcuts and starts by itself. If
   the .NET Desktop Runtime 10 is missing, the installer downloads it.
2. After that the app **updates itself**, like Discord: every 6 hours (and 30
   seconds after start) it checks GitHub Releases, downloads the new version
   in the background and offers "Restart and update" in the tray menu and in
   the settings. If you don't click it, the update is applied on the next
   launch. Auto-update can be turned off with a checkbox in the settings.
3. Uninstall via "Settings → Apps". Autostart is removed automatically.

Your settings live in `%AppData%\WifiBssidTool` and survive updates.

The installer is not signed with a certificate (code signing is paid), so
SmartScreen may show "Unknown publisher": click "More info → Run anyway".
Updates work only in the installed build, not when running from `bin\`.

## Languages

English, Русский, Українська, Deutsch, Français, Español, Italiano,
Português, Polski, Türkçe, 简体中文, 日本語, 한국어. The system language is used
by default (English if it is not supported); you can change it in the
settings, on the fly. The log (`log.txt`) is always in English so it is easy to
share in an issue.

Translations were written by an AI model; corrections from native speakers are
very welcome (pull requests or issues).

To add a language: copy `Localization/en.json` to `Localization/xx.json`,
translate the values, add the language to `Loc.Languages`
(`Localization/Loc.cs`) and run `tools/check-locales.ps1`: it checks that all
files have the same keys and placeholders.

## Requirements

- Windows 10/11 with a Wi-Fi adapter.
- [.NET SDK 10](https://dotnet.microsoft.com/download) to build from source.
- Windows must have a **saved profile** for the network (you must have
  connected to it at least once with the password remembered). The app does
  not store passwords and does not create profiles.

## Build and run

```powershell
dotnet build -c Release
dotnet run -c Release
```

The exe is at `bin\Release\net10.0-windows\WifiBssidTool.exe`. No
administrator rights are needed.

Non-interactive check (creates the window and menus in all languages, exits
with code 0 on success): `powershell -File tools/smoke-test.ps1`.

## How to publish a release

```powershell
git tag v1.2.0
git push --tags
```

GitHub Actions (`.github/workflows/release.yml`) builds the app, packs it with
[Velopack](https://velopack.io) (installer, full and delta packages) and
publishes the release. The version is taken from the tag and overrides
`<Version>` in the csproj. The repository must be public, otherwise clients
cannot download updates.

## Usage

1. Start the app. The window opens with the list on the **Access points** tab.
2. Select a network in the drop-down (the current one is preselected).
3. Select the repeater you need and click **Pin selected access point** or
   double-click the row. To tell access points apart, give them names with
   **Name…**.
4. Close the window. The app keeps running in the tray.

You can change the access point at any time: tray menu → **Change access
point…**. The new one is picked up immediately, no restart needed.

Command-line options:

| Option | What it does |
|---|---|
| `--minimized` | start without a window, tray icon only (used by autostart) |
| `--lang=xx` | force the language for this run (`en`, `ru`, `de`, ...) |
| `--smoke-test` | UI self-check and exit (for CI) |

## Where data is stored

Folder `%AppData%\WifiBssidTool\` (can be opened with a button in the
settings):

- `wifi-config.json`: chosen access point, access point names, settings;
- `log.txt`: the log (when it exceeds 1 MB the older part moves to
  `log.txt.old`).

If a `wifi-config.json` from the old console version lies next to the exe, it
is picked up automatically.

```json
{
  "Ssid": "WLAN-SR",
  "Bssid": "d8:b3:70:d8:19:55",
  "Profile": "",
  "Names": { "d8:b3:70:d8:19:55": "Bedroom repeater" },
  "MinimizeToTrayOnClose": true,
  "ShowNotifications": true,
  "Language": "",
  "AutoUpdate": true
}
```

`Profile` is only needed if the Wi-Fi profile name in Windows differs from the
SSID. `Language` is a language code; empty means the system language.

## How it works

1. Once a second the current connection is read (`WlanQueryInterface`).
2. If you are on the chosen access point, nothing else is done.
3. Otherwise a scan is requested (`WlanScan`) and the access point list is
   read (`WlanGetNetworkBssList`): every 15 seconds if the chosen access point
   is visible, every 10 seconds while it is unavailable.
4. If the chosen access point is visible, `WlanConnect` is called with the
   profile and an allow-list of BSSIDs containing a single address.
5. After 8 seconds the result is checked. After two failures in a row the app
   first calls `WlanDisconnect` and then connects again. Windows accepts a
   BSSID much more willingly when there is no connection.

## Limitations and Windows quirks

- `WlanConnect` is a **request**, not a guarantee. Windows may ignore it or
  move back to another access point: it has its own roaming rules. The app
  retries in that case, but no more often than once every 10 seconds.
- If the chosen access point is not visible, connecting to it is impossible.
  The app waits and keeps looking for it.
- During a hard reconnect the connection drops briefly.
- It is best to set roaming aggressiveness in the adapter settings (Device
  Manager → adapter → Advanced → *Roaming Aggressiveness*) to the minimum,
  otherwise the driver may move you away from the chosen access point on its
  own.
- Access points with a hidden SSID are not shown in the list.
- One adapter is supported: the connected one, or the first physical one if
  none is connected.

## Troubleshooting

- Check that the network profile exists: `netsh wlan show profiles`. If its
  name differs from the SSID, put it into the `Profile` field of
  `wifi-config.json`.
- In the window on the "Access points" tab the chosen access point must be in
  the list with an acceptable signal. A "not visible" mark means it is not on
  the air.
- Close other programs that manage Wi-Fi (the adapter vendor's utilities and
  the like).
- Check the "Log" tab: Windows error codes are printed with text.

## License

See [LICENSE.txt](LICENSE.txt).
