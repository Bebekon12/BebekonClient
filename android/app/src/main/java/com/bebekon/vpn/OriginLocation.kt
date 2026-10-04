package com.bebekon.vpn

import android.content.Context
import android.net.*
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.MutableStateFlow
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL

data class OriginPoint(val longitude: Float, val latitude: Float, val country: String)
fun parseOrigin(text: String): OriginPoint {
    val o = JSONObject(text)
    require(o.optBoolean("success"))
    val lon = o.getDouble("longitude"); val lat = o.getDouble("latitude")
    require(lon.isFinite() && lat.isFinite() && lon in -180.0..180.0 && lat in -90.0..90.0)
    return OriginPoint(lon.toFloat(), lat.toFloat(), o.optString("country_code").takeIf { Regex("[A-Z]{2}").matches(it) }.orEmpty())
}

/** Approximate IP location, never GPS; bound to a validated physical network, never the VPN. */
class OriginLocation(context: Context, private val scope: CoroutineScope) {
    val point = MutableStateFlow<OriginPoint?>(null)
    private val connectivity = context.getSystemService(ConnectivityManager::class.java)
    private var enabled = false
    private var network: Network? = null
    private var checked = 0L
    private var job: Job? = null
    private val callback = object : ConnectivityManager.NetworkCallback() {
        override fun onCapabilitiesChanged(n: Network, c: NetworkCapabilities) { scope.launch { refresh() } }
        override fun onLost(n: Network) { scope.launch { refresh() } }
    }
    init { connectivity.registerNetworkCallback(NetworkRequest.Builder().addCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET).addCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN).build(), callback) }
    fun enabled(value: Boolean) { enabled = value; if (!value) { job?.cancel(); network = null; checked = 0; point.value = null } else refresh() }
    fun refresh() {
        if (!enabled) return
        val physical = connectivity.allNetworks.firstOrNull { connectivity.getNetworkCapabilities(it)?.let { c -> c.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN) && c.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED) && c.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET) } == true }
        if (physical == null) { job?.cancel(); network = null; point.value = null; return }
        if (physical == network && (job?.isActive == true || System.currentTimeMillis() - checked < 30 * 60_000)) return
        job?.cancel(); if (network != physical) point.value = null
        network = physical; checked = System.currentTimeMillis()
        job = scope.launch {
            val result = withContext(Dispatchers.IO) { runCatching {
                val connection = physical.openConnection(URL("https://ipwho.is/?fields=success,latitude,longitude,country_code")) as HttpURLConnection
                try {
                    connection.connectTimeout = 5000; connection.readTimeout = 5000; connection.instanceFollowRedirects = false
                    require(connection.responseCode == 200)
                    parseOrigin(connection.inputStream.use { it.readLimited(16 * 1024).toString(Charsets.UTF_8) })
                } finally { connection.disconnect() }
            }.getOrNull() }
            ensureActive()
            if (enabled && network == physical) point.value = result
        }
    }
    fun close() { job?.cancel(); runCatching { connectivity.unregisterNetworkCallback(callback) } }
}
