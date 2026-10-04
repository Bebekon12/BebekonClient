param([string]$Distribution = 'Ubuntu')
$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot -Parent
$dependency = Join-Path $workspace '.tools/trusttunnel'
$archive = Join-Path $dependency 'server.tar.gz'
$endpoint = Join-Path $dependency 'endpoint/trusttunnel-v1.1.0-linux-x86_64/trusttunnel_endpoint'
New-Item -ItemType Directory -Force -Path $dependency | Out-Null
if (!(Test-Path $archive)) {
    Invoke-WebRequest 'https://github.com/TrustTunnel/TrustTunnel/releases/download/v1.1.0/trusttunnel-v1.1.0-linux-x86_64.tar.gz' -OutFile $archive
}
if ((Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne '91c2ea3db7416a01b5258a4c047ec22890490bc55e1b194206031aa75144f0e7') { throw 'Official endpoint archive checksum mismatch.' }
function LinuxPath([string]$path) { (& wsl -d $Distribution --exec wslpath -a $path).Trim() }
if (!(Test-Path $endpoint)) {
    New-Item -ItemType Directory -Force (Join-Path $dependency 'endpoint') | Out-Null
    & wsl -d $Distribution --exec tar -xf (LinuxPath $archive) -C (LinuxPath (Join-Path $dependency 'endpoint'))
    if ($LASTEXITCODE -ne 0) { throw 'Endpoint extraction failed.' }
}
$fixture = Join-Path $workspace ('artifacts/trusttunnel-fixture-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $fixture | Out-Null
$linuxFixture = LinuxPath $fixture
$info = [System.Diagnostics.ProcessStartInfo]::new('wsl.exe')
$info.UseShellExecute = $false
$info.CreateNoWindow = $true
foreach ($arg in @('-d', $Distribution, '--exec', 'python3', (LinuxPath (Join-Path $workspace 'tests/fixtures/trusttunnel_endpoint.py')), $linuxFixture, (LinuxPath $endpoint))) { $info.ArgumentList.Add($arg) }
$helper = [System.Diagnostics.Process]::Start($info)
$previous = $env:BEBEKON_TRUSTTUNNEL_FIXTURE
try {
    $deadline = [datetime]::UtcNow.AddSeconds(15)
    while (!(Test-Path (Join-Path $fixture 'fixture.json'))) {
        if ($helper.HasExited -or [datetime]::UtcNow -ge $deadline) { throw "Fixture failed. Inspect $fixture/endpoint.log" }
        Start-Sleep -Milliseconds 100
    }
    $env:BEBEKON_TRUSTTUNNEL_FIXTURE = Join-Path $fixture 'fixture.json'
    & dotnet test (Join-Path $workspace 'tests/Bebekon.Tests') -c Release --nologo --filter 'FullyQualifiedName~TrustTunnel' --logger 'trx;LogFileName=trusttunnel-live.trx' --results-directory (Join-Path $workspace 'artifacts/tests')
    if ($LASTEXITCODE -ne 0) { throw 'TrustTunnel verification failed.' }
} finally {
    $env:BEBEKON_TRUSTTUNNEL_FIXTURE = $previous
    $pidFile = Join-Path $fixture 'pid'
    if (Test-Path $pidFile) { & wsl -d $Distribution --exec kill -TERM (Get-Content $pidFile) }
    if (!$helper.WaitForExit(10000)) { $helper.Kill($true) }
    $helper.Dispose()
    # Fixture credentials are disposable; remove its private key after the endpoint has exited.
    Remove-Item -LiteralPath (Join-Path $fixture 'key.pem') -ErrorAction SilentlyContinue
}
