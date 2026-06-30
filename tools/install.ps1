<#
.SYNOPSIS
  Installs WinShot the "professional" way: publishes a self-contained build to a
  stable per-user location, creates Desktop + Start Menu shortcuts pointing there,
  and launches it. Autostart (the HKCU Run key) is owned by the app itself and is
  written on launch when "Launch at startup" is enabled in Settings.

.NOTES
  Because the exe lives at a fixed install path, you can move/delete the shortcuts
  freely and rebuild the source repo without ever breaking the installed app.
  Re-run this script to update the installed copy after code changes.
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\WinShot'),
    [switch]$NoLaunch
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $repo 'src\WinShot\WinShot.csproj'

Write-Host "Stopping any running WinShot..."
Get-Process WinShot -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800

Write-Host "Publishing self-contained build to $InstallDir ..."
dotnet publish $proj -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -p:PublishSingleFile=false -o $InstallDir --nologo
$exe = Join-Path $InstallDir 'WinShot.exe'
if (-not (Test-Path $exe)) { throw "Publish failed: $exe not found." }

function Set-Shortcut([string]$Path) {
    $dir = Split-Path -Parent $Path
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $ws = New-Object -ComObject WScript.Shell
    $sc = $ws.CreateShortcut($Path)
    $sc.TargetPath = $exe
    $sc.WorkingDirectory = $InstallDir
    $sc.IconLocation = "$exe,0"
    $sc.Description = 'WinShot'
    $sc.Save()
}

Set-Shortcut (Join-Path ([Environment]::GetFolderPath('Desktop')) 'WinShot.lnk')
Set-Shortcut (Join-Path ([Environment]::GetFolderPath('Programs')) 'WinShot.lnk')
Start-Process ie4uinit.exe -ArgumentList '-show' -WindowStyle Hidden  # refresh icon cache

if (-not $NoLaunch) { Start-Process $exe }
Write-Host "Installed WinShot -> $exe"
