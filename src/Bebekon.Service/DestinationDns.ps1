# Embedded in the installed helper. Only normalized ASCII hostnames arrive over stdin.
$ErrorActionPreference = 'Stop'
$owner = 'BebekonVPN.DestinationGuard.6caa6470'
$resolver = '172.29.255.2'
$hostsWanted = @(([Console]::In.ReadToEnd() | ConvertFrom-Json).domains)
$wanted = @(foreach ($domain in $hostsWanted) {
    if ($domain -notmatch '^(?=.{1,253}$)(?:[a-z0-9-]+\.)+[a-z0-9-]+$') { throw 'Invalid namespace' }
    $domain
    '.' + $domain
})
$rules = @(Get-DnsClientNrptRule)
$ours = @($rules | Where-Object Comment -eq $owner)
foreach ($rule in $rules) {
    if ($rule.Comment -ne $owner -and @($rule.Namespace | Where-Object { $_ -in $wanted }).Count) {
        throw 'Existing DNS policy for this namespace'
    }
}
if ($wanted.Count) {
    if ($ours.Count -gt 1) { throw 'Ambiguous owned DNS rules' }
    if ($ours.Count -eq 0) {
        Add-DnsClientNrptRule -Namespace $wanted -NameServers $resolver -Comment $owner -DisplayName 'Bebekon VPN destination protection' | Out-Null
    } elseif ((Compare-Object @($ours[0].Namespace) $wanted) -or (@($ours[0].NameServers) -join ',') -ne $resolver) {
        Set-DnsClientNrptRule -Name $ours[0].Name -Namespace $wanted -NameServers $resolver | Out-Null
    }
    $effective = @(Get-DnsClientNrptPolicy -Effective)
    foreach ($name in $wanted) {
        $matchesWanted = @($effective | Where-Object { $name -in $_.Namespace })
        if (!$matchesWanted.Count -or @($matchesWanted | Where-Object { (@($_.NameServers) -join ',') -ne $resolver }).Count) {
            throw 'DNS policy overridden'
        }
    }
    foreach ($rule in $effective) {
        if ((@($rule.NameServers) -join ',') -eq $resolver) { continue }
        foreach ($ns in $rule.Namespace) {
            foreach ($domain in $hostsWanted) {
                if ($ns.TrimStart('.').EndsWith('.' + $domain,[StringComparison]::OrdinalIgnoreCase)) {
                    throw 'A more specific DNS policy overrides a protected subdomain'
                }
            }
        }
    }
} else {
    foreach ($rule in $ours) { Remove-DnsClientNrptRule -Name $rule.Name -Force }
}
