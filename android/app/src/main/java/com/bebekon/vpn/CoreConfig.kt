package com.bebekon.vpn

import org.json.JSONObject

object CoreConfig {
    fun build(state: SavedState, geo: (String) -> JSONObject, tunnel: Boolean = true, policy: AppTunnelPolicy = appTunnelPolicy(state), vpnOverride: JSONObject? = null, health: TunnelCheck.Endpoint? = null): String {
        val node = state.selectedNode ?: error("Выберите сервер")
        require(node.unsupported.isEmpty()) { node.unsupported }
        // The outer connection may still need IPv6 (e.g. an IPv6-only mobile network).
        // It is independent from which address families apps see inside the tunnel.
        if (node.config.optString("type") == "trusttunnel") { TrustTunnelProfile.validate(node); require(vpnOverride != null) { "Запустите модуль TrustTunnel" } }
        fun outbound(config: JSONObject, tag: String) = config.also { val reason = ConnectionSafety.reason(it); require(reason.isEmpty()) { reason }; it.put("tag", tag); it.put("domain_resolver", json("server" to "direct-dns", "strategy" to "prefer_ipv4")); it.put("connect_timeout", "10s") }
        val vpn = outbound(vpnOverride ?: node.config, "vpn")
        // Auto mode: "vpn" becomes a urltest group. sing-box measures the servers, uses the
        // fastest working one and leaves a server that fails. Probes keep testing one server.
        val auto = if (tunnel && vpnOverride == null) autoNodes(state) else emptyList()
        val proxies = if (auto.size < 2) listOf(vpn) else listOf(json("type" to "urltest", "tag" to "vpn", "outbounds" to array(auto.indices.map { "auto-$it" }), "url" to "https://www.gstatic.com/generate_204", "interval" to "5m", "tolerance" to 50, "idle_timeout" to "30m")) +
            auto.mapIndexed { i, n -> outbound(n.config, "auto-$i") }
        val rules = mutableListOf<JSONObject>()
        val dnsRules = mutableListOf<JSONObject>()
        val sets = mutableMapOf<String, JSONObject>()
        // A probe dials the VPN outbound directly; routing rules and geo sets would only
        // slow its start inside the measured timeout.
        if (tunnel) {
            // The service's own tunnel check (TunnelCheck) always measures the VPN path.
            if (health != null) rules += json("inbound" to array(listOf("health")), "outbound" to "vpn")
            rules += json("action" to "sniff", "timeout" to "300ms")
            rules += json("protocol" to "dns", "action" to "hijack-dns")
            if (state.preferences.allowLan) rules += json("ip_is_private" to true, "outbound" to "direct")
        }
        if (tunnel && state.preferences.routing == RoutingMode.RULES) {
            // Last authored exception wins. UI and execution share this precedence.
            for (rule in state.rules.sortedByDescending { it.created }) {
                validateRule(rule)
                val target = if (rule.vpn) "vpn" else "direct"
                val field = rule.kind.field
                // Site-only browsers get their direct fallback after every site rule (below).
                if (rule.kind == RuleKind.APP && rule.values.all { it in policy.siteOnlyBrowsers }) continue
                val values = if (rule.kind == RuleKind.APP) rule.values.filterNot { it in policy.siteOnlyBrowsers } else if (rule.kind in listOf(RuleKind.GEOSITE, RuleKind.GEOIP)) rule.values.map { val key = (if (rule.kind == RuleKind.GEOSITE) "geosite-" else "geoip-") + it.lowercase(); sets.getOrPut(key) { json("type" to "inline", "tag" to key, "rules" to geo(key).getJSONArray("rules")) }; key } else rule.values
                rules += json(field to array(values), "outbound" to target).also { if (!rule.vpn && state.preferences.tlsFragment) it.put("tls_fragment", true) }
                if (rule.kind in listOf(RuleKind.DOMAIN, RuleKind.KEYWORD, RuleKind.GEOSITE, RuleKind.APP)) dnsRules += json(field to array(values), "server" to if (rule.vpn) "vpn-dns" else "direct-dns")
            }
        }
        // A WebAPK shares the browser's UID. Including its browser in Android's allowlist
        // must not turn an unselected browser into a whole-app VPN rule. Authored site rules
        // above still win, then unrelated traffic from that browser goes directly.
        if (tunnel && policy.siteOnlyBrowsers.isNotEmpty()) {
            rules += json("package_name" to array(policy.siteOnlyBrowsers), "outbound" to "direct").also { if (state.preferences.tlsFragment) it.put("tls_fragment", true) }
            dnsRules += json("package_name" to array(policy.siteOnlyBrowsers), "server" to "direct-dns")
        }
        // Pure web-app selection needs a direct fallback even without owner metadata.
        // Native selected apps retain the VPN fallback for Android resolver/isolated traffic.
        val defaultVpn = state.preferences.routing == RoutingMode.ALL || policy.appOnly && policy.nativeApps.isNotEmpty()
        // Fragmentation only ever applies to direct connections; inside the VPN it would only add delay.
        if (tunnel && !defaultVpn && state.preferences.tlsFragment) rules += json("outbound" to "direct", "tls_fragment" to true)
        val route = json("rules" to array(rules), "final" to if (defaultVpn) "vpn" else "direct", "auto_detect_interface" to true, "default_domain_resolver" to "direct-dns", "rule_set" to array(sets.values))
        val resolver = state.preferences.dnsResolver
        val dns = json("servers" to array(listOf(
            // The physical network's resolver (see SystemDns): public DoH is throttled on many mobile networks.
            json("type" to "local", "tag" to "direct-dns"),
            json("type" to "https", "tag" to "vpn-dns", "server" to resolver.address, "server_port" to 443, "path" to "/dns-query", "tls" to json("enabled" to true, "server_name" to resolver.hostname), "detour" to "vpn")
        )), "rules" to array(dnsRules), "final" to if (defaultVpn) "vpn-dns" else "direct-dns", "strategy" to if (state.preferences.ipv6) "prefer_ipv4" else "ipv4_only", "reverse_mapping" to true)
        val log = json("disabled" to !(state.preferences.routingDiagnostics || BuildConfig.DEBUG && node.host == "10.0.2.2"), "level" to "debug")
        val addresses = listOf("172.19.0.1/30") + if (state.preferences.ipv6) listOf("fdfe:dcba:9876::1/126") else emptyList()
        return json("log" to log, "dns" to dns, "inbounds" to array(if (tunnel) listOfNotNull(json("type" to "tun", "tag" to "tun", "address" to array(addresses), "mtu" to state.preferences.mtu, "auto_route" to true, "strict_route" to true, "stack" to "gvisor"),
            health?.let { json("type" to "mixed", "tag" to "health", "listen" to "127.0.0.1", "listen_port" to it.port, "users" to array(listOf(json("username" to "bebekon", "password" to it.password)))) }) else emptyList()), "outbounds" to array(proxies + json("type" to "direct", "tag" to "direct")), "route" to route, "experimental" to json("clash_api" to json())).toString()
    }
    /**
     * Servers the auto group measures: the selected one first (used until the first measurement),
     * then the other visible servers, at most 64. TrustTunnel needs its own native client per
     * server, so it is not part of the group; with a TrustTunnel server selected Auto is off.
     */
    fun autoNodes(state: SavedState): List<Node> {
        val selected = state.selectedNode ?: return emptyList()
        if (!state.preferences.autoServer || selected.config.optString("type") == "trusttunnel") return emptyList()
        return (listOf(selected) + state.visibleNodes).asSequence().distinctBy { it.id }.filter(::autoEligible).take(64).toList()
    }
    fun autoEligible(node: Node) = node.unsupported.isEmpty() && node.config.optString("type") != "trusttunnel" && ConnectionSafety.reason(node.config).isEmpty()
    /** Changes when the auto pool changes (refresh, hidden subscription), not when pings change. */
    fun autoKey(state: SavedState) = autoNodes(state).map { it.outbound }.sorted().joinToString("\n")
    /** Accepts what users paste: "https://www.example.com/page?q" → "example.com". */
    fun siteHost(value: String): String {
        var v = value.trim().substringAfter("://").substringBefore('/').substringBefore('?').substringBefore('#').substringAfterLast('@')
        if (v.count { it == ':' } == 1) v = v.substringBefore(':')
        v = v.trimEnd('.').removePrefix("*.").trimStart('.').lowercase()
        // "www." would leave out the bare domain and every other subdomain of the site.
        return if (v.startsWith("www.") && v.count { it == '.' } >= 2) v.removePrefix("www.") else v
    }
    fun validateRule(r: Rule) {
        require(r.values.isNotEmpty() && r.values.size <= 256 && r.values.all { it.isNotBlank() && it.length <= 253 && it.none(Char::isISOControl) }) { "Укажите значение правила" }
        when (r.kind) {
            RuleKind.DOMAIN -> require(r.values.all { Regex("[A-Za-z0-9_\\-.]+|[\\p{L}0-9_\\-.]+").matches(it) && !it.startsWith('.') }) { "Укажите домен без https:// и пути" }
            RuleKind.APP -> require(r.values.all { Regex("[A-Za-z0-9_]+(?:\\.[A-Za-z0-9_]+)+").matches(it) && !it.endsWith(".exe", true) }) { "Нужно имя пакета Android, например org.telegram.messenger" }
            RuleKind.CIDR -> r.values.forEach { value -> val p = value.split('/'); require(p.size == 2 && p[1].toIntOrNull() in 0..(if (p[0].contains(':')) 128 else 32) && Regex("[0-9a-fA-F:.]+").matches(p[0])) { "Укажите IP-подсеть, например 192.168.1.0/24" } }
            RuleKind.GEOSITE, RuleKind.GEOIP -> require(r.values.all { Regex("[a-z0-9-]+").matches(it) }) { "Некорректное имя гео-набора" }
            else -> Unit
        }
    }
}
