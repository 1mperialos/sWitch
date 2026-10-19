$ErrorActionPreference = "Stop"
$logDir = Join-Path $env:LOCALAPPDATA "VALORANT\Saved\Logs"
$logPath = Join-Path $logDir "ShooterGame.log"

Write-Host "TrueStretch Switcher - Valorant match-log probe" -ForegroundColor Cyan
Write-Host "Looking for: $logPath"
if (-not (Test-Path $logPath)) {
    Write-Host ""
    Write-Host "ShooterGame.log not found." -ForegroundColor Yellow
    Write-Host "1) Launch VALORANT, enter a match, then close the game."
    Write-Host "2) Run this probe again."
    Read-Host "Press Enter to exit"
    exit 1
}

$patterns = @(
    'Reconcile',
    'InGame',
    'MainMenu',
    'WaitingToStart',
    'WaitingToPostMatch',
    'MatchState',
    'Map Name:',
    'GameMode',
    'game_mode',
    'MatchID',
    'match'
)

$lines = Get-Content -LiteralPath $logPath -Tail 12000 -ErrorAction Stop
$matches = $lines | Where-Object {
    $line = $_
    $found = $false
    foreach ($pattern in $patterns) {
        if ($line -match $pattern) { $found = $true; break }
    }
    $found
} | Select-Object -Last 100

$outPath = Join-Path $PSScriptRoot "match-log-sample.txt"
if ($matches.Count -eq 0) {
    Write-Host "No matching lines found in the latest 12,000 lines." -ForegroundColor Yellow
    Write-Host "The game's log format may differ; we will inspect another source."
} else {
    $matches | Set-Content -LiteralPath $outPath -Encoding UTF8
    Write-Host ""
    Write-Host "Saved $($matches.Count) filtered lines to:" -ForegroundColor Green
    Write-Host $outPath
    Write-Host "These lines are filtered for match-state keywords, not the full log."
    Write-Host "Open the file and send it here so we can implement match detection against your actual log format."
}
Read-Host "Press Enter to exit"
