# OpenRemoteDesk 安装编排（Inno ssPostInstall，管理员）
param(
    [string]$AppDir  = 'C:\Program Files\LingWangRemote',
    [string]$DataDir = 'C:\ProgramData\LingWangRemote'
)
$ErrorActionPreference = 'Continue'

New-Item -ItemType Directory -Force -Path "$DataDir\logs" | Out-Null
$log = Join-Path $DataDir 'logs\installer.log'
function Log($m){ $line = '[install] ' + (Get-Date -Format 'HH:mm:ss') + ' ' + $m; Add-Content $log $line -Encoding UTF8; Write-Host $m }
function Run($file,$arguments,$workdir){
    Log ("RUN {0} {1}" -f $file,$arguments)
    if($workdir){
        $p = Start-Process -FilePath $file -ArgumentList $arguments -WorkingDirectory $workdir -Wait -PassThru -NoNewWindow
    } else {
        $p = Start-Process -FilePath $file -ArgumentList $arguments -Wait -PassThru -NoNewWindow
    }
    Log ("  exit={0}" -f $p.ExitCode)
    return $p.ExitCode
}

# 本机散装旧版的配置目录（仅迁移用；纯净安装时不存在）
$OldSunshineConfig = '<repo-root>\sunshine\config'

# 本机在虚拟网络里的固定 IP：同一账号下每台电脑必须不同。
# 规则：升级安装保留原值；老版本服务带 -i 参数就沿用；全新机器按 MachineGuid 稳定分配 10.126.126.4~250。
# .2 留给外部 EasyTier App，.3 留给平板，电脑端不用。
$ipFile = Join-Path $DataDir 'virtual-ip.txt'
$hostIp = ''
if (Test-Path $ipFile) { $hostIp = (Get-Content $ipFile -Raw).Trim() }
if (-not $hostIp) {
    $svcParams = ''
    try {
        $svcParams = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\EasyTierService\Parameters' -Name AppParameters -ErrorAction Stop).AppParameters
    } catch { }
    if ($svcParams -match '-i\s+(10\.126\.126\.\d+)') { $hostIp = $Matches[1] }
    elseif (Get-Service EasyTierService -ErrorAction SilentlyContinue) { $hostIp = '10.126.126.1' }
}
$validIp = $false
if ($hostIp -match '^10\.126\.126\.(\d+)$') {
    $last = [int]$Matches[1]
    if ($last -ge 1 -and $last -le 250 -and $last -ne 2 -and $last -ne 3) { $validIp = $true }
}
if (-not $validIp) {
    $machineGuid = ''
    try { $machineGuid = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Cryptography' -Name MachineGuid -ErrorAction Stop).MachineGuid } catch { }
    if (-not $machineGuid) { $machineGuid = [guid]::NewGuid().ToString() }
    $sha = [Security.Cryptography.SHA256]::Create()
    $hashBytes = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($machineGuid))
    $num = [BitConverter]::ToUInt32($hashBytes, 0)
    $hostIp = '10.126.126.' + (4 + ($num % 247))
}
[IO.File]::WriteAllText($ipFile, $hostIp, (New-Object System.Text.ASCIIEncoding))
Log "[0] 本机虚拟 IP = $hostIp（写入 $ipFile）"

