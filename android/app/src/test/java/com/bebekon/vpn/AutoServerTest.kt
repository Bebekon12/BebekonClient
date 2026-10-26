package com.bebekon.vpn

import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import java.net.InetAddress
import java.net.ServerSocket
import kotlin.concurrent.thread

class AutoServerTest {
    private fun node(host: String) = SubscriptionParser.link("vless://3bf154da-0ee6-4c45-b5f4-8512b4a2d2bd@$host:443#$host")
    private fun state(nodes: List<Node>, selected: Node = nodes.first(), auto: Boolean = true, hidden: List<Node> = emptyList()) = SavedState(
        subscriptions = listOf(Subscription(name = "A", source = "", nodes = nodes), Subscription(name = "B", source = "", nodes = hidden, hidden = true)),
        selected = selected.id, preferences = Preferences(autoServer = auto))

    @Test fun urltestGroupStartsWithSelectedServerAndSkipsHiddenOnes() {
        val a = node("a.example"); val b = node("b.example"); val c = node("c.example"); val hidden = node("h.example")
        val config = JSONObject(CoreConfig.build(state(listOf(a, b, c), selected = b, hidden = listOf(hidden)), { error("No geo") }))
        val outbounds = config.getJSONArray("outbounds")
        val group = outbounds.getJSONObject(0)
        assertEquals("urltest", group.getString("type")); assertEquals("vpn", group.getString("tag"))
        assertEquals(listOf("auto-0", "auto-1", "auto-2"), group.getJSONArray("outbounds").strings())
        val members = (0 until outbounds.length()).map(outbounds::getJSONObject).filter { it.optString("tag").startsWith("auto-") }
        assertEquals(listOf("b.example", "a.example", "c.example"), members.map { it.getString("server") })
        assertTrue(members.all { it.getJSONObject("domain_resolver").getString("server") == "direct-dns" && it.getString("connect_timeout") == "10s" })
        assertEquals("vpn", config.getJSONObject("route").getString("final"))
    }

    @Test fun probesSingleServerAndOffStayPlain() {
        val a = node("a.example"); val b = node("b.example")
        assertEquals("vless", JSONObject(CoreConfig.build(state(listOf(a, b)), { error("No geo") }, tunnel = false)).getJSONArray("outbounds").getJSONObject(0).getString("type"))
        assertEquals("vless", JSONObject(CoreConfig.build(state(listOf(a, b), auto = false), { error("No geo") })).getJSONArray("outbounds").getJSONObject(0).getString("type"))
        assertEquals("vless", JSONObject(CoreConfig.build(state(listOf(a)), { error("No geo") })).getJSONArray("outbounds").getJSONObject(0).getString("type"))
        assertEquals(1, CoreConfig.autoNodes(state(listOf(a))).size)
    }

    @Test fun poolKeyFollowsVisibleServersNotOrder() {
        val a = node("a.example"); val b = node("b.example"); val c = node("c.example")
        assertEquals(CoreConfig.autoKey(state(listOf(a, b))), CoreConfig.autoKey(state(listOf(b, a))))
        assertNotEquals(CoreConfig.autoKey(state(listOf(a, b))), CoreConfig.autoKey(state(listOf(a, b, c))))
        assertEquals("", CoreConfig.autoKey(state(listOf(a, b), auto = false)))
        val many = (0 until 80).map { node("s$it.example") }
        assertEquals(64, CoreConfig.autoNodes(state(many, selected = many[70])).size)
        assertEquals(many[70].id, CoreConfig.autoNodes(state(many, selected = many[70])).first().id)
    }

    @Test fun healthInboundIsLoopbackAuthenticatedAndAlwaysUsesVpn() {
        val a = node("a.example")
        val endpoint = TunnelCheck.Endpoint(23456, "f".repeat(48))
        val config = JSONObject(CoreConfig.build(state(listOf(a)).copy(preferences = Preferences(routing = RoutingMode.RULES)), { error("No geo") }, health = endpoint))
        val inbound = config.getJSONArray("inbounds").let { list -> (0 until list.length()).map(list::getJSONObject).single { it.getString("tag") == "health" } }
        assertEquals("127.0.0.1", inbound.getString("listen")); assertEquals(23456, inbound.getInt("listen_port"))
        assertEquals("f".repeat(48), inbound.getJSONArray("users").getJSONObject(0).getString("password"))
        val first = config.getJSONObject("route").getJSONArray("rules").getJSONObject(0)
        assertEquals(listOf("health"), first.getJSONArray("inbound").strings()); assertEquals("vpn", first.getString("outbound"))
        // Probe cores never expose it.
        assertFalse(CoreConfig.build(state(listOf(a)), { error("No geo") }, tunnel = false, health = endpoint).contains("\"health\""))
    }

    @Test fun tunnelCheckAuthenticatesAndRejectsRefusedProxy() {
        val server = ServerSocket(0, 1, InetAddress.getLoopbackAddress())
        var request = ""
        val worker = thread {
            server.accept().use { socket ->
                val reader = socket.getInputStream().bufferedReader()
                request = generateSequence { reader.readLine()?.takeIf(String::isNotEmpty) }.joinToString("\n")
                socket.getOutputStream().write("HTTP/1.1 407 Proxy Authentication Required\r\nContent-Length: 0\r\n\r\n".toByteArray())
            }
        }
        val endpoint = TunnelCheck.Endpoint(server.localPort, "secret")
        assertThrows(IllegalStateException::class.java) { TunnelCheck.get(endpoint, "www.gstatic.com", "/generate_204", 3000) }
        worker.join(3000); server.close()
        assertTrue(request.startsWith("CONNECT www.gstatic.com:443 HTTP/1.1"))
        assertTrue(request.contains("Proxy-Authorization: Basic " + java.util.Base64.getEncoder().encodeToString("bebekon:secret".toByteArray())))
        assertThrows(IllegalArgumentException::class.java) { TunnelCheck.get(endpoint, "bad host\r\n", "/") }
        val fresh = TunnelCheck.endpoint(); assertTrue(fresh.port in 1..65535); assertEquals(48, fresh.password.length)
    }
}
