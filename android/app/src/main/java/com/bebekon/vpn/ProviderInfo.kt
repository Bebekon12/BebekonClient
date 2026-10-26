package com.bebekon.vpn

import java.net.URI
import java.util.Locale

/**
 * Optional subscription response headers used by Happ, Clash and panels such as Remnawave or
 * Marzban. Display data only: nothing here changes routing or connection settings.
 */
data class SubscriptionUsage(val upload: Long = 0, val download: Long = 0, val total: Long = 0, val expire: Long = 0) {
    val used get() = upload + download
    fun label(now: Long = System.currentTimeMillis()): String {
        val parts = mutableListOf<String>()
        if (total > 0) parts += "Осталось ${ProviderHeaders.size((total - used).coerceAtLeast(0))} из ${ProviderHeaders.size(total)}"
        else if (used > 0) parts += "Использовано ${ProviderHeaders.size(used)} · без лимита"
        if (expire > 0) parts += if (expire <= now) "срок истёк" else "до " + java.text.SimpleDateFormat("dd.MM.yyyy", Locale.ROOT).format(java.util.Date(expire))
        return parts.joinToString(" · ")
    }
    /** Shown before a provider cuts the connection: expiry within 3 days or under 10% traffic left. */
    fun warning(now: Long = System.currentTimeMillis()): String = when {
        expire > 0 && expire <= now -> "Подписка истекла — продлите её у провайдера"
        expire > 0 && expire - now < 3 * 86_400_000L -> "Подписка закончится через ${((expire - now + 86_399_999) / 86_400_000).coerceAtLeast(1)} дн."
        total > 0 && total - used <= total / 10 -> if (used >= total) "Трафик закончился" else "Осталось мало трафика"
        else -> ""
    }
    companion object {
        /** "upload=1; download=2; total=3; expire=1700000000" in any order; unknown keys are ignored. */
        fun parse(header: String?): SubscriptionUsage {
            if (header.isNullOrBlank() || header.length > 512) return SubscriptionUsage()
            var usage = SubscriptionUsage()
            for (part in header.split(';')) {
                val key = part.substringBefore('=').trim().lowercase(); val value = part.substringAfter('=', "").trim().toLongOrNull()?.takeIf { it >= 0 } ?: continue
                usage = when (key) {
                    "upload" -> usage.copy(upload = value); "download" -> usage.copy(download = value); "total" -> usage.copy(total = value)
                    "expire" -> usage.copy(expire = if (value in 1 until 32_503_680_000) value * 1000 else 0)
                    else -> usage
                }
            }
            return usage
        }
    }
}

object ProviderHeaders {
    fun updateInterval(value: String?): Int = value?.trim()?.toIntOrNull()?.takeIf { it > 0 }?.coerceAtMost(168) ?: 0
    /** Only https links are kept; a subscription cannot make the app open other schemes. */
    fun safeLink(value: String?): String {
        val text = value?.trim().orEmpty()
        val uri = runCatching { URI(text) }.getOrNull() ?: return ""
        return if (text.length <= 512 && uri.scheme == "https" && !uri.host.isNullOrBlank() && uri.rawUserInfo == null && text.none(Char::isISOControl)) text else ""
    }
    /** Plain or "base64:" text, single-spaced and bounded. */
    fun text(value: String?, max: Int): String {
        var text = value?.trim().orEmpty()
        if (text.startsWith("base64:", true)) text = runCatching { SubscriptionParser.decode(text.substring(7).trim()) }.getOrDefault("")
        text = text.split(Regex("\\s+")).filter(String::isNotEmpty).joinToString(" ").filterNot(Char::isISOControl)
        return if (text.length > max) text.take(max).trimEnd() + "…" else text
    }
    fun size(bytes: Long): String {
        val units = listOf("Б", "КБ", "МБ", "ГБ", "ТБ"); var value = bytes.toDouble(); var unit = 0
        while (value >= 1024 && unit < units.lastIndex) { value /= 1024; unit++ }
        return (if (unit >= 3 && value < 100) String.format(Locale.forLanguageTag("ru"), "%.1f", value).removeSuffix(",0") else Math.round(value).toString()) + " " + units[unit]
    }
}
