package com.bebekon.vpn

import android.content.Context
import android.net.ConnectivityManager
import android.net.DnsResolver
import android.net.Network
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import android.os.Build
import android.os.CancellationSignal
import android.os.Handler
import android.system.ErrnoException
import io.nekohasekai.libbox.ExchangeContext
import io.nekohasekai.libbox.LocalDNSTransport
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit

/**
 * The network Android itself chose for Bebekon. The app is always excluded from its own tunnel,
 * so its active network is the physical default (Wi-Fi before mobile data), never the VPN.
 * The first INTERNET network in allNetworks is often mobile data kept alive in the background.
 */
object PhysicalNetwork {
    fun current(context: Context): Network? = current(context.getSystemService(ConnectivityManager::class.java))
    fun current(connectivity: ConnectivityManager): Network? {
        connectivity.activeNetwork?.takeIf { usable(connectivity, it) }?.let { return it }
        // Handover gap: rank like Android does instead of trusting list order.
        return connectivity.allNetworks.filter { usable(connectivity, it) }.minByOrNull { rank(connectivity, it) }
    }
    fun usable(connectivity: ConnectivityManager, network: Network) = connectivity.getNetworkCapabilities(network)?.let {
        it.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET) && it.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN)
    } == true
    private fun rank(connectivity: ConnectivityManager, network: Network): Int {
        val c = connectivity.getNetworkCapabilities(network) ?: return Int.MAX_VALUE
        val transport = when {
            c.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET) -> 0
            c.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) -> 1
            c.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR) -> 2
            else -> 3
        }
        return (if (c.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED)) 0 else 10) + transport
    }
    /** Follows Android's default physical network, including Wi-Fi ↔ mobile data handovers. */
    fun listen(connectivity: ConnectivityManager, callback: ConnectivityManager.NetworkCallback, handler: Handler) {
        if (Build.VERSION.SDK_INT >= 31) connectivity.registerBestMatchingNetworkCallback(NetworkRequest.Builder()
            .addCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET).addCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN).build(), callback, handler)
        else connectivity.registerDefaultNetworkCallback(callback, handler)
    }
}

/**
 * Direct DNS through the resolver of the current physical network (operator/router DNS or
 * Android Private DNS). Encrypted public resolvers are often throttled on mobile networks,
 * which delayed or broke the VPN server's own address lookup. VPN DNS stays DoH via the tunnel.
 */
internal class SystemDns(private val connectivity: ConnectivityManager) : LocalDNSTransport {
    override fun raw() = true
    override fun lookup(ctx: ExchangeContext, network: String, domain: String) { error("Используется raw DNS") }
    override fun exchange(ctx: ExchangeContext, message: ByteArray) {
        val done = CountDownLatch(1)
        val signal = CancellationSignal()
        ctx.onCancel { signal.cancel(); done.countDown() }
        DnsResolver.getInstance().rawQuery(PhysicalNetwork.current(connectivity), message, DnsResolver.FLAG_EMPTY, Runnable::run, signal, object : DnsResolver.Callback<ByteArray> {
            override fun onAnswer(answer: ByteArray, rcode: Int) { ctx.rawSuccess(answer); done.countDown() }
            override fun onError(error: DnsResolver.DnsException) {
                val cause = error.cause
                if (cause is ErrnoException) ctx.errnoCode(cause.errno) else ctx.errorCode(2)
                done.countDown()
            }
        })
        // sing-box bounds the query by its own context; this only guarantees the thread is released.
        if (!done.await(15, TimeUnit.SECONDS)) { signal.cancel(); ctx.errorCode(2) }
    }
}
