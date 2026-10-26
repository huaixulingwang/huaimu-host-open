# OpenRemoteDesk 卸载编排（Inno usUninstall，管理员）
# 默认保守策略：停服删服务、撤防火墙与自启动；保留 驱动/证书/配对数据/显示器档案。
param(
    [string]$AppDir  = 'C:\Program Files\LingWangRemote',
    [string]$DataDir = 'C:\ProgramData\LingWangRemote'
)
$ErrorActionPreference = 'Continue'
New-Item -ItemType Directory -Force -Path "$DataDir\logs" | Out-Null
$log = Join-Path $DataDir 'logs\installer.log'
function Log($m){ $line = '[uninst] ' + (Get-Date -Format 'HH:mm:ss') + ' ' + $m; Add-Content $log $line -Encoding UTF8 }

try {
    Log '=== uninstall begin ==='

    # 0) 先把配对配置快照留下来（卸完想重装就不用重新配对）
    foreach($cfgDir in "$AppDir\sunshine\config","$AppDir\sunshine-lw\config"){
        if(Test-Path "$cfgDir\sunshine_state.json"){
            $snap = "$DataDir\config-snapshot-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + (Split-Path (Split-Path $cfgDir -Parent) -Leaf)
            robocopy $cfgDir $snap /E /B /R:1 /W:1 | Out-Null
            Log "配置快照 -> $snap (rc=$LASTEXITCODE)"
        }
    }

    # 1) 停并删三个服务
    foreach($svc in 'LingWangStreamService','SunshineService','EasyTierService'){
        sc.exe query $svc 2>$null | Out-Null
        if($LASTEXITCODE -eq 0){
            Log "stop $svc"; sc.exe stop $svc | Out-Null
            Start-Sleep -Seconds 3
        }
    }
    foreach($exe in 'easytier-core.exe','Sunshine.exe','sunshinesvc.exe'){
        Get-Process -Name ($exe -replace '\.exe$','') -ErrorAction SilentlyContinue | ForEach-Object {
            Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
        }
    }
    $nssm = "$AppDir\nssm\nssm.exe"
    if(Test-Path $nssm){ & $nssm remove EasyTierService confirm | Out-Null }
    sc.exe delete LingWangStreamService | Out-Null
    sc.exe delete SunshineService | Out-Null
    sc.exe delete EasyTierService | Out-Null
    Log 'services deleted'

    # 2) 撤防火墙
    netsh advfirewall firewall delete rule name='Sunshine' | Out-Null
    netsh advfirewall firewall delete rule name='LingWangStream' | Out-Null
    netsh advfirewall firewall delete rule name='LingWang-EasyTier' | Out-Null

    # 3) 自启动 / 托盘
    Remove-ItemProperty -Path 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'LingWangTray' -ErrorAction SilentlyContinue
    Get-Process -Name 'LingWangTray' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    $startup = [Environment]::GetFolderPath('Startup')
    Remove-Item (Join-Path $startup 'lingwang-watchdog.bat') -Force -ErrorAction SilentlyContinue
    Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -match 'session-watchdog|launch-watchdog' } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }

    # 4) 撤值班员脚本（保留 logs 与 display-profile.json）
    foreach($f in 'launch-watchdog.ps1','session-watchdog.ps1','recover-physical-display.ps1'){
        Remove-Item "$DataDir\scripts\$f" -Force -ErrorAction SilentlyContinue
    }

    # 驱动（ZakoVDD/ZakoVirtualMouse）与信任证书默认保留：
    #  - 保留可避免删虚拟屏引发物理屏救援问题；
    #  - 重装OpenRemoteDesk后可直接继续使用。
    Log 'drivers & certs KEPT by design'
    Log '=== uninstall done ==='
    exit 0
}
catch { Log ('FATAL ' + $_.Exception.Message); exit 1 }
