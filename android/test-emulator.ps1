param([Parameter(Mandatory)][string]$FixtureExecutable, [string]$Serial = 'emulator-5580', [string]$ProviderFixture, [string]$UpdateFixture)
$ErrorActionPreference = 'Stop'
if ($Serial -notmatch '^emulator-\d+$') { throw 'This script only operates on a disposable Android emulator.' }
if (-not $env:ANDROID_HOME) { throw 'Set ANDROID_HOME.' }
$adb = Join-Path $env:ANDROID_HOME 'platform-tools/adb.exe'
$output = Join-Path $PSScriptRoot 'app/build/validation'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start(); $port = $listener.LocalEndpoint.Port; $listener.Stop()
$listener.Start(); $streamPort = $listener.LocalEndpoint.Port; $listener.Stop()
$stream = Start-Process -FilePath (Get-Command python).Source -ArgumentList @(('"'+(Join-Path $PSScriptRoot 'fixtures/stream_server.py')+'"'), $streamPort) -PassThru -WindowStyle Hidden
$config = @{log=@{disabled=$true}; inbounds=@(@{type='vless'; tag='fixture'; listen='127.0.0.1'; listen_port=$port; users=@(@{uuid='3bf154da-0ee6-4c45-b5f4-8512b4a2d2bd'})}); outbounds=@(@{type='direct'; tag='direct'}); route=@{rules=@(@{ip_cidr=@('198.18.0.10/32'); action='route'; outbound='direct'; override_address='127.0.0.1'; override_port=$streamPort})}}
$configPath = Join-Path $output 'fixture.json'
$config | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $configPath -Encoding utf8
$fixture = Start-Process -FilePath $FixtureExecutable -ArgumentList @('run','-c',('"'+$configPath+'"')) -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $output 'fixture.log') -RedirectStandardError (Join-Path $output 'fixture-error.log')
try {
    # Reinstalling over a retained emulator can leave SystemUI holding the old
    # TileService binder/state. The live test adds a fresh tile after connection.
    & $adb -s $Serial shell cmd statusbar remove-tile com.bebekon.vpn/.VpnTileService
    & $adb -s $Serial install -r (Join-Path $PSScriptRoot 'app/build/outputs/apk/debug/app-debug.apk')
    if ($LASTEXITCODE -ne 0) { throw 'App install failed.' }
    & $adb -s $Serial install -r (Join-Path $PSScriptRoot 'app/build/outputs/apk/androidTest/debug/app-debug-androidTest.apk')
    if ($LASTEXITCODE -ne 0) { throw 'Test APK install failed.' }
    foreach ($hostFlavor in @('browser', 'probe')) {
        & $adb -s $Serial install -r (Join-Path $PSScriptRoot "webapp-fixture/build/outputs/apk/$hostFlavor/debug/webapp-fixture-$hostFlavor-debug.apk")
        if ($LASTEXITCODE -ne 0) { throw 'WebAPK metadata fixture install failed. Build :webapp-fixture:assembleDebug first.' }
    }
    & $adb -s $Serial shell appops set com.bebekon.vpn ACTIVATE_VPN allow
    $arguments = @('-s', $Serial, 'shell', 'am', 'instrument', '-w', '-e', 'fixture_port', $port)
    if ($ProviderFixture) {
        & $adb -s $Serial push $ProviderFixture /data/local/tmp/bebekon-provider-diagnostic.json
        if ($LASTEXITCODE -ne 0) { throw 'Provider fixture copy failed.' }
        $arguments += @('-e', 'provider_fixture', '/data/local/tmp/bebekon-provider-diagnostic.json')
    }
    if ($UpdateFixture) {
        & $adb -s $Serial push $UpdateFixture /data/local/tmp/bebekon-update-fixture.apk
        if ($LASTEXITCODE -ne 0) { throw 'Update fixture copy failed.' }
        $arguments += @('-e', 'update_fixture', '/data/local/tmp/bebekon-update-fixture.apk')
    }
    $arguments += 'com.bebekon.vpn.test/androidx.test.runner.AndroidJUnitRunner'
    & $adb @arguments | Tee-Object -FilePath (Join-Path $output 'instrumentation.txt')
    if ($LASTEXITCODE -ne 0 -or -not (Select-String -LiteralPath (Join-Path $output 'instrumentation.txt') -Pattern '^OK \(' -Quiet)) { throw 'Android instrumentation tests failed.' }
} finally {
    if ($ProviderFixture) { & $adb -s $Serial shell rm -f /data/local/tmp/bebekon-provider-diagnostic.json }
    if ($UpdateFixture) { & $adb -s $Serial shell rm -f /data/local/tmp/bebekon-update-fixture.apk }
    if (-not $fixture.HasExited) { Stop-Process -Id $fixture.Id }
    if (-not $stream.HasExited) { Stop-Process -Id $stream.Id }
}
