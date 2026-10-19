# sWitch v1.0.0

A resolution switcher for Valorant, featuring automatic resolution switching and a customizable hotkey.

### About

When the match is ready, sWitch automatically switches to your preferred resolution. Once the match ends, it switches back to your native resolution.

There are also two extra situations it can handle:

- Game crash: If Valorant crashes and closes, sWitch switches back to your native resolution after a few seconds.
- Internet connection loss: If your internet connection drops, sWitch switches back to your native resolution. Once your connection is restored, it automatically switches again when the match is ready.

### Downloads

- sWitch.7z - Full build archive.
- sWitch-Source.zip - Source code and build files.

### Requirements

You'll need the .NET 8 SDK to build the source yourself.

### Build from source

Follow the instructions in the included README.

Open PowerShell in the extracted project folder and run:

```powershell
dotnet publish .\TrueStretchSwitcher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\publish
```

Once the build is complete, run `publish\sWitch.exe`
