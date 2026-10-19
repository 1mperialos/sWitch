$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host " .NET 8 SDK is required. Install it from https://dotnet.microsoft.com/download/dotnet/8.0" -ForegroundColor Yellow
    exit 1
}

dotnet publish .\TrueStretchSwitcher.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -o .\publish

Write-Host ""
Write-Host "Build complete: $PSScriptRoot\publish\sWitch.exe" -ForegroundColor Green
Write-Host "Run the EXE. It will add itself to your current user's Windows startup."
