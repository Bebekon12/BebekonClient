package com.bebekon.vpn

import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class TlsFragmentTest {
    @Test fun onlyDirectConnectionsAreFragmented() {
        val node = SubscriptionParser.link("vless://3bf154da-0ee6-4c45-b5f4-8512b4a2d2bd@a.example:443#A")
        val rules = listOf(Rule(name = "yt", kind = RuleKind.DOMAIN, values = listOf("youtube.com"), vpn = false, created = 2), Rule(name = "ai", kind = RuleKind.DOMAIN, values = listOf("openai.com"), created = 1))
        fun config(routing: RoutingMode, fragment: Boolean) = JSONObject(CoreConfig.build(SavedState(subscriptions = listOf(Subscription(name = "s", source = "", nodes = listOf(node))), selected = node.id, rules = rules, preferences = Preferences(routing = routing, tlsFragment = fragment)), { error("No geo") }))
        val list = config(RoutingMode.RULES, true).getJSONObject("route").getJSONArray("rules").let { a -> (0 until a.length()).map(a::getJSONObject) }
        assertTrue(list.single { it.optJSONArray("domain_suffix")?.optString(0) == "youtube.com" }.getBoolean("tls_fragment"))
        assertFalse(list.single { it.optJSONArray("domain_suffix")?.optString(0) == "openai.com" }.has("tls_fragment"))
        assertEquals("direct", list.last().getString("outbound")); assertTrue(list.last().getBoolean("tls_fragment"))
        assertTrue(list.none { it.optString("outbound") == "vpn" && it.has("tls_fragment") })
        assertFalse(config(RoutingMode.ALL, true).toString().contains("tls_fragment"))
        assertFalse(config(RoutingMode.RULES, false).toString().contains("tls_fragment"))
    }
}
