package com.bebekon.vpn

import org.json.JSONArray
import org.json.JSONObject
import java.security.MessageDigest
import java.util.UUID

fun json(vararg entries: Pair<String, Any?>) = JSONObject().also { o -> entries.forEach { (k, v) -> if (v != null) o.put(k, v) } }
fun array(items: Iterable<Any?>) = JSONArray().also { a -> items.forEach { a.put(it) } }
fun JSONArray.objects() = (0 until length()).map { getJSONObject(it) }
fun JSONArray.strings() = (0 until length()).map { getString(it) }
fun digest(value: String) = MessageDigest.getInstance("SHA-256").digest(value.toByteArray()).joinToString("") { "%02x".format(it) }

/** Only connection parameters are retained. Provider routing/listeners/commands never enter the runtime. */
data class Node(val id: String, val name: String, val outbound: String, val country: String = "", val unsupported: String = "") {
    val config get() = JSONObject(outbound)
    val host get() = config.optString("server")
    val port get() = config.optInt("server_port", 443)
    val protocol get() = config.optString("type").replace("shadowsocks", "SS").uppercase()
    val transport get() = config.optJSONObject("transport")?.optString("type")?.uppercase() ?: "TCP"
    fun toJson() = json("id" to id, "name" to name, "outbound" to config, "country" to country, "unsupported" to unsupported)
    companion object { fun fromJson(o: JSONObject) = Node(o.getString("id"), o.getString("name"), o.getJSONObject("outbound").toString(), o.optString("country"), o.optString("unsupported")) }
}
data class Subscription(val id: String = UUID.randomUUID().toString(), val name: String, val source: String, val nodes: List<Node>, val updated: Long = System.currentTimeMillis(), val info: String = "") {
    fun toJson() = json("id" to id, "name" to name, "source" to source, "nodes" to array(nodes.map(Node::toJson)), "updated" to updated, "info" to info)
    companion object { fun fromJson(o: JSONObject) = Subscription(o.getString("id"), o.getString("name"), o.getString("source"), o.getJSONArray("nodes").objects().map(Node::fromJson), o.optLong("updated"), o.optString("info")) }
}
enum class RuleKind(val label: String, val field: String) {
    DOMAIN("Сайт и поддомены", "domain_suffix"), KEYWORD("Слово в домене", "domain_keyword"), APP("Приложение", "package_name"), CIDR("IP / подсеть", "ip_cidr"), GEOSITE("GeoSite", "rule_set"), GEOIP("GeoIP", "rule_set")
}
data class Rule(val id: String = UUID.randomUUID().toString(), val name: String, val kind: RuleKind, val values: List<String>, val vpn: Boolean = true, val created: Long = System.currentTimeMillis()) {
    fun toJson() = json("id" to id, "name" to name, "kind" to kind.name, "values" to array(values), "vpn" to vpn, "created" to created)
    companion object { fun fromJson(o: JSONObject) = Rule(o.getString("id"), o.getString("name"), RuleKind.valueOf(o.getString("kind")), o.getJSONArray("values").strings(), o.optBoolean("vpn", true), o.optLong("created")) }
}
fun effectiveAppRules(rules: List<Rule>): Map<String, Boolean> {
    val actions = linkedMapOf<String, Boolean>()
    rules.filter { it.kind == RuleKind.APP }.sortedByDescending { it.created }.forEach { rule -> rule.values.forEach { actions.putIfAbsent(it, rule.vpn) } }
    return actions
}
fun replaceAppRules(rules: List<Rule>, actions: Map<String, Boolean>, labels: Map<String, String>, now: Long): List<Rule> {
    val old = rules.filter { it.kind == RuleKind.APP }.sortedByDescending { it.created }
    return rules.filterNot { it.kind == RuleKind.APP } + actions.map { (pkg, vpn) ->
        val previous = old.firstOrNull { pkg in it.values }
        Rule(id = previous?.takeIf { it.values.size == 1 }?.id ?: UUID.randomUUID().toString(),
            name = previous?.takeIf { it.values.size == 1 }?.name ?: labels[pkg] ?: pkg, kind = RuleKind.APP, values = listOf(pkg), vpn = vpn,
            created = if (previous != null && previous.vpn == vpn) previous.created else now)
    }
}
enum class ThemeChoice(val label: String) { DARK("Тёмная"), LIGHT("Светлая"), SYSTEM("Как в системе") }
enum class RoutingMode(val label: String) { ALL("Весь трафик"), RULES("По правилам") }
enum class PingMethod(val label: String) { HTTPS_GET("HTTPS GET · рекомендуется"), HTTPS_HEAD("HTTPS HEAD"), TCP("TCP") }
enum class PingTarget(val label: String, val url: String) { CLOUDFLARE("Cloudflare", "https://cp.cloudflare.com/generate_204"), GOOGLE("Google", "https://www.gstatic.com/generate_204") }
enum class DnsResolver(val label: String, val address: String, val hostname: String) {
    CLOUDFLARE("Cloudflare", "1.1.1.1", "cloudflare-dns.com"), GOOGLE("Google", "8.8.8.8", "dns.google")
}
data class Preferences(val theme: ThemeChoice = ThemeChoice.DARK, val routing: RoutingMode = RoutingMode.ALL, val ping: PingMethod = PingMethod.HTTPS_GET, val animations: Boolean = true, val allowLan: Boolean = true, val autoReconnect: Boolean = true, val pingTarget: PingTarget = PingTarget.CLOUDFLARE,
    val mapLocation: Boolean = true, val dnsResolver: DnsResolver = DnsResolver.CLOUDFLARE, val mtu: Int = 1400, val connectionNotifications: Boolean = true, val checkUpdates: Boolean = true, val sitesInAllApps: Boolean = false, val routingDiagnostics: Boolean = false, val ipv6: Boolean = false) {
    fun toJson() = json("theme" to theme.name, "routing" to routing.name, "ping" to ping.name, "animations" to animations, "allowLan" to allowLan, "autoReconnect" to autoReconnect, "pingTarget" to pingTarget.name,
        "mapLocation" to mapLocation, "dnsResolver" to dnsResolver.name, "mtu" to mtu, "connectionNotifications" to connectionNotifications, "checkUpdates" to checkUpdates, "sitesInAllApps" to sitesInAllApps, "routingDiagnostics" to routingDiagnostics, "ipv6" to ipv6)
    companion object { fun fromJson(o: JSONObject) = Preferences(ThemeChoice.valueOf(o.optString("theme", "DARK")), RoutingMode.valueOf(o.optString("routing", "ALL")), PingMethod.valueOf(o.optString("ping", "HTTPS_GET")), o.optBoolean("animations", true), o.optBoolean("allowLan", true), o.optBoolean("autoReconnect", true), PingTarget.valueOf(o.optString("pingTarget", "CLOUDFLARE")),
        o.optBoolean("mapLocation", true), DnsResolver.valueOf(o.optString("dnsResolver", "CLOUDFLARE")), o.optInt("mtu", 1400).also { require(it in 1280..1500) }, o.optBoolean("connectionNotifications", true), o.optBoolean("checkUpdates", true), o.optBoolean("sitesInAllApps", false), o.optBoolean("routingDiagnostics", false), o.optBoolean("ipv6", false)) }
}

