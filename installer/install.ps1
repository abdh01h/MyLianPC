<#
.SYNOPSIS
  Installs MyLianPC from an unpacked release folder (alternative to the setup .exe). Run from an elevated PowerShell:
    powershell -ExecutionPolicy Bypass -File .\install.ps1 [-NoTakeover] [-NoTrayApp]
#>
param([switch]$NoTakeover, [switch]$NoTrayApp, [string]$InstallDir = "$env:ProgramFiles\MyLianPC")

$ErrorActionPreference = 'Stop'
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated (administrator) PowerShell.'
}
$src = $PSScriptRoot
if (-not (Test-Path (Join-Path $src 'MyLianPC.Service.exe'))) { throw "MyLianPC.Service.exe not found next to install.ps1 ($src)." }

Write-Host 'Stopping a previous installation (if any)...'
Get-Process MyLianPC -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
if (Get-Service MyLianPCService -ErrorAction SilentlyContinue) { Stop-Service MyLianPCService -Force -ErrorAction SilentlyContinue }
# Files stay locked until both processes have exited; the service can take several seconds when the cooler is not answering.
Get-Process MyLianPC, MyLianPC.Service -ErrorAction SilentlyContinue | Wait-Process -Timeout 30 -ErrorAction SilentlyContinue

# The app used to be called MyPcRgb: remove that service and folder so two services never drive the same hardware.
Get-Process MyPcRgb -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
if (Get-Service MyPcRgbService -ErrorAction SilentlyContinue) {
    Write-Host 'Removing the previous MyPcRgb service...'
    Stop-Service MyPcRgbService -Force -ErrorAction SilentlyContinue
    sc.exe delete MyPcRgbService | Out-Null
}
Remove-Item "$env:ProgramFiles\MyPcRgb" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item "$env:ProgramData\Microsoft\Windows\Start Menu\Programs\MyPcRgb.lnk" -Force -ErrorAction SilentlyContinue
Remove-Item "$env:ProgramData\Microsoft\Windows\Start Menu\Programs\MyPcRgb" -Recurse -Force -ErrorAction SilentlyContinue
Remove-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -Name MyPcRgb -ErrorAction SilentlyContinue

if ($src -ne $InstallDir) {
    New-Item -ItemType Directory -Force $InstallDir | Out-Null
    Copy-Item "$src\*" $InstallDir -Recurse -Force
}
$svc = Join-Path $InstallDir 'MyLianPC.Service.exe'

Write-Host 'Registering the service...'
if (Get-Service MyLianPCService -ErrorAction SilentlyContinue) {
    sc.exe config MyLianPCService binPath= "`"$svc`"" start= auto | Out-Null
} else {
    sc.exe create MyLianPCService binPath= "`"$svc`"" start= auto DisplayName= "MyLianPC Hardware Service" | Out-Null
}
sc.exe description MyLianPCService "Controls RGB lighting, the cooler LCD and fan curves for MyLianPC, independent of which Windows account is signed in." | Out-Null
sc.exe failure MyLianPCService reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null

if (-not (Test-Path "$env:ProgramFiles\PawnIO\PawnIOLib.dll")) {
    Write-Host 'Installing the PawnIO driver (needed for CPU temperatures, motherboard fans and RAM lighting)...'
    winget install --id namazso.PawnIO -e --silent --accept-package-agreements --accept-source-agreements
}

if (-not $NoTakeover) {
    Write-Host 'Disabling conflicting vendor software (reversible with uninstall.ps1 or Settings -> Restore vendor software)...'
    & $svc --vendor-takeover | Out-Null
}

if (-not $NoTrayApp) {
    Set-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -Name MyLianPC -Value "`"$InstallDir\MyLianPC.exe`" --tray"
}

$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut("$env:ProgramData\Microsoft\Windows\Start Menu\Programs\MyLianPC.lnk")
$lnk.TargetPath = Join-Path $InstallDir 'MyLianPC.exe'
$lnk.Save()

Write-Host 'Starting the service...'
Start-Service MyLianPCService
Get-Service MyLianPCService | Format-Table Status, Name, DisplayName -AutoSize
Write-Host "Installed to $InstallDir. Open 'MyLianPC' from the Start menu."
