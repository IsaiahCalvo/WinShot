# Registers a per-user logon Scheduled Task to autostart the app, and removes any
# legacy HKCU "Run" value so the two mechanisms can't both fire and double-launch.
#
# Windows 11 throttles/delays HKCU "Run" startup apps (they often don't launch
# promptly, or at all, after a reboot), so the durable autostart is a logon task.
# Called by the Inno installer's [Run] step with the installed exe path. Wrapped in
# try/catch so a task-registration hiccup never fails the install.
#
# Settings mirror the live "Clip Autostart" / "WinShot Autostart" tasks:
#   AtLogOn, -User $env:USERNAME, Delay PT3S, Interactive, Limited,
#   ExecutionTimeLimit 0, MultipleInstances IgnoreNew.

param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$TaskName,
    [Parameter(Mandatory = $true)][string]$RunValueName
)

try {
    # Drop the legacy Run-key entry the old installer created (if present).
    Remove-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" `
        -Name $RunValueName -ErrorAction SilentlyContinue

    $workingDir = Split-Path -Parent $Exe
    $action    = New-ScheduledTaskAction -Execute $Exe -WorkingDirectory $workingDir
    $trigger   = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
    $trigger.Delay = "PT3S"
    $settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
        -StartWhenAvailable -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
    $principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Limited
    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger `
        -Settings $settings -Principal $principal -Force | Out-Null

    Write-Output "Registered logon task '$TaskName' -> $Exe"
}
catch {
    # Never fail the install over autostart; the app still installs and runs.
    Write-Warning "Autostart task registration failed: $($_.Exception.Message)"
}
