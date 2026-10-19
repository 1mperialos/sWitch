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

## Create a custom resolution
1. Open the tray icon context menu → **Resolution settings…**.
2. Enter width and height.
3. Click **Test & create**. NVIDIA may briefly blank/change the display while testing the mode.
4. If NVAPI reports success, close/reopen the settings window if the new mode does not immediately appear in the lists.
5. Select the desired Native/lobby and Stretched/gameplay resolutions, then click **Save selections**.

The custom timing is calculated using the current active refresh rate (or the highest enumerated refresh rate for the current resolution if Windows reports 0). A mode can be rejected by NVIDIA if the monitor, cable, driver, or link bandwidth does not support that timing. This source was not compiled or tested on the user's Windows machine in this environment; test cautiously and send the full PowerShell output if compilation fails.

## Safety note
The NVIDIA custom mode trial can cause a brief black screen. If a custom mode is rejected, the app displays the NVAPI status and does not save it. Do not create extreme resolutions or timings.


NVAPI custom-mode creation now tries three documented parameter combinations per refresh rate: automatic timing/default format, automatic timing with 32-bpp A8R8G8B8, and CVT reduced-blanking timing with 32-bpp A8R8G8B8. This is experimental and requires testing on the target NVIDIA driver.


Audit update: this build asserts the expected NVAPI structure sizes at runtime and tries CVT-RB (enum value 6) with 32-bpp A8R8G8B8 first, following NVIDIA's CustomTiming sample, then documented fallback combinations. This is still experimental and requires testing on the target Windows/NVIDIA system.
\n\nThis build adds experimental NVAPI custom-mode enumeration and deletion diagnostics. It has not been tested against a live NVIDIA driver. Use Scan driver modes first; delete only a mode that is not currently active. Runtime assertions validate the managed struct sizes against the audited NVAPI layout.\n

Save timing fix: the custom-mode creation flow now waits five seconds after a successful TryCustomDisplay before calling SaveCustomDisplay, matching NVIDIA's CustomTiming sample. This is a targeted diagnostic fix and still needs testing on the target NVIDIA driver.
\n\n## Log menu items\nThe tray menu includes Open Valorant log, Open Valorant log folder, and Copy Valorant log path. The log path is `%LOCALAPPDATA%\\VALORANT\\Saved\\Logs\\ShooterGame.log`.\n

## Network-loss native fallback (experimental)
While already in a match, the app switches to native resolution when the log reports `CreateSavedMove: Hit limit of 96 saved moves`, `Connection TIMED OUT`, `Network Failure: GameNetDriver[ConnectionTimeout]`, or `BroadcastNetworkFailure: FailureType = ConnectionTimeout`. The first signal is an early warning that appeared repeatedly about 12 seconds before timeout in the supplied log; it can also occur during severe latency, so this is intentionally fail-safe and may switch native during a bad-lag spike. Once a later `UpdateMapReady` line has all three readiness flags TRUE, the app returns to stretched. Test in a real match; not hardware-verified here.

## Restore native resolution when VALORANT closes
The app polls for the `VALORANT-Win64-Shipping` game process. If it observes the game running and later detects that process has exited (normal close, Alt+F4, or crash), it clears the match/recovery state and restores the configured native resolution. This follows the existing **Auto-switch on match start/end** setting; when that option is disabled, automatic switching remains disabled.


## Final branding
- App display name: `sWitch`
- Executable name after publishing: `sWitch.exe`
- App icon: `sWitch.ico` (pixel-art blue magic sprite)
- The startup registry value and LocalAppData settings folder retain their existing internal names so existing installs/settings remain compatible.


Hotkey customization and tray icon fix: open Resolution settings, click Toggle shortcut, press the desired key combination, then Save selections. The tray uses the embedded sWitch.ico icon.