# 组网身份：
#  - 内测/官方版：沿用构建时注入的固定网络（安装包不带明文密码，构建时替换占位符）
#  - 公共版（没有注入密码时）：本机随机生成一套，平板配对时由校验口下发
# 两种情况都把最终值写进 network.json，作为「本机当前身份」的唯一来源。
$netFile = Join-Path $DataDir 'network.json'
$netName = ''
$netSecret = ''
if (Test-Path $netFile) {
    try {
        $netObj = Get-Content $netFile -Raw | ConvertFrom-Json
        $netName = [string]$netObj.name
        $netSecret = [string]$netObj.secret
    } catch { }
}
if (-not $netName -or -not $netSecret) {
    $legacy = '__EASYTIER_NETWORK_SECRET__'
    if ($legacy -and $legacy -notmatch '^__') {
        $netName = 'open-remote-2026'
        $netSecret = $legacy
    } else {
        $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
        $nb = New-Object byte[] 8
        $sb = New-Object byte[] 16
        $rng.GetBytes($nb); $rng.GetBytes($sb)
        $netName = 'lw-' + (($nb | ForEach-Object { $_.ToString('x2') }) -join '')
        $netSecret = (($sb | ForEach-Object { $_.ToString('x2') }) -join '')
        Log "[0] 已生成新的组网身份（仅存本机）"
    }
    $netJson = '{"name":"' + $netName + '","secret":"' + $netSecret + '"}'
    [IO.File]::WriteAllText($netFile, $netJson, (New-Object System.Text.UTF8Encoding($false)))
}
$etArgs = "--network-name $netName --network-secret $netSecret -i $hostIp -p tcp://hub.example.com:11010 -p wss://hub.example.com:11012"

