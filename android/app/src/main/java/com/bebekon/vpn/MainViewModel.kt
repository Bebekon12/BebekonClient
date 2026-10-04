package com.bebekon.vpn

import android.app.Application
import android.content.Intent
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.sync.Semaphore
import kotlinx.coroutines.sync.withPermit

class MainViewModel(app: Application) : AndroidViewModel(app) {
    val repo = app.repo
    val saved = repo.state
    val session = VpnController.session
    val busy = MutableStateFlow(false)
    val message = MutableStateFlow(repo.storageError.value)
    val importText = MutableStateFlow("")
    private val pingLimit = Semaphore(3)
    fun task(action: suspend () -> Unit) = viewModelScope.launch {
        busy.value = true
        try { withContext(Dispatchers.IO) { action() } } catch (e: Exception) { message.value = e.message?.takeIf { !it.contains("://") && it.length < 180 && !it.contains("Failed requirement") } ?: "Не удалось выполнить действие. Проверьте параметры" } finally { busy.value = false }
    }
    fun import(source: String, name: String) = task { val subscription = repo.load(source, name); repo.add(subscription); importText.value = ""; message.value = "Добавлено серверов: ${subscription.nodes.size}" }
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
    fun select(node: Node) { if (node.unsupported.isNotEmpty()) { message.value = node.unsupported; return }; task { repo.update { it.copy(selected = node.id) }; VpnController.reload(getApplication()) } }
    fun favorite(node: Node) = task { repo.update { it.copy(favorites = if (node.id in it.favorites) it.favorites - node.id else it.favorites + node.id) } }
    fun preferences(value: Preferences) = task { val old = saved.value.preferences; repo.update { it.copy(preferences = value) }; if (old.routing != value.routing || old.allowLan != value.allowLan) VpnController.reload(getApplication()) }
    fun saveRule(rule: Rule) = task { CoreConfig.validateRule(rule); if (rule.kind in listOf(RuleKind.GEOSITE, RuleKind.GEOIP)) rule.values.forEach { repo.geo((if (rule.kind == RuleKind.GEOSITE) "geosite-" else "geoip-") + it) }; repo.update { it.copy(rules = it.rules.filterNot { r -> r.id == rule.id } + rule) }; VpnController.reload(getApplication()) }
    fun removeRule(id: String) = task { repo.update { it.copy(rules = it.rules.filterNot { r -> r.id == id }) }; VpnController.reload(getApplication()) }
    fun preset(rules: List<Rule>) = task { repo.update { state -> val signatures = state.rules.map { "${it.kind}|${it.vpn}|${it.values.joinToString()}" }.toSet(); val additions = rules.filter { "${it.kind}|${it.vpn}|${it.values.joinToString()}" !in signatures }; state.copy(rules = state.rules + additions.mapIndexed { i, r -> r.copy(created = System.currentTimeMillis() + i) }) }; VpnController.reload(getApplication()); message.value = "Готовые правила добавлены" }
    fun ping(node: Node) { if (repo.pings.value[node.id]?.running == true || node.unsupported.isNotEmpty()) return
        viewModelScope.launch {
            pingLimit.withPermit {
                repo.pings.value = repo.pings.value + (node.id to (repo.pings.value[node.id] ?: PingResult()).copy(running = true))
                val result = withContext(Dispatchers.IO) { runCatching {
                    if (saved.value.preferences.ping == PingMethod.TCP) {
                        NativeCore.setup(getApplication()); val platform = AndroidPlatform(getApplication()); try { io.nekohasekai.libbox.Libbox.bebekonTCPProbe(node.host, node.port, platform) } finally { platform.close() }
                    } else NativeCore.probe(getApplication(), SavedState(subscriptions = listOf(Subscription(name = "probe", source = "", nodes = listOf(node))), selected = node.id), if (saved.value.preferences.ping == PingMethod.HTTPS_HEAD) "HEAD" else "GET").millis
                } }
                repo.pings.value = repo.pings.value + (node.id to result.fold({ PingResult(millis = it.coerceAtLeast(1)) }, { PingResult(failed = true) }))
            }
        }
    }
    fun pingAll() { saved.value.nodes.forEach(::ping) }
    fun receive(intent: Intent?) { val text = when (intent?.action) { Intent.ACTION_SEND -> intent.getStringExtra(Intent.EXTRA_TEXT); Intent.ACTION_VIEW -> intent.dataString; else -> null }; if (!text.isNullOrBlank()) importText.value = text }
}
