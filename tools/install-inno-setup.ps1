param(
    [string]$Version = '6.7.4',
    [string]$TargetDir,
    [switch]$Force
)

# ═══════════════════════════════════════════════════════════════════════════
#  把 Inno Setup 6 静默装进仓库内的 tools\InnoSetup6
#
#  用途：给 tools\make-installer.ps1 提供 ISCC.exe，且不污染系统（不建开始菜单项、
#        不注册卸载项）。装完只需要 ISCC.exe 及其同级文件。
#
#  用法：
#      powershell -ExecutionPolicy Bypass -File tools\install-inno-setup.ps1
#      powershell -ExecutionPolicy Bypass -File tools\install-inno-setup.ps1 -Version 6.4.3 -Force
#
#  许可：Inno Setup 由 Jordan Russell / Martijn Laan 以 Inno Setup License 发布，
#        允许自由使用与分发由其编译的安装程序。该二进制不入库（见 .gitignore）。
# ═══════════════════════════════════════════════════════════════════════════

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
if (-not $TargetDir) { $TargetDir = Join-Path $root 'tools\InnoSetup6' }

$isccInTarget = Join-Path $TargetDir 'ISCC.exe'
if ((Test-Path $isccInTarget) -and -not $Force) {
    Write-Host "已存在：$isccInTarget（要重装加 -Force）" -ForegroundColor Yellow
    exit 0
}

$url = "https://files.jrsoftware.org/is/6/innosetup-$Version.exe"
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) "innosetup-$Version.exe"

Write-Host "=== 下载 Inno Setup $Version ===" -ForegroundColor Cyan
Write-Host "  $url"
$oldProgress = $ProgressPreference
$ProgressPreference = 'SilentlyContinue'
try {
    Invoke-WebRequest -Uri $url -OutFile $tmp -UseBasicParsing -TimeoutSec 300
}
finally {
    $ProgressPreference = $oldProgress
}

$hash = (Get-FileHash $tmp -Algorithm SHA256).Hash
Write-Host ("  已下载：{0}（{1:N1} MB）" -f $tmp, ((Get-Item $tmp).Length / 1MB))
Write-Host "  SHA256：$hash"

Write-Host "=== 静默安装到 $TargetDir ===" -ForegroundColor Cyan
if (Test-Path $TargetDir) { Remove-Item $TargetDir -Recurse -Force }

# /VERYSILENT /SUPPRESSMSGBOXES /NORESTART 走它自带的安装器；
# /DIR 与 /NOICONS 控制落点，不建开始菜单项。
$installArgs = @(
    '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-',
    "/DIR=$TargetDir",
    '/NOICONS',
    '/COMPONENTS='
)
$proc = Start-Process -FilePath $tmp -ArgumentList $installArgs -Wait -PassThru
if ($proc.ExitCode -ne 0) { throw "安装器返回 $($proc.ExitCode)" }

Remove-Item $tmp -Force -ErrorAction SilentlyContinue

if (-not (Test-Path $isccInTarget)) { throw "安装后仍找不到 ISCC.exe：$isccInTarget" }

Write-Host ''
Write-Host '完成。' -ForegroundColor Green
Write-Host "  ISCC：$isccInTarget"
Write-Host '  现在可以跑：powershell -ExecutionPolicy Bypass -File tools\make-installer.ps1'
