<#
.SYNOPSIS
    APISwitch 一键发布打包脚本：生成绿色便携版 ZIP 与 Windows 安装包 Setup.exe
#>

$ErrorActionPreference = "Stop"
$root = Resolve-Path "$PSScriptRoot\.."
Set-Location $root

Write-Host "========================================" -ForegroundColor Cyan
Write-Host " 🚀 开始打包 APISwitch 发布版本" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

# 1. 解析版本号
[xml]$proj = Get-Content "$root\APISwitch.csproj"
$version = $proj.Project.PropertyGroup.Version
if (-not $version) { $version = "0.1.1" }
Write-Host "📦 目标版本: v$version" -ForegroundColor Green

# 2. 执行多文件发布 (框架依赖模式，安装后展示清晰的 DLL 模块结构)
$publishDir = "$root\bin\publish"
$releaseDir = "$root\Release_Package"

if (-not (Test-Path $releaseDir)) {
    New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null
}

if (Test-Path $publishDir) {
    Remove-Item $publishDir -Recurse -Force
}

Write-Host "`n🔨 正在编译生成多文件发布版本 (Framework-Dependent)..." -ForegroundColor Yellow
dotnet publish "$root\APISwitch.csproj" -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=false `
    -o $publishDir

if ($LASTEXITCODE -ne 0) {
    Write-Error "编译失败！请检查代码错误。"
    exit 1
}

$mainExe = "$publishDir\APISwitch.exe"
if (-not (Test-Path $mainExe)) {
    Write-Error "未找到编译产物: $mainExe"
    exit 1
}

# 3. 生成便携 ZIP 包 (包含完整的应用和所有依赖 DLL)
$portableZip = "$releaseDir\APISwitch-v$version-win-x64.zip"

Write-Host "`n📁 正在准备便携包..." -ForegroundColor Yellow
if (Test-Path $portableZip) { Remove-Item $portableZip -Force }
Compress-Archive -Path "$publishDir\*" -DestinationPath $portableZip -Force
Write-Host "✔ 便携压缩包已生成: $portableZip ($([Math]::Round((Get-Item $portableZip).Length / 1MB, 1)) MB)" -ForegroundColor Green

# 4. 尝试检测 Inno Setup 编译器并生成安装包
$isccCandidates = @(
    "iscc.exe",
    "$env:ProgramFiles (x86)\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LocalAppData\Programs\Inno Setup 6\ISCC.exe"
)

$isccPath = $null
foreach ($candidate in $isccCandidates) {
    if (Get-Command $candidate -ErrorAction SilentlyContinue) {
        $isccPath = $candidate
        break
    }
    if (Test-Path $candidate) {
        $isccPath = $candidate
        break
    }
}

if ($isccPath) {
    Write-Host "`n💿 检测到 Inno Setup: $isccPath" -ForegroundColor Yellow
    Write-Host "🔨 正在生成 Windows 安装包 Setup.exe..." -ForegroundColor Yellow
    & $isccPath "$root\installer\APISwitch.iss"
    if ($LASTEXITCODE -eq 0) {
        Write-Host "✔ 安装包已生成: $releaseDir\APISwitch-Setup-v$version.exe" -ForegroundColor Green
    } else {
        Write-Warning "Inno Setup 打包返回非 0 状态码。"
    }
} else {
    Write-Host "`nℹ 未检测到 Inno Setup 编译器。" -ForegroundColor DarkGray
    Write-Host "  若需自动编译生成 Setup.exe 安装包，可通过 winget 一键安装：" -ForegroundColor DarkGray
    Write-Host "  winget install JRSoftware.InnoSetup -e" -ForegroundColor Cyan
    Write-Host "  或手动使用 Inno Setup 打开 installer\APISwitch.iss 编译。" -ForegroundColor DarkGray
}

Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host " 🎉 打包完成！发布文件清单：" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Get-ChildItem $releaseDir | Select-Object Name, @{Name="大小(MB)";Expression={[Math]::Round($_.Length/1MB, 1)}}, LastWriteTime | Format-Table -AutoSize