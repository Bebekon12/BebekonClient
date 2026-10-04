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
import androidx.compose.ui.semantics.Role
import androidx.compose.foundation.selection.toggleable
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import androidx.core.graphics.drawable.toBitmap
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

data class InstalledApp(val packageName: String, val label: String, val webDomain: String = "", val webBrowser: String = "")

fun installedApps(context: Context): List<InstalledApp> = context.packageManager
    .queryIntentActivities(Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_LAUNCHER), 0)
    .map {
        val web = webApp(context, it.activityInfo.packageName)
        val browser = web?.browser?.let { pkg -> runCatching { context.packageManager.getApplicationLabel(context.packageManager.getApplicationInfo(pkg, 0)).toString() }.getOrDefault(pkg) }.orEmpty()
        InstalledApp(it.activityInfo.packageName, it.loadLabel(context.packageManager).toString(), web?.domain.orEmpty(), browser)
    }
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
    AppPicker(initial.associateWith { initialVpn }, initialVpn, false, routing, dismiss) { apps, actions, vpn -> choose(apps.filter { it.packageName in actions }, vpn) }
}
@Composable fun ApplicationRulesPicker(rules: List<Rule>, routing: RoutingMode, dismiss: () -> Unit, save: (List<InstalledApp>, Map<String, Boolean>) -> Unit) {
    AppPicker(effectiveAppRules(rules), true, true, routing, dismiss) { apps, actions, _ -> save(apps, actions) }
}
@Composable private fun AppPicker(initial: Map<String, Boolean>, initialVpn: Boolean, manageRules: Boolean, routing: RoutingMode, dismiss: () -> Unit, choose: (List<InstalledApp>, Map<String, Boolean>, Boolean) -> Unit) {
    val context = LocalContext.current
    var search by remember { mutableStateOf("") }
    var assignments by remember { mutableStateOf(initial) }
    var vpn by remember { mutableStateOf(initialVpn) }
    var onlySelected by remember { mutableStateOf(false) }
    var loading by remember { mutableStateOf(true) }
    val apps by produceState<List<InstalledApp>>(emptyList()) {
        value = withContext(Dispatchers.IO) {
            val installed = installedApps(context)
            installed + initial.keys.filter { pkg -> installed.none { it.packageName == pkg } }.map { InstalledApp(it, it) }
        }
        loading = false
    }
    Dialog(dismiss, properties = DialogProperties(usePlatformDefaultWidth = false)) {
        BoxWithConstraints(Modifier.fillMaxSize().padding(horizontal = 14.dp, vertical = 32.dp), contentAlignment = Alignment.Center) {
            Surface(Modifier.fillMaxWidth().heightIn(max = maxHeight), shape = RoundedCornerShape(26.dp), color = MaterialTheme.colorScheme.surface, border = BorderStroke(1.dp, MaterialTheme.colorScheme.outline)) {
                Column(Modifier.padding(16.dp)) {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Column(Modifier.weight(1f)) { Text("Приложения", fontSize = 22.sp, fontWeight = FontWeight.Bold); Text("Правил приложений: ${assignments.size}", fontSize = 12.sp, color = MaterialTheme.colorScheme.onSurfaceVariant) }
                        IconButton(dismiss) { Icon(Icons.Outlined.Close, "Закрыть выбор приложений") }
                    }
                    OutlinedTextField(search, { search = it }, Modifier.fillMaxWidth().padding(top = 12.dp).testTag("app-search"), placeholder = { Text("Найти приложение") }, leadingIcon = { Icon(Icons.Outlined.Search, null) }, singleLine = true, shape = RoundedCornerShape(15.dp))
                    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        FilterChip(!onlySelected, { onlySelected = false }, { Text("Все приложения") })
                        FilterChip(onlySelected, { onlySelected = true }, { Text("Выбранные (${assignments.count { it.value == vpn }})") })
                    }
                    Row(Modifier.fillMaxWidth().padding(bottom = 8.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        FilterChip(vpn, { vpn = true; if (!manageRules) assignments = assignments.mapValues { true } }, { Text("Через VPN") }, Modifier.weight(1f).testTag("apps-vpn"))
                        FilterChip(!vpn, { vpn = false; if (!manageRules) assignments = assignments.mapValues { false } }, { Text("Напрямую") }, Modifier.weight(1f).testTag("apps-direct"))
                    }
                    if (manageRules) Text("При выборе приложений VPN получают только отмеченные: остальные сохраняют обычную сеть и DNS. Правила сайтов действуют внутри выбранных приложений. Общую обработку сайтов можно включить в настройках. «Напрямую» исключает приложение; снять галочку — удалить правило.", fontSize = 11.sp, lineHeight = 15.sp, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(bottom = 8.dp))
                    if (manageRules) Text("Веб-приложение использует свой браузер: в туннель попадает браузер, но через VPN идут только сайты из правил. Для ChatGPT добавьте набор OpenAI / ChatGPT, чтобы учесть авторизацию и загрузку файлов. «Напрямую» для браузера исключает и веб-приложение.", fontSize = 11.sp, lineHeight = 15.sp, color = MaterialTheme.colorScheme.secondary, modifier = Modifier.padding(bottom = 8.dp))
                    val visible = apps.filter { (!onlySelected || assignments[it.packageName] == vpn) && (it.label.contains(search, true) || it.packageName.contains(search, true)) }
                    LazyColumn(Modifier.weight(1f).fillMaxWidth().testTag("app-list"), verticalArrangement = Arrangement.spacedBy(3.dp)) {
                        if (loading) item { Box(Modifier.fillMaxWidth().padding(32.dp), contentAlignment = Alignment.Center) { CircularProgressIndicator() } }
                        else if (visible.isEmpty()) item { Text("Приложения не найдены", Modifier.padding(vertical = 24.dp), color = MaterialTheme.colorScheme.onSurfaceVariant) }
                        items(visible, key = { it.packageName }) { app ->
                            val checked = assignments[app.packageName] == vpn
                            Row(Modifier.fillMaxWidth().testTag("app-${app.packageName}").clip(RoundedCornerShape(14.dp)).background(if (checked) MaterialTheme.colorScheme.primary.copy(alpha = .10f) else androidx.compose.ui.graphics.Color.Transparent)
                                .toggleable(value = checked, role = Role.Checkbox) { assignments = if (checked) assignments - app.packageName else assignments + (app.packageName to vpn) }
                                .padding(horizontal = 8.dp, vertical = 10.dp), verticalAlignment = Alignment.CenterVertically) {
                                ApplicationIcon(app.packageName)
                                Column(Modifier.weight(1f).padding(horizontal = 12.dp)) {
                                    Text(app.label, fontSize = 14.sp, fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis)
                                    Text(app.packageName, fontSize = 10.sp, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1, overflow = TextOverflow.Ellipsis)
                                    if (app.webDomain.isNotEmpty()) Text("${app.webDomain} · браузер: ${app.webBrowser.ifEmpty { "не определён" }}", fontSize = 10.sp, color = MaterialTheme.colorScheme.secondary)
                                    if (manageRules && assignments.containsKey(app.packageName) && !checked) Text(if (vpn) "Сейчас: напрямую" else "Сейчас: через VPN", fontSize = 10.sp, color = MaterialTheme.colorScheme.secondary)
                                }
                                Checkbox(checked, onCheckedChange = null)
                            }
                        }
                    }
                    if (routing == RoutingMode.ALL) Text("Правила приложений работают в режиме «По правилам». Его можно выбрать на странице правил.", Modifier.padding(top = 8.dp), fontSize = 11.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    Button({ choose(apps, assignments, vpn) }, Modifier.fillMaxWidth().padding(top = 12.dp).height(48.dp).testTag("save-apps"), enabled = (manageRules || assignments.isNotEmpty()) && assignments.size <= 256 && !loading, shape = RoundedCornerShape(15.dp)) { Text("Сохранить (${assignments.size})") }
                }
            }
        }
    }
}
