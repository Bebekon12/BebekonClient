package com.bebekon.vpn

import org.junit.Assert.*
import org.junit.Test

class ImportLinkTest {
    @Test fun decodesProviderLinks() {
        assertEquals("https://panel.example/sub/abc?x=1", ImportLink.decode("bebekon://add/https://panel.example/sub/abc?x=1"))
        assertEquals("https://panel.example/sub/abc", ImportLink.decode("bebekon://add/https%3A%2F%2Fpanel.example%2Fsub%2Fabc"))
        assertEquals("https://panel.example/s?t=a+b", ImportLink.decode("bebekon://import?url=https%3A%2F%2Fpanel.example%2Fs%3Ft%3Da%2Bb&name=x"))
        assertEquals("vless://id@host:443#Name", ImportLink.decode("  BEBEKON://install-config?url=vless%3A%2F%2Fid%40host%3A443%23Name "))
        // Ordinary shared text and proxy links pass through for the existing importer.
        assertEquals("vless://id@host:443", ImportLink.decode("vless://id@host:443"))
    }
    @Test fun rejectsEverythingElse() {
        listOf("bebekon://add/javascript:alert(1)", "bebekon://add/file:///sdcard/x", "bebekon://add/http://plain.example/sub", "bebekon://add/https://user:pass@panel.example/sub",
            "bebekon://connect?server=1", "bebekon://import?name=only", "bebekon://add/https%3A%2F%2Fpanel.example%2F%0D%0AX", "bebekon://add/content://x/y")
            .forEach { assertNull(it, ImportLink.decode(it)) }
    }
}
