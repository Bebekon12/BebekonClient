package com.bebekon.vpn

import org.json.JSONArray
import org.json.JSONObject

/**
 * A backup file shared with the Windows client. Subscriptions keep only their source, so
 * importing re-downloads current servers. Site, keyword, IP and geo rules move between
 * platforms; application rules and preferences restore only on the same platform.
 */
object Backup {
    const val FORMAT = "bebekon-backup"
    private const val MAX = 4 * 1024 * 1024
    data class Source(val name: String, val source: String, val hidden: Boolean = false, val manualOutbound: String = "")
    data class Data(val subscriptions: List<Source>, val rules: List<Rule>, val preferences: Preferences?, val skipped: Int)

    private val kinds = mapOf(RuleKind.DOMAIN to "site", RuleKind.KEYWORD to "contains", RuleKind.CIDR to "cidr", RuleKind.GEOSITE to "geoSite", RuleKind.GEOIP to "geoIp", RuleKind.APP to "androidApp")

    fun export(state: SavedState, created: String = java.time.Instant.now().toString()): String = json(
        "format" to FORMAT, "version" to 1, "created" to created, "platform" to "android",
        "subscriptions" to array(state.subscriptions.map { sub ->
            // A manually entered TrustTunnel profile has no link; keep its connection fields.
            val manual = sub.source.startsWith("manual:")
            json("name" to sub.name, "source" to if (manual) "" else sub.source, "hidden" to sub.hidden, "androidOutbound" to if (manual) sub.nodes.singleOrNull()?.config else null)
        }),
        // Highest priority first, as on Windows; Android's newest rule wins.
        "rules" to array(state.rules.sortedByDescending { it.created }.map { json("name" to it.name, "kind" to kinds[it.kind], "values" to array(it.values), "vpn" to it.vpn) }),
        "androidPreferences" to state.preferences.toJson()
    ).toString(2)

    fun parse(text: String): Data {
        require(text.length <= MAX) { "Файл резервной копии слишком большой" }
        val o = runCatching { JSONObject(text) }.getOrNull()
        require(o != null && o.optString("format") == FORMAT && o.optInt("version") == 1) { "Это не резервная копия Bebekon VPN" }
        var skipped = 0
        val subscriptions = (o.optJSONArray("subscriptions") ?: JSONArray()).objects().mapNotNull { s ->
            val outbound = s.optJSONObject("androidOutbound")?.toString().orEmpty()
            val source = s.optString("source").trim()
            if (source.isEmpty() && outbound.isEmpty() || source.length > MAX) { skipped++; null }
            else Source(s.optString("name").take(80).ifBlank { "Подписка" }, source, s.optBoolean("hidden"), outbound)
        }
        val byName = kinds.entries.associate { (kind, name) -> name to kind }
        val base = System.currentTimeMillis()
        val rules = (o.optJSONArray("rules") ?: JSONArray()).objects().mapIndexedNotNull { i, r ->
            val kind = byName[r.optString("kind")]
            val values = r.optJSONArray("values")?.let { a -> (0 until a.length()).map { a.optString(it).trim() } }.orEmpty().filter(String::isNotEmpty)
            val rule = kind?.let { Rule(name = r.optString("name").take(160).ifBlank { values.firstOrNull() ?: "Правило" }, kind = it, values = values, vpn = r.optBoolean("vpn", true), created = base - i) }
            if (rule == null || runCatching { CoreConfig.validateRule(rule) }.isFailure) { skipped++; null } else rule
        }
        val preferences = o.optJSONObject("androidPreferences")?.let { runCatching { Preferences.fromJson(it) }.getOrNull() }
        return Data(subscriptions, rules, preferences, skipped)
    }
}
