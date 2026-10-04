package com.bebekon.vpn

import java.util.Locale

data class TrafficHistory(val down: List<Long> = emptyList(), val up: List<Long> = emptyList(), val totalDown: Long = 0, val totalUp: Long = 0) {
    fun sample(session: Session) = copy(down = (down + session.down.coerceAtLeast(0)).takeLast(120), up = (up + session.up.coerceAtLeast(0)).takeLast(120), totalDown = session.totalDown, totalUp = session.totalUp)
}
class TrafficTotals {
    private var previous = 0L
    var total = 0L; private set
    fun beginCounter() { previous = 0 }
    fun update(raw: Long): Long {
        val current = raw.coerceAtLeast(0)
        total += if (current >= previous) current - previous else current
        previous = current
        return total
    }
}
fun trafficGb(bytes: Long) = String.format(Locale.ROOT, "%.3f ГБ", bytes.coerceAtLeast(0) / 1_000_000_000.0)
