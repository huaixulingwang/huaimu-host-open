# OpenRemoteDesk 安装包一键构建（不需要管理员）
#
# 前置条件：
#   1) 已跑过 build\assemble-payload.ps1，payload 货舱是最新的
#   2) 已跑过 tray\build-tray.ps1，payload\tray\LingWangTray.exe 已生成
#   3) installer\local-secrets.txt 已按 local-secrets.example.txt 模板填好真值
#
# 本脚本只做两件事：
#   a. 把 installer\ps 的三个编排脚本复制到 build\staging\ps，并把 __占位符__
#      替换成本机私密变量（暂存区不进 git）；
#   b. 调用 Inno Setup 编译器出包到 dist\。
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$psSrc    = Join-Path $repoRoot 'installer\ps'
$stageRoot = Join-Path $repoRoot 'build\staging'
$stageDir  = Join-Path $stageRoot 'ps'
$secrets   = Join-Path $repoRoot 'installer\local-secrets.txt'
$iss       = Join-Path $repoRoot 'installer\LingWangRemote.iss'

if(-not (Test-Path $secrets)){
    throw "找不到本机私密文件 $secrets`n请复制 installer\local-secrets.example.txt 为 local-secrets.txt 并填入真值后再构建。"
}

# 读取 key=value（忽略 # 注释与空行）
$map = @{}
foreach($line in Get-Content $secrets){
    $t = $line.Trim()
    if(-not $t -or $t.StartsWith('#')){ continue }
    $i = $t.IndexOf('=')
    if($i -lt 1){ continue }
    $map[$t.Substring(0,$i).Trim()] = $t.Substring($i+1).Trim()
}
# 组网密码已改为「首次安装随机生成、配对时下发」，安装包不再注入固定密码；
# local-secrets.txt 里的 EASYTIER_NETWORK_SECRET 仅作历史兼容保留，可留空。

# 前置检查：货舱清单与托盘产物
foreach($p in 'payload\MANIFEST.txt','payload\tray\LingWangTray.exe'){
    if(-not (Test-Path (Join-Path $repoRoot $p))){
        throw "缺少 $p —— 请先运行 build\assemble-payload.ps1 和 tray\build-tray.ps1"
    }
}

# 暂存脚本并替换占位符（UTF-8 BOM，保证 Windows PowerShell 5.1 正确读取中文）
if(Test-Path $stageRoot){ Remove-Item $stageRoot -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stageDir | Out-Null
foreach($f in 'stop-services.ps1','install-post.ps1','uninstall.ps1'){
    $c = Get-Content (Join-Path $psSrc $f) -Raw
    foreach($k in $map.Keys){ $c = $c.Replace("__$k__", $map[$k]) }
    Set-Content -Path (Join-Path $stageDir $f) -Value $c -Encoding UTF8
}

# 残留占位符一律拦停，绝不能带空密码/半成品出包
$left = Select-String -Path (Join-Path $stageDir '*.ps1') -Pattern '__[A-Z0-9_]+__'
if($left){
    throw ("暂存脚本仍有未替换占位符:`n" + (($left | ForEach-Object { $_.Filename + ': ' + $_.Line.Trim() }) -join "`n"))
}
Write-Host '暂存区就绪，占位符已全部注入' -ForegroundColor Green

# 定位 Inno Setup 编译器
$iscc = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 7\ISCC.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    'C:\Program Files (x86)\Inno Setup 7\ISCC.exe',
    'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if(-not $iscc){
    $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if($cmd){ $iscc = $cmd.Source } else { throw '找不到 Inno Setup 编译器 ISCC.exe，请先安装 Inno Setup 6 或 7' }
}

Write-Host '== 开始编译安装包 ==' -ForegroundColor Cyan
& $iscc $iss
if($LASTEXITCODE -ne 0){ throw "ISCC 编译失败，退出码 $LASTEXITCODE" }
Write-Host '== 完成，成品在 dist\ ==' -ForegroundColor Green
