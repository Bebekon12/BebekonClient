package com.bebekon.vpn

import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import java.util.Base64

class SubscriptionParserTest {
    private val uuid = "3bf154da-0ee6-4c45-b5f4-8512b4a2d2bd"
    private val link = "vless://$uuid@vpn.example:443?security=tls&type=ws&path=%2Fvpn&host=front.example#%F0%9F%87%B8%F0%9F%87%AA%20Sweden"
    private fun b64(s: String) = Base64.getEncoder().encodeToString(s.toByteArray())
    @Test fun vlessWsAndCountry() { val n = SubscriptionParser.parse(link).single(); assertEquals("SE", n.country); assertEquals("WS", n.transport); assertEquals("front.example", n.config.getJSONObject("transport").getJSONObject("headers").getString("Host")); assertEquals("/vpn", n.config.getJSONObject("transport").getString("path")) }
    @Test fun base64Subscription() { assertEquals(SubscriptionParser.parse(link), SubscriptionParser.parse(b64(link))) }
    @Test fun base64UrlSafe() { assertEquals(1, SubscriptionParser.parse(Base64.getUrlEncoder().withoutPadding().encodeToString(link.toByteArray())).size) }
    @Test fun identityIgnoresRename() { assertEquals(SubscriptionParser.link(link).id, SubscriptionParser.link(link.substringBefore('#') + "#Updated").id) }
    @Test fun vmessJson() { val n = SubscriptionParser.link("vmess://" + b64(json("add" to "vpn.example", "port" to "443", "id" to uuid, "ps" to "Sweden", "net" to "grpc", "path" to "service", "tls" to "tls").toString())); assertEquals("service", n.config.getJSONObject("transport").getString("service_name")); assertEquals("vmess", n.config.getString("type")) }
    @Test fun shadowsocksSIP002() { val n = SubscriptionParser.link("ss://" + b64("aes-128-gcm:p@ss:word") + "@vpn.example:8388#Node"); assertEquals("p@ss:word", n.config.getString("password")); assertEquals(8388, n.port) }
    @Test fun shadowsocksLegacy() { assertEquals("shadowsocks", SubscriptionParser.link("ss://" + b64("aes-256-gcm:password@vpn.example:8388")).config.getString("type")) }
    @Test fun trojanTls() { val n = SubscriptionParser.link("trojan://secret@vpn.example:443?sni=tls.example#France"); assertEquals("secret", n.config.getString("password")); assertEquals("tls.example", n.config.getJSONObject("tls").getString("server_name")) }
    @Test fun hysteria2Salamander() { val n = SubscriptionParser.link("hy2://secret@vpn.example:443?obfs=salamander&obfs-password=obfs#Germany"); assertEquals("hysteria2", n.config.getString("type")); assertEquals("salamander", n.config.getJSONObject("obfs").getString("type")) }
    @Test fun hysteria1() { val n = SubscriptionParser.link("hysteria://vpn.example:443?auth=secret&upmbps=80&downmbps=100"); assertEquals(80, n.config.getInt("up_mbps")); assertEquals("secret", n.config.getString("auth_str")) }
    @Test fun clashYaml() { val n = SubscriptionParser.parse("proxies:\n  - name: Sweden\n    type: vless\n    server: vpn.example\n    port: 443\n    uuid: $uuid\n    tls: true\n    network: grpc\n    grpc-opts:\n      grpc-service-name: vpn").single(); assertEquals("GRPC", n.transport); assertEquals("SE", n.country) }
    @Test fun providerRulesNotImported() { val n = SubscriptionParser.parse(json("proxies" to array(listOf(json("name" to "Node", "type" to "trojan", "server" to "vpn.example", "port" to 443, "password" to "secret"))), "rules" to array(listOf("MATCH,DIRECT")), "external-controller" to "0.0.0.0:9090").toString()).single(); assertFalse(n.config.has("rules")); assertFalse(n.config.has("external-controller")) }
    @Test fun singBoxOnlyWhitelist() { val n = SubscriptionParser.parse(json("outbounds" to array(listOf(json("type" to "trojan", "server" to "vpn.example", "server_port" to 443, "password" to "secret", "tls" to json("enabled" to true, "certificate_path" to "/secret", "client_key_path" to "/private")), json("type" to "direct")))).toString()).single(); assertFalse(n.config.getJSONObject("tls").has("certificate_path")); assertFalse(n.config.getJSONObject("tls").has("client_key_path")) }
    @Test fun xrayConfig() {
        val peer = json("address" to "vpn.example", "port" to 443, "users" to array(listOf(json("id" to uuid))))
        val outbound = json("protocol" to "vless", "tag" to "Sweden", "settings" to json("vnext" to array(listOf(peer))), "streamSettings" to json("network" to "grpc", "security" to "tls", "grpcSettings" to json("serviceName" to "vpn")))
        val n = SubscriptionParser.parse(json("outbounds" to array(listOf(outbound))).toString()).single()
        assertEquals("GRPC", n.transport); assertEquals("SE", n.country)
    }
    @Test fun ultimaProfileRemarksRestoreCountriesWithoutChangingIdentity() {
        fun profile(title: String) = json("remarks" to title, "outbounds" to array(listOf(
            json("protocol" to "vless", "tag" to "proxy", "settings" to json("vnext" to array(listOf(json("address" to "vpn.example", "port" to 443, "users" to array(listOf(json("id" to uuid)))))))),
            json("protocol" to "freedom", "tag" to "direct")
        )))
        val first = SubscriptionParser.parse(array(listOf(profile("🇸🇪 Швеция"))).toString()).single()
        val second = SubscriptionParser.parse(array(listOf(profile("🇱🇻 Латвия #2"))).toString()).single()
        assertEquals("🇸🇪 Швеция", first.name); assertEquals("SE", first.country)
        assertEquals("🇱🇻 Латвия #2", second.name); assertEquals("LV", second.country)
        assertEquals(first.id, second.id)
        assertEquals(first.outbound, second.outbound)
    }
    @Test fun singularSingBoxProfileUsesRemarks() {
        val config = json("remarks" to "Germany", "outbounds" to array(listOf(json("type" to "trojan", "tag" to "proxy", "server" to "vpn.example", "server_port" to 443, "password" to "secret"))))
        assertEquals("DE", SubscriptionParser.parse(config.toString()).single().country)
    }
    @Test fun separateOutboundsRetainNamesWithinNamedProfile() {
        val config = json("remarks" to "Provider", "outbounds" to array(listOf("Sweden", "Latvia").map { json("type" to "trojan", "tag" to it, "server" to "$it.example", "server_port" to 443, "password" to "secret") }))
        assertEquals(listOf("SE", "LV"), SubscriptionParser.parse(config.toString()).map { it.country })
    }
    @Test fun latencyColorsUseTheMeasurementMethodAndSurviveLoading() {
        assertEquals(PingQuality.GOOD, PingResult(millis = 300).quality)
        assertEquals(PingQuality.FAIR, PingResult(millis = 301).quality)
        assertEquals(PingQuality.FAIR, PingResult(millis = 600, method = PingMethod.HTTPS_HEAD).quality)
        assertEquals(PingQuality.POOR, PingResult(millis = 601).quality)
        assertEquals(PingQuality.GOOD, PingResult(millis = 100, method = PingMethod.TCP).quality)
        assertEquals(PingQuality.FAIR, PingResult(millis = 250, method = PingMethod.TCP).quality)
        assertEquals(PingQuality.POOR, PingResult(millis = 300, method = PingMethod.TCP).quality)
        val previous = PingResult(millis = 260)
        assertEquals(previous.quality, previous.copy(running = true, queued = true).quality)
        assertEquals(PingQuality.POOR, PingResult(failed = true).quality)
    }
    @Test fun existingPreferencesMigrateToCloudflareAndRetainUserSettings() {
        val old = Preferences(theme = ThemeChoice.LIGHT, routing = RoutingMode.RULES, ping = PingMethod.TCP).toJson().apply { remove("pingTarget") }
        val migrated = Preferences.fromJson(old)
        assertEquals(ThemeChoice.LIGHT, migrated.theme); assertEquals(RoutingMode.RULES, migrated.routing)
        assertEquals(PingMethod.TCP, migrated.ping); assertEquals(PingTarget.CLOUDFLARE, migrated.pingTarget)
        assertEquals(PingTarget.GOOGLE, Preferences.fromJson(migrated.copy(pingTarget = PingTarget.GOOGLE).toJson()).pingTarget)
    }
    @Test fun xhttpIsVisibleWithReason() { assertTrue(SubscriptionParser.link("vless://$uuid@vpn.example:443?type=xhttp").unsupported.contains("XHTTP")) }
    @Test fun ipv6() { assertEquals("2001:db8::1", SubscriptionParser.link("trojan://secret@[2001:db8::1]:443").host) }
    @Test(expected = IllegalArgumentException::class) fun duplicateParameterRejected() { SubscriptionParser.link("vless://$uuid@vpn.example:443?type=ws&type=grpc") }
    @Test(expected = IllegalArgumentException::class) fun htmlRejected() { SubscriptionParser.parse("<html>login</html>") }
    @Test fun maliciousYamlRejected() { assertTrue(runCatching { SubscriptionParser.parse("proxies:\n - !!java.lang.Runtime {}") }.isFailure) }
    @Test fun savedStateRoundTrip() { val n = SubscriptionParser.link(link); val s = SavedState(subscriptions = listOf(Subscription(name = "Provider", source = "private", nodes = listOf(n))), selected = n.id, favorites = setOf(n.id), rules = listOf(Rule(name = "OpenAI", kind = RuleKind.DOMAIN, values = listOf("openai.com")))); assertEquals(s, SavedState.fromJson(s.toJson())) }
    @Test fun emptySelectionSurvivesRestart() { assertNull(SavedState.fromJson(SavedState().toJson()).selected) }
    @Test fun newerRulesWin() { val n = SubscriptionParser.link(link); val s = SavedState(subscriptions = listOf(Subscription(name = "P", source = "", nodes = listOf(n))), selected = n.id, preferences = Preferences(routing = RoutingMode.RULES), rules = listOf(Rule(name = "Old", kind = RuleKind.DOMAIN, values = listOf("example.com"), created = 10), Rule(name = "New", kind = RuleKind.DOMAIN, values = listOf("example.com"), vpn = false, created = 20))); val c = JSONObject(CoreConfig.build(s, { error("Unexpected geo") })); assertEquals("direct", c.getJSONObject("route").getJSONArray("rules").getJSONObject(3).getString("outbound")); assertEquals("direct", c.getJSONObject("route").getString("final")) }
    @Test fun dnsMatchesDomainRouting() { val n = SubscriptionParser.link(link); val s = SavedState(subscriptions = listOf(Subscription(name = "P", source = "", nodes = listOf(n))), selected = n.id, preferences = Preferences(routing = RoutingMode.RULES), rules = listOf(Rule(name = "ChatGPT", kind = RuleKind.DOMAIN, values = listOf("chatgpt.com")))); val c = JSONObject(CoreConfig.build(s, { error("Unexpected geo") })); assertEquals("vpn-dns", c.getJSONObject("dns").getJSONArray("rules").getJSONObject(0).getString("server")) }
    @Test(expected = IllegalArgumentException::class) fun websiteValueCannotBeUrl() { CoreConfig.validateRule(Rule(name = "Invalid", kind = RuleKind.DOMAIN, values = listOf("https://example.com/path"))) }
    @Test(expected = IllegalArgumentException::class) fun windowsProcessNotAndroidPackage() { CoreConfig.validateRule(Rule(name = "Telegram", kind = RuleKind.APP, values = listOf("Telegram.exe"))) }
}
