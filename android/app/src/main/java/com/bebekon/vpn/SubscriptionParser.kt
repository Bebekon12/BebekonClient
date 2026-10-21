package com.bebekon.vpn

import org.json.JSONArray
import org.json.JSONObject
import org.snakeyaml.engine.v2.api.Load
import java.net.IDN
import java.net.URI
import java.net.URLDecoder
import java.util.Base64
import java.util.UUID

object SubscriptionParser {
    private val types = setOf("vless", "vmess", "shadowsocks", "trojan", "hysteria", "hysteria2", "wireguard", "trusttunnel")
    private val allowed = setOf("type", "server", "server_port", "uuid", "password", "method", "flow", "packet_encoding", "security", "alter_id", "global_padding", "authenticated_length", "tls", "transport", "up_mbps", "down_mbps", "auth_str", "obfs", "server_ports", "hop_interval", "plugin", "plugin_opts", "network")
    fun decode(s: String): String = Base64.getDecoder().decode(s.filterNot(Char::isWhitespace).replace('-', '+').replace('_', '/').let { it.padEnd((it.length + 3) / 4 * 4, '=') }).toString(Charsets.UTF_8)
    private fun unescape(s: String) = URLDecoder.decode(s.replace("+", "%2B"), "UTF-8")
    fun parse(raw: String): List<Node> {
        require(raw.length <= 4 * 1024 * 1024) { "Подписка слишком большая" }
        var text = raw.trim().removePrefix("\uFEFF").trim()
        require(!text.startsWith('<')) { "Провайдер вернул веб-страницу. Нужна ссылка на подписку" }
        if (!text.contains("://") && !text.startsWith('{') && !text.startsWith('[') && !Regex("(?m)^\\s*proxies\\s*:").containsMatchIn(text) && !Regex("(?m)^\\s*\\[endpoint\\]\\s*$").containsMatchIn(text)) text = try { decode(text).trim() } catch (_: Exception) { error("Неизвестный формат подписки") }
        val nodes = when {
            Regex("(?m)^\\s*\\[endpoint\\]\\s*$").containsMatchIn(text) -> listOf(TrustTunnelProfile.parseToml(text))
            text.startsWith('{') || text.startsWith('[') -> parseJson(text)
            Regex("(?m)^\\s*proxies\\s*:").containsMatchIn(text) -> {
                ImportSafety.yamlText(text)
                val root = Load(ImportSafety.yamlSettings).loadFromString(text) as? Map<*, *> ?: error("Повреждённая YAML подписка")
                ImportSafety.yamlTree(root)
                (root["proxies"] as? List<*>)?.map { clash(JSONObject(it as Map<*, *>)) } ?: error("Нет серверов в подписке")
            }
            else -> text.lines().filter { it.isNotBlank() && !it.trim().startsWith('#') }.mapIndexed { index, s -> try { link(s.trim()) } catch (_: Exception) { error("Строка ${index + 1}: повреждённая VPN ссылка") } }
        }
        require(nodes.isNotEmpty() && nodes.size <= 5000) { "Подписка пустая или содержит слишком много серверов" }
        return nodes.distinctBy { it.id }
    }
    private fun parseJson(text: String): List<Node> {
        ImportSafety.jsonText(text)
        if (text.startsWith('[')) return JSONArray(text).objects().flatMap { config(it) }
        return config(JSONObject(text))
    }
    private fun config(root: JSONObject): List<Node> = when {
        root.has("endpoint") || root.has("hostname") && (root.has("addresses") || root.has("address")) -> listOf(TrustTunnelProfile.endpoint(root))
        root.has("proxies") -> root.getJSONArray("proxies").objects().map(::clash)
        root.has("outbounds") -> {
            val profileName = listOf("remarks", "remark", "ps", "name").firstNotNullOfOrNull { root.optString(it).trim().takeIf(String::isNotEmpty) }.orEmpty()
            val outbounds = root.getJSONArray("outbounds").objects().filter { it.optString("type", it.optString("protocol")) in types }
            outbounds.map { outbound ->
                val tag = outbound.optString("tag")
                val title = if (outbounds.size == 1) profileName.ifBlank { tag } else if (profileName.isBlank()) tag else "$profileName · $tag"
                if (outbound.has("protocol")) xray(outbound, title) else node(title, outbound)
            }
        }
        root.optString("type") in types -> listOf(node(root.optString("name", root.optString("tag")), root))
        else -> error("Нет поддерживаемых серверов в JSON подписке")
    }
    fun link(input: String): Node {
        val scheme = input.substringBefore("://").lowercase()
        if (scheme == "tt") {
            val (endpoint, _) = TrustTunnelProfile.link(input)
            require(endpoint.has("addresses")) { "Ссылка TrustTunnel содержит подписку — добавьте её в «Подписках»" }
            return TrustTunnelProfile.endpoint(endpoint)
        }
        if (scheme == "vmess" && !input.substringAfter("://").contains('@')) {
            val o = JSONObject(ImportSafety.jsonText(decode(input.substringAfter("://"))))
            val c = json("type" to "vmess", "server" to o.getString("add"), "server_port" to o.getString("port").toInt(), "uuid" to o.getString("id"), "security" to o.optString("scy", "auto"), "alter_id" to o.optString("aid", "0").toInt())
            if (o.optString("tls") in listOf("tls", "true", "1")) c.put("tls", tls(o.optString("sni", o.optString("host")), o.optString("fp"), o.optString("alpn")))
            transport(c, o.optString("net", "tcp"), o.optString("path", "/"), o.optString("host"), o.optString("path"))
            return node(o.optString("ps"), c)
        }
        var normalized = input
        if (scheme == "ss") {
            var body = input.substringAfter("://")
            if (!body.substringBefore('#').substringBefore('?').contains('@')) {
                val suffix = body.substring(body.indexOfAny(charArrayOf('#', '?')).takeIf { it >= 0 } ?: body.length)
                body = decode(body.substringBefore('#').substringBefore('?')) + suffix
            }
            val at = body.lastIndexOf('@'); require(at > 0)
            var auth = unescape(body.substring(0, at)); if (!auth.contains(':')) auth = decode(auth)
            val uri = URI("ss://" + body.substring(at + 1))
            val c = json("type" to "shadowsocks", "server" to uri.host.trim('[', ']'), "server_port" to uri.port, "method" to auth.substringBefore(':'), "password" to auth.substringAfter(':'))
            val plugin = query(uri)["plugin"]
            if (plugin != null) { c.put("plugin", plugin.substringBefore(';').replace("simple-obfs", "obfs-local")); c.put("plugin_opts", plugin.substringAfter(';', "")) }
            return node(unescape(uri.rawFragment ?: ""), c)
        }
        // Normalize Hysteria port ranges before URI parsing, retain hopping options in the outbound.
        var ports = emptyList<String>()
        if (scheme in listOf("hysteria", "hysteria2", "hy2")) {
            val authority = input.substringAfter("://").substringBefore('/').substringBefore('?').substringBefore('#')
            val port = authority.substringAfterLast(':')
            if (port.contains(',') || port.contains('-')) { ports = port.split(',').map { it.replace('-', ':') }; normalized = input.replace(authority, authority.substringBeforeLast(':') + ":" + ports.first().substringBefore(':')) }
        }
        val u = URI(normalized); val q = query(u); fun get(k: String, fallback: String = "") = q[k] ?: fallback
        val type = if (scheme == "hy2") "hysteria2" else scheme
        require(type in types || type == "tt") { "Неизвестный протокол" }
        val credential = unescape(u.rawUserInfo ?: "")
        val c = json("type" to if (type == "tt") "trusttunnel" else type, "server" to u.host?.trim('[', ']'), "server_port" to if (u.port > 0) u.port else 443)
        if (type in listOf("vless", "vmess")) { c.put("uuid", credential); c.put("packet_encoding", get("packetEncoding", "xudp")) }
        if (type == "vless") { require(get("encryption", "none") == "none"); if (get("flow").isNotEmpty()) c.put("flow", get("flow")) }
        if (type == "vmess") { c.put("security", get("scy", "auto")); c.put("alter_id", get("alterId", "0").toInt()) }
        if (type in listOf("trojan", "hysteria2", "tt")) c.put("password", credential)
        if (type == "hysteria") { c.put("auth_str", get("auth", credential)); c.put("up_mbps", get("upmbps", get("up", "100")).toInt()); c.put("down_mbps", get("downmbps", get("down", "100")).toInt()); if (get("obfs").isNotEmpty()) c.put("obfs", get("obfs")) }
        if (type == "hysteria2" && get("obfs").isNotEmpty()) c.put("obfs", json("type" to get("obfs"), "password" to get("obfs-password")))
        if (ports.isNotEmpty()) { c.remove("server_port"); c.put("server_ports", array(ports)); c.put("hop_interval", get("hopInterval", "30") + "s") }
        if (get("mport").isNotEmpty()) { c.remove("server_port"); c.put("server_ports", array(get("mport").split(',').map { it.replace('-', ':') })) }
        val security = get("security", if (type in listOf("vless", "vmess")) "none" else "tls")
        if (security != "none") {
            require(security in listOf("tls", "reality"))
            val t = tls(get("sni", get("peer", get("serverName"))), get("fp", if (security == "reality") "chrome" else ""), get("alpn"))
            if (security == "reality") t.put("reality", json("enabled" to true, "public_key" to get("pbk"), "short_id" to get("sid")))
            t.put("insecure", get("insecure", get("allowInsecure", "0")) in listOf("1", "true"))
            c.put("tls", t)
        }
        transport(c, get("type", "tcp"), get("path", "/"), get("host"), get("serviceName"))
        return node(unescape(u.rawFragment ?: ""), c)
    }
    private fun query(u: URI): Map<String, String> {
        val r = mutableMapOf<String, String>()
        u.rawQuery?.split('&')?.filter(String::isNotEmpty)?.forEach { val k = unescape(it.substringBefore('=')); require(!r.containsKey(k)) { "Повторяющийся параметр" }; r[k] = unescape(it.substringAfter('=', "")) }
        return r
    }
    private fun tls(sni: String = "", fingerprint: String = "", alpn: String = "") = json("enabled" to true, "server_name" to sni.takeIf(String::isNotEmpty), "alpn" to alpn.split(',').filter(String::isNotEmpty).takeIf(List<String>::isNotEmpty)?.let(::array), "utls" to fingerprint.takeIf(String::isNotEmpty)?.let { json("enabled" to true, "fingerprint" to it) })
    private fun transport(c: JSONObject, rawType: String, path: String = "/", host: String = "", service: String = "") {
        val type = when (rawType.lowercase()) { "h2" -> "http"; "splithttp" -> "xhttp"; else -> rawType.lowercase() }
        if (type == "tcp" || type.isEmpty()) return
        val t = json("type" to type)
        if (type == "grpc") t.put("service_name", service) else { t.put("path", path); if (host.isNotEmpty()) { if (type == "ws") t.put("headers", json("Host" to host)) else t.put("host", if (type == "http") array(listOf(host)) else host) } }
        c.put("transport", t)
    }
    private fun clash(o: JSONObject): Node {
        val raw = o.getString("type"); val type = when (raw) { "ss" -> "shadowsocks"; "hy2" -> "hysteria2"; else -> raw }
        val c = json("type" to type, "server" to o.getString("server"), "server_port" to o.getInt("port"))
        for ((src, dst) in mapOf("uuid" to "uuid", "password" to "password", "cipher" to if (type == "vmess") "security" else "method", "alterId" to "alter_id", "flow" to "flow", "obfs" to "obfs", "obfs-password" to "obfs_password")) if (o.has(src)) c.put(dst, o.get(src))
        if (type == "hysteria") { c.put("auth_str", o.optString("auth-str", o.optString("auth"))); c.put("up_mbps", o.optString("up", "100").substringBefore(' ').toInt()); c.put("down_mbps", o.optString("down", "100").substringBefore(' ').toInt()) }
        if (type == "hysteria2" && o.has("obfs")) c.put("obfs", json("type" to o.get("obfs"), "password" to o.optString("obfs-password")))
        if (o.optBoolean("tls") || type in listOf("trojan", "hysteria", "hysteria2") || o.has("reality-opts")) {
            val t = tls(o.optString("servername", o.optString("sni")), o.optString("client-fingerprint")); if (o.has("alpn")) t.put("alpn", o.getJSONArray("alpn")); t.put("insecure", o.optBoolean("skip-cert-verify"))
            o.optJSONObject("reality-opts")?.let { t.put("reality", json("enabled" to true, "public_key" to it.getString("public-key"), "short_id" to it.optString("short-id"))) }; c.put("tls", t)
        }
        val network = o.optString("network", "tcp")
        val opts = o.optJSONObject("$network-opts") ?: JSONObject()
        transport(c, network, opts.optString("path", "/"), opts.optJSONObject("headers")?.optString("Host") ?: opts.optJSONArray("host")?.optString(0) ?: "", opts.optString("grpc-service-name"))
        return node(o.optString("name"), c)
    }
    private fun xray(o: JSONObject, title: String = o.optString("tag")): Node {
        val type = o.getString("protocol"); val s = o.getJSONObject("settings")
        val peer = s.optJSONArray("vnext")?.getJSONObject(0) ?: s.optJSONArray("servers")?.getJSONObject(0) ?: error("Нет адреса сервера")
        val user = peer.optJSONArray("users")?.getJSONObject(0) ?: peer
        val c = json("type" to type, "server" to peer.getString("address"), "server_port" to peer.getInt("port"))
        if (type in listOf("vless", "vmess")) c.put("uuid", user.getString("id"))
        if (type == "vless" && user.optString("flow").isNotEmpty()) c.put("flow", user.getString("flow"))
        if (type == "vmess") { c.put("security", user.optString("security", "auto")); c.put("alter_id", user.optInt("alterId")) }
        if (type in listOf("trojan", "shadowsocks")) c.put("password", user.getString("password"))
        if (type == "shadowsocks") c.put("method", user.getString("method"))
        val stream = o.optJSONObject("streamSettings") ?: JSONObject(); val security = stream.optString("security", "none")
        if (security != "none") { val t = stream.optJSONObject("${security}Settings") ?: JSONObject(); val tls = tls(t.optString("serverName"), t.optString("fingerprint")); tls.put("insecure", t.optBoolean("allowInsecure")); if (t.has("alpn")) tls.put("alpn", t.get("alpn")); if (security == "reality") tls.put("reality", json("enabled" to true, "public_key" to t.optString("publicKey"), "short_id" to t.optString("shortId"))); c.put("tls", tls) }
        val net = stream.optString("network", "tcp"); val t = stream.optJSONObject("${net}Settings") ?: JSONObject()
        transport(c, net, t.optString("path", "/"), t.optJSONObject("headers")?.optString("Host") ?: "", t.optString("serviceName"))
        return node(title, c)
    }
    private fun node(name: String, source: JSONObject): Node {
        if (source.optString("type") == "trusttunnel") return TrustTunnelProfile.endpoint(source.getJSONObject("trusttunnel").apply { put("name", name) })
        val c = JSONObject(); source.keys().forEach { if (it in allowed) c.put(it, source.get(it)) }
        // Whitelist nested fields too: no file reads, client certificates, download URLs or provider headers.
        c.optJSONObject("tls")?.let { t -> val clean = JSONObject(); for (k in listOf("enabled", "server_name", "insecure", "alpn", "utls", "reality")) if (t.has(k)) clean.put(k, t.get(k)); for ((k, keys) in mapOf("utls" to listOf("enabled", "fingerprint"), "reality" to listOf("enabled", "public_key", "short_id"))) clean.optJSONObject(k)?.let { nested -> val n = JSONObject(); keys.forEach { key -> if (nested.has(key)) n.put(key, nested.get(key)) }; clean.put(k, n) }; c.put("tls", clean) }
        c.optJSONObject("transport")?.let { t -> val clean = JSONObject(); for (k in listOf("type", "path", "host", "service_name", "max_early_data", "early_data_header_name")) if (t.has(k)) clean.put(k, t.get(k)); t.optJSONObject("headers")?.optString("Host")?.takeIf(String::isNotEmpty)?.let { clean.put("headers", json("Host" to it)) }; c.put("transport", clean) }
        val type = c.getString("type"); val host = c.getString("server").trim('[', ']'); require(host.isNotBlank() && host.length <= 253 && host.none { it.isWhitespace() || it.isISOControl() || it in "/\\@" }); c.put("server", IDN.toASCII(host))
        require(c.optInt("server_port", 443) in 1..65535)
        if (type in listOf("vless", "vmess")) UUID.fromString(c.getString("uuid"))
        val t = c.optJSONObject("transport")?.optString("type") ?: "tcp"
        val unsupported = when { host in listOf("0.0.0.0", "::") -> "Провайдер вернул информационную запись. Проверьте лимит устройств и HWID"; type == "trusttunnel" -> "TrustTunnel пока доступен в Windows версии"; type == "wireguard" -> "WireGuard пока не поддерживается на Android"; t == "xhttp" -> "XHTTP пока доступен в Windows версии"; type !in types -> "Протокол $type пока не поддерживается"; t !in listOf("tcp", "ws", "grpc", "http", "httpupgrade") -> "Транспорт $t не поддерживается"; else -> "" }
        val securityProblem = ConnectionSafety.reason(c)
        val title = name.takeIf(String::isNotBlank) ?: host
        val identity = canonical(c)
        return Node(digest(identity).take(24), title.take(160), c.toString(), country(title), securityProblem.ifEmpty { unsupported })
    }
    private fun canonical(value: Any?): String = when (value) {
        is JSONObject -> value.keys().asSequence().sorted().joinToString(prefix = "{", postfix = "}") { JSONObject.quote(it) + ":" + canonical(value.get(it)) }
        is JSONArray -> (0 until value.length()).joinToString(prefix = "[", postfix = "]") { canonical(value.get(it)) }
        is String -> JSONObject.quote(value)
        else -> value.toString()
    }
    // Ordered: more specific stems first (Австралия before Австрия, Индонезия before Индия).
    // Stems match at a word start, so Russian case endings and city names are recognised.
    private val countryWords = listOf(
        "SE" to "швец|sweden|stockholm|стокгольм", "LV" to "латв|latvia|riga|рига", "LT" to "литв|lithuan|vilnius|вильнюс", "EE" to "эстон|estonia|tallinn|таллин",
        "NL" to "нидерланд|голланд|netherland|holland|amsterdam|амстердам", "DE" to "герман|germany|frankfurt|франкфурт|berlin|берлин|munich|мюнхен|nuremberg|нюрнберг|falkenstein",
        "FI" to "финлян|finland|helsinki|хельсинки", "FR" to "франц|france|paris|париж", "US" to "сша|usa|united states|америк|america|new york|нью-йорк|los angeles|chicago|dallas|miami|seattle",
        "GB" to "британ|англи|united kingdom|england|london|лондон", "RU" to "росси|russia|moscow|москв|petersburg|петербург", "PL" to "польш|poland|warsaw|варшав",
        "TR" to "турци|turkey|türkiye|istanbul|стамбул", "CH" to "швейцар|switzerland|zurich|цюрих", "JP" to "япони|japan|tokyo|токио", "SG" to "сингапур|singapore",
        "CA" to "канад|canada|toronto|торонто", "HU" to "венгр|hungary|budapest|будапешт", "BG" to "болгар|bulgaria|sofia|софия", "CZ" to "чехи|czech|prague|прага",
        "NO" to "норвег|norway|oslo|осло", "AU" to "австрал|australia|sydney|сидней", "AT" to "австри|austria|vienna|вена", "RO" to "румын|romania|bucharest|бухарест",
        "KZ" to "казах|kazakh|almaty|алматы|astana|астана", "BR" to "бразил|brazil", "AE" to "оаэ|uae|emirates|эмират|dubai|дубай", "HK" to "гонконг|hong kong|hongkong",
        "IT" to "итали|italy|milan|милан|rome|рим", "ES" to "испани|spain|madrid|мадрид", "KR" to "корея|south korea|korea|seoul|сеул", "ID" to "индонез|indonesia|jakarta|джакарт",
        "IN" to "индия|india|mumbai|мумбаи", "AM" to "армени|armenia|yerevan|ереван", "GE" to "грузи|tbilisi|тбилиси", "MD" to "молдов|moldova|chisinau|кишин",
        "UA" to "украин|ukrain|kyiv|киев", "BY" to "белорус|беларус|belarus|minsk|минск", "RS" to "серби|serbia|belgrade|белград", "IL" to "израил|israel|tel aviv|тель-авив",
        "IE" to "ирланд|ireland|dublin|дублин", "PT" to "португал|portugal|lisbon|лиссабон", "GR" to "греци|greece|athens|афин", "DK" to "дания|denmark|copenhagen|копенгаген",
        "BE" to "бельги|belgium|brussels|брюссел", "LU" to "люксембург|luxembourg", "IS" to "исланд|iceland|reykjavik", "CY" to "кипр|cyprus", "SK" to "словак|slovakia|bratislava",
        "SI" to "словени|slovenia|ljubljana", "HR" to "хорват|croatia|zagreb", "AR" to "аргентин|argentina", "MX" to "мексик|mexico", "VN" to "вьетнам|vietnam", "TH" to "таиланд|thailand|bangkok|бангкок",
        "MY" to "малайз|malaysia", "UZ" to "узбек|uzbek|tashkent|ташкент", "KG" to "киргиз|кыргыз|kyrgyz|bishkek|бишкек", "AZ" to "азербайдж|azerbaijan|baku|баку",
        "CN" to "китай|china", "TW" to "тайван|taiwan", "ZA" to "юар|south africa", "EG" to "египет|egypt")
    private val countryPatterns = countryWords.map { (code, words) -> code to Regex("(?iu)(?<![\\p{L}])($words)") }
    // Uppercase tokens like "DE-1" or "[NL]"; common VPN locations only, so "WS"/"AI"/"TV" in names stay text.
    private val codeToken = Regex("(?<![A-Za-z])(" + (countryWords.map { it.first } + "UK").joinToString("|") + ")(?![A-Za-z])")
    private val countryNames by lazy {
        java.util.Locale.getISOCountries().flatMap { code -> listOf(java.util.Locale.ENGLISH, java.util.Locale("ru")).map { java.util.Locale("", code).getDisplayCountry(it) to code } }
            .filter { it.first.length >= 4 }.sortedByDescending { it.first.length }
    }
    fun country(name: String): String {
        val flag = name.codePoints().toArray(); for (i in 0 until flag.size - 1) if (flag[i] in 0x1F1E6..0x1F1FF && flag[i + 1] in 0x1F1E6..0x1F1FF) return "${(flag[i] - 0x1F1E6 + 65).toChar()}${(flag[i + 1] - 0x1F1E6 + 65).toChar()}"
        countryPatterns.firstOrNull { it.second.containsMatchIn(name) }?.let { return it.first }
        codeToken.find(name)?.value?.let { return if (it == "UK") "GB" else it }
        return countryNames.firstOrNull { (label, _) ->
            val index = name.indexOf(label, ignoreCase = true)
            index >= 0 && (index == 0 || !name[index - 1].isLetter()) && (index + label.length == name.length || !name[index + label.length].isLetter())
        }?.second.orEmpty()
    }
}
