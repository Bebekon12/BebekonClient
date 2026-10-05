package com.bebekon.vpn

import kotlinx.coroutines.CancellationException
import org.json.JSONObject
import java.net.InetAddress
import java.util.Locale

enum class GeoProvider(val host: String) {
    IPAPI_IS("api.ipapi.is"), IPWHO("ipwho.is"), IPAPI_CO("ipapi.co");
    fun url(ip: String) = when (this) {
        IPAPI_IS -> "https://$host/" + if (ip.isBlank()) "" else "?q=$ip"
        IPWHO -> "https://$host/$ip?fields=success,ip,latitude,longitude,country_code"
        IPAPI_CO -> "https://$host/${if (ip.isBlank()) "" else "$ip/"}json/"
    }
}
data class GeoReply(val status: Int, val body: String = "", val retryAfterSeconds: Long? = null)
fun locationIp(value: String): Boolean = if (':' in value) value.matches(Regex("[0-9a-fA-F:.]{3,45}")) && runCatching { InetAddress.getByName(value) }.isSuccess
    else value.split('.').let { parts -> parts.size == 4 && parts.all { it.matches(Regex("[0-9]{1,3}")) && it.toInt() in 0..255 } }
fun Node.displayCountry(resolved: Map<String, String>) = country.ifBlank { resolved[id].orEmpty() }
fun parseIpapiIs(text: String): OriginPoint {
    val o = JSONObject(text)
    require(!o.has("error") && !o.optBoolean("is_bogon") && locationIp(o.optString("ip")))
    val location = o.optJSONObject("location") ?: o
    val lon = location.getDouble(if (location.has("longitude")) "longitude" else "lon")
    val lat = location.getDouble(if (location.has("latitude")) "latitude" else "lat")
    require(lon.isFinite() && lat.isFinite() && lon in -180.0..180.0 && lat in -90.0..90.0)
    val code = location.optString("country_code").takeIf { it.matches(Regex("[A-Z]{2}")) }
        ?: Locale.getISOCountries().firstOrNull { Locale("", it).getDisplayCountry(Locale.ENGLISH).equals(location.optString("country"), true) }.orEmpty()
    return OriginPoint(lon.toFloat(), lat.toFloat(), code)
}

/** Provider failures are separate from VPN state; never log IPs, coordinates or response bodies. */
class GeoLookup(private val clock: () -> Long = { System.nanoTime() / 1_000_000 }, private val report: (String) -> Unit = {}) {
    private val blocked = java.util.concurrent.ConcurrentHashMap<GeoProvider, Long>()
    fun lookup(ip: String, fetch: (String) -> GeoReply): OriginPoint? {
        require(ip.isBlank() || locationIp(ip))
        for (provider in GeoProvider.entries) {
            if (blocked[provider]?.let { it > clock() } == true) continue
            try {
                val reply = fetch(provider.url(ip))
                if (reply.status != 200) {
                    if (reply.status == 403 || reply.status == 429) blocked[provider] = clock() + ((reply.retryAfterSeconds ?: 3600L).coerceIn(60, 86400) * 1000)
                    report("Карта: ${provider.host} HTTP ${reply.status}"); continue
                }
                val point = when (provider) { GeoProvider.IPAPI_IS -> parseIpapiIs(reply.body); GeoProvider.IPWHO -> parseOrigin(reply.body); GeoProvider.IPAPI_CO -> parseFallbackLocation(reply.body) }
                val echoed = JSONObject(reply.body).optString("ip")
                if (ip.isNotBlank()) require(locationIp(echoed) && InetAddress.getByName(echoed) == InetAddress.getByName(ip))
                report("Карта: ${if (ip.isBlank()) "начало маршрута" else "сервер"} определён через ${provider.host}")
                return point
            } catch (e: CancellationException) { throw e }
            catch (_: Exception) { report("Карта: ${provider.host} недоступен или вернул неверные данные") }
        }
        return null
    }
}
