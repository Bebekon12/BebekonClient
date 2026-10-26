package com.bebekon.vpn

import android.content.Context
import android.net.*
import android.os.Handler
import android.os.HandlerThread
import android.os.ParcelFileDescriptor
import android.system.OsConstants
import io.nekohasekai.libbox.*
import java.net.InetSocketAddress
import java.net.NetworkInterface as JavaInterface
import java.util.concurrent.ConcurrentHashMap

class Strings(private val values: List<String>) : StringIterator {
    private var index = 0
    override fun len() = values.size
    override fun hasNext() = index < values.size
    override fun next() = values[index++]
}
class Interfaces(private val values: List<NetworkInterface>) : NetworkInterfaceIterator {
    private var index = 0
    override fun hasNext() = index < values.size
    override fun next() = values[index++]
}
class AndroidPlatform(private val context: Context, private val vpn: BebekonVpnService? = null) : PlatformInterface {
    var tunnelState: SavedState? = null
    var tunnelPolicy: AppTunnelPolicy? = null
    private val connectivity = context.getSystemService(ConnectivityManager::class.java)
    private val monitors = ConcurrentHashMap<InterfaceUpdateListener, ConnectivityManager.NetworkCallback>()
    @Volatile var descriptor: ParcelFileDescriptor? = null
    override fun localDNSTransport(): LocalDNSTransport = SystemDns(connectivity)
    override fun usePlatformAutoDetectInterfaceControl() = true
    override fun autoDetectInterfaceControl(fd: Int) { if (vpn != null && !vpn.protect(fd)) error("Не удалось исключить сокет VPN из туннеля") }
    override fun openTun(options: TunOptions): Int {
        val service = vpn ?: error("Проверка сервера не создаёт VPN туннель")
        val builder = service.Builder().setSession("Bebekon VPN").setMtu(options.mtu).setBlocking(false).setMetered(false)
        fun addresses(iterator: RoutePrefixIterator) { while (iterator.hasNext()) { val p = iterator.next(); builder.addAddress(p.address(), p.prefix()) } }
        addresses(options.inet4Address); addresses(options.inet6Address)
        // No allowFamily(AF_INET6): in IPv4 mode Android must block IPv6 for
        // captured apps, rather than let it bypass the VPN on the physical network.
        val dns = options.dnsServerAddress; while (dns.hasNext()) builder.addDnsServer(dns.next())
        fun routes(iterator: RoutePrefixIterator) { while (iterator.hasNext()) { val p = iterator.next(); builder.addRoute(p.address(), p.prefix()) } }
        routes(options.inet4RouteRange); routes(options.inet6RouteRange)
        val policy = tunnelPolicy ?: appTunnelPolicy(tunnelState ?: context.repo.state.value)
        if (policy.appOnly) {
            var included = 0
            policy.allowed!!.filterNot { it == context.packageName }.forEach { pkg ->
                try { builder.addAllowedApplication(pkg); included++ } catch (_: android.content.pm.PackageManager.NameNotFoundException) { }
            }
            check(included > 0) { "Выберите хотя бы одно установленное приложение для VPN в правилах" }
        } else {
            // Explicit direct apps bypass VPN transport and DNS, including mixed site/app routing.
            (policy.excluded + context.packageName).forEach { pkg ->
                try { builder.addDisallowedApplication(pkg) } catch (_: android.content.pm.PackageManager.NameNotFoundException) { }
            }
        }
        val fd = builder.establish() ?: error("Разрешение на VPN отозвано")
        descriptor?.close(); descriptor = fd
        context.repo.log(if (policy.appOnly) "Android: туннель только для выбранных приложений (${policy.allowed!!.size}); остальные используют обычную сеть" else "Android: общий туннель, исключений приложений: ${policy.excluded.size}")
        if (policy.siteOnlyBrowsers.isNotEmpty()) context.repo.log("Браузеры: ${policy.siteOnlyBrowsers.joinToString()}; через VPN только сайты из правил, остальное напрямую")
        context.repo.log(if ((tunnelState ?: context.repo.state.value).preferences.ipv6) "Адреса туннеля: IPv4 + IPv6" else "Адреса туннеля: IPv4; IPv6 внутри туннеля отключён")
        return fd.fd
    }
    override fun useProcFS() = false
    override fun findConnectionOwner(ipProtocol: Int, sourceAddress: String, sourcePort: Int, destinationAddress: String, destinationPort: Int): ConnectionOwner {
        val uid = connectivity.getConnectionOwnerUid(ipProtocol, InetSocketAddress(sourceAddress, sourcePort), InetSocketAddress(destinationAddress, destinationPort))
        return ConnectionOwner().apply { userId = uid; setAndroidPackageNames(Strings(context.packageManager.getPackagesForUid(uid)?.toList() ?: emptyList())) }
    }
    private val monitorLoop = lazy { HandlerThread("BebekonNetwork").apply { start() } }
    private val monitorThread by monitorLoop
    private var underlying: Network? = null
    private var reported = ""
    private var reportedInterface = ""
    private var reportedIdentity = ""
    /** Called (on the monitor thread) when the same interface reconnected or changed address; see update(). */
    var onSameInterfaceChange: (() -> Unit)? = null
    /** Reports Android's own default physical network; a real change makes sing-box drop stale connections. */
    @Synchronized private fun update(listener: InterfaceUpdateListener, preferred: Network?) {
        val network = preferred?.takeIf { PhysicalNetwork.usable(connectivity, it) } ?: PhysicalNetwork.current(connectivity)
        val link = network?.let(connectivity::getLinkProperties); val name = link?.interfaceName
        var index = -1
        // A new interface can appear in LinkProperties shortly before the kernel reports it.
        if (name != null) for (attempt in 0 until 10) { index = runCatching { JavaInterface.getByName(name)?.index }.getOrNull() ?: -1; if (index >= 0 || monitors.isEmpty()) break; Thread.sleep(100) }
        val metered = network?.let(connectivity::getNetworkCapabilities)?.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_METERED) == false
        val key = if (index < 0) "none" else "$name/$index/$metered/${link?.linkAddresses?.joinToString()}"
        if (key != reported) {
            reported = key
            if (index < 0) listener.updateDefaultInterface("", -1, false, false) else listener.updateDefaultInterface(name, index, metered, false)
        }
        // sing-box ignores a report with the same interface name and index. Yet mobile data that
        // reconnects after sleep gets a new address on the same rmnet interface, and Wi-Fi can
        // reconnect as a new network on the same wlan0: every old socket, including the DNS-over-
        // HTTPS connection, is dead then. Reset explicitly instead of waiting for a manual reconnect.
        val iface = if (index < 0) "" else "$name/$index"
        // IPv4 only: rotating IPv6 privacy addresses keep the old ones valid for hours.
        val identity = if (index < 0) "" else "${network?.networkHandle}/${link?.linkAddresses?.map { it.address }?.filterIsInstance<java.net.Inet4Address>()?.map { it.hostAddress }?.sorted()}"
        val sameInterfaceChanged = iface.isNotEmpty() && iface == reportedInterface && identity != reportedIdentity
        reportedInterface = iface; reportedIdentity = identity
        if (sameInterfaceChanged) onSameInterfaceChange?.invoke()
        val current = network.takeIf { index >= 0 }
        if (vpn != null && current != underlying) { underlying = current; vpn.setUnderlyingNetworks(current?.let { arrayOf(it) }) }
    }
    override fun startDefaultInterfaceMonitor(listener: InterfaceUpdateListener) {
        val callback = object : ConnectivityManager.NetworkCallback() {
            @Volatile private var chosen: Network? = null
            override fun onAvailable(network: Network) { chosen = network; update(listener, network) }
            override fun onLost(network: Network) { if (chosen == network) chosen = null; update(listener, chosen) }
            override fun onLinkPropertiesChanged(network: Network, linkProperties: LinkProperties) { if (chosen == null || chosen == network) update(listener, network) }
            override fun onCapabilitiesChanged(network: Network, networkCapabilities: NetworkCapabilities) { if (chosen == null || chosen == network) update(listener, network) }
        }
        monitors[listener] = callback
        synchronized(this) { reported = ""; reportedInterface = ""; reportedIdentity = "" }
        PhysicalNetwork.listen(connectivity, callback, Handler(monitorThread.looper))
        update(listener, null)
    }
    override fun closeDefaultInterfaceMonitor(listener: InterfaceUpdateListener) { monitors.remove(listener)?.let { runCatching { connectivity.unregisterNetworkCallback(it) } } }
    override fun getInterfaces(): NetworkInterfaceIterator {
        val interfaces = connectivity.allNetworks.mapNotNull { network ->
            val c = connectivity.getNetworkCapabilities(network) ?: return@mapNotNull null
            if (!c.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN)) return@mapNotNull null
            val lp = connectivity.getLinkProperties(network) ?: return@mapNotNull null; val name = lp.interfaceName ?: return@mapNotNull null
            val ni = runCatching { JavaInterface.getByName(name) }.getOrNull() ?: return@mapNotNull null
            NetworkInterface().apply {
                index = ni.index; mtu = ni.mtu; this.name = name
                addresses = Strings(lp.linkAddresses.map { it.toString() }); dnsServer = Strings(lp.dnsServers.mapNotNull { it.hostAddress }); gateway = Strings(lp.routes.mapNotNull { it.gateway?.hostAddress })
                flags = OsConstants.IFF_UP or OsConstants.IFF_RUNNING or if (ni.isLoopback) OsConstants.IFF_LOOPBACK else 0
                type = when { c.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) -> Libbox.InterfaceTypeWIFI; c.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR) -> Libbox.InterfaceTypeCellular; else -> Libbox.InterfaceTypeEthernet }
                metered = !c.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_METERED)
            }
        }.distinctBy { it.name }
        return Interfaces(interfaces)
    }
    override fun underNetworkExtension() = false
    override fun includeAllNetworks() = false
    override fun readWIFIState(): WIFIState? = null
    override fun clearDNSCache() = Unit
    override fun sendNotification(notification: Notification?) = Unit
    override fun cancelNotification(identifier: String?, typeID: Int) = Unit
    override fun startNeighborMonitor(listener: NeighborUpdateListener?) = Unit
    override fun closeNeighborMonitor(listener: NeighborUpdateListener?) = Unit
    override fun registerMyInterface(name: String?) = Unit
    override fun usePlatformShell() = false
    override fun checkPlatformShell() { error("Shell недоступен") }
    override fun openShellSession(user: PlatformUser?, command: String?, environ: StringIterator?, term: String?, rows: Int, cols: Int): ShellSession { error("Shell недоступен") }
    override fun lookupUser(username: String?): PlatformUser { error("Shell недоступен") }
    override fun lookupSFTPServer(): String { error("SFTP недоступен") }
    override fun readSystemSSHHostKey(): String { error("SSH недоступен") }
    override fun tailscaleHostname() = "BebekonAndroid"
    override fun usePlatformBridge() = false
    override fun createBridge(options: BridgeOptions?): BridgeSession { error("Bridge недоступен") }
    fun close() { monitors.keys.toList().forEach(::closeDefaultInterfaceMonitor); descriptor?.close(); descriptor = null; if (monitorLoop.isInitialized()) monitorThread.quitSafely() }
}
