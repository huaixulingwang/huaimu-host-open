# 编译OpenRemoteDesk托盘（WebView2 界面版）
# 界面在 tray2\ui，宿主与业务在 tray2\*.cs；输出整目录到 payload\tray
$ErrorActionPreference = 'Stop'

$here    = Split-Path -Parent $MyInvocation.MyCommand.Path
$root    = Split-Path -Parent $here
$proj    = Join-Path $root 'tray2\LingWangTray.csproj'
$outDir  = Join-Path $root 'payload\tray'
$buildDir = Join-Path $root 'tray2\bin\Release\net48'

if (-not (Test-Path $proj)) { throw "找不到项目: $proj" }

# 运行中的托盘会锁住输出文件，先结束
Get-Process LingWangTray -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800

& dotnet build $proj -c Release --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'dotnet build 失败' }

if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
Copy-Item (Join-Path $buildDir '*') $outDir -Recurse -Force
# 托盘/窗口图标素材（tray_ok / tray_error）必须跟着 exe 一起走，否则运行时会退回国标
foreach ($ico in 'tray_ok.ico', 'tray_error.ico') {
    $srcIco = Join-Path $root ("tray2\" + $ico)
    if (-not (Test-Path $srcIco)) { throw "缺少图标素材: $srcIco（先运行 tray2\make-icons.ps1）" }
    Copy-Item $srcIco $outDir -Force
}

$exe = Join-Path $outDir 'LingWangTray.exe'
$fi = Get-Item $exe
Write-Host ('编译完成: {0}  ({1:N1} KB)' -f $fi.FullName, ($fi.Length / 1KB)) -ForegroundColor Green
