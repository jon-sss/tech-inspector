# Tech Inspector

**by [Simpatico Sim Shop](https://simpaticosims.com/)**

A SimHub plugin that explains, checks and edits iRacing's settings.

- **Overview.** A health check of your setup: values that differ from iRacing's defaults or look like mistakes, an oversized telemetry folder, low disk space and more. Each finding can be ignored.
- **Settings.** Opens on common fixes (frame rate, image quality, view, spotter and sound, night and wet, wheel, replays), each showing only the handful of settings behind it. Every setting in `app.ini`, `core.ini` and the graphics files is a click away, with a plain-language description and search.
- **Triple screens.** A live top view of your triple-monitor layout, the ideal side-screen angle for your seating position, and a guard that puts your layout back when iRacing resets it.
- **Force feedback.** Remembers iRacing's wheel strength per car and sets it again when you drive that car, using your keys for iRacing's strength controls.
- **Backups.** A full backup before every change, plus ones you take yourself. Compare any backup with your current files, restore all of it or one file, and turn your hand-made copies (`rendererDX11Monitor - Copy.ini` and the like) into dated backups.

## What it touches

- It changes iRacing's files in `Documents\iRacing` only while the sim is closed, and only when you write changes or restore a backup. Changes made while iRacing runs wait until it closes.
- It backs up those files before every write. Backups are kept in SimHub's data folder, not in your iRacing folder.
- It sets the wheel strength you saved for a car by pressing your keys for iRacing's strength controls. No file is changed.
- It moves telemetry files and hand-made copies to the Recycle Bin only when you ask.
- It never changes the sim or its install folder.
- It doesn't connect to the internet. Links open in your browser only when you click them.

## Install

Requires SimHub 9.12 or later on Windows, and iRacing.

1. Download `TechInspector-<version>.zip` from [Releases](https://github.com/jon-sss/tech-inspector/releases).
2. Close SimHub.
3. Extract the zip into your SimHub folder (usually `C:\Program Files (x86)\SimHub`). `TechInspector.Plugin.dll` and `TechInspector.Core.dll` go next to `SimHubWPF.exe`, and `TechInspector.Plugin.resx` goes into its `Languages` folder.
4. Start SimHub. If it asks, enable **Tech Inspector**. It appears in the left menu.

Both DLLs are signed by Simpatico Sim Shop, LLC. To check, right-click one, choose **Properties**, and open the **Digital Signatures** tab.

To uninstall, close SimHub and delete those three files. Backups and preferences are in `PluginsData\Common\TechInspector` inside the SimHub folder.

## Build from source

Requires the .NET SDK (10 or later) and a SimHub install. Packages restore from nuget.org through the repo's `nuget.config`.

```
dotnet test tests/TechInspector.Core.Tests
dotnet build src/TechInspector.Plugin -c Debug
```

- `src/TechInspector.Core` is the engine: a line-preserving ini model, safe writes, backups, the settings catalog, the health check and the triple-screen geometry. It has no SimHub dependency (netstandard2.0).
- `src/TechInspector.Plugin` is the SimHub plugin itself (.NET Framework 4.8, WPF).
- `tests/TechInspector.Core.Tests` holds the engine tests (xunit). Where iRacing is installed they also read its files, read-only. They only ever write to temp folders.

The plugin builds against the DLLs of the local SimHub install. It finds the install from the `SIMHUB_INSTALL_PATH` environment variable, or falls back to `C:\Program Files (x86)\SimHub\`. A Debug build copies the plugin into that folder; restart SimHub to load it. While SimHub has the plugin loaded the copy fails with a warning: close SimHub, then build again.

`tools/package.ps1` makes the release zip from a Release build. With `-Sign` it signs the DLLs first through Azure Artifact Signing; see the script for what that needs.

## License

Tech Inspector is source available under the [PolyForm Noncommercial License 1.0.0](LICENSE.md). You're free to read, build, change and share it for any non-commercial purpose. Selling it, bundling it with a paid product or using it in a business needs permission from Simpatico Sim Shop.

The license covers the code. It doesn't grant any rights to the Simpatico Sim Shop name or logo (`src/TechInspector.Plugin/Assets`).

## Contributing

Bug reports and ideas are welcome in [Issues](https://github.com/jon-sss/tech-inspector/issues). By opening a pull request, you agree that Simpatico Sim Shop may use your contribution in Tech Inspector under any license, including this one.

## Contact

Questions, feedback or partnership enquiries: [info@simpaticosims.com](mailto:info@simpaticosims.com). Bugs are best reported in [Issues](https://github.com/jon-sss/tech-inspector/issues).

## Support development

Tech Inspector is free, and built and maintained by one person at Simpatico Sim Shop. If it's useful to you, a donation on [Ko-fi](https://ko-fi.com/simpaticosimshop) helps keep it current with each iRacing season, and so does a visit to [the shop](https://simpaticosims.com/).

---

Not affiliated with iRacing.com Motorsport Simulations or SimHub. iRacing is a trademark of iRacing.com Motorsport Simulations, LLC.
