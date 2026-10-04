package com.bebekon.vpn

import android.content.Context
import android.net.*
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
    private val connectivity = context.getSystemService(ConnectivityManager::class.java)
    private val monitors = ConcurrentHashMap<InterfaceUpdateListener, ConnectivityManager.NetworkCallback>()
    @Volatile var descriptor: ParcelFileDescriptor? = null
    override fun localDNSTransport(): LocalDNSTransport? = null
    override fun usePlatformAutoDetectInterfaceControl() = true
    override fun autoDetectInterfaceControl(fd: Int) { if (vpn != null && !vpn.protect(fd)) error("Не удалось исключить сокет VPN из туннеля") }
    override fun openTun(options: TunOptions): Int {
        val service = vpn ?: error("Проверка сервера не создаёт VPN туннель")
        val builder = service.Builder().setSession("Bebekon VPN").setMtu(options.mtu).setBlocking(false).setMetered(false)
        fun addresses(iterator: RoutePrefixIterator) { while (iterator.hasNext()) { val p = iterator.next(); builder.addAddress(p.address(), p.prefix()) } }
        addresses(options.inet4Address); addresses(options.inet6Address)
        val dns = options.dnsServerAddress; while (dns.hasNext()) builder.addDnsServer(dns.next())
        fun routes(iterator: RoutePrefixIterator) { while (iterator.hasNext()) { val p = iterator.next(); builder.addRoute(p.address(), p.prefix()) } }
        routes(options.inet4RouteRange); routes(options.inet6RouteRange)
        val policy = appTunnelPolicy(tunnelState ?: context.repo.state.value)
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
        return fd.fd
    }
    override fun useProcFS() = false
    override fun findConnectionOwner(ipProtocol: Int, sourceAddress: String, sourcePort: Int, destinationAddress: String, destinationPort: Int): ConnectionOwner {
        val uid = connectivity.getConnectionOwnerUid(ipProtocol, InetSocketAddress(sourceAddress, sourcePort), InetSocketAddress(destinationAddress, destinationPort))
        return ConnectionOwner().apply { userId = uid; setAndroidPackageNames(Strings(context.packageManager.getPackagesForUid(uid)?.toList() ?: emptyList())) }
    }
    private fun update(listener: InterfaceUpdateListener) {
        val network = connectivity.allNetworks.firstOrNull { connectivity.getNetworkCapabilities(it)?.let { c -> c.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET) && c.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN) && c.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED) } == true }
        val link = network?.let(connectivity::getLinkProperties); val name = link?.interfaceName ?: ""; val index = runCatching { JavaInterface.getByName(name)?.index ?: -1 }.getOrDefault(-1)
        listener.updateDefaultInterface(name, index, network?.let { connectivity.isActiveNetworkMetered } ?: false, false)
        vpn?.setUnderlyingNetworks(network?.let { arrayOf(it) })
    }
    override fun startDefaultInterfaceMonitor(listener: InterfaceUpdateListener) {
        val callback = object : ConnectivityManager.NetworkCallback() {
            override fun onAvailable(network: Network) { update(listener) }
            override fun onLost(network: Network) { update(listener) }
            override fun onLinkPropertiesChanged(network: Network, linkProperties: LinkProperties) { update(listener) }
            override fun onCapabilitiesChanged(network: Network, networkCapabilities: NetworkCapabilities) { update(listener) }
        }
        monitors[listener] = callback
        connectivity.registerNetworkCallback(NetworkRequest.Builder().addCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET).addCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN).build(), callback)
        update(listener)
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
    fun close() { monitors.keys.toList().forEach(::closeDefaultInterfaceMonitor); descriptor?.close(); descriptor = null }
}
