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
    val countries = MutableStateFlow<Map<String, String>>(emptyMap())
    private val geo = GeoLookup(report = context.repo::log)
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
    fun enabled(value: Boolean) { enabled = value; if (!value) { job?.cancel(); destinationJob?.cancel(); network = null; checked = 0; attempted = 0; point.value = null; destination.value = null; countries.value = emptyMap(); destinationKey = "" } else refresh() }
    fun refresh() {
        if (!enabled) return
        val physical = connectivity.allNetworks.firstOrNull { connectivity.getNetworkCapabilities(it)?.let { c -> c.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN) && c.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED) && c.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET) } == true }
        if (physical == null) { job?.cancel(); network = null; point.value = null; return }
        if (physical == network && (job?.isActive == true || System.currentTimeMillis() - checked < 30 * 60_000 || System.currentTimeMillis() - attempted < 30_000)) return
        job?.cancel(); if (network != physical) point.value = null
        network = physical; attempted = System.currentTimeMillis()
        job = scope.launch {
            val result = withContext(Dispatchers.IO) { lookup(physical, "", currentCoroutineContext()) }
            ensureActive()
            if (enabled && network == physical && result != null) { point.value = result; checked = System.currentTimeMillis() }
        }
    }
    fun serverIp(serverId: String, ip: String) {
        if (!enabled || !locationIp(ip)) { destinationJob?.cancel(); destinationKey = ""; destination.value = null; return }
        val key = "$serverId/$ip"
        if (key == destinationKey && (destination.value != null || destinationJob?.isActive == true || System.currentTimeMillis() - destinationAttempted < 30_000)) return
        destinationJob?.cancel(); destination.value = null; destinationKey = key; destinationAttempted = System.currentTimeMillis()
        val physical = connectivity.allNetworks.firstOrNull { connectivity.getNetworkCapabilities(it)?.let { c -> c.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN) && c.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED) } == true } ?: return
        destinationJob = scope.launch {
            val result = withContext(Dispatchers.IO) { lookup(physical, ip, currentCoroutineContext()) }; ensureActive()
            if (enabled && destinationKey == key) {
                destination.value = result
                if (result != null && result.country.isNotBlank()) countries.value = countries.value + (serverId to result.country)
            }
        }
    }
    private fun lookup(physical: Network, ip: String, task: kotlin.coroutines.CoroutineContext): OriginPoint? = geo.lookup(ip) { url ->
                task.ensureActive()
                val connection = physical.openConnection(URL(url)) as HttpURLConnection
                try {
                    connection.connectTimeout = 4000; connection.readTimeout = 4000; connection.instanceFollowRedirects = false
                    val status = connection.responseCode
                    val retry = connection.getHeaderField("Retry-After")?.let { value -> value.toLongOrNull() ?: runCatching { java.time.Duration.between(java.time.Instant.now(), java.time.ZonedDateTime.parse(value, java.time.format.DateTimeFormatter.RFC_1123_DATE_TIME).toInstant()).seconds }.getOrNull() }
                    GeoReply(status, if (status == 200) connection.inputStream.use { it.readLimited(16 * 1024).toString(Charsets.UTF_8) } else "", retry)
                } finally { connection.disconnect() }
    }
    fun close() { job?.cancel(); destinationJob?.cancel(); runCatching { connectivity.unregisterNetworkCallback(callback) } }
}
