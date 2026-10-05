package com.bebekon.vpn

/** Keep diagnostics local and bounded; redact provider credentials before putting them in UI. */
fun redactRoutingLog(message: String, state: SavedState): String {
    val secrets = mutableSetOf<String>()
    fun collect(value: org.json.JSONObject) {
        value.keys().forEach { key ->
            val item = value.opt(key)
            if (item is org.json.JSONObject) collect(item)
            else if (key in setOf("password", "auth_str", "uuid", "private_key", "public_key", "token", "username") && item is String && item.isNotBlank()) secrets += item
        }
    }
    state.selectedNode?.config?.let(::collect)
    state.subscriptions.mapTo(secrets) { it.source }
    var result = message.take(8192)
    secrets.filter { it.isNotBlank() }.sortedByDescending { it.length }.forEach { result = result.replace(it, "[скрыто]", ignoreCase = true) }
    return result.replace(Regex("(?i)https?://[^\\s]+"), "[URL скрыт]")
        .replace(Regex("(?i)[a-z][a-z0-9+.-]*://[^\\s/@]+@"), "[данные скрыты]@")
        .replace(Regex("(?i)(password|auth_str|uuid|token|private_key)\\s*[=:]\\s*[^\\s,}]+"), "$1=[скрыто]")
        .replace(Regex("\\u001B\\[[0-9;]*m"), "").take(2000)
}
