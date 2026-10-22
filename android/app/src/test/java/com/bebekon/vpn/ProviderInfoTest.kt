package com.bebekon.vpn

import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class ProviderInfoTest {
    private val now = 1_792_000_000_000L
    @Test fun usageHeaderParsesInAnyOrderAndIgnoresJunk() {
        val u = SubscriptionUsage.parse("download=2147483648; foo=bar; upload=1073741824; total=10737418240; expire=1893456000")
        assertEquals(3221225472L, u.used); assertEquals(10737418240L, u.total); assertEquals(1893456000000L, u.expire)
        assertEquals(SubscriptionUsage(), SubscriptionUsage.parse("expire=0; total=-5"))
    }
    @Test fun remainingTrafficAndEarlyWarnings() {
        val gb = 1L shl 30
        val ok = SubscriptionUsage(download = 63 * gb, total = 100 * gb, expire = now + 30 * 86_400_000L)
        assertTrue(ok.label(now).startsWith("Осталось 37 ГБ из 100 ГБ · до ")); assertEquals("", ok.warning(now))
        assertEquals("Осталось мало трафика", ok.copy(download = 95 * gb).warning(now))
        assertEquals("Трафик закончился", ok.copy(download = 100 * gb).warning(now))
        assertEquals("Подписка закончится через 2 дн.", ok.copy(expire = now + 30 * 3_600_000L).warning(now))
        assertEquals("Подписка истекла — продлите её у провайдера", ok.copy(expire = now - 60_000).warning(now))
        assertEquals("Использовано 1,5 ГБ · без лимита", SubscriptionUsage(download = 3 * gb / 2).label(now))
    }
    @Test fun linksMustBeHttpsAndStateRoundTrips() {
        assertEquals("https://t.me/support", ProviderHeaders.safeLink(" https://t.me/support "))
        assertEquals("", ProviderHeaders.safeLink("http://example.com")); assertEquals("", ProviderHeaders.safeLink("tg://resolve?domain=x")); assertEquals("", ProviderHeaders.safeLink("https://u:p@example.com"))
        assertEquals("Новые серверы", ProviderHeaders.text("base64:" + java.util.Base64.getEncoder().encodeToString("Новые\nсерверы".toByteArray()), 300))
        assertEquals(12, ProviderHeaders.updateInterval("12")); assertEquals(168, ProviderHeaders.updateInterval("999")); assertEquals(0, ProviderHeaders.updateInterval("soon"))
        val sub = Subscription(name = "P", source = "https://p.example/s", nodes = emptyList(), info = "total=1", updateInterval = 6, supportUrl = "https://t.me/p", webPageUrl = "https://p.example", announce = "Привет")
        assertEquals(sub, Subscription.fromJson(sub.toJson()))
        assertEquals("", Subscription.fromJson(sub.toJson().put("supportUrl", "javascript:alert(1)")).supportUrl)
        assertEquals(0, Subscription.fromJson(JSONObject(sub.toJson().toString()).apply { remove("updateInterval") }).updateInterval)
    }
}
