# OpenRemoteDesk 安装前：停止两个服务并收尸，解锁即将替换的 exe 文件
# 由 Inno PrepareToInstall 以管理员身份调用
$logDir = 'C:\ProgramData\LingWangRemote\logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$log = Join-Path $logDir 'installer.log'
function Log($m){ $line = '[stop] ' + (Get-Date -Format 'HH:mm:ss') + ' ' + $m; Add-Content $log $line -Encoding UTF8 }

try {
    Log 'stop-services begin'
    foreach($svc in 'EasyTierService','LingWangStreamService','SunshineService'){
        $q = sc.exe query $svc 2>$null
        if($LASTEXITCODE -eq 0){
            Log "stopping $svc"
            sc.exe stop $svc | Out-Null
            for($i=0;$i -lt 20;$i++){
                Start-Sleep -Milliseconds 500
                $st = (Get-Service $svc -ErrorAction SilentlyContinue).Status
                if($st -eq 'Stopped'){ break }
            }
        } else { Log "$svc not present" }
    }
    # 兜底：强杀残留进程，释放文件锁
    foreach($exe in 'easytier-core.exe','Sunshine.exe','sunshinesvc.exe'){
        Get-Process -Name ($exe -replace '\.exe$','') -ErrorAction SilentlyContinue | ForEach-Object {
            Log "kill $exe pid=$($_.Id)"; Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
        }
    }
    Start-Sleep -Seconds 2
    Log 'stop-services done'
    exit 0
}
catch { Log ('FATAL ' + $_.Exception.Message); exit 1 }
