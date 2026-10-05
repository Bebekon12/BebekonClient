@file:OptIn(androidx.compose.material3.ExperimentalMaterial3Api::class)
package com.bebekon.vpn

import android.content.Intent
import android.provider.Settings
import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.core.*
import androidx.compose.foundation.*
import androidx.compose.foundation.gestures.detectTransformGestures
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.*
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.*
import kotlinx.coroutines.delay
import org.json.JSONArray
import java.util.Locale
import kotlin.math.*

private val Blue = Color(0xFF387FFF)
private val Green = Color(0xFF20D884)
private val Amber = Color(0xFFFFAD47)
private val Red = Color(0xFFFF687A)
private val Dark = darkColorScheme(primary = Blue, onPrimary = Color.White, background = Color(0xFF06182B), onBackground = Color(0xFFF0F5FF), surface = Color(0xFF0C2440), onSurface = Color(0xFFF0F5FF), surfaceVariant = Color(0xFF153554), onSurfaceVariant = Color(0xFF9DC5E8), outline = Color(0xFF274B70), secondary = Color(0xFF53ADFF), secondaryContainer = Color(0xFF163C64), onSecondaryContainer = Color(0xFFDBEEFF), surfaceContainerHigh = Color(0xFF163452), surfaceContainerHighest = Color(0xFF1C3D5C))
private val Light = lightColorScheme(primary = Color(0xFF086AFF), onPrimary = Color.White, background = Color(0xFFEAF5FF), onBackground = Color(0xFF12234B), surface = Color.White, onSurface = Color(0xFF12234B), surfaceVariant = Color(0xFFE0EFFF), onSurfaceVariant = Color(0xFF526F99), outline = Color(0xFFBDD9F7), secondary = Color(0xFF087FFF), secondaryContainer = Color(0xFFDBEEFF), onSecondaryContainer = Color(0xFF143663), surfaceContainerHigh = Color(0xFFE5F2FF), surfaceContainerHighest = Color(0xFFDDEEFF))

