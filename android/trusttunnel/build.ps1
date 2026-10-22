param([string[]]$Architectures = @('x86_64','arm64-v8a','armeabi-v7a'), [string]$CacheDirectory)
$ErrorActionPreference = 'Stop'
if (-not $Architectures -or @($Architectures | Where-Object { $_ -notin @('x86_64','arm64-v8a','armeabi-v7a') }).Count) { throw 'Unsupported Android architecture.' }
$adapter = $PSScriptRoot.Replace('\','/')
$output = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../app/src/main/jniLibs')).Replace('\','/')
New-Item -ItemType Directory -Path $output -Force | Out-Null
& docker build --tag bebekon-trusttunnel-build:local --file (Join-Path $PSScriptRoot 'Dockerfile') $PSScriptRoot
if ($LASTEXITCODE -ne 0) { throw 'TrustTunnel build image failed.' }
# A host directory (CI) can be cached between builds; locally a named Docker volume keeps the cache.
$work = if ($CacheDirectory) { New-Item -ItemType Directory -Path $CacheDirectory -Force | Out-Null; [IO.Path]::GetFullPath($CacheDirectory).Replace('\','/') } else { 'bebekon-trusttunnel-build-cache-v1' }
& docker run --rm --memory 3g --memory-swap 4g -e ('TT_ABIS=' + ($Architectures -join ' ')) -v ($work + ':/work') -v ($adapter + ':/input:ro') -v ($output + ':/output') bebekon-trusttunnel-build:local bash /input/build.sh
if ($LASTEXITCODE -ne 0) { throw 'TrustTunnel native build failed.' }
Get-ChildItem -LiteralPath $output -Recurse -Filter '*.so' | Get-FileHash -Algorithm SHA256
