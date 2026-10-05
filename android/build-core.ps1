param([string]$SourceDirectory, [string]$GoBinDirectory)
$ErrorActionPreference = 'Stop'
$androidRoot = $PSScriptRoot
if (-not $SourceDirectory) { $SourceDirectory = Join-Path $androidRoot '.core-source' }
$SourceDirectory = [IO.Path]::GetFullPath($SourceDirectory)
# Official sing-box v1.14.2, fixed independently of a moving branch or release tag.
$commit = 'af6e64c3b69e6132ebaee0e1a3d24e93903f6709'
if (-not (Test-Path -LiteralPath $SourceDirectory)) {
    & git clone --depth 1 --branch v1.14.2 https://github.com/SagerNet/sing-box.git $SourceDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Could not fetch sing-box.' }
}
$actual = (& git -C $SourceDirectory rev-parse HEAD).Trim()
if ($actual -ne $commit) { throw 'Unexpected sing-box commit. Use a clean v1.14.2 checkout.' }
if (-not $GoBinDirectory) { $GoBinDirectory = Join-Path (& go env GOPATH) 'bin' }
$env:GOBIN = $GoBinDirectory
& go install github.com/sagernet/gomobile/cmd/gomobile@v0.1.12
if ($LASTEXITCODE -ne 0) { throw 'gomobile installation failed.' }
& go install github.com/sagernet/gomobile/cmd/gobind@v0.1.12
if ($LASTEXITCODE -ne 0) { throw 'gobind installation failed.' }
$env:PATH = $GoBinDirectory + [IO.Path]::PathSeparator + $env:PATH
if ($env:JAVA_HOME) { $env:PATH = (Join-Path $env:JAVA_HOME 'bin') + [IO.Path]::PathSeparator + $env:PATH }
Copy-Item -LiteralPath (Join-Path $androidRoot 'core/bebekon.go') -Destination (Join-Path $SourceDirectory 'experimental/libbox/bebekon.go')
$generated = [IO.Path]::GetFullPath((Join-Path $SourceDirectory 'build'))
if (Test-Path -LiteralPath $generated) {
    if (-not $generated.StartsWith($SourceDirectory + [IO.Path]::DirectorySeparatorChar)) { throw 'Unexpected generated directory.' }
    Rename-Item -LiteralPath $generated -NewName ('build-previous-' + [DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))
}
New-Item -ItemType Directory (Join-Path $androidRoot 'app/libs') -Force | Out-Null
Push-Location $SourceDirectory
try {
    $securityPins = Get-Content -LiteralPath (Join-Path (Split-Path -Parent $androidRoot) 'core/security-pins.json') -Raw | ConvertFrom-Json
    $compilerVersion = ((& go version) -split ' ')[2].Replace('go','')
    if ([version]$compilerVersion -lt [version]$securityPins.goVersion) { throw 'Upgrade Go to the pinned security patch before building libbox.' }
    & go get @($securityPins.modules)
    if ($LASTEXITCODE -ne 0) { throw 'Native dependency security update failed.' }
    & gomobile init
    if ($LASTEXITCODE -ne 0) { throw 'gomobile init failed.' }
    $arguments = @('bind', '-target=android/arm64,android/arm,android/amd64', '-androidapi', '29', '-javapkg=io.nekohasekai', '-libname=box', '-trimpath', '-buildvcs=false', '-ldflags', '-X github.com/sagernet/sing-box/constant.Version=1.14.2 -checklinkname=0 -s -w -buildid=', '-tags', 'with_gvisor,with_quic,with_utls,with_clash_api,with_low_memory', '-o', (Join-Path $androidRoot 'app/libs/libbox.aar'), './experimental/libbox')
    & gomobile @arguments
    if ($LASTEXITCODE -ne 0) { throw 'libbox build failed.' }
} finally { Pop-Location }
