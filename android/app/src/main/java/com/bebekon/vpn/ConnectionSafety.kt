package com.bebekon.vpn

import org.json.JSONObject

/** Rechecked at connection time, including configurations saved by older versions. */
internal object ConnectionSafety {
    const val UNSAFE_TLS = "Небезопасный TLS: проверка сертификата VPN-сервера обязательна. Попросите провайдера безопасную конфигурацию"
    fun reason(config: JSONObject): String {
        for (key in listOf("password", "auth_str", "uuid", "plugin_opts")) {
            val value = config.optString(key)
            if (value.length > 4096 || value.any(Char::isISOControl)) return "Недопустимые параметры VPN-сервера"
        }
        if (config.optJSONObject("tls")?.optBoolean("insecure") == true) return UNSAFE_TLS
        val plugin = config.optString("plugin")
        if (plugin.isEmpty()) return ""
        if (plugin !in setOf("obfs-local", "v2ray-plugin")) return "Поддерживаются только встроенные Shadowsocks-плагины"
        val options = config.optString("plugin_opts")
        if (options.length > 4096 || options.any { it.isISOControl() || it == '\\' }) return "Небезопасные параметры Shadowsocks-плагина"
        val keys = mutableSetOf<String>()
        for (entry in options.split(';').filter(String::isNotEmpty)) {
            val key = entry.substringBefore('='); val value = entry.substringAfter('=', "")
            val allowed = if (plugin == "obfs-local") setOf("obfs", "obfs-host") else setOf("tls", "host", "path", "mode", "mux")
            // cert/certRaw and unknown future options must not read files or replace trust roots.
            if (key !in allowed || !keys.add(key)) return "Небезопасные параметры Shadowsocks-плагина"
            if (key == "mux" && value.toIntOrNull()?.let { it in 0..16 } != true || key == "mode" && value !in setOf("websocket", "quic") || key == "obfs" && value !in setOf("http", "tls")) return "Неподдерживаемые параметры Shadowsocks-плагина"
        }
        return ""
    }
}
