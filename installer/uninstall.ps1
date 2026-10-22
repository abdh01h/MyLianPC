<#
.SYNOPSIS
  Removes a MyLianPC installation made with install.ps1 and restores vendor software. Run elevated:
    powershell -ExecutionPolicy Bypass -File .\uninstall.ps1 [-KeepData]
#>
param([switch]$KeepData, [string]$InstallDir = "$env:ProgramFiles\MyLianPC")

$ErrorActionPreference = 'Continue'
$svc = Join-Path $InstallDir 'MyLianPC.Service.exe'
sc.exe stop MyLianPCService | Out-Null
Start-Sleep -Seconds 3
Get-Process MyLianPC, MyLianPC.Service -ErrorAction SilentlyContinue | Stop-Process -Force
if (Test-Path $svc) { & $svc --vendor-restore | Out-Null }
sc.exe delete MyLianPCService | Out-Null
Remove-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -Name MyLianPC -ErrorAction SilentlyContinue
Remove-Item "$env:ProgramData\Microsoft\Windows\Start Menu\Programs\MyLianPC.lnk" -ErrorAction SilentlyContinue
if ($PSScriptRoot -ne $InstallDir) { Remove-Item $InstallDir -Recurse -Force -ErrorAction SilentlyContinue }
if (-not $KeepData) { Remove-Item "$env:ProgramData\MyLianPC" -Recurse -Force -ErrorAction SilentlyContinue }
# Start-up entries: the machine-wide one from the installer and any per-account one made in Settings.
Remove-Item "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run" -Name MyLianPC -ErrorAction SilentlyContinue
Write-Host 'MyLianPC removed. Vendor software was restored to its previous state. PawnIO is left installed (remove it from Apps & features if you no longer need it).'
