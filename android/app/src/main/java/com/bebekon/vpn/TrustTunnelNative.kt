package com.bebekon.vpn

import android.content.Context
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import android.net.VpnService
import android.os.ParcelFileDescriptor
import org.json.JSONObject
import java.net.ServerSocket
import java.security.KeyStore
import java.security.cert.CertificateFactory
import java.security.cert.X509Certificate
import java.util.UUID
import javax.net.ssl.TrustManagerFactory
import javax.net.ssl.X509TrustManager

/** A private authenticated SOCKS bridge; sing-box retains all TUN, DNS and app/site rules. */
internal class TrustTunnelNative(private val context: Context, private val service: VpnService? = null) : AutoCloseable {
    companion object { init { System.loadLibrary("bebekon_trusttunnel") } }
    private val connectivity = context.getSystemService(ConnectivityManager::class.java)
    private val trust = (TrustManagerFactory.getInstance(TrustManagerFactory.getDefaultAlgorithm()).apply { init(null as KeyStore?) }.trustManagers.first { it is X509TrustManager } as X509TrustManager)
    private var pointer = 0L
    @Volatile private var closed = false
    @Volatile var state = 0; private set
    private var registered = false
    private val callback = object : ConnectivityManager.NetworkCallback() {
        override fun onAvailable(network: Network) = changed()
        override fun onLost(network: Network) = changed()
        override fun onCapabilitiesChanged(network: Network, capabilities: NetworkCapabilities) = changed()
        override fun onLinkPropertiesChanged(network: Network, properties: android.net.LinkProperties) = changed()
    }
    private fun network(): Network? = connectivity.allNetworks.firstOrNull { network -> connectivity.getNetworkCapabilities(network)?.let { it.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET) && it.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN) && it.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED) } == true }
    @Synchronized private fun changed() { if (!closed && pointer != 0L) networkNative(pointer, network() != null) }
    @Synchronized fun start(node: Node, timeoutMillis: Long = 4500): JSONObject {
        val deadline = android.os.SystemClock.elapsedRealtime() + timeoutMillis.coerceIn(1, 4500)
        check(pointer == 0L && !closed)
        TrustTunnelProfile.validate(node)
        val password = UUID.randomUUID().toString()
        // Never reuse a static port/password across instances or concurrent probes.
        val port = ServerSocket(0, 1, java.net.InetAddress.getByName("127.0.0.1")).use { it.localPort }
        val dns = network()?.let(connectivity::getLinkProperties)?.dnsServers?.mapNotNull { it.hostAddress }.orEmpty()
        require(dnsNative((dns.ifEmpty { listOf("1.1.1.1", "8.8.8.8") }).toTypedArray())) { "Не удалось настроить DNS TrustTunnel" }
        val config = TrustTunnelProfile.config(node, port, password).toByteArray(Charsets.UTF_8)
        try { pointer = createNative(config) } finally { config.fill(0) }
        check(pointer != 0L) { "Не удалось создать TrustTunnel" }
        check(startNative(pointer)) { "Не удалось запустить TrustTunnel. Проверьте адрес и параметры" }
        connectivity.registerNetworkCallback(NetworkRequest.Builder().addCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET).addCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN).build(), callback)
        registered = true
        // AutoSetup returns before TLS/authentication completes. In particular, UDP sent
        // before CONNECTED is discarded by the upstream kill switch.
        while (state != 2 && android.os.SystemClock.elapsedRealtime() < deadline) Thread.sleep(20)
        check(state == 2) { "TrustTunnel: таймаут подключения. Проверьте адрес, сертификат и авторизацию" }
        return json("type" to "socks", "server" to "127.0.0.1", "server_port" to port, "version" to "5", "username" to "bebekon", "password" to password)
    }
    // Exact names/signatures are retained by R8 for JNI callbacks.
    fun protectSocket(fd: Int): Boolean = runCatching {
        if (closed) return false
        if (service != null && !service.protect(fd)) return false
        val network = network() ?: return false
        ParcelFileDescriptor.fromFd(fd).use { network.bindSocket(it.fileDescriptor) }
        true
    }.getOrDefault(false)
    fun verifyCertificate(raw: Array<ByteArray>): Boolean = runCatching {
        require(raw.size in 1..17 && raw.all { it.size in 1..65536 })
        val factory = CertificateFactory.getInstance("X.509")
        val chain = raw.map { factory.generateCertificate(it.inputStream()) as X509Certificate }.distinctBy { digest(Base64Encoder.encode(it.encoded)) }.toTypedArray()
        trust.checkServerTrusted(chain, chain.first().publicKey.algorithm)
        // TrustTunnel additionally verifies the configured certificate hostname, independently of custom SNI.
        true
    }.getOrDefault(false)
    fun onStateChanged(value: Int) { state = value }
    @Synchronized override fun close() {
        if (closed) return
        if (registered) { runCatching { connectivity.unregisterNetworkCallback(callback) }; registered = false }
        // Let native shutdown finish while protect callbacks still work for already queued operations.
        val old = pointer; pointer = 0
        if (old != 0L) destroyNative(old)
        closed = true
    }
    private external fun dnsNative(servers: Array<String>): Boolean
    private external fun createNative(config: ByteArray): Long
    private external fun startNative(pointer: Long): Boolean
    private external fun networkNative(pointer: Long, available: Boolean)
    private external fun destroyNative(pointer: Long)
}
private object Base64Encoder { fun encode(bytes: ByteArray) = java.util.Base64.getEncoder().encodeToString(bytes) }
