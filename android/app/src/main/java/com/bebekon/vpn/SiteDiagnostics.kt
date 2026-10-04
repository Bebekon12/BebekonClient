package com.bebekon.vpn

import android.content.Context
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import kotlinx.coroutines.*
import java.net.URI
import java.net.SocketTimeoutException
import java.net.UnknownHostException
import javax.net.ssl.SSLException

data class SiteCheck(val running: Boolean = false, val direct: String = "", val vpn: String = "")

suspend fun checkSite(context: Context, state: SavedState, address: String): SiteCheck = coroutineScope {
    val uri = URI(if (address.trim().contains("://")) address.trim() else "https://${address.trim()}")
    require(uri.scheme == "https" && !uri.host.isNullOrBlank() && uri.rawUserInfo == null) { "Укажите HTTPS-адрес сайта" }
    val direct = async(Dispatchers.IO) {
        runCatching {
            val connection = directConnection(context, uri.toURL())
            try { connection.requestMethod = "HEAD"; connection.connectTimeout = 5000; connection.readTimeout = 5000; connection.instanceFollowRedirects = false; "HTTP ${connection.responseCode}" }
            finally { connection.disconnect() }
        }.fold({ it }, ::siteError)
    }
    val vpn = async(Dispatchers.IO) {
        if (state.selectedNode == null) "Сначала выберите сервер"
        else runCatching { "HTTP ${NativeCore.probe(context, state, "HEAD", uri.toString()).statusCode}" }.fold({ it }, ::siteError)
    }
    SiteCheck(direct = direct.await(), vpn = vpn.await())
}
private fun siteError(error: Throwable): String = when {
    error is UnknownHostException -> "Не удалось определить адрес: DNS"
    error is SocketTimeoutException || error.message?.contains("deadline", true) == true -> "Таймаут"
    error is SSLException || error.message?.contains("certificate", true) == true -> "Ошибка TLS / сертификата"
    error.message?.startsWith("probe HTTP ") == true -> error.message!!.removePrefix("probe ")
    error.message?.contains("DNS", true) == true || error.message?.contains("lookup", true) == true -> "Ошибка DNS"
    else -> "Не удалось установить соединение"
}

@Composable fun SiteDiagnosticsDialog(model: MainViewModel, dismiss: () -> Unit) {
    var address by remember { mutableStateOf("") }
    var error by remember { mutableStateOf("") }
    val result by model.siteCheck.collectAsState()
    AlertDialog(onDismissRequest = { if (!result.running) dismiss() }, title = { Text("Проверка сайта") }, text = {
        Column(Modifier.verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            Text("Сравним ответ сервера напрямую и через выбранный VPN. Проверяется HTTPS-соединение, без загрузки всей страницы. Ваше подключение и правила сохранятся.", fontSize = 12.sp)
            OutlinedTextField(address, { address = it; error = "" }, Modifier.fillMaxWidth(), singleLine = true, label = { Text("Адрес страницы") }, placeholder = { Text("https://example.com") }, enabled = !result.running)
            if (result.running) LinearProgressIndicator(Modifier.fillMaxWidth())
            if (error.isNotEmpty()) Text(error, color = MaterialTheme.colorScheme.error)
            if (result.direct.isNotEmpty()) {
                Text("Напрямую: ${result.direct}", fontWeight = FontWeight.SemiBold)
                Text("Через VPN: ${result.vpn}", fontWeight = FontWeight.SemiBold)
                Text("Режим: ${model.saved.collectAsState().value.preferences.routing.label}. HTTP 200–399 означает ответ сайта; 403 может означать ограничение сайта. Эти запросы выполняет клиент, они не проверяют маршрут другого приложения или всю страницу. Для ChatGPT, установленного из браузера, используйте набор правил OpenAI / ChatGPT; исключение самого браузера из VPN отменяет и эти правила.", fontSize = 11.sp)
            }
        }
    }, confirmButton = { Button({
        runCatching { val value = URI(if (address.contains("://")) address.trim() else "https://${address.trim()}"); require(value.scheme == "https" && !value.host.isNullOrBlank() && value.rawUserInfo == null) }
            .onSuccess { model.diagnoseSite(address) }.onFailure { error = "Укажите HTTPS-адрес сайта" }
    }, enabled = address.isNotBlank() && !result.running) { Text("Проверить") } }, dismissButton = { TextButton(dismiss, enabled = !result.running) { Text("Закрыть") } })
}
