# VolumeNest

VolumeNest is a lightweight Windows system-tray volume mixer for controlling the master volume and individual applications from one compact flyout.

## Features

- Master volume and mute control
- Per-application volume and mute controls
- Per-application output/input device routing on supported Windows 11 builds
- Optional Equalizer APO integration
- System-tray operation with global hotkeys

## Hotkeys

- `Ctrl + Alt + V` — open or close the mixer
- `Ctrl + Alt + E` — open or close the equalizer

## Requirements

- Windows 10 or Windows 11
- .NET 8 SDK for building
- NAudio 2.2.1 is restored automatically by NuGet
- Windows 11 build 21390 or later for per-application device routing
- Equalizer APO is optional and required only for the equalizer to affect system audio

## Build

From the repository root:

```bash
dotnet restore VolumeNest/VolumeNest.csproj
dotnet build VolumeNest/VolumeNest.csproj -c Release
```

Publish a self-contained single-file Windows build:

```bash
dotnet publish VolumeNest/VolumeNest.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish/win-x64
```

The published executable is `publish/win-x64/VolumeNest.exe`.

## Installer

Install [Inno Setup](https://jrsoftware.org/isinfo.php), publish the application, then compile `VolumeNest.iss` with the Inno Setup Compiler. The installer is created as:

```text
publish/VolumeNest-Setup-1.0.0.exe
```

Build output is intentionally ignored by Git. Releases and installers should be shared through GitHub Releases rather than committed to the source repository.

## License

VolumeNest is distributed under the MIT License. See [LICENSE](LICENSE).