@Composable fun BebekonApp(model: MainViewModel, toggle: () -> Unit, addTile: () -> Unit, scan: () -> Unit, importFile: () -> Unit) {
    val saved by model.saved.collectAsState(); val session by model.session.collectAsState(); val message by model.message.collectAsState(); val imported by model.importText.collectAsState(); val busy by model.busy.collectAsState()
    val isDark = when (saved.preferences.theme) { ThemeChoice.DARK -> true; ThemeChoice.LIGHT -> false; ThemeChoice.SYSTEM -> isSystemInDarkTheme() }
    var tab by rememberSaveable { mutableIntStateOf(0) }; var settings by rememberSaveable { mutableStateOf(false) }; var addSubscription by remember { mutableStateOf(false) }; var editRule by remember { mutableStateOf<Rule?>(null) }; var newRule by remember { mutableStateOf(false) }; var presets by remember { mutableStateOf(false) }; var routing by remember { mutableStateOf(false) }
    var applications by remember { mutableStateOf(false) }
    var trustTunnel by remember { mutableStateOf(false) }; var editTrustTunnel by remember { mutableStateOf<Node?>(null) }
    val activity = androidx.activity.compose.LocalActivity.current
    androidx.activity.compose.BackHandler { if (settings || tab != 0) { settings = false; tab = 0 } else activity?.moveTaskToBack(true) }
    val snackbar = remember { SnackbarHostState() }
    SideEffect { activity?.let { androidx.core.view.WindowCompat.getInsetsController(it.window, it.window.decorView).apply { isAppearanceLightStatusBars = !isDark; isAppearanceLightNavigationBars = !isDark } } }
    LaunchedEffect(message) { if (message.isNotEmpty()) { snackbar.showSnackbar(message); model.message.value = "" } }
    LaunchedEffect(imported) { if (imported.isNotEmpty()) { tab = 3; addSubscription = true } }
    MaterialTheme(colorScheme = if (isDark) Dark else Light) {
        Scaffold(containerColor = MaterialTheme.colorScheme.background, snackbarHost = { SnackbarHost(snackbar) }, topBar = {
            Row(Modifier.statusBarsPadding().fillMaxWidth().padding(horizontal = 20.dp, vertical = 10.dp), verticalAlignment = Alignment.CenterVertically) {
                if (settings) IconButton({ settings = false; tab = 0 }) { Icon(Icons.Outlined.ArrowBack, "Назад") } else Image(painterResource(R.drawable.snowman), "Снеговик Bebekon в шляпе", Modifier.size(58.dp))
                Column(Modifier.weight(1f).padding(start = 10.dp)) { Text(if (settings) "Настройки" else "Bebekon VPN", fontSize = 23.sp, fontWeight = FontWeight.Bold); if (settings) Text("Сделайте приложение своим", fontSize = 11.sp, color = MaterialTheme.colorScheme.onSurfaceVariant) }
                if (!settings) IconButton({ settings = true }, Modifier.clip(CircleShape).background(MaterialTheme.colorScheme.secondary.copy(alpha = .10f))) { Icon(Icons.Outlined.Settings, "Настройки", tint = MaterialTheme.colorScheme.onSurface) }
            }
        }, bottomBar = {
            // Deliberate gap, border and a separate background keep navigation distinct from the sheet in BOTH themes.
            Box(Modifier.fillMaxWidth().background(MaterialTheme.colorScheme.background).navigationBarsPadding().padding(horizontal = 12.dp, vertical = 10.dp)) {
                Surface(shape = RoundedCornerShape(25.dp), color = if (isDark) Color(0xFF0E192B) else Color.White, border = BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = .65f)), shadowElevation = if (isDark) 0.dp else 4.dp) {
                    NavigationBar(containerColor = Color.Transparent, tonalElevation = 0.dp, windowInsets = WindowInsets(0, 0, 0, 0), modifier = Modifier.height(72.dp)) {
                        listOf("Главная" to Icons.Outlined.Home, "Серверы" to Icons.Outlined.Public, "Правила" to Icons.Outlined.Tune, "Подписки" to Icons.Outlined.Link).forEachIndexed { i, (title, icon) -> NavigationBarItem(selected = tab == i && !settings, onClick = { tab = i; settings = false }, icon = { Icon(icon, title, Modifier.size(22.dp)) }, label = { Text(title, fontSize = 10.sp, maxLines = 1) }, colors = NavigationBarItemDefaults.colors(indicatorColor = MaterialTheme.colorScheme.primary.copy(alpha = .15f), selectedIconColor = MaterialTheme.colorScheme.primary, selectedTextColor = MaterialTheme.colorScheme.primary, unselectedIconColor = MaterialTheme.colorScheme.onSurfaceVariant, unselectedTextColor = MaterialTheme.colorScheme.onSurfaceVariant)) }
                    }
                }
            }
        }) { padding ->
            Box(Modifier.fillMaxSize().padding(padding)) {
                when {
                    settings -> FullSettingsScreen(saved.preferences, model, addTile, { routing = true }, { applications = true }, { presets = true }, { settings = false; tab = 3 })
                    tab == 0 -> HomeScreen(saved, session, model, toggle, { tab = 1 }, { routing = true }, { tab = 3 })
                    tab == 1 -> ServersScreen(saved, model, { trustTunnel = true }, { editTrustTunnel = it; trustTunnel = true })
                    tab == 2 -> RulesScreen(saved, model, { newRule = true }, { editRule = it }, { presets = true }, { routing = true }, { applications = true })
                    else -> SubscriptionsScreen(saved, model, { addSubscription = true }, scan, importFile, { trustTunnel = true }, { editTrustTunnel = it; trustTunnel = true })
                }
                if (busy) LinearProgressIndicator(Modifier.fillMaxWidth().align(Alignment.TopCenter), color = MaterialTheme.colorScheme.primary, trackColor = Color.Transparent)
            }
        }
        if (addSubscription) SubscriptionDialog(imported, { addSubscription = false; model.importText.value = "" }) { source, name -> model.import(source, name); addSubscription = false }
        if (trustTunnel) TrustTunnelDialog(editTrustTunnel, { trustTunnel = false; editTrustTunnel = null }, { model.saveTrustTunnel(it, editTrustTunnel); trustTunnel = false; editTrustTunnel = null }, editTrustTunnel?.let { node -> { model.removeNode(node); trustTunnel = false; editTrustTunnel = null } })
        if (newRule || editRule != null) RuleDialog(editRule, { newRule = false; editRule = null }) { model.saveRule(it); newRule = false; editRule = null }
        if (presets) PresetsDialog(model, { presets = false })
        UpdateDialog(LocalContext.current.updater)
        if (applications) ApplicationRulesPicker(saved.rules, saved.preferences.routing, { applications = false }) { apps, actions -> model.saveAppRules(apps, actions); applications = false }
        if (routing) AlertDialog(onDismissRequest = { routing = false }, title = { Text("Маршрутизация") }, text = { Column { RoutingMode.entries.forEach { m -> Row(Modifier.fillMaxWidth().clip(RoundedCornerShape(12.dp)).clickable { model.preferences(saved.preferences.copy(routing = m)); routing = false }.padding(12.dp), verticalAlignment = Alignment.CenterVertically) { RadioButton(saved.preferences.routing == m, onClick = null); Column(Modifier.padding(start = 10.dp)) { Text(m.label, fontWeight = FontWeight.SemiBold); Text(if (m == RoutingMode.ALL) "Всё через VPN, кроме исключений LAN" else "VPN для выбранных сайтов и приложений", fontSize = 12.sp, color = MaterialTheme.colorScheme.onSurfaceVariant) } } } } }, confirmButton = { TextButton({ routing = false }) { Text("Готово") } })
    }
}