try {
    Log "=== install-post begin AppDir=$AppDir ==="

    # 1) 信任我们自己的根证书与驱动章（后续驱动 bat 也会再导入一次，重复无害）
    Log '[1] 导入信任证书'
    & certutil.exe -addstore -f Root "$AppDir\cert\LingWangRoot.cer"           | Out-Null
    & certutil.exe -addstore -f Root "$AppDir\cert\LingWangDriverSign.cer"     | Out-Null
    & certutil.exe -addstore -f TrustedPublisher "$AppDir\cert\LingWangDriverSign.cer" | Out-Null

    # 2) Sunshine 配置：迁移旧配置（保留配对证书），纯净机则放干净模板
    $cfg = "$AppDir\sunshine\config"
    if(-not (Test-Path "$cfg\sunshine.conf")){
        if(Test-Path "$OldSunshineConfig\sunshine.conf"){
            Log "[2] 迁移旧 Sunshine 配置（/B 备份特权读取被锁的凭据）"
            $rc = robocopy $OldSunshineConfig $cfg /E /B /R:1 /W:1
            Log ("  robocopy rc={0}" -f $LASTEXITCODE)
        } else {
            Log '[2] 写入干净配置模板'
            $rc = robocopy "$AppDir\config-template" $cfg /E /R:1 /W:1
            Log ("  robocopy rc={0}" -f $LASTEXITCODE)
        }
    } else { Log '[2] 配置已存在，保持不动' }

    # 3) 值班员脚本：固定落 C:\ProgramData\LingWangRemote（sunshine.conf 里写死了该路径）
    Log '[3] 部署值班员脚本'
    New-Item -ItemType Directory -Force -Path "$DataDir\scripts" | Out-Null
    foreach($f in 'launch-watchdog.ps1','launch-watchdog-hidden.vbs','session-watchdog.ps1','recover-physical-display.ps1'){
        Copy-Item "$AppDir\watchdog\$f" "$DataDir\scripts\$f" -Force
    }
    # 显示器档案只在不存在时落地（已验证的本机档案绝不覆盖）
    if(-not (Test-Path "$DataDir\display-profile.json") -and (Test-Path "$AppDir\watchdog\display-profile.json")){
        Copy-Item "$AppDir\watchdog\display-profile.json" "$DataDir\display-profile.json"
    }
    # 开机静默拉起值班员（启动文件夹）
    $startup = [Environment]::GetFolderPath('Startup')
    $wb = Join-Path $startup 'lingwang-watchdog.bat'
    Set-Content -Path $wb -Encoding ASCII -Value @(
        '@echo off',
        'wscript.exe //nologo //b "C:\ProgramData\LingWangRemote\scripts\launch-watchdog-hidden.vbs"'
    )
    Log "  startup bat -> $wb"

    # 4) 驱动：已有健康 VDD 就保留（避免主屏闪烁），纯净机全新安装；虚拟鼠标走官方流程
    Log '[4] 安装/校验驱动'
    $vddExist = @(Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object {
        @($_.HardwareID) -contains 'Root\ZakoVDD' -and $_.Status -eq 'OK' }).Count -gt 0
    if($vddExist){
        Log '  检测到健康的现有虚拟屏，保留设备、只更新驱动包'
        $a = '/c ""{0}\sunshine\scripts\install-vdd.bat" --preserve-healthy-existing"' -f $AppDir
        [void](Run $env:ComSpec $a)
    } else {
        Log '  全新安装虚拟屏'
        $a = '/c ""{0}\sunshine\scripts\install-vdd.bat""' -f $AppDir
        [void](Run $env:ComSpec $a)
    }
    $a = '/c ""{0}\sunshine\scripts\vmouse\install-vmouse.bat""' -f $AppDir
    [void](Run $env:ComSpec $a)

    # 5) 防火墙（先于服务启动）
    Log '[5] 防火墙放行'
    $a = '/c ""{0}\sunshine\scripts\add-firewall-rule.bat""' -f $AppDir
    [void](Run $env:ComSpec $a)
    netsh advfirewall firewall delete rule name='LingWang-EasyTier' >$null 2>&1
    netsh advfirewall firewall add rule name='LingWang-EasyTier' dir=in action=allow program="`"$AppDir\easytier\easytier-core.exe`"" enable=yes protocol=tcp | Out-Null
    netsh advfirewall firewall add rule name='LingWang-EasyTier' dir=in action=allow program="`"$AppDir\easytier\easytier-core.exe`"" enable=yes protocol=udp | Out-Null

    # 6) Sunshine 服务：官方脚本自动处理 新建/改指向/启动；再强制设为开机自启
    Log '[6] SunshineService'
    $a = '/c ""{0}\sunshine\scripts\install-service.bat""' -f $AppDir
    [void](Run $env:ComSpec $a)
    $a = '/c ""{0}\sunshine\scripts\autostart-service.bat""' -f $AppDir
    [void](Run $env:ComSpec $a)

    # 6.5) 自有串流实例：与社区版并行，端口 48989 段，配对数据独立
    Log '[6.5] 自有串流实例 sunshine-lw'
    $lwDir = "$AppDir\sunshine-lw"
    if(-not (Test-Path "$lwDir\Sunshine.exe")){
        robocopy "$AppDir\sunshine" $lwDir /E /XD "$AppDir\sunshine\config" /R:1 /W:1 | Out-Null
        Log ("  robocopy rc={0}" -f $LASTEXITCODE)
    }
    New-Item -ItemType Directory -Force -Path "$lwDir\config" | Out-Null
    $lwConf = @(
        'address_family = both',
        'locale = zh',
        'max_bitrate = 12000',
        'output_name = ZakoHDR',
        'global_prep_cmd = [{"do":"cmd.exe /c exit 0","undo":"cmd.exe /c exit 0","elevated":"true"}]',
        'port = 48989',
        'mdns_broadcast = off'
    ) -join "`r`n"
    # 必须无 BOM：带 BOM 时 Sunshine 会读不到第一行 port，退回 47989 段
    [IO.File]::WriteAllText("$lwDir\config\sunshine.conf", $lwConf + "`r`n", (New-Object System.Text.ASCIIEncoding))
    if(-not (Test-Path "$lwDir\config\apps.json")){
        Copy-Item "$AppDir\config-template\apps.json" "$lwDir\config\apps.json" -Force
    }

    # 网页登录口令与实例身份：只在首次安装时生成，升级时保留（否则平板配对关系会失效）
    if(-not (Test-Path "$lwDir\config\sunshine_state.json")){
        $webUser = 'lingwang'
        $chars = (48..57) + (65..90) + (97..122)
        $webPass = -join ($chars | Get-Random -Count 16 | ForEach-Object { [char]$_ })
        $salt    = -join ($chars | Get-Random -Count 16 | ForEach-Object { [char]$_ })
        $sha = [Security.Cryptography.SHA256]::Create()
        $hash = (($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($salt + $webPass))) | ForEach-Object { $_.ToString('X2') }) -join ''
        $stateObj = @{
            username = $webUser
            salt     = $salt
            password = $hash
            root     = @{ uniqueid = [guid]::NewGuid().ToString().ToUpper(); named_devices = @() }
        }
        $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
        [IO.File]::WriteAllText("$lwDir\config\sunshine_state.json", ($stateObj | ConvertTo-Json -Depth 8), $utf8NoBom)
        [IO.File]::WriteAllText("$lwDir\config\stream-web.ini", "username=$webUser`r`npassword=$webPass`r`n", $utf8NoBom)
        Log '  已生成实例身份与本机网页口令（供托盘自动配对使用）'
    } else { Log '  实例身份已存在，保持不动' }
    netsh advfirewall firewall delete rule name='LingWangStream' >$null 2>&1
    netsh advfirewall firewall add rule name='LingWangStream' dir=in action=allow program="`"$lwDir\Sunshine.exe`"" enable=yes protocol=tcp | Out-Null
    netsh advfirewall firewall add rule name='LingWangStream' dir=in action=allow program="`"$lwDir\Sunshine.exe`"" enable=yes protocol=udp | Out-Null

    $svcBin = "$AppDir\streamsvc\LingWangStreamSvc.exe"
    if(Test-Path $svcBin){
        $lwSvc = 'LingWangStreamService'
        sc.exe qc $lwSvc >$null 2>&1
        if($LASTEXITCODE -eq 0){
            & sc.exe config $lwSvc binPath= "`"$svcBin`"" start= auto | Out-Null
        } else {
            & sc.exe create $lwSvc binPath= "`"$svcBin`"" start= auto DisplayName= "LingWang Stream Service" | Out-Null
        }
        & sc.exe description $lwSvc "LingWang remote streaming host (isolated instance, ports 48984/48989/48990/49010)." | Out-Null
        & sc.exe start $lwSvc | Out-Null
        Log '  LingWangStreamService 已配置并启动'
    } else { Log '  跳过服务：未找到 streamsvc\LingWangStreamSvc.exe' }

    # 7) EasyTier 服务（NSSM 托管，崩溃 5 秒自拉起）
    Log '[7] EasyTierService'
    $nssm = "$AppDir\nssm\nssm.exe"
    $core = "$AppDir\easytier\easytier-core.exe"
    $svcExists = $null -ne (Get-Service EasyTierService -ErrorAction SilentlyContinue)
    if(-not $svcExists){
        [void](Run $nssm "install EasyTierService `"$core`"")
    } else {
        sc.exe config EasyTierService binPath= "`"$nssm`"" | Out-Null
    }
    & $nssm set EasyTierService Application "$core"        | Out-Null
    & $nssm set EasyTierService AppDirectory "$AppDir\easytier" | Out-Null
    & $nssm set EasyTierService AppParameters $etArgs      | Out-Null
    & $nssm set EasyTierService AppStdout "$DataDir\logs\easytier-out.log" | Out-Null
    & $nssm set EasyTierService AppStderr "$DataDir\logs\easytier-err.log" | Out-Null
    & $nssm set EasyTierService AppRotateFiles 1 | Out-Null
    & $nssm set EasyTierService AppRotateBytes 10485760 | Out-Null
    & $nssm set EasyTierService AppExit Default Restart | Out-Null
    & $nssm set EasyTierService AppRestartDelay 5000 | Out-Null
    & $nssm set EasyTierService Start SERVICE_AUTO_START | Out-Null
    & $nssm start EasyTierService | Out-Null

    # 8) 托盘开机启动 + 立即启动
    Log '[8] 托盘程序'
    New-Item -Path 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run' -Force | Out-Null
    Set-ItemProperty -Path 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'LingWangTray' -Value "`"$AppDir\tray\LingWangTray.exe`""
    Start-Process "$AppDir\tray\LingWangTray.exe"

    Log '=== install-post done ==='
    exit 0
}
catch {
    Log ('FATAL ' + $_.Exception.Message)
    Log $_.ScriptStackTrace
    exit 1
}
