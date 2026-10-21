package com.bebekon.vpn

import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import java.net.URI

data class WebApp(val domain: String, val browser: String)
/**
 * A browser marked «Напрямую» stays in the tunnel while VPN site rules exist: its traffic goes
 * directly except those sites. Excluding it entirely made site rules silently stop working there.
 * Other Direct apps (banks, government services) stay fully outside the VPN and cannot detect it.
 */
fun resolveWebAppPolicy(state: SavedState, identify: (String) -> WebApp?) = resolveWebAppPolicy(state, identify) { false }
fun resolveWebAppPolicy(state: SavedState, identify: (String) -> WebApp?, isBrowser: (String) -> Boolean): AppTunnelPolicy {
    val policy = appTunnelPolicy(state)
    if (state.preferences.routing != RoutingMode.RULES) return policy
    val apps = effectiveAppRules(state.rules)
    val directBrowsers = if (state.rules.any { it.kind != RuleKind.APP && it.vpn }) apps.filter { (pkg, vpn) -> !vpn && isBrowser(pkg) }.keys else emptySet()
    if (!policy.appOnly) return if (directBrowsers.isEmpty()) policy else policy.copy(excluded = policy.excluded - directBrowsers, siteOnlyBrowsers = directBrowsers)
    val browsers = directBrowsers.toMutableSet()
    val allowed = policy.allowed!!.map { pkg ->
        val app = identify(pkg) ?: return@map pkg
        require(app.browser.isNotBlank() && (apps[app.browser] != false || app.browser in directBrowsers)) { "Браузер веб-приложения исключён из VPN. Разрешите VPN для его браузера" }
        if (apps[app.browser] != true) browsers += app.browser
        app.browser
    }.toSet() + directBrowsers
    return policy.copy(allowed = allowed, siteOnlyBrowsers = browsers)
}
/** Packages that open arbitrary https links, i.e. web browsers. */
fun browserPackages(context: Context): Set<String> = runCatching {
    context.packageManager.queryIntentActivities(Intent(Intent.ACTION_VIEW, Uri.parse("https://example.com/")).addCategory(Intent.CATEGORY_BROWSABLE), PackageManager.MATCH_ALL)
        .map { it.activityInfo.packageName }.toSet()
}.getOrDefault(emptySet())
fun webApp(context: Context, pkg: String): WebApp? = runCatching {
    val metadata = context.packageManager.getApplicationInfo(pkg, PackageManager.GET_META_DATA).metaData ?: return@runCatching null
    val start = metadata.getString("org.chromium.webapk.shell_apk.startUrl") ?: return@runCatching null
    val uri = URI(start)
    if (uri.scheme != "https" || uri.rawUserInfo != null || uri.host.isNullOrBlank()) return@runCatching null
    WebApp(uri.host.lowercase(), metadata.getString("org.chromium.webapk.shell_apk.runtimeHost").orEmpty())
}.getOrNull()

/** A WebAPK launches its host browser: its own package does not own browser sockets. */
fun resolveWebAppRules(state: SavedState, identify: (String) -> WebApp?) = resolveWebAppRules(state, identify) { false }
fun resolveWebAppRules(state: SavedState, identify: (String) -> WebApp?, isBrowser: (String) -> Boolean): SavedState {
    if (state.preferences.routing != RoutingMode.RULES) return state
    val appActions = effectiveAppRules(state.rules)
    val resolved = state.rules.flatMap { rule ->
        if (rule.kind != RuleKind.APP) return@flatMap listOf(rule)
        val native = rule.values.filter { identify(it) == null }
        val web = rule.values.mapNotNull { pkg -> identify(pkg)?.let { pkg to it } }
        listOfNotNull(rule.takeIf { native.isNotEmpty() }?.copy(values = native)) + web.map { (pkg, app) ->
            require(!rule.vpn || appActions[app.browser] != false || isBrowser(app.browser)) { "Браузер веб-приложения исключён из VPN. Уберите его правило «Напрямую» и используйте правила сайтов" }
            rule.copy(id = rule.id + ":web:" + pkg, kind = RuleKind.DOMAIN, values = listOf(app.domain))
        }
    }
    return state.copy(rules = resolved)
}
