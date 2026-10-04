package com.bebekon.vpn

import android.app.Activity
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.provider.Settings
import androidx.core.content.FileProvider
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import java.io.File
import java.net.HttpURLConnection
import java.net.URL
import java.security.MessageDigest

val Context.updater get() = (applicationContext as BebekonApplication).updates

class AppUpdater(private val context: Context, private val releaseSource: () -> String = ::fetchAndroidRelease,
                 private val downloadSource: (AndroidUpdate) -> java.io.InputStream = ::openUpdateDownload, automatic: Boolean = true) {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private val mutable = MutableStateFlow(UpdateState())
    val state = mutable.asStateFlow()
    private var operation: Job? = null
    private var ignored = ""
    private var permissionPending = false
    private val directory get() = File(context.cacheDir, "updates").apply { mkdirs() }
    private val apk get() = File(directory, "bebekon-update.apk")
    init { if (automatic) scope.launch { delay(3000); if (context.repo.state.value.preferences.checkUpdates) check(false) } }

    fun check(manual: Boolean = true) {
        if (operation?.isActive == true) { if (manual) mutable.value = mutable.value.copy(visible = true); return }
        if (mutable.value.phase in listOf(UpdatePhase.READY, UpdatePhase.PERMISSION) && apk.exists()) { mutable.value = mutable.value.copy(visible = manual); return }
        mutable.value = UpdateState(UpdatePhase.CHECKING, visible = manual)
        operation = scope.launch {
            try {
                val info = withContext(Dispatchers.IO) { AndroidRelease.parse(releaseSource(), BuildConfig.VERSION_NAME) }
                mutable.value = if (info == null) UpdateState(UpdatePhase.CURRENT, visible = manual, message = "Установлена актуальная версия ${BuildConfig.VERSION_NAME}")
                else UpdateState(UpdatePhase.AVAILABLE, info, visible = manual || info.version != ignored)
            } catch (e: CancellationException) { throw e }
            catch (_: Exception) { mutable.value = UpdateState(UpdatePhase.ERROR, visible = manual, message = "Не удалось проверить обновление. Проверьте интернет и повторите") }
        }
    }
    fun dismiss() { if (mutable.value.phase != UpdatePhase.DOWNLOADING) { ignored = mutable.value.info?.version.orEmpty(); mutable.value = mutable.value.copy(visible = false) } }
    fun download() {
        val info = mutable.value.info ?: return
        if (operation?.isActive == true) return
        mutable.value = UpdateState(UpdatePhase.DOWNLOADING, info, visible = true, installAfterDownload = true)
        operation = scope.launch {
            val part = File(directory, "download.part")
            try {
                withContext(Dispatchers.IO) {
                    val workerContext = currentCoroutineContext()
                    downloadSource(info).use { input ->
                        copyUpdate(input, part, info) { count ->
                            workerContext.ensureActive()
                            mutable.value = mutable.value.copy(received = count)
                        }
                    }
                    verifyUpdateApk(context, part, info)
                    if (apk.exists()) require(apk.delete())
                    require(part.renameTo(apk))
                }
                mutable.value = UpdateState(UpdatePhase.READY, info, info.bytes, true, installAfterDownload = true)
            } catch (e: CancellationException) { mutable.value = UpdateState(UpdatePhase.AVAILABLE, info, visible = true); throw e }
            catch (_: Exception) { mutable.value = UpdateState(UpdatePhase.ERROR, info, visible = true, message = "Загрузка или проверка APK не удалась. Повторите попытку") }
            finally { part.delete() }
        }
    }
    fun cancelDownload() { operation?.cancel() }
    fun install(activity: Activity) {
        if (!apk.exists() || mutable.value.info == null) return
        mutable.value = mutable.value.copy(installAfterDownload = false)
        if (!activity.packageManager.canRequestPackageInstalls()) {
            permissionPending = true
            mutable.value = mutable.value.copy(phase = UpdatePhase.PERMISSION, visible = true)
            runCatching { activity.startActivity(Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:${context.packageName}"))) }
                .onFailure { permissionPending = false; mutable.value = mutable.value.copy(phase = UpdatePhase.READY, message = "Разрешите установку обновлений для Bebekon VPN в настройках Android") }
            return
        }
        try {
            val info = mutable.value.info!!
            verifyUpdateApk(context, apk, info)
            activity.startActivity(updateInstallIntent(context, apk))
            mutable.value = mutable.value.copy(phase = UpdatePhase.READY, message = "Подтвердите обновление в системном окне Android")
        } catch (_: Exception) { mutable.value = mutable.value.copy(phase = UpdatePhase.ERROR, message = "Не удалось открыть установщик. Повторите загрузку") }
    }
    fun resumed(activity: Activity) {
        if (!permissionPending) return
        permissionPending = false
        mutable.value = mutable.value.copy(phase = UpdatePhase.READY)
        if (activity.packageManager.canRequestPackageInstalls()) install(activity)
        else mutable.value = mutable.value.copy(message = "Без разрешения Android не сможет установить обновление. APK уже загружен")
    }
}

fun fetchAndroidRelease(): String {
    val connection = URL(AndroidRelease.API).openConnection() as HttpURLConnection
    try {
        connection.connectTimeout = 10_000; connection.readTimeout = 10_000
        connection.setRequestProperty("User-Agent", "BebekonAndroid/${BuildConfig.VERSION_NAME}")
        connection.setRequestProperty("Accept", "application/vnd.github+json")
        require(connection.responseCode == 200)
        return connection.inputStream.use { it.readLimited(2 * 1024 * 1024) }.toString(Charsets.UTF_8)
    } finally { connection.disconnect() }
}
fun openUpdateDownload(info: AndroidUpdate): java.io.InputStream {
    var url = info.url
    for (redirect in 0..5) {
        AndroidRelease.downloadLocation(url)
        val connection = URL(url).openConnection() as HttpURLConnection
        try {
            connection.connectTimeout = 10_000; connection.readTimeout = 15_000; connection.instanceFollowRedirects = false
            connection.setRequestProperty("User-Agent", "BebekonAndroid/${BuildConfig.VERSION_NAME}")
            val code = connection.responseCode
            if (code == 200) {
                if (connection.contentLengthLong > 0) require(connection.contentLengthLong == info.bytes)
                return object : java.io.FilterInputStream(connection.inputStream) {
                    override fun close() { try { super.close() } finally { connection.disconnect() } }
                }
            }
            require(code in listOf(301, 302, 303, 307, 308) && redirect < 5)
            url = URL(URL(url), connection.getHeaderField("Location") ?: error("Нет адреса загрузки")).toString()
            connection.disconnect()
        } catch (e: Exception) { connection.disconnect(); throw e }
    }
    error("Не удалось загрузить APK")
}

fun copyUpdate(input: java.io.InputStream, destination: File, info: AndroidUpdate, progress: (Long) -> Unit) {
    val hash = MessageDigest.getInstance("SHA-256"); var received = 0L; var last = 0L
    destination.outputStream().use { output ->
        val buffer = ByteArray(64 * 1024)
        while (true) {
            val count = input.read(buffer); if (count < 0) break
            received += count; require(received <= info.bytes && received <= AndroidRelease.MAX_APK)
            hash.update(buffer, 0, count); output.write(buffer, 0, count)
            val now = System.nanoTime(); if (now - last > 200_000_000) { progress(received); last = now }
        }
    }
    require(received == info.bytes)
    require(hash.digest().joinToString("") { "%02x".format(it) } == info.sha256)
    progress(received)
}

fun verifyUpdateApk(context: Context, file: File, info: AndroidUpdate) {
    val pm = context.packageManager
    val candidate = pm.getPackageArchiveInfo(file.path, PackageManager.GET_SIGNING_CERTIFICATES) ?: error("Неверный APK")
    val installed = pm.getPackageInfo(context.packageName, PackageManager.GET_SIGNING_CERTIFICATES)
    require(candidate.packageName == context.packageName && candidate.longVersionCode > installed.longVersionCode && candidate.versionName == info.version)
    fun signers(p: android.content.pm.PackageInfo) = p.signingInfo?.apkContentsSigners?.map { signature -> MessageDigest.getInstance("SHA-256").digest(signature.toByteArray()).joinToString("") { "%02x".format(it) } }?.toSet().orEmpty()
    val trusted = signers(installed); require(trusted.isNotEmpty() && signers(candidate) == trusted)
}
fun updateInstallIntent(context: Context, file: File): Intent = Intent(Intent.ACTION_VIEW)
    .setDataAndType(FileProvider.getUriForFile(context, "${context.packageName}.updates", file), "application/vnd.android.package-archive")
    .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
