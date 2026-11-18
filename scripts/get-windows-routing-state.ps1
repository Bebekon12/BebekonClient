param([System.Net.IPAddress]$CorporateAddress)

$ErrorActionPreference = 'Stop'
$netBird = $null
$image = Get-ItemPropertyValue -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\NetBird' -Name 'ImagePath' -ErrorAction SilentlyContinue
if ($image) {
    $exe = [regex]::Match($image, '^\s*"?(?<exe>[^"]+?\.exe)(?="|\s|$)', 'IgnoreCase').Groups['exe'].Value
    if ($exe -and (Test-Path -LiteralPath $exe -PathType Leaf)) {
        $raw = & $exe status --json 2>$null
        if ($LASTEXITCODE -eq 0) {
            $state = $raw | Out-String | ConvertFrom-Json
            $netBird = [pscustomobject]@{
                State = $state.daemonStatus
                Version = $state.daemonVersion
                Peer = $state.netbirdIp
                ManagementConnected = $state.management.connected
                SignalConnected = $state.signal.connected
                Networks = $state.networks
            }
        }
    }
}

$peer = if ($netBird.Peer) { $netBird.Peer.Split('/')[0] }
$peerInterfaces = @(Get-NetIPAddress -AddressFamily IPv4,IPv6 | Where-Object IPAddress -eq $peer | Select-Object -ExpandProperty InterfaceIndex)
$interfaces = @(Get-NetIPInterface | Where-Object {
    $_.ConnectionState -eq 'Connected' -or $_.InterfaceAlias -eq 'Bebekon' -or $_.InterfaceIndex -in $peerInterfaces
} | Select-Object InterfaceAlias,InterfaceIndex,AddressFamily,InterfaceMetric,ConnectionState)
$indices = @($interfaces.InterfaceIndex | Sort-Object -Unique)
$dns = @(Get-DnsClientServerAddress | Where-Object InterfaceIndex -in $indices |
    Select-Object InterfaceAlias,InterfaceIndex,AddressFamily,ServerAddresses)
$nrpt = @(Get-DnsClientNrptPolicy -Effective | Select-Object Namespace,NameServers,DnsSecValidationRequired)
$routes = @(Get-NetRoute -PolicyStore ActiveStore | Where-Object {
    $_.DestinationPrefix -in @('0.0.0.0/0', '::/0') -or $_.InterfaceAlias -eq 'Bebekon' -or $_.InterfaceIndex -in $peerInterfaces
} | Select-Object InterfaceAlias,InterfaceIndex,DestinationPrefix,NextHop,RouteMetric)
$corporateRoute = if ($CorporateAddress) {
    @(Find-NetRoute -RemoteIPAddress $CorporateAddress.ToString() |
        Select-Object InterfaceAlias,InterfaceIndex,DestinationPrefix,NextHop,RouteMetric,IPAddress)
}

[pscustomobject]@{
    CapturedAt = [DateTimeOffset]::Now
    NetBird = $netBird
    Interfaces = $interfaces
    Dns = $dns
    EffectiveNrpt = $nrpt
    Routes = $routes
    CorporateRoute = $corporateRoute
} | ConvertTo-Json -Depth 6
