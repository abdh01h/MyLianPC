<#
.SYNOPSIS
  Builds MyLianPC into artifacts\MyLianPC (self-contained, no .NET install needed), a zip, and - if Inno Setup 6
  is installed - artifacts\MyLianPC-Setup-<version>.exe.
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File build\publish.ps1
#>
param([string]$Configuration = 'Release', [switch]$SkipTests)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'artifacts\MyLianPC'
$version = ([xml](Get-Content (Join-Path $root 'src\MyLianPC.App\MyLianPC.App.csproj'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe' }
if (-not (Test-Path $dotnet)) { throw 'The .NET 10 SDK was not found. Install it from https://dot.net.' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_ROOT = Split-Path $dotnet -Parent

if (-not $SkipTests) {
    & $dotnet test (Join-Path $root 'tests\MyLianPC.Core.Tests') -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw 'Unit tests failed.' }
}

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
foreach ($project in 'src\MyLianPC.Service\MyLianPC.Service.csproj', 'src\MyLianPC.App\MyLianPC.App.csproj') {
    & $dotnet publish (Join-Path $root $project) -c $Configuration -r win-x64 --self-contained true -o $out `
        -p:PublishSingleFile=false -p:DebugType=none -p:SatelliteResourceLanguages=en
    if ($LASTEXITCODE -ne 0) { throw "Publishing $project failed." }
}
foreach ($doc in 'README.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md') { Copy-Item (Join-Path $root $doc) $out }
Copy-Item (Join-Path $root 'docs') (Join-Path $out 'docs') -Recurse
Copy-Item (Join-Path $root 'installer\install.ps1'), (Join-Path $root 'installer\uninstall.ps1') $out

$zip = Join-Path $root "artifacts\MyLianPC-$version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path "$out\*" -DestinationPath $zip
Write-Host "Package: $out"
Write-Host "Zip:     $zip"

$iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if ($iscc) {
    & $iscc "/DAppVersion=$version" "/DSourceDir=$out" "/O$(Join-Path $root 'artifacts')" (Join-Path $root 'installer\MyLianPC.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed.' }
} else {
    Write-Warning 'Inno Setup 6 not found: skipped the setup .exe (winget install JRSoftware.InnoSetup). The zip with install.ps1 works without it.'
}
