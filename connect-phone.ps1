# Reconnect the phone to the dedicated adb server (port 5860, avoids the traccar
# conflict on 5037). Usage:  .\connect-phone.ps1 41733      (port from Wireless debugging screen)
param([Parameter(Mandatory=$true)][string]$Port, [string]$Ip = "192.168.0.212")

$env:ANDROID_ADB_SERVER_PORT = "5860"
$adb = "C:\Program Files (x86)\Android\android-sdk\platform-tools\adb.exe"

& $adb -P 5860 connect "${Ip}:${Port}"
Start-Sleep -Milliseconds 500
& $adb -P 5860 devices -l
