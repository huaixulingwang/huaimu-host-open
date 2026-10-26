# OpenRemoteDesk 安装包货舱拼装脚本（可重复执行，不需要管理员）
# 作用：把散装的 Sunshine / EasyTier / NSSM / 值班员脚本汇成干净的 payload 货舱，
#       剔除日志、配对状态、卸载残留，并把每个驱动角落都覆盖成我们自签的版本。
$ErrorActionPreference = 'Stop'

$root      = '<repo-root>'
$sunSrc    = Join-Path $root 'sunshine'
$etSrc     = Join-Path $root 'EasyTier\core\easytier-windows-x86_64'
$nssmSrc   = Join-Path $root 'NSSM\nssm-2.24\win64\nssm.exe'
$wdSrc     = 'C:\ProgramData\LingWangRemote'
$payload   = Join-Path $root 'lingwang-host\payload'

$sunDst    = Join-Path $payload 'sunshine'
$etDst     = Join-Path $payload 'easytier'
$nssmDst   = Join-Path $payload 'nssm'
$wdDst     = Join-Path $payload 'watchdog'
$tplDst    = Join-Path $payload 'config-template'

Write-Host '== [1/6] 清理旧货舱（保留 cert 与 drivers） ==' -ForegroundColor Cyan
foreach($d in $sunDst,$etDst,$nssmDst,$wdDst,$tplDst){
    if(Test-Path $d){ Remove-Item $d -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $d | Out-Null
}

Write-Host '== [2/6] 复制 Sunshine 运行树（robocopy 跳过被锁的 config） ==' -ForegroundColor Cyan
# config 含被 ACL 锁死的配对凭据，本就不进包；config 模板在第6步单独放
$rc = robocopy $sunSrc $sunDst /E /XD (Join-Path $sunSrc 'config') /XF unins000.dat unins000.exe Sunshine.lnk /NFL /NDL /NJH /NP /R:1 /W:1
if($LASTEXITCODE -ge 8){ throw "robocopy sunshine 失败，退出码 $LASTEXITCODE" }
# Win10 旧驱动变体（本机 Win11 用 latest；将来要支持 Win10 时需另走提权重签）
Remove-Item "$sunDst\scripts\driver\win10" -Recurse -Force -ErrorAction SilentlyContinue

Write-Host '== [3/6] 用自签驱动覆盖货舱内每一个驱动角落 ==' -ForegroundColor Cyan
$vddCan  = Join-Path $payload 'drivers\ZakoVDD'
$vmCan   = Join-Path $payload 'drivers\ZakoVirtualMouse'
# canonical 目录里带的是 ZakoVDD.cer / zakovdd.cat，目标角落文件名一致，直接整盖
foreach($spot in @("$sunDst\tools\vdd","$sunDst\scripts\driver\latest")){
    New-Item -ItemType Directory -Force -Path $spot | Out-Null
    Copy-Item (Join-Path $vddCan '*') -Destination $spot -Force
}
foreach($spot in @("$sunDst\tools\vmouse","$sunDst\scripts\vmouse\driver")){
    New-Item -ItemType Directory -Force -Path $spot | Out-Null
    Copy-Item (Join-Path $vmCan '*') -Destination $spot -Force
}

Write-Host '-- 校验货舱内全部 .cat --' -ForegroundColor DarkGray
$catFail = 0
Get-ChildItem $sunDst -Recurse -Filter '*.cat' | ForEach-Object {
    $s = Get-AuthenticodeSignature $_.FullName
    if($s.Status -ne 'Valid'){ $script:catFail++ }
    Write-Host ("  {0}  {1}" -f $s.Status, $_.FullName.Replace($sunDst,'.'))
}
if($catFail -gt 0){ throw "货舱内有 $catFail 个目录文件签名不是 Valid" }

Write-Host '== [4/6] 精简复制 EasyTier（只留后台组网必需的5个文件） ==' -ForegroundColor Cyan
foreach($f in 'easytier-core.exe','easytier-cli.exe','wintun.dll','WinDivert64.sys','Packet.dll'){
    Copy-Item (Join-Path $etSrc $f) -Destination $etDst -Force
}

Write-Host '== [5/6] 复制 NSSM、串流服务外壳与值班员脚本 ==' -ForegroundColor Cyan
Copy-Item $nssmSrc -Destination $nssmDst -Force
$svcDst = Join-Path $payload 'streamsvc'
if(Test-Path $svcDst){ Remove-Item $svcDst -Recurse -Force }
New-Item -ItemType Directory -Force -Path $svcDst | Out-Null
$svcExe = Join-Path $root 'lingwang-host\build\out\streamsvc\LingWangStreamSvc.exe'
if(-not (Test-Path $svcExe)){ throw "缺少串流服务外壳，请先跑 streamsvc\build-streamsvc.ps1: $svcExe" }
Copy-Item $svcExe -Destination $svcDst -Force
foreach($f in 'launch-watchdog.ps1','session-watchdog.ps1','recover-physical-display.ps1'){
    Copy-Item (Join-Path $wdSrc "scripts\$f") -Destination $wdDst -Force
}
# 值班员的无窗口拉起器（安装脚本会把它铺到 C:\ProgramData\LingWangRemote\scripts）
$vbsLines = @(
    "' OpenRemoteDesk 值班员拉起器（无窗口版）",
    "' 由 Sunshine 的 global_prep_cmd 调用，无窗口启动 PowerShell，避免串流时桌面闪窗。",
    'Set sh = CreateObject("WScript.Shell")',
    'sh.Run "powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""C:\ProgramData\LingWangRemote\scripts\launch-watchdog.ps1""", 0, False'
)
[IO.File]::WriteAllLines((Join-Path $wdDst 'launch-watchdog-hidden.vbs'), $vbsLines, [Text.Encoding]::Default)
Copy-Item (Join-Path $wdSrc 'display-profile.json') -Destination $wdDst -Force -ErrorAction SilentlyContinue

Write-Host '== [6/6] 放置干净配置模板（不含任何配对/证书/日志） ==' -ForegroundColor Cyan
Copy-Item (Join-Path $sunSrc 'config\sunshine.conf') -Destination $tplDst -Force
Copy-Item (Join-Path $sunSrc 'config\apps.json')     -Destination $tplDst -Force

Write-Host '== 生成清单 MANIFEST.txt ==' -ForegroundColor Cyan
$files = Get-ChildItem $payload -Recurse -File | Sort-Object FullName
$lines = @()
$total = 0
foreach($fi in $files){
    $total += $fi.Length
    $rel = $fi.FullName.Replace($payload+'\','')
    $hash = (Get-FileHash $fi.FullName -Algorithm SHA256).Hash
    $lines += ('{0,12}  {1}  {2}' -f $fi.Length, $hash, $rel)
}
$header = @(
    '# LingWangRemote payload manifest',
    ('# generated: ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')),
    ('# files: ' + $files.Count + '   total: ' + ('{0:N1} MB' -f ($total/1MB))),
    ''
)
Set-Content -Path (Join-Path $payload 'MANIFEST.txt') -Value ($header + $lines) -Encoding ASCII

# 禁运品扫描：货舱里绝不能出现配对状态/凭据/旧日志
Write-Host '== 禁运品扫描 ==' -ForegroundColor Cyan
$banned = $files | Where-Object {
    $_.Name -match 'sunshine_state\.json|cakey\.pem|cacert\.pem|sunshine\.log|\.bak|unins000|et_(out|err|proc)\.txt|easytier-(gui|web)'
}
if($banned){ $banned | ForEach-Object { Write-Host ("  禁运: " + $_.FullName) -ForegroundColor Red }; throw '货舱含禁运品' }
else { Write-Host '  干净，无配对状态/凭据/日志/旧卸载器' -ForegroundColor Green }

Write-Host ''
Write-Host ('==== 货舱拼装完成：{0} 个文件，{1:N1} MB ====' -f $files.Count,($total/1MB)) -ForegroundColor Green
