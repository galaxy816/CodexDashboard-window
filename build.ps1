[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$sourcePath = Join-Path $projectRoot 'src\CodexQuotaPet.cs'
$xamlPath = Join-Path $projectRoot 'src\PetWindow.xaml'
$manifestPath = Join-Path $projectRoot 'app.manifest'
$outputExe = Join-Path $projectRoot 'CodexDashboard.exe'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$assemblyRoot = Join-Path $env:WINDIR 'Microsoft.NET\assembly'

if (-not (Test-Path -LiteralPath $compiler)) {
    throw 'Windows C# compiler was not found.'
}

function Find-GacAssembly([string] $fileName) {
    $match = Get-ChildItem -Path $assemblyRoot -Filter $fileName -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\v4\.0_' } |
        Select-Object -First 1
    if ($null -eq $match) { throw "Required assembly was not found: $fileName" }
    return $match.FullName
}

$references = @(
    (Find-GacAssembly 'WindowsBase.dll'),
    (Find-GacAssembly 'PresentationCore.dll'),
    (Find-GacAssembly 'PresentationFramework.dll'),
    (Find-GacAssembly 'System.Xaml.dll')
)

$compilerArgs = @(
    '/nologo',
    '/target:winexe',
    '/optimize+',
    '/platform:x64',
    ('/out:' + $outputExe),
    ('/win32manifest:' + $manifestPath),
    ('/resource:' + $xamlPath + ',CodexQuotaPet.PetWindow.xaml')
)
$compilerArgs += $references | ForEach-Object { '/reference:' + $_ }
$compilerArgs += $sourcePath

& $compiler $compilerArgs

if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE"
}

Write-Host "Build completed: $outputExe"
