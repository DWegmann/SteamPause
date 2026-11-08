# SteamPause

**English** | [Deutsch](README.de.md)

SteamPause is a Windows notification-area app that pauses or limits Steam downloads while selected programs are running. It uses C#, WinForms and .NET 10 and has a German interface.

## Download and first launch

1. Download [SteamPause.exe](https://github.com/DWegmann/SteamPause/releases/latest/download/SteamPause.exe) and open it on a 64-bit Windows PC with the Steam desktop client. The portable EXE includes the .NET runtime and is unsigned.
2. Choose **EXE hinzufügen** to select an executable or **Laufendes Programm …** to select a running program.
3. Choose **Steam-Downloads pausieren** or set a limit in MB/s under **Begrenzen auf**. When several rules apply, a pause takes priority. Otherwise, the lowest applicable limit wins.
4. Close the window to keep SteamPause running in the notification area. To start it automatically, enable **Bei der Windows-Anmeldung starten**.

SteamPause restores the previous Steam download settings when the rules stop applying. To start scheduled Steam updates early when the queue is idle, enable **Geplante Steam-Updates bei freier Warteschlange vorziehen**. SteamPause stores its settings locally under `%LOCALAPPDATA%\SteamPause`.

## Steam compatibility

SteamPause 1.0.0 supports the exact 64-bit Steam client build it was tested with (`steamclient64.dll` file version `11.15.70.27`). It verifies the DLL's SHA-256 before accessing Steam's download controls. A Steam update can require a new SteamPause release.

## Build

Install the .NET 10 SDK on Windows and run these commands from the repository root.

```powershell
dotnet build src/SteamPause -c Release
dotnet publish src/SteamPause -c Release -p:PublishProfile=WindowsSingleFile -o dist/single-file
```

The publish command creates `dist/single-file/SteamPause.exe`, a self-contained x64 executable.

## Project structure

- `src/SteamPause/` contains the Windows app, tray icons and publish profile.
- `ReleaseNotes/` records published versions.
- `licenses/dotnet/` contains the license texts and notices for the bundled .NET runtime. See [ThirdPartyNotices.md](ThirdPartyNotices.md).
