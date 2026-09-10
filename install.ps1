[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$buildScript = Join-Path $projectRoot 'build.ps1'
$installedExe = Join-Path $projectRoot 'CodexDashboard.exe'
$legacyInstallDir = Join-Path $env:LOCALAPPDATA 'CodexQuotaPet'
$legacyInstalledExe = Join-Path $legacyInstallDir 'CodexDashboard.exe'
$olderLegacyInstalledExe = Join-Path $legacyInstallDir 'CodexQuotaPet.exe'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$desktopShortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Codex Usage Dashboard.lnk'

$dashboardProcesses = @(Get-Process -Name 'CodexQuotaPet','CodexDashboard' -ErrorAction SilentlyContinue)
$legacyProcessDirectories = @($dashboardProcesses |
    Where-Object { $_.Path -like '*\Packages\OpenAI.Codex_*\LocalCache\Local\CodexQuotaPet\CodexDashboard.exe' } |
    ForEach-Object { Split-Path -Parent $_.Path } |
    Select-Object -Unique)

$dashboardProcesses |
    Where-Object {
        $_.Path -eq $installedExe -or
        $_.Path -eq $legacyInstalledExe -or
        $_.Path -eq $olderLegacyInstalledExe -or
        $_.Path -like '*\Packages\OpenAI.Codex_*\LocalCache\Local\CodexQuotaPet\CodexDashboard.exe'
    } |
    Stop-Process -Force

& $buildScript

$positionTarget = Join-Path $projectRoot 'window-position.txt'
$legacyDirectories = @(@($legacyInstallDir) + $legacyProcessDirectories) | Select-Object -Unique
foreach ($legacyDirectory in $legacyDirectories) {
    if ([string]::IsNullOrWhiteSpace($legacyDirectory)) { continue }
    $legacyPosition = Join-Path $legacyDirectory 'window-position.txt'
    if (-not (Test-Path -LiteralPath $positionTarget) -and (Test-Path -LiteralPath $legacyPosition)) {
        Copy-Item -LiteralPath $legacyPosition -Destination $positionTarget -Force
    }
    $resolvedLegacy = [System.IO.Path]::GetFullPath($legacyDirectory)
    $isExpectedLeaf = (Split-Path -Leaf $resolvedLegacy) -eq 'CodexQuotaPet'
    $isKnownLocation = $resolvedLegacy -eq [System.IO.Path]::GetFullPath($legacyInstallDir) -or
        $resolvedLegacy -like '*\Packages\OpenAI.Codex_*\LocalCache\Local\CodexQuotaPet'
    if ($isExpectedLeaf -and $isKnownLocation -and (Test-Path -LiteralPath $resolvedLegacy)) {
        Remove-Item -LiteralPath $resolvedLegacy -Recurse -Force
    }
}

New-Item -Path $runKey -Force | Out-Null
Remove-ItemProperty -Path $runKey -Name 'CodexQuotaPet' -ErrorAction SilentlyContinue
New-ItemProperty -Path $runKey -Name 'CodexUsageDashboard' -Value ('"' + $installedExe + '" --background') -PropertyType String -Force | Out-Null

$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($desktopShortcut)
$shortcut.TargetPath = $installedExe
$shortcut.WorkingDirectory = $projectRoot
$shortcut.Description = 'Open the Codex usage dashboard'
$shortcut.Save()

Stop-ScheduledTask -TaskName 'CodexUsageDashboardMonitor' -ErrorAction SilentlyContinue
Unregister-ScheduledTask -TaskName 'CodexUsageDashboardMonitor' -Confirm:$false -ErrorAction SilentlyContinue

Add-Type @'
using System;
using System.IO;
using System.Runtime.InteropServices;
public static class CodexDashboardDetachedStart {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct StartupInfo {
        public int cb; public string reserved; public string desktop; public string title;
        public int x; public int y; public int xSize; public int ySize;
        public int xChars; public int yChars; public int fill; public int flags;
        public short show; public short reservedSize; public IntPtr reserved2;
        public IntPtr input; public IntPtr output; public IntPtr error;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessInfo {
        public IntPtr process; public IntPtr thread; public int processId; public int threadId;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(string application, string commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment,
        string currentDirectory, ref StartupInfo startupInfo, out ProcessInfo processInfo);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
    public static int Start(string executable) {
        StartupInfo startup = new StartupInfo();
        startup.cb = Marshal.SizeOf(startup);
        ProcessInfo process;
        const uint DetachedProcess = 0x00000008;
        const uint BreakawayFromJob = 0x01000000;
        bool started = CreateProcess(executable, "\"" + executable + "\" --background",
            IntPtr.Zero, IntPtr.Zero, false, DetachedProcess | BreakawayFromJob, IntPtr.Zero,
            Path.GetDirectoryName(executable), ref startup, out process);
        if (!started) return -Marshal.GetLastWin32Error();
        CloseHandle(process.thread);
        CloseHandle(process.process);
        return process.processId;
    }
}
'@

$detachedResult = [CodexDashboardDetachedStart]::Start($installedExe)
if ($detachedResult -lt 0) {
    Write-Warning "Detached startup failed with Win32 error $(-$detachedResult); using normal startup."
    Start-Process -FilePath $installedExe -ArgumentList '--background'
}
Write-Host "Portable executable: $installedExe"
Write-Host "Manual launcher: $desktopShortcut"
Write-Host 'The detached monitor is running and will also start at sign-in.'
