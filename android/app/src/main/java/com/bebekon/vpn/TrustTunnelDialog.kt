package com.bebekon.vpn

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.input.VisualTransformation
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties

@Composable internal fun TrustTunnelDialog(existing: Node?, dismiss: () -> Unit, save: (Node) -> Unit, remove: (() -> Unit)? = null) {
    val options = remember(existing) { existing?.config?.optJSONObject("trusttunnel") }
    var name by remember(existing) { mutableStateOf(existing?.name.orEmpty()) }
    var address by remember(existing) { mutableStateOf(existing?.let { (if (it.host.contains(':')) "[${it.host}]" else it.host) + ":${it.port}" }.orEmpty()) }
    var hostname by remember(existing) { mutableStateOf(options?.optString("hostname").orEmpty()) }
    var sni by remember(existing) { mutableStateOf(options?.optString("custom_sni").orEmpty()) }
    var username by remember(existing) { mutableStateOf(options?.optString("username").orEmpty()) }
    // Credentials are intentionally not rememberSaveable or placed into external intents/clipboard.
    var password by remember(existing) { mutableStateOf(options?.optString("password").orEmpty()) }
    var visible by remember { mutableStateOf(false) }
    var protocol by remember(existing) { mutableStateOf(options?.optString("upstream_protocol", "http2") ?: "http2") }
    var menu by remember { mutableStateOf(false) }; var error by remember { mutableStateOf("") }; var confirmDelete by remember { mutableStateOf(false) }
    Dialog(onDismissRequest = dismiss, properties = DialogProperties(usePlatformDefaultWidth = false)) {
        Surface(Modifier.fillMaxWidth().padding(16.dp).heightIn(max = 760.dp), shape = RoundedCornerShape(26.dp), color = MaterialTheme.colorScheme.surface) {
            Column(Modifier.imePadding().padding(20.dp)) {
                Row { Text(if (existing == null) "Новый сервер TrustTunnel" else "Сервер TrustTunnel", Modifier.weight(1f), style = MaterialTheme.typography.titleLarge); if (remove != null) IconButton({ confirmDelete = true }) { Icon(Icons.Outlined.DeleteOutline, "Удалить сервер") } }
                Column(Modifier.weight(1f, fill = false).verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                    Text("Введите данные сервера. Все параметры сохраняются с шифрованием на этом устройстве.", fontSize = 12.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    OutlinedTextField(name, { name = it.take(160) }, Modifier.fillMaxWidth(), label = { Text("Название — необязательно") }, singleLine = true, shape = RoundedCornerShape(14.dp))
                    OutlinedTextField(address, { address = it.take(300) }, Modifier.fillMaxWidth(), label = { Text("Адрес сервера") }, placeholder = { Text("192.0.2.1:443") }, supportingText = { Text("IP или домен; без порта используется 443") }, singleLine = true, shape = RoundedCornerShape(14.dp))
                    OutlinedTextField(hostname, { hostname = it.take(253) }, Modifier.fillMaxWidth(), label = { Text("Домен из сертификата") }, placeholder = { Text("vpn.example.com") }, supportingText = { Text("Имя, на которое выдан сертификат сервера") }, singleLine = true, shape = RoundedCornerShape(14.dp))
                    OutlinedTextField(sni, { sni = it.take(253) }, Modifier.fillMaxWidth(), label = { Text("Свой SNI — необязательно") }, supportingText = { Text("Укажите, только если это требуется сервером") }, singleLine = true, shape = RoundedCornerShape(14.dp))
                    OutlinedTextField(username, { username = it.take(1024) }, Modifier.fillMaxWidth(), label = { Text("Логин") }, singleLine = true, shape = RoundedCornerShape(14.dp))
                    OutlinedTextField(password, { password = it.take(4096) }, Modifier.fillMaxWidth(), label = { Text("Пароль") }, singleLine = true, visualTransformation = if (visible) VisualTransformation.None else PasswordVisualTransformation(), trailingIcon = { IconButton({ visible = !visible }) { Icon(if (visible) Icons.Outlined.VisibilityOff else Icons.Outlined.Visibility, if (visible) "Скрыть пароль" else "Показать пароль") } }, shape = RoundedCornerShape(14.dp))
                    Box { OutlinedButton({ menu = true }, Modifier.fillMaxWidth().height(52.dp), shape = RoundedCornerShape(14.dp)) { Text("Протокол: " + when (protocol) { "http2" -> "HTTP/2"; "http3" -> "HTTP/3 (QUIC)"; else -> "Авто" }, Modifier.weight(1f)); Icon(Icons.Outlined.ExpandMore, null) }; DropdownMenu(menu, { menu = false }) { listOf("http2" to "HTTP/2 · по умолчанию", "http3" to "HTTP/3 (QUIC)", "auto" to "Авто").forEach { (value, title) -> DropdownMenuItem(text = { Text(title) }, onClick = { protocol = value; menu = false }) } } }
                    Text("Маршрутизация задаётся в ваших правилах. Проверка сертификата обязательна.", fontSize = 11.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    if (error.isNotEmpty()) Text(error, color = MaterialTheme.colorScheme.error, fontSize = 12.sp)
                }
                Row(Modifier.fillMaxWidth().padding(top = 16.dp), horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                    OutlinedButton(dismiss, Modifier.weight(1f).height(48.dp), shape = RoundedCornerShape(14.dp)) { Text("Отмена") }
                    Button({ try { save(TrustTunnelProfile.manual(name, address, hostname, sni, username, password, protocol, existing)) } catch (e: Exception) { error = e.message?.takeIf { it.length < 180 && !it.contains("://") } ?: "Проверьте данные сервера" } }, Modifier.weight(1f).height(48.dp), enabled = address.isNotBlank() && hostname.isNotBlank() && username.isNotBlank() && password.isNotEmpty(), shape = RoundedCornerShape(14.dp)) { Text("Сохранить") }
                }
            }
        }
    }
    if (confirmDelete) AlertDialog(onDismissRequest = { confirmDelete = false }, title = { Text("Удалить сервер?") }, text = { Text("Сервер будет удалён из локального списка. Активное подключение к нему отключится.") }, confirmButton = { TextButton({ remove?.invoke() }) { Text("Удалить") } }, dismissButton = { TextButton({ confirmDelete = false }) { Text("Отмена") } })
}
