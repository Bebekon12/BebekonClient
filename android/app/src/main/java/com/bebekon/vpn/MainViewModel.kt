package com.bebekon.vpn

import android.app.Application
import android.content.Intent
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.sync.Semaphore
import kotlinx.coroutines.sync.withPermit

class MainViewModel(app: Application) : AndroidViewModel(app) {
    val repo = app.repo
    val saved = repo.state
    val session = VpnController.session
    val busy = MutableStateFlow(false)
    val message = MutableStateFlow(repo.storageError.value)
    val importText = MutableStateFlow("")
    val siteCheck = MutableStateFlow(SiteCheck())
    val origin = OriginLocation(app, viewModelScope)
    fun diagnoseSite(address: String) {
        if (siteCheck.value.running) return
        siteCheck.value = SiteCheck(running = true)
        viewModelScope.launch {
            siteCheck.value = try { checkSite(getApplication(), saved.value, address) }
            catch (_: Exception) { SiteCheck(direct = "Не удалось проверить адрес", vpn = "Не удалось проверить адрес") }
        }
    }
    private val pingLimit = Semaphore(4)
    private val pingJobs = mutableMapOf<String, Job>()
    init {
        viewModelScope.launch { saved.collect { origin.enabled(it.preferences.mapLocation) } }
        viewModelScope.launch { combine(saved, session) { state, current -> state to current }.collect { (state, current) ->
            refreshMapDestination(state, current)
        } }
        viewModelScope.launch { while (isActive) { delay(30_000); origin.refresh(); refreshMapDestination(saved.value, session.value) } }
        // Older Xray imports lost profile remarks. Refresh them once per launch; identity stays unchanged.
        saved.value.subscriptions.filter { sub -> sub.source.startsWith("https://") && sub.nodes.any { it.country.isBlank() && it.name.lowercase() in setOf("proxy", "vpn", "out", "outbound") } }.forEach(::refresh)
    }
    private fun refreshMapDestination(state: SavedState, current: Session) {
        val node = state.selectedNode
        val ip = if (current.phase == Phase.ON && node?.country.isNullOrBlank() && current.server == node?.name) current.publicIp else ""
        origin.serverIp(node?.id.orEmpty(), ip)
    }
    fun task(action: suspend () -> Unit) = viewModelScope.launch {
        busy.value = true
        try { withContext(Dispatchers.IO) { action() } } catch (e: Exception) { message.value = e.message?.takeIf { !it.contains("://") && it.length < 180 && !it.contains("Failed requirement") } ?: "Не удалось выполнить действие. Проверьте параметры" } finally { busy.value = false }
    }
    fun import(source: String, name: String) = task { val subscription = repo.load(source, name); repo.add(subscription); importText.value = ""; message.value = "Добавлено серверов: ${subscription.nodes.size}" }
    fun saveTrustTunnel(node: Node, previous: Node?) = task {
        TrustTunnelProfile.validate(node)
        repo.update { state ->
            val present = previous != null && state.nodes.any { it.id == previous.id }
            val subscriptions = if (present) state.subscriptions.map { sub ->
                val replacing = sub.nodes.any { it.id == previous!!.id }
                sub.copy(nodes = sub.nodes.map { if (it.id == previous!!.id) node else it },
                    name = if (replacing && sub.source.startsWith("manual:")) node.name else sub.name,
                    source = if (remoteSubscription(sub.source) || sub.source.startsWith("manual:")) sub.source else if (sub.nodes.size == 1 && replacing) node.config.toString() else sub.source)
            }
            else state.subscriptions + Subscription(name = node.name, source = "manual:" + java.util.UUID.randomUUID(), nodes = listOf(node))
            state.copy(subscriptions = subscriptions, selected = if (state.selected == previous?.id || state.selected == null) node.id else state.selected,
                favorites = if (previous?.id in state.favorites) state.favorites - previous!!.id + node.id else state.favorites)
        }
        if (saved.value.selected == node.id) VpnController.reload(getApplication())
        message.value = "Сервер TrustTunnel сохранён"
    }
    fun removeNode(node: Node) = task {
        repo.update { state -> state.copy(subscriptions = state.subscriptions.map { it.copy(nodes = it.nodes.filterNot { n -> n.id == node.id }) }.filter { it.nodes.isNotEmpty() }, selected = state.selected?.takeIf { it != node.id }, favorites = state.favorites - node.id) }
        if (saved.value.selectedNode == null && session.value.active) VpnController.stop(getApplication())
    }
    fun refresh(sub: Subscription) = task {
        val before = saved.value.selectedNode
        val new = repo.load(sub.source, sub.name).copy(id = sub.id); repo.add(new)
        val after = saved.value.selectedNode
        if (before != null && after == null) VpnController.stop(getApplication())
        else if (before?.outbound != after?.outbound) VpnController.reload(getApplication())
        message.value = if (before != null && after == null) "Подписка обновлена. Прежний сервер удалён провайдером — выберите сервер" else "Подписка обновлена"
    }
    fun removeSubscription(id: String) = task {
        repo.update { s -> val remaining = s.subscriptions.filterNot { it.id == id }; s.copy(subscriptions = remaining, selected = s.selected?.takeIf { key -> remaining.any { sub -> sub.nodes.any { it.id == key } } }) }
        if (saved.value.selectedNode == null && session.value.active) VpnController.stop(getApplication())
    }
    fun toggleSubscriptionVisibility(id: String) = task {
        repo.update { state -> state.copy(subscriptions = state.subscriptions.map { if (it.id == id) it.copy(hidden = !it.hidden) else it }) }
    }
    fun select(node: Node) { if (node.unsupported.isNotEmpty()) { message.value = node.unsupported; return }; task { repo.update { it.copy(selected = node.id) }; VpnController.reload(getApplication()) } }
    fun favorite(node: Node) = task { repo.update { it.copy(favorites = if (node.id in it.favorites) it.favorites - node.id else it.favorites + node.id) } }
    fun preferences(value: Preferences) = task {
        require(value.mtu in 1280..1500)
        val old = saved.value.preferences; repo.update { it.copy(preferences = value) }
        if (old.ping != value.ping || old.pingTarget != value.pingTarget) withContext(Dispatchers.Main) { pingJobs.values.forEach { it.cancel() }; pingJobs.clear(); repo.pings.value = emptyMap() }
        if (old.routing != value.routing || old.sitesInAllApps != value.sitesInAllApps || old.allowLan != value.allowLan || old.dnsResolver != value.dnsResolver || old.mtu != value.mtu || old.routingDiagnostics != value.routingDiagnostics || old.ipv6 != value.ipv6) VpnController.reload(getApplication())
    }
    private fun remoteSubscription(source: String): Boolean = source.startsWith("https://") ||
        (source.startsWith("tt://", true) && runCatching { TrustTunnelProfile.link(source).second != null }.getOrDefault(false))
    fun refreshAll() { saved.value.subscriptions.filter { remoteSubscription(it.source) }.forEach(::refresh) }
    override fun onCleared() { origin.close(); super.onCleared() }
    fun saveAppRules(apps: List<InstalledApp>, actions: Map<String, Boolean>) = task {
        require(actions.size <= 256)
        repo.update { state ->
            val rules = replaceAppRules(state.rules, actions, apps.associate { it.packageName to it.label }, System.currentTimeMillis())
            rules.forEach(CoreConfig::validateRule)
            state.copy(rules = rules)
        }
        VpnController.reload(getApplication()); message.value = "Сохранено правил приложений: ${actions.size}"
    }
    fun saveRule(rule: Rule) = task { CoreConfig.validateRule(rule); if (rule.kind in listOf(RuleKind.GEOSITE, RuleKind.GEOIP)) rule.values.forEach { repo.geo((if (rule.kind == RuleKind.GEOSITE) "geosite-" else "geoip-") + it) }; repo.update { it.copy(rules = it.rules.filterNot { r -> r.id == rule.id } + rule) }; VpnController.reload(getApplication()) }
    fun removeRule(id: String) = task { repo.update { it.copy(rules = it.rules.filterNot { r -> r.id == id }) }; VpnController.reload(getApplication()) }
    fun preset(rules: List<Rule>) = task { repo.update { state -> val signatures = state.rules.map { "${it.kind}|${it.vpn}|${it.values.joinToString()}" }.toSet(); val additions = rules.filter { "${it.kind}|${it.vpn}|${it.values.joinToString()}" !in signatures }; state.copy(rules = state.rules + additions.mapIndexed { i, r -> r.copy(created = System.currentTimeMillis() + i) }) }; VpnController.reload(getApplication()); message.value = "Готовые правила добавлены" }
    fun ping(node: Node) { if (pingJobs[node.id]?.isActive == true || node.unsupported.isNotEmpty()) return
        val preferences = saved.value.preferences
        val method = preferences.ping
        val target = preferences.pingTarget
        repo.pings.value = repo.pings.value + (node.id to (repo.pings.value[node.id] ?: PingResult(method = method)).copy(running = true, queued = true))
        pingJobs[node.id] = viewModelScope.launch {
            pingLimit.withPermit {
                repo.pings.value = repo.pings.value + (node.id to (repo.pings.value[node.id] ?: PingResult(method = method)).copy(running = true, queued = false))
                val result = withContext(Dispatchers.IO) { runCatching {
                    if (method == PingMethod.TCP) {
                        NativeCore.setup(getApplication()); val platform = AndroidPlatform(getApplication()); try { io.nekohasekai.libbox.Libbox.bebekonTCPProbe(node.host, node.port, platform) } finally { platform.close() }
                    } else NativeCore.probe(getApplication(), SavedState(subscriptions = listOf(Subscription(name = "probe", source = "", nodes = listOf(node))), selected = node.id, preferences = preferences), if (method == PingMethod.HTTPS_HEAD) "HEAD" else "GET").also { check(it.statusCode == 204) { "HTTPS-проверка не вернула ожидаемый ответ 204" } }.millis
                } }
                repo.pings.value = repo.pings.value + (node.id to result.fold({ PingResult(millis = it.coerceAtLeast(1), method = method, target = target) }, { PingResult(failed = true, method = method, target = target) }))
            }
        }
    }
    fun pingAll() { saved.value.visibleNodes.forEach(::ping) }
    fun receive(intent: Intent?) { val text = when (intent?.action) { Intent.ACTION_SEND -> intent.getStringExtra(Intent.EXTRA_TEXT); Intent.ACTION_VIEW -> intent.dataString; else -> null }; if (!text.isNullOrBlank()) importText.value = text }
}
