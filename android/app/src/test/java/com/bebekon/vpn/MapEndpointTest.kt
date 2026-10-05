package com.bebekon.vpn

import org.junit.Assert.*
import org.junit.Test

class MapEndpointTest {
    @Test fun primaryAndFallbackIdentifyEgressWithoutProviderCountryName() {
        val primary = """{"success":true,"longitude":18.1,"latitude":59.3,"country_code":"SE"}"""
        val fallback = """{"ip":"203.0.113.1","longitude":18.1,"latitude":59.3,"country_code":"SE"}"""
        assertEquals(OriginPoint(18.1f, 59.3f, "SE"), parseOrigin(primary))
        assertEquals(parseOrigin(primary), parseFallbackLocation(fallback))
        assertTrue(runCatching { parseFallbackLocation("""{"error":true}""") }.isFailure)
        assertTrue(runCatching { parseFallbackLocation(fallback.replace("18.1", "181")) }.isFailure)
    }
}
