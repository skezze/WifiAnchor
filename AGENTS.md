# AGENTS.md

Guide for AI coding agents. Humans: see `README.md` / `README.ru.md`.

## What this is

Wi-Fi Anchor (internal id `WifiAnchor`): a Windows tray app (WinForms, .NET 10, `net10.0-windows`) that
pins the Wi-Fi connection to a chosen BSSID via the native `wlanapi.dll`.
Installed per-user with a Velopack installer; updates itself from GitHub
Releases (`skezze/WifiAnchor`). UI is localized into 13 languages.

No admin rights are needed or wanted. There are no unit tests; validation is
the build plus the smoke test.

## Layout

| Path | Contents |
|---|---|
| `Program.cs` | Entry point, single-instance mutex, `--minimized` / `--lang=xx` / `--smoke-test` |
| `Wlan/WlanClient.cs` | P/Invoke and wrapper over `wlanapi.dll` (query, scan, BSS list, connect, disconnect) |
| `Services/WatcherService.cs` | Background loop: keeps the connection on the target BSSID |
| `Services/UpdateService.cs` | Velopack auto-update (check / download / apply), status events |
| `Configuration/AppConfig.cs` | JSON config in `%AppData%\WifiAnchor\wifi-config.json` |
| `Infrastructure/` | `AppLog` (log file), `AutoStart` (registry Run key), `Mac` (parse/format) |
| `Localization/` | `Loc.cs` + one `xx.json` per language, embedded in the exe |
| `UI/` | `TrayApplicationContext` (tray icon, menu, smoke test), `MainForm` (settings window), `StateStyle`, `Controls` |
| `tools/` | `check-locales.ps1`, `smoke-test.ps1`, `make-icon.ps1` (regenerates `app.ico`) |
| `.github/workflows/release.yml` | Release pipeline, triggered by `v*` tags |

## Commands

```powershell
dotnet build -c Release                                   # must stay at 0 warnings, 0 errors
powershell -NoProfile -File tools/check-locales.ps1       # all locale files have the same keys/placeholders
powershell -NoProfile -File tools/smoke-test.ps1          # builds UI in every language, exit code 0 = OK
```

Release: `git tag vX.Y.Z && git push origin vX.Y.Z`. CI builds with
`-p:Version=X.Y.Z` (overrides `<Version>` in the csproj), packs with `vpk`
(pinned to the same version as the `Velopack` NuGet package) and uploads to
GitHub Releases. Pushing to `master` alone does not release anything.

## Rules and pitfalls

- **Velopack:** `VelopackApp.Build()...Run()` must be the very first call in
  `Main`, before the mutex. Keep the `vpk` CLI and the `Velopack` package on
  the same version.
- **Packaging:** the build is framework-dependent and packed with
  `--framework net10.0-x64-desktop`. Do not combine self-contained with
  `--framework`.
- **Naming:** the user-visible product name is "Wi-Fi Anchor"; the internal
  identifier is `WifiAnchor` (`packId`, exe, namespace, `%AppData%` folder,
  autostart key, mutex, repo). Do not rename `packId` again: it breaks
  auto-update for installed copies.
- **Storage:** user data lives only in `%AppData%\WifiAnchor`, never in the
  install folder (it is replaced on update).
- **Updates only work in an installed build** (`UpdateManager.IsInstalled`),
  not when running from `bin\`.
- **Localization:**
  - Any new user-visible string goes through `Loc.T("key")` and must be added
    to **all** `Localization/*.json` files; then run `check-locales.ps1`.
  - `en.json` is the fallback and the reference for keys.
  - To add a language: new `Localization/xx.json` + entry in `Loc.Languages`.
    The csproj embeds `Localization\*.json` automatically.
  - Units (GHz, MHz, dBm) stay Latin.
- **Logs are English-only** (`AppLog.Write`), UI text and notifications are
  localized.
- **Code comments are in Russian.** Keep that style; do not translate
  existing comments.
- **Windows Wi-Fi:** `WlanConnect` is only a request; Windows may ignore it.
  Do not add tighter retry loops than the existing intervals.
- Do not commit `bin/`, `obj/`, `publish/`, `Releases/` (already ignored).
- Do not change the app manifest to request administrator rights.
- No paid services: no code signing, no paid hosting. SmartScreen warnings
  for the unsigned installer are expected.

## Commit messages

Short imperative subject, at most about 50 characters, capitalized, no
trailing period. Add a body (wrapped at 72 columns) only when it carries
useful information. Do not create commits, branches or tags unless the user
asks.
