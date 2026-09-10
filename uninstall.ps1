[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$installedExe = Join-Path $projectRoot 'CodexDashboard.exe'
$legacyInstallDir = Join-Path $env:LOCALAPPDATA 'CodexQuotaPet'
$legacyInstalledExe = Join-Path $legacyInstallDir 'CodexDashboard.exe'
$olderLegacyInstalledExe = Join-Path $legacyInstallDir 'CodexQuotaPet.exe'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$taskName = 'CodexUsageDashboardMonitor'
$desktopShortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Codex Usage Dashboard.lnk'

Remove-ItemProperty -Path $runKey -Name 'CodexQuotaPet' -ErrorAction SilentlyContinue
Remove-ItemProperty -Path $runKey -Name 'CodexUsageDashboard' -ErrorAction SilentlyContinue
Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue

Get-Process -Name 'CodexQuotaPet','CodexDashboard' -ErrorAction SilentlyContinue |
    Where-Object {
        $_.Path -eq $installedExe -or
        $_.Path -eq $legacyInstalledExe -or
        $_.Path -eq $olderLegacyInstalledExe -or
        $_.Path -like '*\Packages\OpenAI.Codex_*\LocalCache\Local\CodexQuotaPet\CodexDashboard.exe'
    } |
    Stop-Process -Force

if (Test-Path -LiteralPath $desktopShortcut) { Remove-Item -LiteralPath $desktopShortcut -Force }

Write-Host 'Startup integration was removed. All project files were kept.'
