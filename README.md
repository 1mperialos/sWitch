# sWitch — NVIDIA Custom Resolution build

Windows tray utility for switching between a native/lobby resolution and a stretched/gameplay resolution. It preserves the tested MapReady auto-switch logic and F6 hotkey.

## What's new
- Resolution settings list is populated from Windows display modes exposed by the NVIDIA/Windows driver.
- The resolution switcher tries to keep the currently active refresh rate when the target resolution has a matching mode. If the exact Hz is not supported at the target resolution, Windows may choose a supported refresh rate instead.
- The Resolution settings dialog has a **Create custom resolution** section. It calls NVIDIA NVAPI to calculate timing at the current refresh rate, test the mode, save it, and revert the temporary test mode.

## Requirements
- Windows 10/11
- NVIDIA GPU and installed NVIDIA display driver (`nvapi64.dll`)
- .NET 8 SDK to build the source

## Build
Open PowerShell in the extracted project folder and run:

```powershell
dotnet publish .\TrueStretchSwitcher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\publish
```

Run `publish\sWitch.exe`.

