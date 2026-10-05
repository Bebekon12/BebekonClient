package com.bebekon.vpn

import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import org.tomlj.Toml
import java.util.Base64

class TrustTunnelTest {
    private fun profile(protocol: String = "http2") = TrustTunnelProfile.manual("Sweden", "vpn.example", "cert.example", "sni.example", "user", "p\"a\\ss/word", protocol)
    @Test fun manualProfileUsesDefaultPortAndSeparateCertificateName() {
        val node = profile(); assertEquals(443, node.port); assertEquals("SE", node.country)
        assertEquals("HTTP/2", node.transport); assertEquals("cert.example", node.config.getJSONObject("tls").getString("server_name"))
        assertEquals("sni.example", node.config.getJSONObject("trusttunnel").getString("custom_sni"))
        val config = Toml.parse(TrustTunnelProfile.config(node, 12345, "random-local-password"))
        assertFalse(config.errors().toString(), config.hasErrors()); assertTrue(config.getBoolean("killswitch_enabled")!!)
        assertEquals("p\"a\\ss/word", config.getString("endpoint.password")); assertEquals("127.0.0.1:12345", config.getString("listener.socks.address"))
        assertEquals("random-local-password", config.getString("listener.socks.password")); assertFalse(config.getBoolean("endpoint.skip_verification")!!)
    }
    @Test fun officialTomlImportsOnlyConnectionAndKeepsIpv6AndProtocol() {
        val text = """
            loglevel = "debug"
            exclusions = ["all"]
            [endpoint]
            hostname = "cert.example"
            addresses = ["[2001:db8::1]:443", "192.0.2.1:443"]
            username = "user"
            password = "secret"
            upstream_protocol = "http3"
            [listener.tun]
            name = "provider-device"
        """.trimIndent()
        val node = SubscriptionParser.parse(text).single(); assertEquals("HTTP/3", node.transport)
        assertFalse(node.config.has("listener")); assertFalse(node.config.has("exclusions")); assertEquals(2, node.config.getJSONObject("trusttunnel").getJSONArray("addresses").length())
        assertEquals(node.id, SubscriptionParser.parse(node.config.toString()).single().id)
    }
    private fun link(vararg tags: Pair<Int, String>): String {
        val bytes = java.io.ByteArrayOutputStream()
        for ((tag, value) in tags) { val data = value.toByteArray(); require(data.size < 64); bytes.write(tag); bytes.write(data.size); bytes.write(data) }
        return "tt://" + Base64.getUrlEncoder().withoutPadding().encodeToString(bytes.toByteArray())
    }
    @Test fun officialTlvLinkAndSubscription() {
        val endpoint = link(0 to "\u0001", 1 to "cert.example", 2 to "192.0.2.1:443", 5 to "user", 6 to "secret", 9 to "\u0001", 12 to "Sweden")
        val node = SubscriptionParser.parse(endpoint).single(); assertEquals("SE", node.country); assertEquals("192.0.2.1", node.host)
        val subscription = link(0 to "\u0002", 14 to "https://user:pass@vpn.example/servers")
        assertEquals("https://user:pass@vpn.example/servers", TrustTunnelProfile.link(subscription).second)
        assertThrows(IllegalArgumentException::class.java) { TrustTunnelProfile.link(link(0 to "\u0001", 14 to "https://vpn.example/servers")) }
    }
    @Test fun rejectsUnsafeTlsAndHostInjectionAndUnboundedNesting() {
        assertThrows(IllegalArgumentException::class.java) { TrustTunnelProfile.endpoint(profile().config.getJSONObject("trusttunnel").put("skip_verification", true)) }
        assertThrows(IllegalArgumentException::class.java) { TrustTunnelProfile.manual("Test", "vpn.example:443/path", "cert.example", "", "u", "p", "http2") }
        assertThrows(IllegalArgumentException::class.java) { TrustTunnelProfile.manual("Test", "vpn.example", "cert.example", "evil\n.example", "u", "p", "http2") }
        assertThrows(IllegalArgumentException::class.java) { TrustTunnelProfile.parseToml("[endpoint]\na=" + "[".repeat(100) + "0" + "]".repeat(100)) }
        assertThrows(IllegalArgumentException::class.java) { TrustTunnelProfile.parseToml("[" + "a.".repeat(100) + "b]\nx=1") }
        assertThrows(IllegalArgumentException::class.java) { TrustTunnelProfile.endpoint(profile().config.getJSONObject("trusttunnel").put("certificate", "-----BEGIN CERTIFICATE-----\ninvalid\n-----END CERTIFICATE-----")) }
    }
    @Test fun socksOverrideRetainsRulesAndNeverLeaksNativeCredentials() {
        val node = profile(); val saved = SavedState(subscriptions = listOf(Subscription(name = "Fixture", source = "", nodes = listOf(node))), selected = node.id, preferences = Preferences(routing = RoutingMode.RULES), rules = listOf(Rule(name = "Only this domain", kind = RuleKind.DOMAIN, values = listOf("example.org"))))
        val config = JSONObject(CoreConfig.build(saved, { JSONObject() }, tunnel = false, vpnOverride = json("type" to "socks", "server" to "127.0.0.1", "server_port" to 12345, "username" to "bebekon", "password" to "local-secret")))
        assertFalse(config.toString().contains("cert.example")); assertFalse(config.toString().contains("sni.example"))
        assertEquals("socks", config.getJSONArray("outbounds").objects().single { it.optString("tag") == "vpn" }.getString("type"))
        assertTrue(config.getJSONObject("route").getJSONArray("rules").toString().contains("example.org"))
        assertThrows(IllegalArgumentException::class.java) { CoreConfig.build(saved, { JSONObject() }, tunnel = false) }
    }
}
