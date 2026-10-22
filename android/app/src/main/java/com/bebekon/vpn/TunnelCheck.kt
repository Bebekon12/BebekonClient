package com.bebekon.vpn

import java.io.BufferedInputStream
import java.io.InputStream
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket
import java.security.SecureRandom
import java.util.Base64
import javax.net.ssl.HttpsURLConnection
import javax.net.ssl.SSLSocket
import javax.net.ssl.SSLSocketFactory

/**
 * HTTPS requests through the running tunnel: a private loopback inbound of the active core,
 * protected by a random password and routed straight to "vpn" (see CoreConfig). Unlike a
 * separate probe core, this sees exactly what apps see, including the server Auto picked.
 * Certificates and host names are verified as for any HTTPS request.
 */
object TunnelCheck {
    class Endpoint(val port: Int, val password: String)
    class Response(val status: Int, val body: String)

    fun endpoint(): Endpoint {
        val port = ServerSocket(0, 1, InetAddress.getLoopbackAddress()).use { it.localPort }
        val secret = ByteArray(24).also(SecureRandom()::nextBytes).joinToString("") { "%02x".format(it) }
        return Endpoint(port, secret)
    }

    /** True when either check host answers 204 through the tunnel. */
    fun works(endpoint: Endpoint): Boolean = listOf("www.gstatic.com", "cp.cloudflare.com").any { host ->
        runCatching { get(endpoint, host, "/generate_204").status == 204 }.getOrDefault(false)
    }

    fun get(endpoint: Endpoint, host: String, path: String, timeoutMs: Int = 10_000): Response {
        require(Regex("[A-Za-z0-9.-]{1,253}").matches(host) && path.startsWith("/"))
        val deadline = System.nanoTime() + timeoutMs * 1_000_000L
        fun remaining() = ((deadline - System.nanoTime()) / 1_000_000).toInt().also { check(it > 0) { "Таймаут" } }
        val raw = Socket()
        try {
            raw.connect(InetSocketAddress(InetAddress.getLoopbackAddress(), endpoint.port), remaining())
            raw.soTimeout = remaining()
            val auth = Base64.getEncoder().encodeToString("bebekon:${endpoint.password}".toByteArray())
            raw.getOutputStream().apply { write("CONNECT $host:443 HTTP/1.1\r\nHost: $host:443\r\nProxy-Authorization: Basic $auth\r\n\r\n".toByteArray()); flush() }
            val proxyStatus = statusLine(readHead(raw.getInputStream()))
            check(proxyStatus == 200) { "Туннель отклонил проверку: $proxyStatus" }
            val tls = (SSLSocketFactory.getDefault() as SSLSocketFactory).createSocket(raw, host, 443, true) as SSLSocket
            tls.use {
                it.soTimeout = remaining()
                it.sslParameters = it.sslParameters.apply { endpointIdentificationAlgorithm = "HTTPS" }
                it.startHandshake()
                // Android's default verifier is the platform's real one (on a desktop JVM it rejects everything).
                if (System.getProperty("java.vm.vendor").orEmpty().contains("Android", true)) check(HttpsURLConnection.getDefaultHostnameVerifier().verify(host, it.session)) { "Сертификат не соответствует $host" }
                it.outputStream.apply { write("GET $path HTTP/1.1\r\nHost: $host\r\nUser-Agent: BebekonVPN\r\nAccept: */*\r\nConnection: close\r\n\r\n".toByteArray()); flush() }
                val input = BufferedInputStream(it.inputStream)
                val head = readHead(input)
                val status = statusLine(head)
                val length = Regex("(?im)^content-length:\\s*(\\d{1,6})\\s*$").find(head)?.groupValues?.get(1)?.toInt()
                val chunked = Regex("(?im)^transfer-encoding:\\s*chunked\\s*$").containsMatchIn(head)
                val body = if (status == 204 || status == 304) ByteArray(0) else if (chunked) readChunked(input) else readBody(input, length ?: 4096)
                return Response(status, String(body, Charsets.UTF_8))
            }
        } finally { runCatching { raw.close() } }
    }

    private fun statusLine(head: String) = head.substringBefore("\r\n").split(' ').getOrNull(1)?.toIntOrNull() ?: error("Некорректный ответ")

    /** Reads up to the blank line; small and bounded. */
    private fun readHead(input: InputStream): String {
        val bytes = java.io.ByteArrayOutputStream()
        var match = 0
        while (match < 4) {
            val b = input.read(); check(b >= 0 && bytes.size() < 16_384) { "Некорректный ответ" }
            bytes.write(b)
            match = when { b == '\r'.code && match % 2 == 0 -> match + 1; b == '\n'.code && match % 2 == 1 -> match + 1; b == '\r'.code -> 1; else -> 0 }
        }
        return bytes.toString(Charsets.ISO_8859_1.name())
    }

    /** Small bodies only (an IP address); stops at 4 KB. */
    private fun readChunked(input: InputStream): ByteArray {
        val out = java.io.ByteArrayOutputStream()
        while (out.size() < 4096) {
            val line = StringBuilder()
            while (true) { val b = input.read(); check(b >= 0 && line.length < 32) { "Некорректный ответ" }; if (b == '\n'.code) break; if (b != '\r'.code) line.append(b.toChar()) }
            val size = line.toString().substringBefore(';').trim().toIntOrNull(16) ?: error("Некорректный ответ")
            if (size == 0) break
            out.write(readBody(input, size.coerceAtMost(4096 - out.size())))
            if (size > 4096) break
            input.read(); input.read()
        }
        return out.toByteArray()
    }
    private fun readBody(input: InputStream, limit: Int): ByteArray {
        val max = limit.coerceIn(0, 4096); val out = ByteArray(max); var read = 0
        while (read < max) { val n = input.read(out, read, max - read); if (n < 0) break; read += n }
        return out.copyOf(read)
    }
}
