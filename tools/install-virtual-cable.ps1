# Install VB-Audio Virtual Cable (VBCABLE) - a silent virtual output endpoint
# used as the transport for VideoTurner's virtual channel mode.
#
#   Usage:   powershell -ExecutionPolicy Bypass -File tools\install-virtual-cable.ps1
#            powershell -ExecutionPolicy Bypass -File tools\install-virtual-cable.ps1 -Silent
#
# The driver package is downloaded from the official VB-Audio server and installed
# with VB-Audio's own setup program. Nothing is bundled with this repository.
# Must stay ASCII-only: this file may be read by powershell.exe on a GBK system.

param(
    [switch]$Silent,
    [string]$WorkDir = "$env:TEMP\vbcable"
)

$ErrorActionPreference = 'Stop'

$zipUrl  = 'https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip'
$zipPath = Join-Path $WorkDir 'VBCABLE_Driver_Pack45.zip'
$packDir = Join-Path $WorkDir 'pack'

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    return (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Admin)) {
    Write-Host '需要管理员权限，正在请求提权...'
    $argsList = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    if ($Silent) { $argsList += ' -Silent' }
    Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $argsList
    exit 0
}

New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null

if (-not (Test-Path $zipPath)) {
    Write-Host "下载 $zipUrl"
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $zipUrl -OutFile $zipPath -UseBasicParsing
}

if (-not (Test-Path (Join-Path $packDir 'VBCABLE_Setup_x64.exe'))) {
    Write-Host '解压...'
    if (Test-Path $packDir) { Remove-Item $packDir -Recurse -Force }
    Expand-Archive -Path $zipPath -DestinationPath $packDir -Force
}

# 64-bit OS check (the x64 setup refuses to run on 32-bit anyway)
$is64 = [Environment]::Is64BitOperatingSystem
$setup = if ($is64) { 'VBCABLE_Setup_x64.exe' } else { 'VBCABLE_Setup.exe' }
$setupPath = Join-Path $packDir $setup

if (-not (Test-Path $setupPath)) {
    Write-Host "找不到 $setupPath"
    exit 1
}

if ($Silent) {
    Write-Host "静默安装：$setup -i"
    $p = Start-Process -FilePath $setupPath -ArgumentList '-i' -WorkingDirectory $packDir -Wait -PassThru
    Write-Host "安装程序退出码：$($p.ExitCode)"
} else {
    Write-Host "打开安装界面：请在窗口里点 'Install Driver'，装完重启一次本程序。"
    Start-Process -FilePath $setupPath -WorkingDirectory $packDir
}

Write-Host ''
Write-Host '装好之后：'
Write-Host '  1) 重新打开 VideoTurner（或设置页 -> 虚拟通道 -> 重新扫描）'
Write-Host '  2) 应该能看到 "CABLE Input (VB-Audio Virtual Cable)"'
Write-Host '  3) 选中它 -> 打开虚拟通道开关 -> 纳入正在出声的程序 -> 重启那个程序一次'
Write-Host ''
Write-Host '卸载：再次运行本脚本所在目录的 VBCABLE_Setup_x64.exe，点 Remove Driver。'
