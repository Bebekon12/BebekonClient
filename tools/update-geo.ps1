param([string]$SiteRef='417ea33286a8c3a77fdce9b68ce25d58565dad43', [string]$IpRef='7fe82a879ad2666526730c195b55a6d8d9147908')
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$destination=Join-Path $repo 'resources/geo'
$cache=Join-Path $repo 'artifacts/geo-source'
New-Item -ItemType Directory -Force $destination,$cache | Out-Null
$records=@()
$groups=@{geosite=@('anthropic','discord','facebook','google','instagram','netflix','openai','telegram','tiktok','twitch','twitter','whatsapp','youtube'); geoip=@('ru','de','us','nl','lv','lt','se','gb','ee','fr','pl','ua','cn','fi','kz')}
foreach($kind in @('geosite','geoip')) {
  $ref=if($kind -eq 'geosite') {$SiteRef} else {$IpRef}
  $repository=if($kind -eq 'geosite') {'sing-geosite'} else {'sing-geoip'}
  foreach($tag in $groups[$kind]) {
    $name="$kind-$tag"
    $url="https://raw.githubusercontent.com/SagerNet/$repository/$ref/$name.srs"
    $binary=Join-Path $cache "$name.srs"
    Invoke-WebRequest -Uri $url -OutFile $binary
    $target=Join-Path $destination "$name.json"
    & (Join-Path $repo 'core/sing-box.exe') rule-set decompile $binary -o $target
    if($LASTEXITCODE -ne 0) {throw "Cannot decompile $name"}
    $records+=@{name=$name;source=$url;sha256=(Get-FileHash $binary -Algorithm SHA256).Hash;jsonSha256=(Get-FileHash $target -Algorithm SHA256).Hash}
  }
}
$url='https://core.telegram.org/resources/cidr.txt'
$cidr=(Invoke-WebRequest $url).Content -split '\s+' | Where-Object {$_}
@{version=3;rules=@(@{ip_cidr=@($cidr)})} | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $destination 'geoip-telegram.json') -Encoding utf8
$records+=@{name='geoip-telegram';source=$url;jsonSha256=(Get-FileHash (Join-Path $destination 'geoip-telegram.json') -Algorithm SHA256).Hash}
@{updated=[DateTime]::UtcNow.ToString('yyyy-MM-dd');sources=$records} | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $destination 'sources.json') -Encoding utf8
Write-Output "Bundled $($records.Count) geo sets."
