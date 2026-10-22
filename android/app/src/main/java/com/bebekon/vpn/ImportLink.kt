package com.bebekon.vpn

import java.net.URI
import java.net.URLDecoder

/**
 * "Add to Bebekon" links on provider pages: bebekon://add/<link> (the Happ-style form) and
 * bebekon://import?url=<link>. They only prefill the add-subscription dialog; the user still
 * confirms, and nothing connects or changes on its own.
 */
object ImportLink {
    private val schemes = listOf("https", "vless", "vmess", "ss", "trojan", "hysteria", "hysteria2", "hy2", "tuic", "tt")

    /** The link to import, or null when [text] is not a valid bebekon:// link. Other text passes through unchanged. */
    fun decode(text: String): String? {
        val value = text.trim()
        if (!value.startsWith("bebekon://", true)) return value
        if (value.length > 16_384) return null
        val rest = value.substring("bebekon://".length)
        val link = when {
            rest.startsWith("add/", true) -> rest.substring(4).let { if (it.contains("://")) it else decodePart(it) }
            rest.startsWith("import?", true) || rest.startsWith("install-config?", true) ->
                rest.substringAfter('?').split('&').firstOrNull { it.startsWith("url=", true) }?.substring(4)?.let(::decodePart)
            else -> null
        }?.trim() ?: return null
        val scheme = link.substringBefore("://", "").lowercase()
        if (scheme !in schemes || link.any(Char::isISOControl)) return null
        if (scheme == "https") { val uri = runCatching { URI(link) }.getOrNull() ?: return null; if (uri.host.isNullOrBlank() || uri.rawUserInfo != null) return null }
        return link
    }

    private fun decodePart(value: String) = runCatching { URLDecoder.decode(value.replace("+", "%2B"), Charsets.UTF_8) }.getOrNull()
}
