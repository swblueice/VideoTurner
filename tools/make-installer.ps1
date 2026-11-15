param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$Iscc,
    [switch]$SkipPublish,
    [switch]$ToDesktop
)

# ═══════════════════════════════════════════════════════════════════════════
#  VideoTurner 安装包一键构建
#
#  用法（在仓库根目录执行）：
#      powershell -ExecutionPolicy Bypass -File tools\make-installer.ps1
#      powershell -ExecutionPolicy Bypass -File tools\make-installer.ps1 -SkipPublish   # 只重新打包
#      powershell -ExecutionPolicy Bypass -File tools\make-installer.ps1 -ToDesktop     # 顺手复制一份到桌面
#      powershell -ExecutionPolicy Bypass -File tools\make-installer.ps1 -Iscc "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
#
#  产物：
#      dist\installer\VideoTurner-1.0.0-win-x64-setup.exe（+ 同名 .sha256.txt）
#      -ToDesktop 时再复制一份到桌面
#
#  前置：
#      * .NET SDK 8 或更高（dotnet publish 用）
#      * Inno Setup 6 的命令行编译器 ISCC.exe。查找顺序：
#          1) -Iscc 显式指定
#          2) PATH 里的 iscc
#          3) %ProgramFiles(x86)%\Inno Setup 6\ISCC.exe
#          4) %ProgramFiles%\Inno Setup 6\ISCC.exe
#          5) 仓库内 tools\InnoSetup6\ISCC.exe（tools\install-inno-setup.ps1 会装到这里）
#
#  注意：本脚本必须存成 UTF-8 带 BOM（PS 5.1 否则按 GBK 解析，中文全乱、直接报语法错）。
#        改过 .iss 之后也别忘了 BOM —— 脚本里的 Ensure-Utf8Bom 会自动补。
# ═══════════════════════════════════════════════════════════════════════════

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\VideoTurner\VideoTurner.csproj'
$publishDir = Join-Path $root "dist\publish\$Runtime"
$iss = Join-Path $root 'installer\VideoTurner.iss'

function Resolve-Iscc([string]$Explicit) {
    if ($Explicit) {
        if (Test-Path $Explicit) { return (Resolve-Path $Explicit).Path }
        throw "指定的 ISCC.exe 不存在：$Explicit"
    }
    $candidates = @()
    $cmd = Get-Command iscc.exe -ErrorAction SilentlyContinue
    if ($cmd) { $candidates += $cmd.Source }
    if (${env:ProgramFiles(x86)}) { $candidates += (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe') }
    if ($env:ProgramFiles) { $candidates += (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe') }
    $candidates += (Join-Path $root 'tools\InnoSetup6\ISCC.exe')
    foreach ($c in $candidates) {
        if ($c -and (Test-Path $c)) { return $c }
    }
    return $null
}

$iscc = Resolve-Iscc $Iscc

function Ensure-Utf8Bom([string]$Path) {
    if (-not (Test-Path $Path)) { return }
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) { return }
    [System.IO.File]::WriteAllBytes($Path, ([byte[]](0xEF, 0xBB, 0xBF) + $bytes))
    Write-Host "  [已补 UTF-8 BOM] $Path"
}

Write-Host '=== VideoTurner 安装包构建 ===' -ForegroundColor Cyan

if (-not $iscc) {
    throw @"
找不到 Inno Setup 编译器（ISCC.exe）。任选一种：
  1) 装 Inno Setup 6：https://jrsoftware.org/isdl.php
  2) 跑 tools\install-inno-setup.ps1，把它静默装进仓库内的 tools\InnoSetup6
  3) 用 -Iscc 显式指定路径
"@
}
Write-Host "  ISCC     : $iscc"

# 0) 脚本、安装脚本都要 BOM
Ensure-Utf8Bom $PSCommandPath
Ensure-Utf8Bom $iss
Ensure-Utf8Bom (Join-Path $root 'installer\ChineseSimplified.isl')

# 1) 先结束正在运行的实例：exe / dll 被占用时 dotnet publish 会一直重试然后失败
$running = @(Get-Process -Name VideoTurner -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    Write-Host ("[1/4] 结束正在运行的 VideoTurner（PID {0}）..." -f (($running | ForEach-Object { $_.Id }) -join ', '))
    $running | ForEach-Object { try { $_.Kill(); $_.WaitForExit(3000) } catch { } }
    Start-Sleep -Milliseconds 800
}
else {
    Write-Host '[1/4] 没有正在运行的实例。'
}

# 2) 自包含发布（目标机器不需要装 .NET 8 桌面运行时）
if ($SkipPublish) {
    Write-Host '[2/4] 跳过发布（-SkipPublish），直接复用现成的 publish 目录。'
}
else {
    Write-Host "[2/4] dotnet publish -c $Configuration -r $Runtime --self-contained true ..."
    # SatelliteResourceLanguages=zh-Hans：只留中文资源，去掉 13 个语言目录（约省 14 MB）
    & dotnet publish $project -c $Configuration -r $Runtime --self-contained true `
        -p:SatelliteResourceLanguages=zh-Hans -o $publishDir --nologo -v m
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（exit $LASTEXITCODE）" }
}

$publishedExe = Join-Path $publishDir 'VideoTurner.exe'
if (-not (Test-Path $publishedExe)) { throw "发布目录里没有 VideoTurner.exe：$publishDir" }

# 3) 编译安装包
Write-Host '[3/4] 用 Inno Setup 编译安装包（lzma2/max 压缩，约 1 分钟）...'
& $iscc $iss
if ($LASTEXITCODE -ne 0) { throw "ISCC 编译失败（exit $LASTEXITCODE）" }

# 4) 汇总产物
$installerDir = Join-Path $root 'dist\installer'
$setup = Get-ChildItem $installerDir -Filter '*.exe' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
$pubFiles = Get-ChildItem $publishDir -Recurse -File
$pubSize = ($pubFiles | Measure-Object Length -Sum).Sum / 1MB
$hash = (Get-FileHash $setup.FullName -Algorithm SHA256).Hash

# 同目录写一份 sha256（便于分发核对）
[System.IO.File]::WriteAllText(
    "$($setup.FullName).sha256.txt",
    ("{0}  {1}`r`n" -f $hash, $setup.Name),
    (New-Object System.Text.UTF8Encoding($false)))

Write-Host ''
Write-Host '[4/4] 完成' -ForegroundColor Green
Write-Host ("  自包含目录 : {0}（{1} 个文件，{2:N1} MB）" -f $publishDir, $pubFiles.Count, $pubSize)
Write-Host ("  安装包     : {0}（{1:N1} MB）" -f $setup.FullName, ($setup.Length / 1MB))
Write-Host ("  sha256     : {0}" -f $hash)
Write-Host '  安装方式   : 按用户安装（%LOCALAPPDATA%\Programs\VideoTurner），不弹 UAC'
Write-Host '  静默安装   : setup.exe /VERYSILENT /SUPPRESSMSGBOXES'
Write-Host '  静默卸载   : unins000.exe /VERYSILENT /SUPPRESSMSGBOXES（加 /DELETEUSERDATA=1 连设置一起删）'

if ($ToDesktop) {
    $desktop = [Environment]::GetFolderPath('Desktop')
    if (Test-Path $desktop) {
        $target = Join-Path $desktop $setup.Name
        Copy-Item $setup.FullName $target -Force
        Copy-Item "$($setup.FullName).sha256.txt" "$target.sha256.txt" -Force
        Write-Host ("  桌面       : {0}" -f $target)
    }
}
