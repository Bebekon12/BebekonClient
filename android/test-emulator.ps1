param([Parameter(Mandatory)][string]$FixtureExecutable, [string]$Serial = 'emulator-5580')
$ErrorActionPreference = 'Stop'
if ($Serial -notmatch '^emulator-\d+$') { throw 'This script only operates on a disposable Android emulator.' }
if (-not $env:ANDROID_HOME) { throw 'Set ANDROID_HOME.' }
$adb = Join-Path $env:ANDROID_HOME 'platform-tools/adb.exe'
$output = Join-Path $PSScriptRoot 'app/build/validation'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start(); $port = $listener.LocalEndpoint.Port; $listener.Stop()
$config = @{log=@{disabled=$true}; inbounds=@(@{type='vless'; tag='fixture'; listen='127.0.0.1'; listen_port=$port; users=@(@{uuid='3bf154da-0ee6-4c45-b5f4-8512b4a2d2bd'})}); outbounds=@(@{type='direct'; tag='direct'})}
$configPath = Join-Path $output 'fixture.json'
$config | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $configPath -Encoding utf8
$fixture = Start-Process -FilePath $FixtureExecutable -ArgumentList @('run','-c',('"'+$configPath+'"')) -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $output 'fixture.log') -RedirectStandardError (Join-Path $output 'fixture-error.log')
try {
    & $adb -s $Serial install -r (Join-Path $PSScriptRoot 'app/build/outputs/apk/debug/app-x86_64-debug.apk')
    if ($LASTEXITCODE -ne 0) { throw 'App install failed.' }
    & $adb -s $Serial install -r (Join-Path $PSScriptRoot 'app/build/outputs/apk/androidTest/debug/app-debug-androidTest.apk')
    if ($LASTEXITCODE -ne 0) { throw 'Test APK install failed.' }
    & $adb -s $Serial shell appops set com.bebekon.vpn ACTIVATE_VPN allow
    & $adb -s $Serial shell am instrument -w -e fixture_port $port com.bebekon.vpn.test/androidx.test.runner.AndroidJUnitRunner | Tee-Object -FilePath (Join-Path $output 'instrumentation.txt')
    if ($LASTEXITCODE -ne 0 -or -not (Select-String -LiteralPath (Join-Path $output 'instrumentation.txt') -Pattern '^OK \(' -Quiet)) { throw 'Android instrumentation tests failed.' }
} finally { if (-not $fixture.HasExited) { Stop-Process -Id $fixture.Id } }