/** App selection is an OS filter. Adding a site must never silently broaden that filter. */
data class AppTunnelPolicy(val allowed: Set<String>? = null, val excluded: Set<String> = emptySet(), val siteOnlyBrowsers: Set<String> = emptySet()) {
    val appOnly get() = allowed != null
    val nativeApps get() = allowed.orEmpty() - siteOnlyBrowsers
}
fun appTunnelPolicy(state: SavedState): AppTunnelPolicy {
    if (state.preferences.routing == RoutingMode.ALL) return AppTunnelPolicy()
    val apps = effectiveAppRules(state.rules)
    val sitesNeedTunnel = state.rules.any { it.kind != RuleKind.APP && it.vpn }
    val selected = apps.filterValues { it }.keys
    return if (state.preferences.sitesInAllApps || selected.isEmpty() && sitesNeedTunnel) AppTunnelPolicy(excluded = apps.filterValues { !it }.keys)
    else AppTunnelPolicy(allowed = selected)
}
data class SavedState(val subscriptions: List<Subscription> = emptyList(), val selected: String? = null, val favorites: Set<String> = emptySet(), val rules: List<Rule> = emptyList(), val preferences: Preferences = Preferences()) {
    val nodes get() = subscriptions.flatMap { it.nodes }.distinctBy { it.id }
    val selectedNode get() = nodes.firstOrNull { it.id == selected }
    fun toJson() = json("version" to 1, "subscriptions" to array(subscriptions.map(Subscription::toJson)), "selected" to selected, "favorites" to array(favorites), "rules" to array(rules.map(Rule::toJson)), "preferences" to preferences.toJson())
    companion object { fun fromJson(o: JSONObject): SavedState { require(o.optInt("version") == 1) { "Неизвестная версия настроек" }; return SavedState(o.getJSONArray("subscriptions").objects().map(Subscription::fromJson), if (o.isNull("selected")) null else o.optString("selected").takeIf { it.isNotEmpty() }, o.getJSONArray("favorites").strings().toSet(), o.getJSONArray("rules").objects().map(Rule::fromJson), Preferences.fromJson(o.getJSONObject("preferences"))) } }
}
enum class Phase { OFF, STARTING, ON, RECONNECTING, STOPPING, ERROR }
data class Session(val phase: Phase = Phase.OFF, val server: String = "", val message: String = "", val started: Long = 0, val down: Long = 0, val up: Long = 0, val totalDown: Long = 0, val totalUp: Long = 0, val publicIp: String = "") {
    val active get() = phase in listOf(Phase.STARTING, Phase.ON, Phase.RECONNECTING)
    val busy get() = phase in listOf(Phase.STARTING, Phase.STOPPING, Phase.RECONNECTING)
}
enum class PingQuality { GOOD, FAIR, POOR, UNKNOWN }
fun nodesByPing(nodes: List<Node>, pings: Map<String, PingResult>): List<Node> = nodes.sortedWith(compareBy<Node> {
    when (pings[it.id]?.quality) { PingQuality.GOOD -> 0; PingQuality.FAIR -> 1; PingQuality.POOR -> 2; else -> 3 }
}.thenBy { pings[it.id]?.millis ?: Int.MAX_VALUE })
data class PingResult(val millis: Int? = null, val running: Boolean = false, val failed: Boolean = false, val method: PingMethod = PingMethod.HTTPS_GET, val queued: Boolean = false, val target: PingTarget = PingTarget.CLOUDFLARE) {
    val label get() = if (millis != null) "$millis мс" else if (queued) "В очереди" else if (running) "Проверка…" else if (failed) "Таймаут" else "—"
    val quality get() = when {
        millis == null -> if (failed) PingQuality.POOR else PingQuality.UNKNOWN
        method == PingMethod.TCP -> if (millis <= 100) PingQuality.GOOD else if (millis <= 250) PingQuality.FAIR else PingQuality.POOR
        else -> if (millis <= 300) PingQuality.GOOD else if (millis <= 600) PingQuality.FAIR else PingQuality.POOR
    }
}
