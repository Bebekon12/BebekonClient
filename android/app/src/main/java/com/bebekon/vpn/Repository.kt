package com.bebekon.vpn

import android.app.Application
import android.content.Context
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import android.provider.Settings
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.AtomicFile
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import org.json.JSONObject
import java.io.File
import java.net.HttpURLConnection
import java.net.URI
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

class BebekonApplication : Application() {
    lateinit var repository: Repository; private set
    lateinit var updates: AppUpdater; private set
    override fun onCreate() { super.onCreate(); repository = Repository(this); updates = AppUpdater(this) }
}
val Context.repo get() = (applicationContext as BebekonApplication).repository

class Repository(private val context: Context) {
    private val file = AtomicFile(File(context.filesDir, "settings.enc"))
    private val key: SecretKey by lazy {
        val store = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (store.getKey("bebekon-settings", null) as? SecretKey) ?: KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore").apply { init(KeyGenParameterSpec.Builder("bebekon-settings", KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT).setBlockModes(KeyProperties.BLOCK_MODE_GCM).setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE).setKeySize(256).build()) }.generateKey()
    }
    private var readFailure = ""
    private val mutable = MutableStateFlow(read())
    val state = mutable.asStateFlow()
    val pings = MutableStateFlow<Map<String, PingResult>>(emptyMap())
    val logs = MutableStateFlow<List<String>>(emptyList())
    val routingLogs = MutableStateFlow<List<String>>(emptyList())
    @Synchronized fun routingLog(message: String, saved: SavedState) {
        if (state.value.preferences.routingDiagnostics) routingLogs.value = (routingLogs.value + redactRoutingLog(message, saved)).takeLast(200)
    }
    val storageError = MutableStateFlow(readFailure)
    private fun read(): SavedState = try {
        if (!file.baseFile.exists()) SavedState() else {
            val data = file.readFully(); require(data.size > 28); val cipher = Cipher.getInstance("AES/GCM/NoPadding"); cipher.init(Cipher.DECRYPT_MODE, key, GCMParameterSpec(128, data.copyOfRange(0, 12)))
            SavedState.fromJson(JSONObject(cipher.doFinal(data.copyOfRange(12, data.size)).toString(Charsets.UTF_8)))
        }
    } catch (_: Exception) { readFailure = "Не удалось расшифровать настройки. Исходный файл сохранён"; SavedState() }
    @Synchronized fun update(change: (SavedState) -> SavedState) {
        check(readFailure.isEmpty()) { readFailure }
        val next = change(mutable.value); val cipher = Cipher.getInstance("AES/GCM/NoPadding"); cipher.init(Cipher.ENCRYPT_MODE, key)
        val stream = file.startWrite()
        try { stream.write(cipher.iv); stream.write(cipher.doFinal(next.toJson().toString().toByteArray())); file.finishWrite(stream); mutable.value = next } catch (e: Exception) { file.failWrite(stream); throw e }
    }
    @Synchronized fun log(message: String) { logs.value = (logs.value + "${java.text.SimpleDateFormat("HH:mm:ss", java.util.Locale.ROOT).format(java.util.Date())}  ${redactRoutingLog(message, mutable.value)}").takeLast(100) }
    fun geo(name: String): JSONObject = try { JSONObject(context.assets.open("geo/$name.json").bufferedReader().use { it.readText() }) } catch (_: Exception) { error("Набор $name не включён в приложение") }
    fun presets(): List<Pair<String, List<Rule>>> {
        fun rules(o: JSONObject): List<Rule> = o.optJSONArray("rules")?.objects()?.mapNotNull { r ->
            val kind = when (r.optString("kind")) { "site" -> RuleKind.DOMAIN; "contains" -> RuleKind.KEYWORD; "geoSite" -> RuleKind.GEOSITE; "geoIp" -> RuleKind.GEOIP; "cidr" -> RuleKind.CIDR; else -> null }
            kind?.let { Rule(name = r.getString("name"), kind = it, values = r.getJSONArray("values").strings(), vpn = r.optBoolean("useVpn", true)) }
        } ?: o.getJSONArray("domains").strings().let { listOf(Rule(name = o.getString("name"), kind = RuleKind.DOMAIN, values = it)) }
        val list = mutableListOf<Pair<String, List<Rule>>>()
        for (name in listOf("admin", "russia")) { val o = JSONObject(context.assets.open("presets/$name.json").bufferedReader().use { it.readText() }); list += o.getString("name") to rules(o) }
        org.json.JSONArray(context.assets.open("presets/services.json").bufferedReader().use { it.readText() }).objects().forEach { list += it.getString("name") to rules(it) }
        return list
    }
    fun load(source: String, name: String): Subscription {
        val text = source.trim(); var content = text; var title = name.trim(); var info = ""
        if (text.startsWith("https://") || text.startsWith("http://")) {
            val u = URI(text); require(u.scheme == "https" || u.host in listOf("localhost", "127.0.0.1", "::1")) { "Для ссылки подписки нужен HTTPS" }
            require(u.rawUserInfo == null) { "Ссылки с логином в адресе пока не поддерживаются" }
            val connection = directConnection(context, u.toURL())
            val budget = NetworkBudget(20_000)
            try {
                connection.connectTimeout = 10_000; connection.readTimeout = 10_000; connection.instanceFollowRedirects = false
                connection.setRequestProperty("User-Agent", "clash.meta BebekonAndroid/${BuildConfig.VERSION_NAME}")
                connection.setRequestProperty("Accept", "text/yaml, application/json, text/plain, */*")
                connection.setRequestProperty("x-hwid", digest("bebekon-android:" + Settings.Secure.getString(context.contentResolver, Settings.Secure.ANDROID_ID)))
                connection.setRequestProperty("x-device-os", "Android"); connection.setRequestProperty("x-ver-os", android.os.Build.VERSION.RELEASE)
                require(connection.responseCode in 200..299) { "Провайдер вернул HTTP ${connection.responseCode}. Проверьте подписку и лимит устройств" }
                budget.check()
                val data = connection.inputStream.use { it.readLimited(4 * 1024 * 1024, budget) }; content = data.toString(Charsets.UTF_8)
                if (title.isEmpty()) { val h = connection.getHeaderField("profile-title") ?: ""; title = if (h.startsWith("base64:")) runCatching { SubscriptionParser.decode(h.substringAfter(':')) }.getOrDefault("") else h }
                info = connection.getHeaderField("subscription-userinfo") ?: ""
            } finally { connection.disconnect() }
            if (title.isEmpty()) title = u.host
        }
        val nodes = SubscriptionParser.parse(content)
        if (title.isEmpty()) title = "Моя подписка"
        return Subscription(name = title.take(80), source = text, nodes = nodes, info = info)
    }
    fun add(subscription: Subscription) { update { s ->
        val old = s.subscriptions.firstOrNull { it.source == subscription.source }
        val sub = subscription.copy(id = old?.id ?: subscription.id)
        val subs = s.subscriptions.filterNot { it.id == sub.id } + sub
        val all = subs.flatMap { it.nodes }
        val previous = s.selectedNode
        val selected = when {
            previous == null && s.selected == null -> all.firstOrNull { it.unsupported.isEmpty() }?.id
            all.any { it.id == s.selected } -> s.selected
            previous != null -> all.singleOrNull { it.host == previous.host && it.port == previous.port && it.protocol == previous.protocol && it.transport == previous.transport }?.id
            else -> null
        }
        s.copy(subscriptions = subs, selected = selected)
    } }
}
fun directConnection(context: Context, url: java.net.URL): HttpURLConnection {
    val manager = context.getSystemService(ConnectivityManager::class.java)
    val network = manager.allNetworks.firstOrNull { manager.getNetworkCapabilities(it)?.let { c -> c.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET) && c.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN) && c.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED) } == true }
    return (network?.openConnection(url) ?: url.openConnection(java.net.Proxy.NO_PROXY)) as HttpURLConnection
}
/** InputStream.readNBytes is not available on all supported Android 10 devices. */
internal fun java.io.InputStream.readLimited(limit: Int, budget: NetworkBudget = NetworkBudget(20_000)): ByteArray {
    val output = java.io.ByteArrayOutputStream(); val buffer = ByteArray(8192)
    while (true) { budget.check(); val count = read(buffer); budget.check(); if (count < 0) break; require(output.size() + count <= limit) { "Файл или подписка слишком большие" }; output.write(buffer, 0, count) }
    return output.toByteArray()
}
