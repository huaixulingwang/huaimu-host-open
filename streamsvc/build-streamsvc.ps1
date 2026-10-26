# 编译「OpenRemoteDesk 串流服务」外壳，输出到 build\out\streamsvc
$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $here 'LingWangStreamSvc.csproj'
$outDir = Join-Path $here 'bin\Release\net48'
$drop = Join-Path $here '..\build\out\streamsvc'

if (-not (Test-Path $proj)) { throw "找不到项目: $proj" }

& dotnet build $proj -c Release --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'dotnet build 失败' }

if (Test-Path $drop) { Remove-Item $drop -Recurse -Force }
New-Item -ItemType Directory -Force -Path $drop | Out-Null
Copy-Item (Join-Path $outDir 'LingWangStreamSvc.exe') $drop -Force

$exe = Join-Path $drop 'LingWangStreamSvc.exe'
$fi = Get-Item $exe
Write-Host ('编译完成: {0}  ({1:N1} KB)' -f $fi.FullName, ($fi.Length / 1KB)) -ForegroundColor Green
