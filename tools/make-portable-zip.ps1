param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$Version = '1.0.0',
    [switch]$Publish,
    [switch]$ToDesktop
)

# ═══════════════════════════════════════════════════════════════════════════
#  VideoTurner 便携版 zip（自包含，解压即用）
#
#  用法：
#      powershell -ExecutionPolicy Bypass -File tools\make-portable-zip.ps1
#      powershell -ExecutionPolicy Bypass -File tools\make-portable-zip.ps1 -Publish -ToDesktop
#
#  产物：
#      dist\portable\VideoTurner-1.0.0-win-x64-portable.zip
#      -ToDesktop 时再复制一份到桌面
#
#  前置：dist\publish\<Runtime> 里已经有自包含发布产物
#        （由 tools\make-installer.ps1 或本脚本的 -Publish 生成）
#
#  注意：本脚本必须存成 UTF-8 带 BOM；说明文本 installer\PORTABLE-README.txt 也要有 BOM。
# ═══════════════════════════════════════════════════════════════════════════

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\VideoTurner\VideoTurner.csproj'
$publishDir = Join-Path $root "dist\publish\$Runtime"
$outDir = Join-Path $root 'dist\portable'
$readme = Join-Path $root 'installer\PORTABLE-README.txt'
$prefix = "VideoTurner-$Version-$Runtime"
$zipName = "$prefix-portable.zip"
$zipPath = Join-Path $outDir $zipName

function Ensure-Utf8Bom([string]$Path) {
    if (-not (Test-Path $Path)) { return }
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) { return }
    [System.IO.File]::WriteAllBytes($Path, ([byte[]](0xEF, 0xBB, 0xBF) + $bytes))
    Write-Host "  [已补 UTF-8 BOM] $Path"
}

Write-Host '=== VideoTurner 便携版 zip ===' -ForegroundColor Cyan
Ensure-Utf8Bom $PSCommandPath
Ensure-Utf8Bom $readme

# 0) 需要时先发布（exe / dll 被占用会让 publish 失败，先结束运行中的实例）
if ($Publish) {
    $running = @(Get-Process -Name VideoTurner -ErrorAction SilentlyContinue)
    if ($running.Count -gt 0) {
        Write-Host ("[1/3] 结束正在运行的 VideoTurner（PID {0}）..." -f (($running | ForEach-Object { $_.Id }) -join ', '))
        $running | ForEach-Object { try { $_.Kill(); $_.WaitForExit(3000) } catch { } }
        Start-Sleep -Milliseconds 800
    }
    Write-Host "[1/3] dotnet publish -c $Configuration -r $Runtime --self-contained true ..."
    & dotnet publish $project -c $Configuration -r $Runtime --self-contained true `
        -p:SatelliteResourceLanguages=zh-Hans -o $publishDir --nologo -v m
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（exit $LASTEXITCODE）" }
}
else {
    Write-Host '[1/3] 跳过发布（需要重新发布请加 -Publish）。'
}

if (-not (Test-Path (Join-Path $publishDir 'VideoTurner.exe'))) {
    throw "发布目录里没有 VideoTurner.exe：$publishDir`n先跑 tools\make-installer.ps1，或加 -Publish。"
}

# 1) 打 zip：解压后是一个 VideoTurner-x.y.z-win-x64\ 目录，不会把 200 多个文件撒在桌面上
Write-Host "[2/3] 打包 $zipName ..."
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

New-Item -ItemType Directory -Force -Path $outDir | Out-Null
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

$sw = [System.Diagnostics.Stopwatch]::StartNew()
$zip = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
$count = 0
try {
    foreach ($file in Get-ChildItem $publishDir -Recurse -File) {
        $rel = $file.FullName.Substring($publishDir.Length).TrimStart('\') -replace '\\', '/'
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $zip, $file.FullName, "$prefix/$rel", [System.IO.Compression.CompressionLevel]::Optimal)
        $count++
    }

    $extras = @(
        @{ Path = $readme; Name = "$prefix/PORTABLE-README.txt" },
        @{ Path = (Join-Path $root 'README.md'); Name = "$prefix/README.md" }
    )
    foreach ($extra in $extras) {
        if (Test-Path $extra.Path) {
            [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $zip, $extra.Path, $extra.Name, [System.IO.Compression.CompressionLevel]::Optimal)
            $count++
        }
    }
}
finally {
    $zip.Dispose()
}
$sw.Stop()

$zi = Get-Item $zipPath
[System.IO.File]::WriteAllText(
    "$zipPath.sha256.txt",
    ("{0}  {1}`r`n" -f (Get-FileHash $zipPath -Algorithm SHA256).Hash, $zipName),
    (New-Object System.Text.UTF8Encoding($false)))

Write-Host ("[3/3] 完成：{0} 个条目，{1:N2} MB，用时 {2:N1}s" -f $count, ($zi.Length / 1MB), $sw.Elapsed.TotalSeconds)
Write-Host "  zip    : $zipPath"
Write-Host "  sha256 : $((Get-FileHash $zipPath -Algorithm SHA256).Hash)"

if ($ToDesktop) {
    $desktop = [Environment]::GetFolderPath('Desktop')
    if (Test-Path $desktop) {
        $target = Join-Path $desktop $zipName
        Copy-Item $zipPath $target -Force
        Copy-Item "$zipPath.sha256.txt" "$target.sha256.txt" -Force
        Write-Host "  桌面   : $target"
    }
}
