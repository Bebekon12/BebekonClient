package com.bebekon.vpn

import org.junit.Assert.*
import org.junit.Test

class MapEndpointTest {
    private val anonymous = """{"ip":"144.31.126.255","is_bogon":false,"country":"Finland","lat":60.16952,"lon":24.93545}"""
    @Test fun anonymousProviderIdentifiesCountryAndCoordinatesWithoutCountryCode() {
        val point = parseIpapiIs(anonymous)
        assertEquals("FI", point.country); assertEquals(60.16952f, point.latitude); assertEquals(24.93545f, point.longitude)
        assertTrue(runCatching { parseIpapiIs(anonymous.replace("false", "true")) }.isFailure)
        assertTrue(runCatching { parseIpapiIs(anonymous.replace("60.16952", "null")) }.isFailure)
        assertTrue(runCatching { parseIpapiIs("""{"error":"limit exceeded"}""") }.isFailure)
    }
    @Test fun unavailableProvidersFallBackWithoutReturningAnotherIPsLocation() {
        val calls = mutableListOf<String>()
        val point = GeoLookup().lookup("144.31.126.255") { url ->
            calls += url
            when { "api.ipapi.is" in url -> GeoReply(200, anonymous.replace("144.31.126.255", "8.8.8.8"))
                "ipwho.is" in url -> throw java.io.IOException("TLS unavailable")
                else -> GeoReply(200, """{"ip":"144.31.126.255","longitude":24.93545,"latitude":60.16952,"country_code":"FI"}""") }
        }
        assertEquals("FI", point?.country); assertEquals(3, calls.size)
        assertTrue(calls.all { it.startsWith("https://") })
    }
    @Test fun rateLimitsSkipProviderUntilRetryAfterAndCancellationStopsFallback() {
        var now = 10L; var calls = 0
        val lookup = GeoLookup(clock = { now })
        val fetch: (String) -> GeoReply = { url -> if ("api.ipapi.is" in url) { calls++; GeoReply(429, retryAfterSeconds = 120) } else GeoReply(403) }
        assertNull(lookup.lookup("144.31.126.255", fetch)); assertNull(lookup.lookup("144.31.126.255", fetch)); assertEquals(1, calls)
        now += 120_001; assertNull(lookup.lookup("144.31.126.255", fetch)); assertEquals(2, calls)
        assertTrue(runCatching { GeoLookup().lookup("") { throw kotlinx.coroutines.CancellationException() } }.exceptionOrNull() is kotlinx.coroutines.CancellationException)
        assertFalse(locationIp("face.face")); assertFalse(locationIp("999.1.2.3")); assertTrue(locationIp("2001:db8::1"))
    }
    @Test fun resolvedFlagsKeepAuthoredNamesCountriesAndSerializedProfileUntouched() {
        val node = Node("manual", "test", "{}")
        assertEquals("FI", node.displayCountry(mapOf("manual" to "FI")))
        assertEquals("SE", node.copy(country = "SE").displayCountry(mapOf("manual" to "FI")))
        assertEquals("", Node.fromJson(node.toJson()).country); assertEquals("test", Node.fromJson(node.toJson()).name)
    }
    @Test fun primaryAndFallbackIdentifyEgressWithoutProviderCountryName() {
        val primary = """{"success":true,"longitude":18.1,"latitude":59.3,"country_code":"SE"}"""
        val fallback = """{"ip":"203.0.113.1","longitude":18.1,"latitude":59.3,"country_code":"SE"}"""
        assertEquals(OriginPoint(18.1f, 59.3f, "SE"), parseOrigin(primary))
        assertEquals(parseOrigin(primary), parseFallbackLocation(fallback))
        assertTrue(runCatching { parseFallbackLocation("""{"error":true}""") }.isFailure)
        assertTrue(runCatching { parseFallbackLocation(fallback.replace("18.1", "181")) }.isFailure)
    }
}
