package com.bebekon.vpn

import android.content.Context
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicBoolean

fun remoteSubscription(source: String): Boolean = source.startsWith("https://") ||
    (source.startsWith("tt://", true) && runCatching { TrustTunnelProfile.link(source).second != null }.getOrDefault(false))

/**
 * Providers rotate servers; a stale subscription looks like a broken VPN. Remote subscriptions
 * refresh at the provider's profile-update-interval, otherwise every 12 hours. A failed attempt
 * keeps the previous servers and is retried after an hour.
 */
object AutoRefresh {
    const val DEFAULT_HOURS = 12
    private const val RETRY_MS = 3_600_000L
    private val running = AtomicBoolean()
    private val attempts = ConcurrentHashMap<String, Long>()

    fun due(sub: Subscription, now: Long, lastAttempt: Long = 0): Boolean =
        remoteSubscription(sub.source) && now - sub.updated >= (if (sub.updateInterval > 0) sub.updateInterval else DEFAULT_HOURS) * 3_600_000L && now - lastAttempt >= RETRY_MS

    /** Blocking network work; call from a background dispatcher. */
    fun runDue(context: Context) {
        if (!running.compareAndSet(false, true)) return
        try {
            val repo = context.repo; val now = System.currentTimeMillis()
            for (sub in repo.state.value.subscriptions.filter { due(it, now, attempts[it.id] ?: 0) }) {
                attempts[sub.id] = now
                runCatching { refresh(context, sub) }
                    .onSuccess { repo.log("Подписка «${sub.name}» обновлена автоматически") }
                    .onFailure { repo.log("Не удалось автоматически обновить подписку «${sub.name}»; серверы сохранены") }
            }
        } finally { running.set(false) }
    }

    /** Re-downloads one subscription, keeping its id, name and visibility, and applies the result to an active VPN. */
    fun refresh(context: Context, sub: Subscription): Boolean {
        val repo = context.repo
        val before = repo.state.value.selectedNode
        repo.add(repo.load(sub.source, sub.name).copy(id = sub.id))
        val after = repo.state.value.selectedNode
        val removed = before != null && after == null
        if (removed) VpnController.stop(context) else if (before?.outbound != after?.outbound) VpnController.reload(context)
        return removed
    }
}
