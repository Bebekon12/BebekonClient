package com.bebekon.vpn

import android.app.*
import android.app.Notification
import android.content.*
import android.content.pm.ServiceInfo
import android.net.VpnService
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.service.quicksettings.TileService
import io.nekohasekai.libbox.*
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import java.io.File
import java.util.UUID
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicLong

object VpnController {
    private val mutable = MutableStateFlow(Session())
    val session = mutable.asStateFlow()
    @Synchronized fun publish(context: Context, change: (Session) -> Session) {
        val previous = mutable.value; val next = change(previous); mutable.value = next
        // Traffic counters must not wake the system's Quick Settings service every second.
        if (previous.phase != next.phase || previous.server != next.server) Handler(Looper.getMainLooper()).post { TileService.requestListeningState(context, ComponentName(context, VpnTileService::class.java)) }
    }
    fun start(context: Context) { context.startForegroundService(Intent(context, BebekonVpnService::class.java).setAction("START")) }
    fun stop(context: Context) { context.startService(Intent(context, BebekonVpnService::class.java).setAction("STOP")) }
    fun reload(context: Context) { if (session.value.active) context.startService(Intent(context, BebekonVpnService::class.java).setAction("RELOAD")) }
}
object NativeCore {
    @Volatile private var ready = false
    @Synchronized fun setup(context: Context) {
        if (ready) return
        val dir = File(context.noBackupFilesDir, "core").apply { mkdirs() }; val temp = File(context.cacheDir, "core").apply { mkdirs() }
        Libbox.setup(SetupOptions().apply { basePath = dir.path; workingPath = dir.path; tempPath = temp.path; fixAndroidStack = true; commandServerSecret = UUID.randomUUID().toString(); logMaxLines = 50; appVersion = BuildConfig.VERSION_NAME; appMarketingVersion = BuildConfig.VERSION_NAME; oomKillerDisabled = true })
        ready = true
    }
    fun probe(context: Context, state: SavedState, method: String, url: String = state.preferences.pingTarget.url): BebekonProbeResult {
        setup(context)
        val platform = AndroidPlatform(context)
        try { return Libbox.bebekonProbe(CoreConfig.build(state, context.repo::geo, tunnel = false), platform, "vpn", url, method) } finally { platform.close() }
    }
}
class BebekonVpnService : VpnService(), CommandServerHandler {
    private val dispatcher = Executors.newSingleThreadExecutor().asCoroutineDispatcher()
    private val scope = CoroutineScope(SupervisorJob() + dispatcher)
    private val generation = AtomicLong()
    private var server: CommandServer? = null
    private var client: CommandClient? = null
    private var platform: AndroidPlatform? = null
    private var reloadJob: Job? = null
    @Volatile private var desired = false
    override fun onCreate() { super.onCreate(); getSystemService(NotificationManager::class.java).createNotificationChannel(NotificationChannel("vpn", "Подключение VPN", NotificationManager.IMPORTANCE_LOW).apply { setShowBadge(false) }) }
    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            "STOP" -> {
                desired = false; val version = generation.incrementAndGet(); reloadJob?.cancel()
                VpnController.publish(this) { it.copy(phase = Phase.STOPPING) }
                scope.launch {
                    closeCore()
                    withContext(Dispatchers.Main) {
                        // A newer START owns the service; an older STOP must not tear it down.
                        if (!desired && generation.get() == version) {
                            VpnController.publish(this@BebekonVpnService) { Session() }
                            stopForeground(STOP_FOREGROUND_REMOVE); stopSelfResult(startId)
                        }
                    }
                }
            }
            "RELOAD" -> { if (desired) { generation.incrementAndGet(); reloadJob?.cancel(); reloadJob = scope.launch { delay(350); connect(generation.get(), true) } } }
            else -> {
                if (!desired) { desired = true; val version = generation.incrementAndGet(); foreground("Подключение…"); VpnController.publish(this) { Session(phase = Phase.STARTING) }; scope.launch { connect(version, false) } }
            }
        }
        return if (desired) START_STICKY else START_NOT_STICKY
    }
    private suspend fun connect(version: Long, reload: Boolean) {
        if (!desired || generation.get() != version) return
        try {
            val saved = repo.state.value; val node = saved.selectedNode ?: error("Добавьте подписку и выберите сервер")
            NativeCore.setup(this)
            val config = CoreConfig.build(saved, repo::geo); Libbox.checkConfig(config)
            VpnController.publish(this) { if (reload) it.copy(phase = Phase.RECONNECTING, message = "Применение правил…") else it.copy(phase = Phase.STARTING, server = node.name) }
            if (server == null) { platform = AndroidPlatform(this, this); server = CommandServer(this, platform).also { it.start() } }
            // The serialized executor owns all native service changes; refresh never selects another node.
            client?.disconnect(); client = null
            server!!.startOrReloadService(config, OverrideOptions())
            if (!desired || generation.get() != version) return
            val statusVersion = version
            client = CommandClient(object : CommandClientHandler {
                override fun connected() = Unit
                override fun disconnected(message: String?) = Unit
                override fun writeStatus(message: StatusMessage) { if (desired && generation.get() == statusVersion) VpnController.publish(this@BebekonVpnService) { it.copy(down = message.downlink, up = message.uplink, totalDown = message.downlinkTotal, totalUp = message.uplinkTotal) } }
                override fun setDefaultLogLevel(level: Int) = Unit
                override fun clearLogs() = Unit
                override fun writeLogs(messageList: LogIterator?) { if (BuildConfig.DEBUG && node.host == "10.0.2.2") while (messageList?.hasNext() == true) android.util.Log.d("BebekonCoreTest", messageList.next().message) }
                override fun writeGroups(message: OutboundGroupIterator?) = Unit
                override fun writeOutbounds(message: OutboundGroupItemIterator?) = Unit
                override fun initializeClashMode(modeList: StringIterator?, currentMode: String?) = Unit
                override fun updateClashMode(newMode: String?) = Unit
                override fun writeConnectionEvents(events: ConnectionEvents?) = Unit
            }, CommandClientOptions().apply { addCommand(Libbox.CommandStatus); if (BuildConfig.DEBUG && node.host == "10.0.2.2") addCommand(Libbox.CommandLog); statusInterval = 1_000_000_000 }).also { it.connect() }
            // A verified HTTPS response through this outbound confirms usable connectivity.
            val probe = runCatching { NativeCore.probe(this, saved, "GET") }.recoverCatching {
                if (!desired || generation.get() != version) throw CancellationException()
                val backup = PingTarget.entries.first { it != saved.preferences.pingTarget }
                NativeCore.probe(this, saved, "GET", backup.url)
            }
            if (BuildConfig.DEBUG && node.host == "10.0.2.2" && probe.isFailure) android.util.Log.w("BebekonDebug", "Fixture HTTPS check failed", probe.exceptionOrNull())
            if (!desired || generation.get() != version) return
            require(probe.isSuccess) { "Сервер не ответил при проверке подключения. Попробуйте другой сервер" }
            VpnController.publish(this) { it.copy(phase = Phase.ON, server = node.name, message = "", started = if (reload && it.started != 0L) it.started else System.currentTimeMillis()) }
            foreground(node.name)
            repo.log(if (reload) "Настройки маршрутизации применены" else "VPN подключён")
            val ip = runCatching { NativeCore.probe(this, saved, "GET", "https://api.ipify.org").body.trim() }.getOrDefault("")
            if (desired && generation.get() == version && Regex("[0-9a-fA-F:.]{3,45}").matches(ip)) VpnController.publish(this) { it.copy(publicIp = ip) }
        } catch (e: Exception) {
            if (BuildConfig.DEBUG) android.util.Log.e("BebekonDebug", "VPN startup failed", e)
            if (desired && generation.get() == version) {
                desired = false; closeCore()
                val message = when { e.message?.startsWith("Добавьте") == true -> "Добавьте подписку и выберите сервер"; e.message?.startsWith("Сервер не") == true -> "Не удалось получить ответ через VPN. Попробуйте другой сервер"; e.message?.contains("permission", true) == true -> "Разрешите VPN в системном окне"; else -> "Не удалось подключиться. Проверьте сервер и параметры подписки" }
                repo.log(message); VpnController.publish(this) { Session(Phase.ERROR, message = message) }
                withContext(Dispatchers.Main) { stopForeground(STOP_FOREGROUND_REMOVE); stopSelf() }
            }
        }
    }
    private fun closeCore() { runCatching { client?.disconnect() }; client = null; runCatching { server?.closeService() }; runCatching { server?.close() }; server = null; runCatching { platform?.close() }; platform = null; File(noBackupFilesDir, "core/configuration.json").delete() }
    private fun foreground(text: String) {
        val open = PendingIntent.getActivity(this, 1, Intent(this, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        val stop = PendingIntent.getService(this, 2, Intent(this, BebekonVpnService::class.java).setAction("STOP"), PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        val notification = Notification.Builder(this, "vpn").setSmallIcon(R.drawable.ic_vpn).setContentTitle("Bebekon VPN").setContentText(text).setContentIntent(open).setOngoing(true).addAction(Notification.Action.Builder(null, "Отключить", stop).build()).build()
        if (Build.VERSION.SDK_INT >= 34) startForeground(1, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE) else startForeground(1, notification)
    }
    override fun onRevoke() { desired = false; generation.incrementAndGet(); scope.launch { closeCore(); VpnController.publish(this@BebekonVpnService) { Session() }; withContext(Dispatchers.Main) { stopForeground(STOP_FOREGROUND_REMOVE); stopSelf() } }; super.onRevoke() }
    override fun onDestroy() { desired = false; generation.incrementAndGet(); scope.launch { closeCore(); scope.cancel(); dispatcher.close() }; super.onDestroy() }
    override fun serviceStop() { Handler(Looper.getMainLooper()).post { VpnController.stop(this) } }
    override fun serviceReload() { Handler(Looper.getMainLooper()).post { VpnController.reload(this) } }
    override fun getSystemProxyStatus() = SystemProxyStatus()
    override fun setSystemProxyEnabled(enabled: Boolean) = Unit
    override fun triggerNativeCrash() { error("Недоступно") }
    override fun writeDebugMessage(message: String?) = Unit
    override fun connectSSHAgent() = -1
}
