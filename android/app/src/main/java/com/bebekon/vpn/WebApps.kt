package com.bebekon.vpn

import android.content.Context
import android.content.pm.PackageManager
import java.net.URI

data class WebApp(val domain: String, val browser: String)
fun webApp(context: Context, pkg: String): WebApp? = runCatching {
    val metadata = context.packageManager.getApplicationInfo(pkg, PackageManager.GET_META_DATA).metaData ?: return@runCatching null
    val start = metadata.getString("org.chromium.webapk.shell_apk.startUrl") ?: return@runCatching null
    val uri = URI(start)
    if (uri.scheme != "https" || uri.rawUserInfo != null || uri.host.isNullOrBlank()) return@runCatching null
    WebApp(uri.host.lowercase(), metadata.getString("org.chromium.webapk.shell_apk.runtimeHost").orEmpty())
}.getOrNull()

/** A WebAPK launches its host browser: its own package does not own browser sockets. */
fun resolveWebAppRules(state: SavedState, identify: (String) -> WebApp?): SavedState {
    if (state.preferences.routing != RoutingMode.RULES) return state
    val appActions = effectiveAppRules(state.rules)
    val resolved = state.rules.flatMap { rule ->
        if (rule.kind != RuleKind.APP) return@flatMap listOf(rule)
        val native = rule.values.filter { identify(it) == null }
        val web = rule.values.mapNotNull { pkg -> identify(pkg)?.let { pkg to it } }
        listOfNotNull(rule.takeIf { native.isNotEmpty() }?.copy(values = native)) + web.map { (pkg, app) ->
            require(!rule.vpn || appActions[app.browser] != false) { "Браузер веб-приложения исключён из VPN. Уберите его правило «Напрямую» и используйте правила сайтов" }
            rule.copy(id = rule.id + ":web:" + pkg, kind = RuleKind.DOMAIN, values = listOf(app.domain))
        }
    }
    return state.copy(rules = resolved)
}
