package com.bebekon.vpn

import org.junit.Assert.*
import org.junit.Test

class SecurityTest {
    @Test fun deepJsonAndBase64VmessFailBeforeRecursiveParsing() {
        val attack = "[".repeat(5000) + "0" + "]".repeat(5000)
        assertTrue(runCatching { SubscriptionParser.parse(attack) }.exceptionOrNull() is IllegalArgumentException)
        val encoded = java.util.Base64.getEncoder().encodeToString(attack.toByteArray())
        assertTrue(runCatching { SubscriptionParser.link("vmess://$encoded") }.exceptionOrNull() is IllegalArgumentException)
        assertEquals("{\"text\":\"[quoted]\\\"brace}\"}", ImportSafety.jsonText("{\"text\":\"[quoted]\\\"brace}\"}"))
    }
    @Test fun yamlCyclesAndDeepUnusedSectionsAreRejected() {
        val cycle = "proxies: &cycle [{name: Test, type: trojan, server: vpn.example, port: 443, password: test, obfs: *cycle}]"
        assertTrue(runCatching { SubscriptionParser.parse(cycle) }.isFailure)
        val deep = "proxies: []\nunused: " + "[".repeat(5000) + "0" + "]".repeat(5000)
        assertTrue(runCatching { SubscriptionParser.parse(deep) }.isFailure)
    }
    @Test fun ordinaryYamlAnchorsStillWork() {
        // SnakeYAML Engine does not import merge directives; ordinary aliases do.
        val plain = "proxies: [{name: Test, type: trojan, server: vpn.example, port: 443, password: &p test}, {name: Other, type: trojan, server: other.example, port: 443, password: *p}]"
        assertEquals(2, SubscriptionParser.parse(plain).size)
    }
    @Test fun insecureTlsCannotConnectEvenWhenSavedByOlderVersion() {
        val node = SubscriptionParser.link("trojan://test@vpn.example:443?insecure=1")
        assertTrue(node.unsupported.contains("сертификата"))
        val legacy = node.copy(unsupported = "")
        val state = SavedState(subscriptions = listOf(Subscription(name = "Test", source = "", nodes = listOf(legacy))), selected = legacy.id)
        assertTrue(runCatching { CoreConfig.build(state, { error("Unexpected geo") }) }.isFailure)
    }
    @Test fun pluginsCannotReadCertificateFilesOrExpandOptions() {
        for (options in listOf("tls;cert=/data/private.pem", "tls;c\\ert=/data/private.pem", "tls;certRaw=untrusted", "mux=999999999", "host=a;host=b"))
            assertTrue(ConnectionSafety.reason(json("plugin" to "v2ray-plugin", "plugin_opts" to options)).isNotEmpty())
        assertEquals("", ConnectionSafety.reason(json("plugin" to "v2ray-plugin", "plugin_opts" to "tls;host=vpn.example;path=/socket;mux=1")))
    }
    @Test fun tricklingBytesCannotExtendTheSubscriptionDeadline() {
        var now = 0L
        val budget = NetworkBudget(20, { now })
        val input = object : java.io.InputStream() {
            override fun read(): Int { now += 9_000_000; return 'x'.code }
            override fun read(buffer: ByteArray, off: Int, len: Int): Int { buffer[off] = read().toByte(); return 1 }
        }
        assertTrue(runCatching { input.readLimited(4096, budget) }.isFailure)
        assertTrue(now >= 20_000_000 && now <= 30_000_000)
    }
}