@Composable fun PingText(ping: PingResult, click: () -> Unit) {
    val color = when (ping.quality) { PingQuality.GOOD -> Green; PingQuality.FAIR -> Amber; PingQuality.POOR -> Red; PingQuality.UNKNOWN -> MaterialTheme.colorScheme.onSurfaceVariant }
    Row(Modifier.clip(RoundedCornerShape(6.dp)).clickable(onClick = click).padding(vertical = 4.dp), verticalAlignment = Alignment.CenterVertically) { Text(ping.label, fontSize = 11.sp, lineHeight = 14.sp, fontWeight = FontWeight.SemiBold, color = color); if (ping.running) CircularProgressIndicator(Modifier.padding(start = 5.dp).size(10.dp), color = color, strokeWidth = 1.dp) }
}
private fun cleanName(s: String) = s.replace(Regex("[\\x{1F1E6}-\\x{1F1FF}]"), "").trim()
@Composable fun Flag(code: String, modifier: Modifier = Modifier.size(34.dp, 24.dp)) {
    val context = LocalContext.current
    val bitmap = remember(code) { runCatching { context.assets.open("flags/$code.png").use { android.graphics.BitmapFactory.decodeStream(it) }.asImageBitmap() }.getOrNull() }
    if (bitmap != null) Image(bitmap, code, modifier.clip(RoundedCornerShape(4.dp))) else Icon(Icons.Outlined.Public, "Сервер", modifier, tint = MaterialTheme.colorScheme.secondary)
}
@Composable private fun StatusPill(text: String, color: Color) { Text(text, fontSize = 10.sp, color = color, fontWeight = FontWeight.SemiBold, modifier = Modifier.clip(CircleShape).background(color.copy(alpha = .12f)).padding(horizontal = 9.dp, vertical = 5.dp)) }
@Composable fun ServerRow(node: Node, selected: Boolean, favorite: Boolean, ping: PingResult, select: () -> Unit, star: () -> Unit, probe: () -> Unit) {
    Surface(shape = RoundedCornerShape(18.dp), color = if (selected) MaterialTheme.colorScheme.primary.copy(alpha = .09f) else MaterialTheme.colorScheme.surface, border = BorderStroke(if (selected) 1.5.dp else 1.dp, if (selected) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.outline.copy(alpha = .45f)), modifier = Modifier.fillMaxWidth().clickable(onClick = select)) {
        Row(Modifier.padding(start = 13.dp, top = 12.dp, bottom = 12.dp, end = 4.dp), verticalAlignment = Alignment.CenterVertically) {
            Flag(node.country); Column(Modifier.weight(1f).padding(horizontal = 10.dp)) { Text(cleanName(node.name), fontSize = 14.sp, fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis); Text(if (node.unsupported.isEmpty()) "${node.protocol} · ${node.transport}" else "Доступен в Windows", fontSize = 10.sp, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(top = 4.dp)) }
            PingText(ping, probe); IconButton(star, Modifier.size(42.dp)) { Icon(if (favorite) Icons.Outlined.Star else Icons.Outlined.StarBorder, "Избранное", Modifier.size(19.dp), tint = if (favorite) Amber else MaterialTheme.colorScheme.onSurfaceVariant) }
        }
    }
}
@Composable private fun ServersScreen(saved: SavedState, model: MainViewModel, addTrustTunnel: () -> Unit, editTrustTunnel: (Node) -> Unit) {
    var search by rememberSaveable { mutableStateOf("") }; var favorites by rememberSaveable { mutableStateOf(false) }; var methods by remember { mutableStateOf(false) }; var sortPing by rememberSaveable { mutableStateOf(true) }
    val ping by model.repo.pings.collectAsState()
    val nodes = saved.visibleNodes.filter { (!favorites || it.id in saved.favorites) && it.name.contains(search, true) }.let { if (sortPing) nodesByPing(it, ping) else it }
    LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(20.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
        item { PageTitle("Выбор сервера", "${saved.visibleNodes.size} серверов · ${saved.subscriptions.size} подписок") }
        item { OutlinedButton(addTrustTunnel, Modifier.fillMaxWidth().height(48.dp), shape = RoundedCornerShape(14.dp)) { Icon(Icons.Outlined.Add, null); Text("TrustTunnel · ввести вручную", Modifier.padding(start = 8.dp)) } }
        item { OutlinedTextField(search, { search = it }, Modifier.fillMaxWidth(), placeholder = { Text("Страна или название") }, leadingIcon = { Icon(Icons.Outlined.Search, null) }, singleLine = true, shape = RoundedCornerShape(16.dp)) }
        item { Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(6.dp), verticalAlignment = Alignment.CenterVertically) { FilterChip(favorites, { favorites = !favorites }, { Text("Избранное", fontSize = 11.sp) }); FilterChip(sortPing, { sortPing = !sortPing }, { Text("По пингу", fontSize = 11.sp) }); Spacer(Modifier.weight(1f)); IconButton({ sortPing = true; model.pingAll() }) { Icon(Icons.Outlined.Speed, "Проверить пинг всех серверов") } } }
        item { Box { TextButton({ methods = true }) { Icon(Icons.Outlined.NetworkCheck, null, Modifier.size(18.dp)); Text(saved.preferences.ping.label, fontSize = 12.sp, modifier = Modifier.padding(start = 6.dp)); Icon(Icons.Outlined.ExpandMore, null) }; DropdownMenu(methods, { methods = false }) { PingMethod.entries.forEach { m -> DropdownMenuItem(text = { Text(m.label) }, onClick = { model.preferences(saved.preferences.copy(ping = m)); methods = false }) } } } }
        item { Text(if (saved.preferences.ping == PingMethod.TCP) "TCP — подключение к порту сервера напрямую. Это не проверка VPN." else "HTTPS — полный запрос через VPN, включая DNS и TLS. Запуск ядра и время очереди не учитываются; таймаут 5 с.", fontSize = 11.sp, color = MaterialTheme.colorScheme.onSurfaceVariant) }
        if (saved.preferences.ping != PingMethod.TCP) item {
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp), verticalAlignment = Alignment.CenterVertically) {
                Text("Сайт для теста", fontSize = 11.sp, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.weight(1f))
                PingTarget.entries.forEach { target -> FilterChip(saved.preferences.pingTarget == target, { model.preferences(saved.preferences.copy(pingTarget = target)) }, { Text(target.label, fontSize = 11.sp) }) }
            }
        }
        if (nodes.isEmpty()) item { EmptyState(Icons.Outlined.Public, "Серверов пока нет", "Добавьте подписку или измените фильтр") }
        items(nodes, key = { it.id }) { node -> Column { ServerRow(node, node.id == saved.selected, node.id in saved.favorites, ping[node.id] ?: PingResult(), { model.select(node) }, { model.favorite(node) }, { model.ping(node) }); if (node.config.optString("type") == "trusttunnel") TextButton({ editTrustTunnel(node) }) { Icon(Icons.Outlined.Edit, null, Modifier.size(16.dp)); Text("Изменить TrustTunnel", Modifier.padding(start = 6.dp), fontSize = 12.sp) } } }
        item { Text(if (saved.preferences.ping == PingMethod.TCP) "TCP: соединение с адресом сервера. До 100 мс — зелёный, до 250 — оранжевый. Доступ через VPN этот тест не проверяет." else "HTTPS: DNS, VPN-соединение, TLS и ответ сайта. До 300 мс — зелёный, до 600 — оранжевый. Обычно выше TCP; это разные замеры.", fontSize = 11.sp, color = MaterialTheme.colorScheme.onSurfaceVariant) }
        item { Text("Тестовый сайт: ${saved.preferences.pingTarget.label}. Таймаут каждой проверки — 5 секунд. На мобильной сети результат зависит также от сигнала, маршрута оператора и выбранного сайта.", fontSize = 11.sp, color = MaterialTheme.colorScheme.onSurfaceVariant) }
    }
}
@Composable private fun RulesScreen(saved: SavedState, model: MainViewModel, add: () -> Unit, edit: (Rule) -> Unit, presets: () -> Unit, routing: () -> Unit, applications: () -> Unit) {
    var search by rememberSaveable { mutableStateOf("") }
    LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(20.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        item { PageTitle("Ваши правила", "Сайты, приложения и готовые наборы") }
        item { ActionCard(Icons.Outlined.Tune, saved.preferences.routing.label, "Настройте, куда направлять трафик", routing) }
        item { ActionCard(Icons.Outlined.Apps, "Приложения", "Выберите несколько приложений с иконками", applications) }
        item { Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) { Button(add, Modifier.weight(1f), shape = RoundedCornerShape(14.dp)) { Icon(Icons.Outlined.Add, null, Modifier.size(18.dp)); Text("Правило") }; OutlinedButton(presets, Modifier.weight(1f), shape = RoundedCornerShape(14.dp)) { Icon(Icons.Outlined.AutoAwesome, null, Modifier.size(18.dp)); Text("Наборы") } } }
        item { OutlinedTextField(search, { search = it }, Modifier.fillMaxWidth(), placeholder = { Text("Поиск по правилам") }, leadingIcon = { Icon(Icons.Outlined.Search, null) }, singleLine = true, shape = RoundedCornerShape(16.dp)) }
        if (saved.rules.isEmpty()) item { EmptyState(Icons.Outlined.Route, "Выберите свой маршрут", "Добавьте готовый набор или правило для отдельного сайта. В режиме «По правилам» остальной трафик идёт напрямую.") }
        items(saved.rules.filter { it.name.contains(search, true) || it.values.any { v -> v.contains(search, true) } }.sortedByDescending { it.created }, key = { it.id }) { r ->
            Surface(shape = RoundedCornerShape(17.dp), color = MaterialTheme.colorScheme.surface) { Row(Modifier.padding(12.dp), verticalAlignment = Alignment.CenterVertically) { if (r.kind == RuleKind.APP) ApplicationIcon(r.values.first(), Modifier.size(36.dp)) else Icon(Icons.Outlined.Language, null, Modifier.size(30.dp), tint = MaterialTheme.colorScheme.secondary); Column(Modifier.weight(1f).padding(horizontal = 10.dp).clickable { edit(r) }) { Text(r.name, fontWeight = FontWeight.SemiBold, fontSize = 14.sp); Text("${r.kind.label} · ${r.values.joinToString()}", fontSize = 10.sp, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 2, overflow = TextOverflow.Ellipsis); StatusPill(if (r.vpn) "Через VPN" else "Напрямую", if (r.vpn) Green else Amber) }; IconButton({ edit(r) }, Modifier.size(36.dp)) { Icon(Icons.Outlined.Edit, "Изменить правило", Modifier.size(19.dp)) }; IconButton({ model.removeRule(r.id) }, Modifier.size(36.dp)) { Icon(Icons.Outlined.DeleteOutline, "Удалить правило", Modifier.size(19.dp)) } } }
        }
    }
}
@Composable private fun SubscriptionsScreen(saved: SavedState, model: MainViewModel, add: () -> Unit, scan: () -> Unit, importFile: () -> Unit, addTrustTunnel: () -> Unit, editTrustTunnel: (Node) -> Unit) {
    LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(20.dp), verticalArrangement = Arrangement.spacedBy(14.dp)) {
        item { PageTitle("Подписки", "Все ваши VPN в одном месте") }
        item { Button(add, Modifier.fillMaxWidth().height(49.dp), shape = RoundedCornerShape(16.dp)) { Icon(Icons.Outlined.Add, null); Text("Добавить подписку", Modifier.padding(start = 8.dp)) } }
        item { ActionCard(Icons.Outlined.VpnKey, "TrustTunnel без ссылки", "Адрес, сертификат, SNI, логин и пароль", addTrustTunnel) }
        item { Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) { OutlinedButton(scan, Modifier.weight(1f), shape = RoundedCornerShape(14.dp)) { Icon(Icons.Outlined.QrCodeScanner, null, Modifier.size(20.dp)); Text("QR-код", Modifier.padding(start = 6.dp)) }; OutlinedButton(importFile, Modifier.weight(1f), shape = RoundedCornerShape(14.dp)) { Icon(Icons.Outlined.UploadFile, null, Modifier.size(20.dp)); Text("Файл", Modifier.padding(start = 6.dp)) } } }
        if (saved.subscriptions.isEmpty()) item { EmptyState(Icons.Outlined.Link, "Ваша первая подписка", "Подойдёт ссылка провайдера, QR-код, список VPN ссылок, Base64, Clash или JSON конфигурация.") }
        items(saved.subscriptions, key = { it.id }) { sub ->
            Surface(shape = RoundedCornerShape(22.dp), color = MaterialTheme.colorScheme.surface, border = BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = .45f))) {
                Column(Modifier.padding(18.dp)) {
                    Row(verticalAlignment = Alignment.CenterVertically) { Icon(Icons.Outlined.Link, null, Modifier.size(28.dp), tint = MaterialTheme.colorScheme.secondary); Column(Modifier.weight(1f).padding(start = 12.dp)) { Text(sub.name, fontSize = 17.sp, fontWeight = FontWeight.SemiBold); Text("${sub.nodes.count { it.unsupported.isEmpty() }} доступных · ${sub.nodes.size} всего", fontSize = 12.sp, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(top = 4.dp)) }; IconButton({ model.toggleSubscriptionVisibility(sub.id) }) { Icon(if (sub.hidden) Icons.Outlined.VisibilityOff else Icons.Outlined.Visibility, if (sub.hidden) "Показать серверы подписки" else "Скрыть серверы подписки") }; IconButton({ model.removeSubscription(sub.id) }) { Icon(Icons.Outlined.DeleteOutline, "Удалить подписку") } }
                    Text(if (sub.source.startsWith("manual:")) "Профиль введён вручную" else "Обновлено ${java.text.SimpleDateFormat("dd.MM HH:mm", Locale.ROOT).format(java.util.Date(sub.updated))}", fontSize = 11.sp, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(top = 16.dp))
                    Row {
                        if (!sub.source.startsWith("manual:")) TextButton({ model.refresh(sub) }) { Icon(Icons.Outlined.Refresh, null, Modifier.size(18.dp)); Text("Обновить", Modifier.padding(start = 6.dp)) }
                        sub.nodes.singleOrNull()?.takeIf { it.config.optString("type") == "trusttunnel" }?.let { node -> TextButton({ editTrustTunnel(node) }) { Icon(Icons.Outlined.Edit, null, Modifier.size(18.dp)); Text("Изменить сервер", Modifier.padding(start = 6.dp)) } }
                    }
                }
            }
        }
        item { Text("Ссылки и ключи хранятся с шифрованием Android. Маршрутизация провайдера не заменяет ваши правила.", fontSize = 11.sp, color = MaterialTheme.colorScheme.onSurfaceVariant) }
    }
}
@Composable internal fun ToggleCard(icon: ImageVector, title: String, subtitle: String, value: Boolean, change: (Boolean) -> Unit) {
    Surface(shape = RoundedCornerShape(20.dp), color = MaterialTheme.colorScheme.surface) { Row(Modifier.fillMaxWidth().padding(16.dp), verticalAlignment = Alignment.CenterVertically) { Icon(icon, null, Modifier.size(23.dp), tint = MaterialTheme.colorScheme.secondary); Column(Modifier.weight(1f).padding(horizontal = 12.dp)) { Text(title, fontWeight = FontWeight.SemiBold, fontSize = 14.sp); Text(subtitle, fontSize = 11.sp, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(top = 4.dp)) }; Switch(value, change) } }
}
@Composable internal fun ActionCard(icon: ImageVector, title: String, subtitle: String, click: () -> Unit) {
    Surface(shape = RoundedCornerShape(20.dp), color = MaterialTheme.colorScheme.surface, modifier = Modifier.fillMaxWidth().clickable(onClick = click)) { Row(Modifier.padding(17.dp), verticalAlignment = Alignment.CenterVertically) { Icon(icon, null, Modifier.size(23.dp), tint = MaterialTheme.colorScheme.secondary); Column(Modifier.weight(1f).padding(horizontal = 12.dp)) { Text(title, fontSize = 14.sp, fontWeight = FontWeight.SemiBold); Text(subtitle, fontSize = 11.sp, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(top = 4.dp)) }; Icon(Icons.Outlined.ChevronRight, null, Modifier.size(20.dp), tint = MaterialTheme.colorScheme.onSurfaceVariant) } }
}
@Composable private fun SectionLabel(title: String) { Text(title.uppercase(), color = MaterialTheme.colorScheme.onSurfaceVariant, fontSize = 11.sp, fontWeight = FontWeight.SemiBold, modifier = Modifier.padding(top = 8.dp, bottom = 2.dp)) }
@Composable private fun PageTitle(title: String, subtitle: String) { Column(Modifier.padding(bottom = 8.dp)) { Text(title, fontSize = 25.sp, fontWeight = FontWeight.Bold); Text(subtitle, fontSize = 12.sp, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(top = 5.dp)) } }
@Composable private fun EmptyState(icon: ImageVector, title: String, subtitle: String, button: String? = null, click: () -> Unit = {}) { Column(Modifier.fillMaxWidth().padding(vertical = 32.dp, horizontal = 12.dp), horizontalAlignment = Alignment.CenterHorizontally) { Icon(icon, null, Modifier.size(40.dp), tint = MaterialTheme.colorScheme.secondary); Text(title, fontSize = 18.sp, fontWeight = FontWeight.SemiBold, modifier = Modifier.padding(top = 15.dp)); Text(subtitle, fontSize = 12.sp, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(top = 8.dp), textAlign = androidx.compose.ui.text.style.TextAlign.Center); if (button != null) TextButton(click) { Text(button) } } }
private fun speed(bytes: Long) = String.format(Locale.ROOT, "%.1f", bytes * 8.0 / 1_000_000) + " Мбит/с"
private fun elapsed(ms: Long) = (ms / 1000).coerceAtLeast(0).let { "%02d:%02d".format(it / 60, it % 60) }
