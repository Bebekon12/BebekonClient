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
fun parseFallbackLocation(text: String): OriginPoint {
    val o = JSONObject(text)
    require(!o.optBoolean("error") && o.optString("ip").matches(Regex("[0-9a-fA-F:.]{3,45}")))
    val lon = o.getDouble("longitude"); val lat = o.getDouble("latitude")
    require(lon.isFinite() && lat.isFinite() && lon in -180.0..180.0 && lat in -90.0..90.0)
    return OriginPoint(lon.toFloat(), lat.toFloat(), o.optString("country_code").takeIf { Regex("[A-Z]{2}").matches(it) }.orEmpty())
}

/** Approximate IP location, never GPS; bound to a validated physical network, never the VPN. */
class OriginLocation(context: Context, private val scope: CoroutineScope) {
    val point = MutableStateFlow<OriginPoint?>(null)
    val destination = MutableStateFlow<OriginPoint?>(null)
    private val connectivity = context.getSystemService(ConnectivityManager::class.java)
    private var enabled = false
    private var network: Network? = null
    private var checked = 0L
    private var attempted = 0L
    private var job: Job? = null
    private var destinationJob: Job? = null
    private var destinationKey = ""
    private var destinationAttempted = 0L
    private val callback = object : ConnectivityManager.NetworkCallback() {
        override fun onCapabilitiesChanged(n: Network, c: NetworkCapabilities) { scope.launch { refresh() } }
        override fun onLost(n: Network) { scope.launch { refresh() } }
    }
    init { connectivity.registerNetworkCallback(NetworkRequest.Builder().addCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET).addCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN).build(), callback) }
    fun enabled(value: Boolean) { enabled = value; if (!value) { job?.cancel(); destinationJob?.cancel(); network = null; checked = 0; attempted = 0; point.value = null; destination.value = null; destinationKey = "" } else refresh() }
    fun refresh() {
        if (!enabled) return
        val physical = connectivity.allNetworks.firstOrNull { connectivity.getNetworkCapabilities(it)?.let { c -> c.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN) && c.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED) && c.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET) } == true }
        if (physical == null) { job?.cancel(); network = null; point.value = null; return }
        if (physical == network && (job?.isActive == true || System.currentTimeMillis() - checked < 30 * 60_000 || System.currentTimeMillis() - attempted < 30_000)) return
        job?.cancel(); if (network != physical) point.value = null
        network = physical; attempted = System.currentTimeMillis()
        job = scope.launch {
            val result = withContext(Dispatchers.IO) { lookup(physical, "") }
            ensureActive()
            if (enabled && network == physical && result != null) { point.value = result; checked = System.currentTimeMillis() }
        }
    }
    fun serverIp(key: String, ip: String) {
        if (!enabled || !Regex("[0-9a-fA-F:.]{3,45}").matches(ip)) { destinationJob?.cancel(); destinationKey = ""; destination.value = null; return }
        if (key == destinationKey && (destination.value != null || destinationJob?.isActive == true || System.currentTimeMillis() - destinationAttempted < 30_000)) return
        destinationJob?.cancel(); destination.value = null; destinationKey = key; destinationAttempted = System.currentTimeMillis()
        val physical = connectivity.allNetworks.firstOrNull { connectivity.getNetworkCapabilities(it)?.let { c -> c.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN) && c.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED) } == true } ?: return
        destinationJob = scope.launch {
            val result = withContext(Dispatchers.IO) { lookup(physical, ip) }; ensureActive()
            if (enabled && destinationKey == key) destination.value = result
        }
    }
    private fun lookup(physical: Network, ip: String): OriginPoint? {
        for (fallback in listOf(false, true)) {
            val result = runCatching {
                val url = if (fallback) "https://ipapi.co/${if (ip.isBlank()) "" else "$ip/"}json/" else "https://ipwho.is/$ip?fields=success,latitude,longitude,country_code"
                val connection = physical.openConnection(URL(url)) as HttpURLConnection
                try {
                    connection.connectTimeout = 4000; connection.readTimeout = 4000; connection.instanceFollowRedirects = false
                    require(connection.responseCode == 200)
                    val text = connection.inputStream.use { it.readLimited(16 * 1024).toString(Charsets.UTF_8) }
                    if (fallback) parseFallbackLocation(text) else parseOrigin(text)
                } finally { connection.disconnect() }
            }.getOrNull()
            if (result != null) return result
        }
        return null
    }
    fun close() { job?.cancel(); destinationJob?.cancel(); runCatching { connectivity.unregisterNetworkCallback(callback) } }
}
