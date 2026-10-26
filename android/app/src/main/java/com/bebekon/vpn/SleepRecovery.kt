package com.bebekon.vpn

/**
 * Screen-on after the phone slept. Short glances change nothing. After a real sleep an idle tunnel
 * gets fresh connections, while one that kept streaming (music, downloads) is left alone and only
 * checked through the tunnel.
 */
object SleepRecovery {
    const val MIN_SLEEP_MS = 30_000L
    const val ACTIVE_WINDOW_MS = 20_000L
    fun slept(screenOffFor: Long) = screenOffFor >= MIN_SLEEP_MS
    fun shouldReset(screenOffFor: Long, sinceTraffic: Long) = slept(screenOffFor) && sinceTraffic >= ACTIVE_WINDOW_MS
}
