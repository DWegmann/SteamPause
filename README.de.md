# Steam Download Control

[English](README.md) | **Deutsch**

Steam Download Control pausiert oder begrenzt Steam-Downloads, solange ausgewählte Programme laufen. Die Windows-App läuft im Infobereich und verwendet C#, WinForms und .NET 10.

## Download und erster Start

1. Lade [SteamDownloadControl.exe](https://github.com/DWegmann/SteamDownloadControl/releases/latest/download/SteamDownloadControl.exe) herunter und öffne sie auf einem 64-Bit-Windows-PC mit installiertem Steam-Desktopclient. Die portable EXE enthält die .NET-Laufzeit und ist unsigniert.
2. Wähle mit **EXE hinzufügen** eine Datei oder mit **Laufendes Programm …** ein bereits gestartetes Programm aus.
3. Wähle **Steam-Downloads pausieren** oder gib unter **Begrenzen auf** ein Limit in MB/s ein. Wenn mehrere Regeln gleichzeitig greifen, hat eine Pause Vorrang. Sonst gilt das niedrigste Limit.
4. Schließe das Fenster, um die App im Infobereich weiterlaufen zu lassen. Aktiviere bei Bedarf **Bei der Windows-Anmeldung starten**.

Wenn keine Regel mehr greift, stellt die App die vorherigen Steam-Download-Einstellungen wieder her. Mit **Geplante Steam-Updates bei freier Warteschlange vorziehen** kannst du fällige Updates bei Leerlauf früher starten.

Wenn du von SteamPause wechselst, beende SteamPause vor dem Start der neuen EXE. Die App verwendet deine vorhandenen Einstellungen unter `%LOCALAPPDATA%\SteamPause` und stellt einen aktivierten Autostart auf die neue EXE um.

## Steam-Kompatibilität

Steam Download Control funktioniert mit 64-Bit-Versionen von Steam, die die benötigte native Download-Schnittstelle bereitstellen. Die App prüft die Valve-Signatur von `steamclient64.dll` und ermittelt die Download-Methoden zur Laufzeit. Kann sie die Schnittstelle nicht bestätigen, zeigt sie eine Kompatibilitätsmeldung an.

## Bauen

Installiere unter Windows das .NET-10-SDK und führe diese Befehle im Repository-Hauptverzeichnis aus.

```powershell
dotnet build src/SteamDownloadControl -c Release
dotnet publish src/SteamDownloadControl -c Release -p:PublishProfile=WindowsSingleFile -o dist/single-file
```

Der Publish-Befehl erzeugt die eigenständige x64-Datei `dist/single-file/SteamDownloadControl.exe`.

## Projektstruktur

- `src/SteamDownloadControl/` enthält die Windows-App, die Symbole für den Infobereich und das Publish-Profil.
- `ReleaseNotes/` dokumentiert die veröffentlichten Versionen.
- `licenses/dotnet/` enthält die Lizenztexte und Hinweise für die mitgelieferte .NET-Laufzeit. Siehe [ThirdPartyNotices.md](ThirdPartyNotices.md).
