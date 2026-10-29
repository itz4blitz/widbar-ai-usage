# One-time elevated setup for WidBar CPU temp + AI Usage widget development.
$ErrorActionPreference = 'Continue'
$log = Join-Path $PSScriptRoot 'elevated-setup.log'
Start-Transcript -Path $log -Force | Out-Null

# 1. Developer Mode (needed to register an unsigned widget package)
New-Item -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' -Force | Out-Null
Set-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' -Name AllowDevelopmentWithoutDevLicense -Value 1 -Type DWord
Write-Host 'Developer Mode: on'

# 2. .NET 10 SDK
if (-not (& dotnet --list-sdks 2>$null | Select-String '^10\.')) {
    winget install --id Microsoft.DotNet.SDK.10 -e --silent --accept-source-agreements --accept-package-agreements --disable-interactivity
}

# 3. Retire OpenHardwareMonitor (no Ryzen 5000 CPU sensor support)
Get-ScheduledTask | Where-Object { $_.TaskName -match 'OpenHardwareMonitor' -or ($_.Actions | Where-Object { $_.Execute -match 'OpenHardwareMonitor' }) } |
    ForEach-Object { Disable-ScheduledTask -TaskName $_.TaskName -TaskPath $_.TaskPath | Out-Null; Write-Host "Disabled task $($_.TaskPath)$($_.TaskName)" }
foreach ($k in 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run', 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run') {
    $p = Get-ItemProperty $k -ErrorAction SilentlyContinue
    if ($p) { $p.PSObject.Properties | Where-Object { "$($_.Value)" -match 'OpenHardwareMonitor' } | ForEach-Object { Remove-ItemProperty $k -Name $_.Name; Write-Host "Removed Run entry $($_.Name)" } }
}
Get-Process OpenHardwareMonitor -ErrorAction SilentlyContinue | Stop-Process -Force
Write-Host 'OpenHardwareMonitor stopped'

# 4. LibreHardwareMonitor: elevated at logon, minimized to tray
$lhm = "$env:LOCALAPPDATA\Microsoft\WinGet\Packages\LibreHardwareMonitor.LibreHardwareMonitor_Microsoft.Winget.Source_8wekyb3d8bbwe\LibreHardwareMonitor.exe"
$cfg = [IO.Path]::ChangeExtension($lhm, '.config')
if (-not (Test-Path $cfg)) {
@'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <appSettings>
    <add key="startMinMenuItem" value="true" />
    <add key="minTrayMenuItem" value="true" />
    <add key="minCloseMenuItem" value="true" />
  </appSettings>
</configuration>
'@ | Set-Content $cfg -Encoding UTF8
}
$action    = New-ScheduledTaskAction -Execute $lhm
$trigger   = New-ScheduledTaskTrigger -AtLogOn -User "$env:USERDOMAIN\$env:USERNAME"
$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Highest
$settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero)
Register-ScheduledTask -TaskName 'LibreHardwareMonitor' -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
Start-ScheduledTask -TaskName 'LibreHardwareMonitor'
Write-Host 'LibreHardwareMonitor task registered and started'

Stop-Transcript | Out-Null
'done' | Set-Content (Join-Path $PSScriptRoot 'elevated-setup.done')
