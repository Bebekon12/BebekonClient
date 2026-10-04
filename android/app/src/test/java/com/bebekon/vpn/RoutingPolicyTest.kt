package com.bebekon.vpn

import org.junit.Assert.*
import org.junit.Test
import org.json.JSONObject

class RoutingPolicyTest {
    private fun state(rules: List<Rule>, mode: RoutingMode = RoutingMode.RULES): SavedState {
        val node = SubscriptionParser.link("vless://3bf154da-0ee6-4c45-b5f4-8512b4a2d2bd@vpn.example:443#Sweden")
        return SavedState(subscriptions = listOf(Subscription(name = "Test", source = "", nodes = listOf(node))), selected = node.id, rules = rules, preferences = Preferences(routing = mode))
    }
    private fun app(pkg: String, vpn: Boolean = true, created: Long = 10) = Rule(name = pkg, kind = RuleKind.APP, values = listOf(pkg), vpn = vpn, created = created)
    @Test fun selectedAppsOnlyUseAndroidAllowList() {
        val s = state(listOf(app("com.openai.chatgpt"), app("com.yandex.browser", false)))
        assertEquals(setOf("com.openai.chatgpt"), appTunnelPolicy(s).allowed)
        val config = JSONObject(CoreConfig.build(s, { error("No geo") }))
        assertEquals("vpn", config.getJSONObject("route").getString("final"))
        assertEquals("vpn-dns", config.getJSONObject("dns").getString("final"))
    }
    @Test fun trafficSurvivesCounterResetAndHistoryIsBounded() {
        val total = TrafficTotals()
        assertEquals(100L, total.update(100)); assertEquals(120L, total.update(120)); assertEquals(125L, total.update(5)); assertEquals(130L, total.update(10))
        total.beginCounter(); assertEquals(180L, total.update(50))
        var history = TrafficHistory()
        repeat(150) { history = history.sample(Session(down = it.toLong(), up = 2, totalDown = 130)) }
        assertEquals(120, history.down.size); assertEquals(30L, history.down.first()); assertEquals(130L, history.totalDown)
        assertEquals("1.500 ГБ", trafficGb(1_500_000_000))
    }
    @Test fun siteRulesMustNotCaptureUnselectedApps() {
        val s = state(listOf(app("com.yandex.browser", false), app("com.openai.chatgpt"), Rule(name = "Site", kind = RuleKind.DOMAIN, values = listOf("example.com"))))
        val policy = appTunnelPolicy(s)
        assertEquals(setOf("com.openai.chatgpt"), policy.allowed)
        assertFalse("Unchecked Gosuslugi keeps the physical network", "ru.minsvyaz.gosuslugi" in policy.allowed!!)
        val config = JSONObject(CoreConfig.build(s, { error("No geo") }))
        assertEquals("vpn", config.getJSONObject("route").getString("final"))
        assertEquals("vpn-dns", config.getJSONObject("dns").getString("final"))
        val shared = s.copy(preferences = s.preferences.copy(sitesInAllApps = true))
        assertNull(appTunnelPolicy(shared).allowed)
        assertEquals(setOf("com.yandex.browser"), appTunnelPolicy(shared).excluded)
        val sharedConfig = JSONObject(CoreConfig.build(shared, { error("No geo") }))
        assertEquals("direct", sharedConfig.getJSONObject("route").getString("final"))
        assertEquals("Unmatched sites retain direct DNS in shared site mode", "direct-dns", sharedConfig.getJSONObject("dns").getString("final"))
        val dnsApps = sharedConfig.getJSONObject("dns").getJSONArray("rules").objects().first { it.has("package_name") && "com.openai.chatgpt" in it.getJSONArray("package_name").strings() }
        assertEquals("vpn-dns", dnsApps.getString("server"))
    }
    @Test fun emptySelectionIsExplicitAndNewestAppActionWins() {
        assertEquals(emptySet<String>(), appTunnelPolicy(state(emptyList())).allowed)
        assertEquals(emptySet<String>(), appTunnelPolicy(state(listOf(app("com.test.browser"), app("com.test.browser", false, 20)))).allowed)
        assertNull(appTunnelPolicy(state(listOf(app("com.test.browser", false)), RoutingMode.ALL)).allowed)
        assertTrue(appTunnelPolicy(state(emptyList(), RoutingMode.ALL)).excluded.isEmpty())
    }
    @Test fun oldPreferencesAndNewSettingsRoundTrip() {
        assertEquals(Preferences(), Preferences.fromJson(JSONObject()))
        val p = Preferences(mapLocation = false, dnsResolver = DnsResolver.GOOGLE, mtu = 1280, connectionNotifications = false, checkUpdates = false, sitesInAllApps = true)
        assertEquals(p, Preferences.fromJson(p.toJson()))
        val c = JSONObject(CoreConfig.build(state(emptyList()).copy(preferences = p), { error("No geo") }))
        assertEquals(1280, c.getJSONArray("inbounds").getJSONObject(0).getInt("mtu"))
        assertEquals("8.8.8.8", c.getJSONObject("dns").getJSONArray("servers").getJSONObject(0).getString("server"))
    }
    @Test fun webAppUsesSiteRuleNotLauncherUid() {
        val s = state(listOf(app("org.chromium.webapk.chatgpt")))
        val resolved = resolveWebAppRules(s) { if (it == "org.chromium.webapk.chatgpt") WebApp("chatgpt.com", "com.yandex.browser") else null }
        assertEquals(RuleKind.DOMAIN, resolved.rules.single().kind)
        assertEquals(listOf("chatgpt.com"), resolved.rules.single().values)
        val policy = resolveWebAppPolicy(s) { if (it == "org.chromium.webapk.chatgpt") WebApp("chatgpt.com", "com.yandex.browser") else null }
        assertEquals(setOf("com.yandex.browser"), policy.allowed)
        assertFalse("Other apps must not be captured by WebAPK conversion", "ru.minsvyaz.gosuslugi" in policy.allowed!!)
        val excludedBrowser = s.copy(rules = s.rules + app("com.yandex.browser", false))
        assertTrue(runCatching { resolveWebAppRules(excludedBrowser) { if (it.startsWith("org.chromium.webapk")) WebApp("chatgpt.com", "com.yandex.browser") else null } }.isFailure)
    }
    @Test fun appOnlyAndSitesOnlyRemainDistinctWithPresetsAndExceptions() {
        val site = Rule(name = "Preset", kind = RuleKind.GEOSITE, values = listOf("openai"))
        val selected = state(listOf(app("com.openai.chatgpt"), site))
        assertEquals(setOf("com.openai.chatgpt"), appTunnelPolicy(selected).allowed)
        assertNull(appTunnelPolicy(state(listOf(site))).allowed)
        assertEquals(setOf("com.yandex.browser"), appTunnelPolicy(state(listOf(site, app("com.yandex.browser", false)))).excluded)
    }
    @Test fun pingResultsRiseAbovePendingNodesWithoutChangingSelectionOrSourceOrder() {
        val nodes = (0..3).map { Node("$it", "$it", "{}") }
        val results = mapOf("0" to PingResult(failed = true), "1" to PingResult(millis = 550), "2" to PingResult(millis = 120))
        assertEquals(listOf("2", "1", "0", "3"), nodesByPing(nodes, results).map { it.id })
        assertEquals(listOf("0", "1", "2", "3"), nodes.map { it.id })
    }
    @Test fun locationMustHaveValidRealCoordinates() {
        assertEquals(OriginPoint(37.6f, 55.7f, "RU"), parseOrigin("{\"success\":true,\"longitude\":37.6,\"latitude\":55.7,\"country_code\":\"RU\"}"))
        assertTrue(runCatching { parseOrigin("{\"success\":false}") }.isFailure)
        assertTrue(runCatching { parseOrigin("{\"success\":true,\"longitude\":181,\"latitude\":55}") }.isFailure)
        assertEquals(-2f, shortestLongitude(358f), .001f)
        assertTrue(mapSpan(-150f, OriginPoint(150f, 60f, "RU")) < 220f)
    }
}
