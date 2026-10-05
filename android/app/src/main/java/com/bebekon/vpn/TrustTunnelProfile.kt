package com.bebekon.vpn

import org.json.JSONObject
import org.tomlj.Toml
import org.tomlj.TomlArray
import org.tomlj.TomlTable
import java.net.IDN
import java.net.URI
import java.nio.ByteBuffer
import java.nio.charset.CodingErrorAction
import java.security.cert.CertificateFactory
import java.util.Base64

/** Connection-only schema. Imported listeners, routing and file paths are never executed. */
internal object TrustTunnelProfile {
    private const val INVALID = "Проверьте адрес, домен сертификата, логин и пароль TrustTunnel"
    private val keys = setOf("version", "hostname", "address", "addresses", "username", "password", "custom_sni", "has_ipv6", "skip_verification", "certificate", "upstream_protocol", "anti_dpi", "client_random", "client_random_prefix", "name", "dns_upstreams", "subscription_url", "post_quantum_group_enabled")
    fun address(text: String): Pair<String, Int> {
        val input = text.trim()
        require(input.length <= 300 && input.none { it.isWhitespace() || it.isISOControl() }) { INVALID }
        val uri = URI("tcp://$input")
        require(uri.rawUserInfo == null && uri.rawQuery == null && uri.rawFragment == null && uri.rawPath.isNullOrEmpty() && uri.port in -1..65535 && uri.port != 0) { INVALID }
        val host = host(uri.host?.trim('[', ']') ?: error(INVALID))
        return host to if (uri.port < 0) 443 else uri.port
    }
    private fun host(text: String): String {
        require(text.isNotBlank() && text.length <= 253 && text.none { it.isWhitespace() || it.isISOControl() || it in "/\\@|?#" } && text !in setOf("0.0.0.0", "::")) { INVALID }
        return if (text.contains(':')) { require(Regex("[0-9a-fA-F:]+(?:[0-9.]+)?").matches(text)) { INVALID }; java.net.InetAddress.getByName(text).hostAddress!! }
        else IDN.toASCII(text, IDN.USE_STD3_ASCII_RULES)
    }
    private fun encodedAddress(value: Pair<String, Int>) = (if (value.first.contains(':')) "[${value.first}]" else value.first) + ":${value.second}"
    fun manual(name: String, endpointAddress: String, hostname: String, sni: String, username: String, password: String, protocol: String, existing: Node? = null): Node {
        val old = existing?.config?.optJSONObject("trusttunnel") ?: JSONObject()
        return endpoint(JSONObject(old.toString()).apply {
            // Preserve advanced imported tuning unless the user changes its addresses.
            if (existing == null || encodedAddress(address(endpointAddress)) != encodedAddress(existing.host to existing.port)) put("addresses", array(listOf(encodedAddress(address(endpointAddress)))))
            put("name", name.trim().ifBlank { endpointAddress.trim() }); put("hostname", hostname.trim()); put("custom_sni", sni.trim())
            put("username", username.trim()); put("password", password); put("upstream_protocol", protocol)
        })
    }
    fun endpoint(source: JSONObject): Node {
        val root = source.optJSONObject("endpoint") ?: source
        require(root.keys().asSequence().all { it in keys }) { "Неизвестные параметры TrustTunnel. Обновите приложение" }
        fun text(key: String, default: String = ""): String = if (!root.has(key)) default else (root.get(key) as? String ?: error(INVALID))
        fun flag(key: String, default: Boolean) = if (!root.has(key)) default else (root.get(key) as? Boolean ?: error(INVALID))
        require(!root.has("version") || root.get("version") is Number && root.getDouble("version") == 1.0) { "Неизвестная версия TrustTunnel JSON" }
        require(!flag("skip_verification", false)) { ConnectionSafety.UNSAFE_TLS }
        val addresses = if (root.has("addresses")) root.getJSONArray("addresses").strings() else listOf(text("address"))
        require(addresses.size in 1..16) { INVALID }
        val normalized = addresses.map { encodedAddress(address(it)) }
        val first = address(normalized.first())
        val hostname = host(text("hostname")); val sni = text("custom_sni").let { if (it.isEmpty()) "" else host(it) }
        val username = text("username"); val password = text("password")
        require(username.isNotEmpty() && username.length <= 1024 && ':' !in username && username.none(Char::isISOControl) && password.isNotEmpty() && password.length <= 4096 && password.none(Char::isISOControl)) { INVALID }
        val protocol = text("upstream_protocol", "http2").lowercase(); require(protocol in setOf("http2", "http3", "auto")) { INVALID }
        val random = text("client_random", text("client_random_prefix"))
        val parts = random.split('/'); require(parts.size <= 2 && parts.all { it.length <= 64 && it.length % 2 == 0 && Regex("[0-9a-fA-F]*").matches(it) } && (parts.size == 1 || parts[0].isNotEmpty() && parts[0].length == parts[1].length)) { INVALID }
        val certificate = text("certificate"); if (certificate.isNotEmpty()) validateCertificate(certificate)
        val dns = root.optJSONArray("dns_upstreams")?.strings().orEmpty()
        require(dns.size <= 16 && dns.all { it.length <= 2048 && it.isNotBlank() && it.none(Char::isISOControl) }) { INVALID }
        val options = json("hostname" to hostname, "addresses" to array(normalized), "username" to username, "password" to password, "custom_sni" to sni,
            "upstream_protocol" to protocol, "has_ipv6" to flag("has_ipv6", true), "anti_dpi" to flag("anti_dpi", false), "client_random" to random, "certificate" to certificate, "post_quantum_group_enabled" to flag("post_quantum_group_enabled", true))
        // Our resolver policy controls DNS; imported upstreams and routing are intentionally omitted.
        val config = json("type" to "trusttunnel", "server" to first.first, "server_port" to first.second, "tls" to json("enabled" to true, "server_name" to hostname), "trusttunnel" to options)
        val name = text("name", first.first).trim().ifBlank { first.first }; require(name.length <= 160 && name.none(Char::isISOControl)) { INVALID }
        return Node(digest(config.toString()).take(24), name, config.toString(), SubscriptionParser.country(name))
    }
    fun validate(node: Node) {
        val c = node.config
        require(c.optString("type") == "trusttunnel" && c.optJSONObject("tls")?.optBoolean("insecure") == false) { INVALID }
        val checked = endpoint(c.getJSONObject("trusttunnel"))
        require(checked.host == node.host && checked.port == node.port) { INVALID }
    }
    fun link(input: String): Pair<JSONObject, String?> {
        require(input.startsWith("tt://", true) && input.length <= 131072) { INVALID }
        val text = input.substring(5).removePrefix("?"); require(text.isNotEmpty() && Regex("[A-Za-z0-9_-]+").matches(text)) { INVALID }
        val bytes = Base64.getUrlDecoder().decode(text); var offset = 0
        fun varint(): Long { require(offset < bytes.size) { INVALID }; val first = bytes[offset++].toInt() and 255; val size = 1 shl (first shr 6); require(size - 1 <= bytes.size - offset) { INVALID }; var value = (first and 63).toLong(); repeat(size - 1) { value = (value shl 8) or (bytes[offset++].toLong() and 255) }; return value }
        val root = JSONObject(); val addresses = mutableListOf<String>(); val seen = mutableSetOf<Long>(); var version = 0L; var subscription: String? = null
        while (offset < bytes.size) {
            val tag = varint(); val length = varint(); require(length <= bytes.size - offset) { INVALID }
            val end = offset + length.toInt(); val data = bytes.copyOfRange(offset, end); offset = end
            fun string() = Charsets.UTF_8.newDecoder().onMalformedInput(CodingErrorAction.REPORT).onUnmappableCharacter(CodingErrorAction.REPORT).decode(ByteBuffer.wrap(data)).toString()
            fun bool(): Boolean { require(data.size == 1 && data[0].toInt() in 0..1) { INVALID }; return data[0].toInt() == 1 }
            if (tag != 2L && tag <= 14L) require(seen.add(tag)) { INVALID }
            when (tag) {
                0L -> { val first = data.firstOrNull()?.toInt()?.and(255) ?: error(INVALID); require(data.size == 1 && first in 0..2) { "Неизвестная версия ссылки TrustTunnel" }; version = first.toLong() }
                1L -> root.put("hostname", string())
                2L -> { addresses += string(); require(addresses.size <= 16) { INVALID } }
                3L -> root.put("custom_sni", string())
                4L -> root.put("has_ipv6", bool())
                5L -> root.put("username", string())
                6L -> root.put("password", string())
                7L -> root.put("skip_verification", bool())
                8L -> { val certs = CertificateFactory.getInstance("X.509").generateCertificates(data.inputStream()); require(certs.size in 1..16) { INVALID }; root.put("certificate", certs.joinToString("\n") { "-----BEGIN CERTIFICATE-----\n" + Base64.getMimeEncoder(64, byteArrayOf(10)).encodeToString(it.encoded) + "\n-----END CERTIFICATE-----" }) }
                9L -> { require(data.size == 1 && data[0].toInt() in 1..2) { INVALID }; root.put("upstream_protocol", if (data[0].toInt() == 1) "http2" else "http3") }
                10L -> root.put("anti_dpi", bool())
                11L -> root.put("client_random", string())
                12L -> root.put("name", string())
                13L -> Unit // Provider resolver hints do not replace our DNS policy.
                14L -> subscription = string()
            }
        }
        if (addresses.isNotEmpty()) root.put("addresses", array(addresses))
        subscription?.let { val u = URI(it); require(version >= 2 && u.scheme == "https" && u.host != null && u.rawFragment == null) { INVALID } }
        if (subscription == null || addresses.isNotEmpty()) endpoint(root)
        return root to subscription
    }
    fun parseToml(text: String): Node {
        require(text.length <= 131072) { "Конфигурация TrustTunnel слишком большая" }
        // Bound recursive TOML arrays/tables before invoking ANTLR. Quoted brackets are ignored.
        var depth = 0; var quote: Char? = null; var escaped = false; var comment = false
        for (char in text) {
            if (comment) { if (char == '\n') comment = false; continue }
            if (quote != null) { if (escaped) escaped = false else if (char == '\\' && quote == '"') escaped = true else if (char == quote) quote = null; continue }
            when (char) { '#' -> comment = true; '"', '\'' -> quote = char; '[', '{' -> { depth++; require(depth <= 16) { "Слишком сложная TOML конфигурация" } }; ']', '}' -> { depth--; require(depth >= 0) { INVALID } } }
        }
        require(text.lineSequence().all { it.substringBefore('=').count { char -> char == '.' } <= 16 }) { "Слишком сложная TOML конфигурация" }
        val parsed = Toml.parse(text); require(!parsed.hasErrors()) { "Повреждённая TOML конфигурация TrustTunnel" }
        fun objectOf(table: TomlTable, level: Int = 0): JSONObject = JSONObject().also { target -> require(level <= 16 && table.size() <= 64) { INVALID }; table.keySet().forEach { key -> val value = table.get(listOf(key)); target.put(key, when (value) { is TomlTable -> objectOf(value, level + 1); is TomlArray -> { require(value.size() <= 16) { INVALID }; array((0 until value.size()).map { value.get(it) }) }; else -> value }) } }
        return endpoint(objectOf(parsed.getTable("endpoint") ?: error(INVALID)))
    }
    private fun validateCertificate(pem: String) {
        require(pem.length <= 65536) { INVALID }
        val pattern = Regex("-----BEGIN CERTIFICATE-----\\s*([A-Za-z0-9+/=\\s]+)-----END CERTIFICATE-----")
        val matches = pattern.findAll(pem).toList(); require(matches.size in 1..16 && pattern.replace(pem, "").isBlank()) { INVALID }
        require(runCatching { matches.forEach { val stream = Base64.getMimeDecoder().decode(it.groupValues[1]).inputStream(); CertificateFactory.getInstance("X.509").generateCertificate(stream); require(stream.available() == 0) } }.isSuccess) { "Повреждённый сертификат TrustTunnel" }
    }
    fun config(node: Node, port: Int, password: String): String {
        validate(node); val options = node.config.getJSONObject("trusttunnel")
        fun q(value: String) = JSONObject.quote(value).replace("\\/", "/")
        return "loglevel = \"error\"\nvpn_mode = \"general\"\nkillswitch_enabled = true\nexclusions = []\npost_quantum_group_enabled = ${options.optBoolean("post_quantum_group_enabled", true)}\n[endpoint]\n" +
            "hostname = ${q(options.getString("hostname"))}\naddresses = ${options.getJSONArray("addresses")}\n" +
            "username = ${q(options.getString("username"))}\npassword = ${q(options.getString("password"))}\n" +
            "custom_sni = ${q(options.optString("custom_sni"))}\nupstream_protocol = ${q(options.getString("upstream_protocol"))}\n" +
            "has_ipv6 = ${options.optBoolean("has_ipv6", true)}\nanti_dpi = ${options.optBoolean("anti_dpi")}\nclient_random = ${q(options.optString("client_random"))}\n" +
            "skip_verification = false\ncertificate = ${q(options.optString("certificate"))}\ndns_upstreams = []\n" +
            "[listener.socks]\naddress = \"127.0.0.1:$port\"\nusername = \"bebekon\"\npassword = ${q(password)}\n"
    }
}
