package com.bebekon.vpn

import android.content.Context
import android.content.Intent
import androidx.compose.foundation.Image
import androidx.compose.foundation.*
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.Apps
import androidx.compose.material.icons.outlined.Search
import androidx.compose.material.icons.outlined.Close
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.Alignment
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import androidx.core.graphics.drawable.toBitmap
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

data class InstalledApp(val packageName: String, val label: String)

fun installedApps(context: Context): List<InstalledApp> = context.packageManager
    .queryIntentActivities(Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_LAUNCHER), 0)
    .map { InstalledApp(it.activityInfo.packageName, it.loadLabel(context.packageManager).toString()) }
    .distinctBy { it.packageName }.filter { it.packageName != context.packageName }
    .sortedWith(compareBy(String.CASE_INSENSITIVE_ORDER) { it.label })

@Composable fun ApplicationIcon(packageName: String, modifier: Modifier = Modifier.size(40.dp)) {
    val context = LocalContext.current
    val bitmap by produceState<ImageBitmap?>(null, packageName) {
        value = withContext(Dispatchers.IO) {
            runCatching { context.packageManager.getApplicationIcon(packageName).toBitmap(96, 96).asImageBitmap() }.getOrNull()
        }
    }
    if (bitmap != null) Image(bitmap!!, "Иконка приложения", modifier.clip(RoundedCornerShape(10.dp)))
    else Icon(Icons.Outlined.Apps, "Приложение", modifier, tint = MaterialTheme.colorScheme.secondary)
}

@Composable fun ApplicationPicker(initial: Set<String>, initialVpn: Boolean, routing: RoutingMode, dismiss: () -> Unit, choose: (List<InstalledApp>, Boolean) -> Unit) {
    val context = LocalContext.current
    var search by remember { mutableStateOf("") }
    var selected by remember { mutableStateOf(initial) }
    var vpn by remember { mutableStateOf(initialVpn) }
    var onlySelected by remember { mutableStateOf(false) }
    var loading by remember { mutableStateOf(true) }
    val apps by produceState<List<InstalledApp>>(emptyList()) {
        value = withContext(Dispatchers.IO) { installedApps(context) }
        loading = false
    }
    Dialog(dismiss, properties = DialogProperties(usePlatformDefaultWidth = false)) {
        BoxWithConstraints(Modifier.fillMaxSize().padding(horizontal = 14.dp, vertical = 32.dp), contentAlignment = Alignment.Center) {
            Surface(Modifier.fillMaxWidth().heightIn(max = maxHeight), shape = RoundedCornerShape(26.dp), color = MaterialTheme.colorScheme.surface, border = BorderStroke(1.dp, MaterialTheme.colorScheme.outline)) {
                Column(Modifier.padding(16.dp)) {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Column(Modifier.weight(1f)) { Text("Приложения", fontSize = 22.sp, fontWeight = FontWeight.Bold); Text("Выбрано: ${selected.size}", fontSize = 12.sp, color = MaterialTheme.colorScheme.onSurfaceVariant) }
                        IconButton(dismiss) { Icon(Icons.Outlined.Close, "Закрыть выбор приложений") }
                    }
                    OutlinedTextField(search, { search = it }, Modifier.fillMaxWidth().padding(top = 12.dp).testTag("app-search"), placeholder = { Text("Найти приложение") }, leadingIcon = { Icon(Icons.Outlined.Search, null) }, singleLine = true, shape = RoundedCornerShape(15.dp))
                    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        FilterChip(!onlySelected, { onlySelected = false }, { Text("Все приложения") })
                        FilterChip(onlySelected, { onlySelected = true }, { Text("Выбранные (${selected.size})") })
                    }
                    Row(Modifier.fillMaxWidth().padding(bottom = 8.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        FilterChip(vpn, { vpn = true }, { Text("Через VPN") }, Modifier.weight(1f))
                        FilterChip(!vpn, { vpn = false }, { Text("Напрямую") }, Modifier.weight(1f))
                    }
                    val visible = apps.filter { (!onlySelected || it.packageName in selected) && (it.label.contains(search, true) || it.packageName.contains(search, true)) }
                    LazyColumn(Modifier.weight(1f).fillMaxWidth().testTag("app-list"), verticalArrangement = Arrangement.spacedBy(3.dp)) {
                        if (loading) item { Box(Modifier.fillMaxWidth().padding(32.dp), contentAlignment = Alignment.Center) { CircularProgressIndicator() } }
                        else if (visible.isEmpty()) item { Text("Приложения не найдены", Modifier.padding(vertical = 24.dp), color = MaterialTheme.colorScheme.onSurfaceVariant) }
                        items(visible, key = { it.packageName }) { app ->
                            Row(Modifier.fillMaxWidth().testTag("app-${app.packageName}").clip(RoundedCornerShape(14.dp)).background(if (app.packageName in selected) MaterialTheme.colorScheme.primary.copy(alpha = .10f) else androidx.compose.ui.graphics.Color.Transparent)
                                .clickable { selected = if (app.packageName in selected) selected - app.packageName else selected + app.packageName }
                                .padding(horizontal = 8.dp, vertical = 10.dp), verticalAlignment = Alignment.CenterVertically) {
                                ApplicationIcon(app.packageName)
                                Column(Modifier.weight(1f).padding(horizontal = 12.dp)) {
                                    Text(app.label, fontSize = 14.sp, fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis)
                                    Text(app.packageName, fontSize = 10.sp, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1, overflow = TextOverflow.Ellipsis)
                                }
                                Checkbox(app.packageName in selected, onCheckedChange = null)
                            }
                        }
                    }
                    if (routing == RoutingMode.ALL) Text("Правила приложений работают в режиме «По правилам». Его можно выбрать на странице правил.", Modifier.padding(top = 8.dp), fontSize = 11.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    Button({ choose(apps.filter { it.packageName in selected }, vpn) }, Modifier.fillMaxWidth().padding(top = 12.dp).height(48.dp).testTag("save-apps"), enabled = selected.isNotEmpty() && selected.size <= 256 && !loading, shape = RoundedCornerShape(15.dp)) { Text("Сохранить (${selected.size})") }
                }
            }
        }
    }
}
