package com.bebekon.vpn

import org.junit.Assert.*
import org.junit.Test

class AndroidUpdateTest {
    @Test fun downloadedApkRequiresExactLengthAndHash() {
        val file = java.io.File.createTempFile("bebekon-update-test", ".part")
        val bytes = "A test package".toByteArray()
        val info = AndroidUpdate("0.1.3", "https://github.com/test", digest("A test package"), bytes.size.toLong())
        try {
            var received = 0L
            copyUpdate(bytes.inputStream(), file, info) { received = it }
            assertEquals(info.bytes, received); assertArrayEquals(bytes, file.readBytes())
            assertTrue(runCatching { copyUpdate(bytes.copyOf(bytes.size - 1).inputStream(), file, info) {} }.isFailure)
            assertTrue(runCatching { copyUpdate((bytes + 1.toByte()).inputStream(), file, info) {} }.isFailure)
            assertTrue(runCatching { copyUpdate(bytes.inputStream(), file, info.copy(sha256 = "b".repeat(64))) {} }.isFailure)
        } finally { file.delete() }
    }
    private fun release(version: String = "0.1.3", url: String = "https://github.com/Bebekon12/BebekonClient/releases/download/v0.1.16/Bebekon-Android.apk", hash: String = "a".repeat(64)) = json(
        "name" to "Bebekon VPN · Windows 0.1.16 и Android $version", "tag_name" to "v0.1.16", "draft" to false, "prerelease" to false,
        "assets" to array(listOf(json("name" to "BebekonVPN-0.1.16-Setup-x64.exe"), json("name" to "Bebekon-Android.apk", "browser_download_url" to url, "digest" to "sha256:$hash", "size" to 187150499)))
    ).toString()
    @Test fun androidVersionIsIndependentOfWindowsTag() { assertEquals("0.1.3", AndroidRelease.parse(release(), "0.1.2")?.version) }
    @Test fun sameAndOlderVersionsDoNotPrompt() { assertNull(AndroidRelease.parse(release("0.1.2"), "0.1.2")); assertNull(AndroidRelease.parse(release("0.1.1"), "0.1.2")); assertTrue(AndroidRelease.newer("0.1.10", "0.1.9")); assertFalse(AndroidRelease.newer("0.1.9", "0.1.10")) }
    @Test(expected = IllegalArgumentException::class) fun unrelatedRepositoryAssetRejected() { AndroidRelease.parse(release(url = "https://github.com/other/app/releases/download/v0.1.16/Bebekon-Android.apk"), "0.1.2") }
    @Test(expected = IllegalArgumentException::class) fun missingHashRejected() { AndroidRelease.parse(release(hash = ""), "0.1.2") }
    @Test fun redirectedDownloadsStayOnOfficialHttpsHosts() {
        AndroidRelease.downloadLocation("https://release-assets.githubusercontent.com/github-production-release-asset/asset?signature=test")
        assertTrue(runCatching { AndroidRelease.downloadLocation("http://github.com/asset") }.isFailure)
        assertTrue(runCatching { AndroidRelease.downloadLocation("https://github.com.evil.example/asset") }.isFailure)
        assertTrue(runCatching { AndroidRelease.downloadLocation("https://user:secret@github.com/asset") }.isFailure)
    }
    @Test fun appChecksFollowNewestRulesAndPreserveDirectRules() {
        val grouped = Rule(name = "Old apps", kind = RuleKind.APP, values = listOf("app.browser", "app.direct"), created = 10)
        val direct = Rule(name = "My direct app", kind = RuleKind.APP, values = listOf("app.direct"), vpn = false, created = 20)
        val site = Rule(name = "Site", kind = RuleKind.DOMAIN, values = listOf("example.com"), created = 30)
        val rules = listOf(grouped, direct, site)
        val actions = effectiveAppRules(rules)
        assertEquals(mapOf("app.browser" to true, "app.direct" to false), actions)
        val saved = replaceAppRules(rules, actions, mapOf("app.browser" to "Browser"), 100)
        assertEquals(actions, effectiveAppRules(saved)); assertTrue(site in saved)
        assertEquals(direct, saved.single { it.values == listOf("app.direct") })
        val removed = replaceAppRules(saved, actions - "app.browser", emptyMap(), 110)
        assertEquals(mapOf("app.direct" to false), effectiveAppRules(removed))
        assertEquals(20L, removed.single { it.kind == RuleKind.APP }.created)
    }
    @Test fun dnsFollowsApplicationRoutingAndPrecedence() {
        val node = SubscriptionParser.link("vless://3bf154da-0ee6-4c45-b5f4-8512b4a2d2bd@vpn.example:443#Sweden")
        val state = SavedState(subscriptions = listOf(Subscription(name = "Test", source = "", nodes = listOf(node))), selected = node.id, preferences = Preferences(routing = RoutingMode.RULES),
            rules = listOf(Rule(name = "Browser", kind = RuleKind.APP, values = listOf("com.yandex.browser"), created = 10), Rule(name = "Direct site", kind = RuleKind.DOMAIN, values = listOf("example.com"), vpn = false, created = 20)))
        val config = org.json.JSONObject(CoreConfig.build(state, { error("No geo") }))
        val dns = config.getJSONObject("dns").getJSONArray("rules")
        assertEquals("direct-dns", dns.getJSONObject(0).getString("server"))
        assertEquals("vpn-dns", dns.getJSONObject(1).getString("server"))
        assertEquals("com.yandex.browser", dns.getJSONObject(1).getJSONArray("package_name").getString(0))
    }
}
