package com.bebekon.vpn

import org.json.JSONObject
import java.net.URI

data class AndroidUpdate(val version: String, val url: String, val sha256: String, val bytes: Long)
enum class UpdatePhase { IDLE, CHECKING, AVAILABLE, DOWNLOADING, READY, PERMISSION, CURRENT, ERROR }
data class UpdateState(val phase: UpdatePhase = UpdatePhase.IDLE, val info: AndroidUpdate? = null, val received: Long = 0, val visible: Boolean = false, val message: String = "", val installAfterDownload: Boolean = false)

object AndroidRelease {
    const val API = "https://api.github.com/repos/Bebekon12/BebekonClient/releases/latest"
    const val MAX_APK = 320L * 1024 * 1024
    fun version(value: String): List<Int> {
        require(Regex("[0-9]+\\.[0-9]+\\.[0-9]+").matches(value))
        return value.split('.').map { it.toInt() }
    }
    fun newer(candidate: String, installed: String): Boolean {
        val a = version(candidate); val b = version(installed)
        for (i in a.indices) if (a[i] != b[i]) return a[i] > b[i]
        return false
    }
    fun parse(text: String, installed: String): AndroidUpdate? {
        val release = JSONObject(text)
        require(!release.optBoolean("draft") && !release.optBoolean("prerelease"))
        val name = Regex("Android\\s+([0-9]+\\.[0-9]+\\.[0-9]+)").find(release.getString("name"))?.groupValues?.get(1) ?: error("Нет версии Android в релизе")
        if (!newer(name, installed)) return null
        val asset = release.getJSONArray("assets").objects().single { it.optString("name") == "Bebekon-Android.apk" }
        val tag = release.getString("tag_name")
        require(Regex("v[0-9][A-Za-z0-9._-]*").matches(tag))
        val url = asset.getString("browser_download_url")
        require(url == "https://github.com/Bebekon12/BebekonClient/releases/download/$tag/Bebekon-Android.apk")
        val hash = asset.getString("digest").removePrefix("sha256:")
        require(Regex("[0-9a-f]{64}").matches(hash))
        val bytes = asset.getLong("size"); require(bytes in 1_000_000..MAX_APK)
        return AndroidUpdate(name, url, hash, bytes)
    }
    fun downloadLocation(url: String) {
        val u = URI(url)
        require(u.scheme == "https" && u.userInfo == null && (u.port == -1 || u.port == 443) && u.host in setOf("github.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com"))
    }
}
