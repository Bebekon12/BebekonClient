package com.bebekon.vpn

/** A slow peer must not reset the entire operation budget on every byte received. */
internal class NetworkBudget(milliseconds: Long, private val clock: () -> Long = System::nanoTime) {
    private val started = clock()
    private val maximum = milliseconds * 1_000_000
    init { require(milliseconds in 1..600_000) }
    fun check() { require(clock() - started < maximum) { "Превышено время загрузки. Повторите попытку" } }
}
