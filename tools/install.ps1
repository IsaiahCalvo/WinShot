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
$InstallDir = [IO.Path]::GetFullPath($InstallDir)
$stage = Join-Path $repo ('artifacts\install-stage-' + [Guid]::NewGuid().ToString('N'))

Write-Host "Publishing self-contained build before stopping WinShot ..."
dotnet publish $proj -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -p:PublishSingleFile=false -o $stage --nologo
if ($LASTEXITCODE -ne 0) { throw "Publish failed with exit code $LASTEXITCODE; the installed app was not changed." }
if (-not (Test-Path -LiteralPath (Join-Path $stage 'WinShot.exe'))) { throw 'Publish did not produce WinShot.exe.' }

Write-Host "Stopping the installed WinShot ..."
Get-Process WinShot -ErrorAction SilentlyContinue | Where-Object {
    $_.Path -eq (Join-Path $InstallDir 'WinShot.exe')
} | ForEach-Object {
    Stop-Process -Id $_.Id -Force
    if (-not $_.WaitForExit(10000)) { throw "WinShot process $($_.Id) did not exit." }
}

New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
$backup = Join-Path $repo ('artifacts\install-backup-' + [Guid]::NewGuid().ToString('N'))
Get-ChildItem -LiteralPath $stage -File -Recurse | ForEach-Object {
    $relative = [IO.Path]::GetRelativePath($stage, $_.FullName)
    $target = Join-Path $InstallDir $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
    try { Copy-Item -LiteralPath $_.FullName -Destination $target -Force }
    catch [IO.IOException] {
        # Windows can retain a file mapping briefly after the process exits.
        # Keep the old file in the repo's build artifacts before replacing it.
        if (-not (Test-Path -LiteralPath $target)) { throw }
        $saved = Join-Path $backup $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $saved) -Force | Out-Null
        Move-Item -LiteralPath $target -Destination $saved
        Copy-Item -LiteralPath $_.FullName -Destination $target
    }
    if ((Get-FileHash -LiteralPath $_.FullName).Hash -ne (Get-FileHash -LiteralPath $target).Hash) {
        throw "Installed file did not match the staged build: $relative"
    }
}
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

if (-not $NoLaunch) { Start-Process -FilePath $exe -WindowStyle Hidden }
Write-Host "Installed WinShot -> $exe"
