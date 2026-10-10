<#
  生成两个自包含单文件 exe。

  为什么用自包含（--self-contained）：用户机器上不装 .NET 也能双击运行。
  代价是每个 exe 约 35-50MB（内含精简运行时）。

  用法：
    tools\build-exe.ps1            编译并发布到 tools\exe\
    tools\build-exe.ps1 -Framework net10.0-windows
#>
param(
    [string]$Framework = 'net10.0-windows'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $root 'BlogPublisher'
$out = Join-Path $root 'exe'

# 发布参数：单文件 + 自包含 + 压缩，尽量压小体积。
$common = @(
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', 'true',
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true',
    '--nologo'
)

Write-Host '==> 检查 .NET SDK' -ForegroundColor Cyan
$sdk = dotnet --version
if ($LASTEXITCODE -ne 0) {
    Write-Error '未找到 .NET SDK。请安装 .NET 10 SDK 后重试。'
}
Write-Host "    SDK 版本 $sdk" -ForegroundColor DarkGray

Write-Host '==> 编译控制台版' -ForegroundColor Cyan
Push-Location $proj
dotnet build -c Release -f $Framework --nologo
if ($LASTEXITCODE -ne 0) { Pop-Location; throw '控制台版编译失败' }
dotnet publish @common -f $Framework -o (Join-Path $proj 'publish\console')
if ($LASTEXITCODE -ne 0) { Pop-Location; throw '控制台版发布失败' }
Pop-Location

Write-Host '==> 编译图形版' -ForegroundColor Cyan
Push-Location (Join-Path $proj 'Gui')
dotnet build -c Release -f $Framework --nologo
if ($LASTEXITCODE -ne 0) { Pop-Location; throw '图形版编译失败' }
dotnet publish @common -f $Framework -o (Join-Path $proj 'publish\gui')
if ($LASTEXITCODE -ne 0) { Pop-Location; throw '图形版发布失败' }
Pop-Location

# 汇总产物到 tools\exe\，方便用户直接找到。
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force -Path $out | Out-Null
Copy-Item (Join-Path $proj 'publish\console\*.exe') $out -Force
Copy-Item (Join-Path $proj 'publish\gui\*.exe') $out -Force

Write-Host ''
Write-Host '==> 完成' -ForegroundColor Green
Get-ChildItem $out -Filter *.exe | ForEach-Object {
    Write-Host ('    {0,-24} {1,6:N1} MB' -f $_.Name, ($_.Length / 1MB)) -ForegroundColor White
}
Write-Host ''
Write-Host "产物目录：$out" -ForegroundColor Cyan
Write-Host '把这两个 exe 放在任意位置都能用，它们会自动向上查找项目目录。' -ForegroundColor DarkGray
