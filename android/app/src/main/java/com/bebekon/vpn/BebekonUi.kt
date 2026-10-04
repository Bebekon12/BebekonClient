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
private val Green = Color(0xFF20CF9A)
private val Amber = Color(0xFFFFAD47)
private val Red = Color(0xFFFF687A)
private val Dark = darkColorScheme(primary = Blue, onPrimary = Color.White, background = Color(0xFF08111F), onBackground = Color(0xFFF0F5FF), surface = Color(0xFF122037), onSurface = Color(0xFFF0F5FF), surfaceVariant = Color(0xFF182A44), onSurfaceVariant = Color(0xFF92A9CA), outline = Color(0xFF2A3E5A), secondary = Color(0xFF72AEFF), surfaceContainerHigh = Color(0xFF182A44), surfaceContainerHighest = Color(0xFF1C304C))
private val Light = lightColorScheme(primary = Color(0xFF226AEA), onPrimary = Color.White, background = Color(0xFFF0F5FC), onBackground = Color(0xFF172A46), surface = Color.White, onSurface = Color(0xFF172A46), surfaceVariant = Color(0xFFE8EFFA), onSurfaceVariant = Color(0xFF667D9B), outline = Color(0xFFCFDCEF), secondary = Color(0xFF297CF4), surfaceContainerHigh = Color(0xFFE8EFFA), surfaceContainerHighest = Color(0xFFE1EBF8))

@Composable fun BebekonApp(model: MainViewModel, toggle: () -> Unit, addTile: () -> Unit, scan: () -> Unit, importFile: () -> Unit) {
    val saved by model.saved.collectAsState(); val session by model.session.collectAsState(); val message by model.message.collectAsState(); val imported by model.importText.collectAsState(); val busy by model.busy.collectAsState()
    val isDark = when (saved.preferences.theme) { ThemeChoice.DARK -> true; ThemeChoice.LIGHT -> false; ThemeChoice.SYSTEM -> isSystemInDarkTheme() }
    var tab by rememberSaveable { mutableIntStateOf(0) }; var settings by rememberSaveable { mutableStateOf(false) }; var addSubscription by remember { mutableStateOf(false) }; var editRule by remember { mutableStateOf<Rule?>(null) }; var newRule by remember { mutableStateOf(false) }; var presets by remember { mutableStateOf(false) }; var routing by remember { mutableStateOf(false) }
    val snackbar = remember { SnackbarHostState() }
    val activity = androidx.activity.compose.LocalActivity.current
    SideEffect { activity?.let { androidx.core.view.WindowCompat.getInsetsController(it.window, it.window.decorView).apply { isAppearanceLightStatusBars = !isDark; isAppearanceLightNavigationBars = !isDark } } }
    LaunchedEffect(message) { if (message.isNotEmpty()) { snackbar.showSnackbar(message); model.message.value = "" } }
    LaunchedEffect(imported) { if (imported.isNotEmpty()) { tab = 3; addSubscription = true } }
    MaterialTheme(colorScheme = if (isDark) Dark else Light) {
        Scaffold(containerColor = MaterialTheme.colorScheme.background, snackbarHost = { SnackbarHost(snackbar) }, topBar = {
            Row(Modifier.statusBarsPadding().fillMaxWidth().padding(horizontal = 20.dp, vertical = 10.dp), verticalAlignment = Alignment.CenterVertically) {
                if (settings) IconButton({ settings = false }) { Icon(Icons.Outlined.ArrowBack, "Назад") } else Image(painterResource(R.drawable.snowman), "Снеговик Bebekon в шляпе", Modifier.size(42.dp))
                Column(Modifier.weight(1f).padding(start = 8.dp)) { Text(if (settings) "Настройки" else "Bebekon", fontSize = 21.sp, fontWeight = FontWeight.Bold); Text(if (settings) "Сделайте приложение своим" else "Ваш маршрут. Ваш интернет.", fontSize = 11.sp, color = MaterialTheme.colorScheme.onSurfaceVariant) }
                if (!settings) IconButton({ settings = true }) { Icon(Icons.Outlined.Settings, "Настройки") }
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
                    settings -> SettingsScreen(saved.preferences, model, addTile)
                    tab == 0 -> HomeScreen(saved, session, model, toggle, { tab = 1 }, { routing = true }, { tab = 3 })
                    tab == 1 -> ServersScreen(saved, model)
                    tab == 2 -> RulesScreen(saved, model, { newRule = true }, { editRule = it }, { presets = true }, { routing = true })
                    else -> SubscriptionsScreen(saved, model, { addSubscription = true }, scan, importFile)
                }
                if (busy) LinearProgressIndicator(Modifier.fillMaxWidth().align(Alignment.TopCenter), color = MaterialTheme.colorScheme.primary, trackColor = Color.Transparent)
            }
        }
        if (addSubscription) SubscriptionDialog(imported, { addSubscription = false; model.importText.value = "" }) { source, name -> model.import(source, name); addSubscription = false }
        if (newRule || editRule != null) RuleDialog(editRule, { newRule = false; editRule = null }) { model.saveRule(it); newRule = false; editRule = null }
        if (presets) PresetsDialog(model, { presets = false })
        if (routing) AlertDialog(onDismissRequest = { routing = false }, title = { Text("Маршрутизация") }, text = { Column { RoutingMode.entries.forEach { m -> Row(Modifier.fillMaxWidth().clip(RoundedCornerShape(12.dp)).clickable { model.preferences(saved.preferences.copy(routing = m)); routing = false }.padding(12.dp), verticalAlignment = Alignment.CenterVertically) { RadioButton(saved.preferences.routing == m, onClick = null); Column(Modifier.padding(start = 10.dp)) { Text(m.label, fontWeight = FontWeight.SemiBold); Text(if (m == RoutingMode.ALL) "Всё через VPN, кроме исключений LAN" else "VPN для выбранных сайтов и приложений", fontSize = 12.sp, color = MaterialTheme.colorScheme.onSurfaceVariant) } } } } }, confirmButton = { TextButton({ routing = false }) { Text("Готово") } })
    }
}

@Composable private fun HomeScreen(saved: SavedState, session: Session, model: MainViewModel, toggle: () -> Unit, servers: () -> Unit, routing: () -> Unit, subscriptions: () -> Unit) {
    val pingMap by model.repo.pings.collectAsState(); var now by remember { mutableLongStateOf(System.currentTimeMillis()) }
    LaunchedEffect(session.started) { while (true) { now = System.currentTimeMillis(); delay(1000) } }
    BoxWithConstraints(Modifier.fillMaxSize()) {
        val peek = (maxHeight * .43f).coerceIn(240.dp, 305.dp)
        BottomSheetScaffold(sheetPeekHeight = peek, sheetContainerColor = MaterialTheme.colorScheme.surface, sheetTonalElevation = 0.dp, sheetShadowElevation = 5.dp, sheetShape = RoundedCornerShape(topStart = 30.dp, topEnd = 30.dp), sheetDragHandle = { Box(Modifier.padding(top = 10.dp, bottom = 8.dp).size(36.dp, 4.dp).clip(CircleShape).background(MaterialTheme.colorScheme.outline)) }, containerColor = MaterialTheme.colorScheme.background, sheetContent = {
            LazyColumn(Modifier.fillMaxWidth(), contentPadding = PaddingValues(start = 20.dp, end = 20.dp, bottom = 20.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                item {
                    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                        Text("Ваше подключение", fontWeight = FontWeight.SemiBold, fontSize = 16.sp, modifier = Modifier.weight(1f)); StatusPill(if (session.phase == Phase.ON) "TUN · защищено" else "TUN", if (session.phase == Phase.ON) Green else MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                }
                item {
                    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                        SheetTile(Modifier.weight(1f), "Сервер", servers) {
                            Row(verticalAlignment = Alignment.CenterVertically) { Flag(saved.selectedNode?.country ?: "", Modifier.size(27.dp, 19.dp)); Text(saved.selectedNode?.name?.let(::cleanName) ?: "Выбрать", fontWeight = FontWeight.SemiBold, fontSize = 14.sp, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f).padding(start = 8.dp)); Icon(Icons.Outlined.ChevronRight, null, Modifier.size(18.dp)) }
                            saved.selectedNode?.let { node -> PingText(pingMap[node.id] ?: PingResult(), { model.ping(node) }) }
                        }
                        SheetTile(Modifier.weight(1f), "Маршрутизация", routing) {
                            Row(verticalAlignment = Alignment.CenterVertically) { Icon(Icons.Outlined.Tune, null, Modifier.size(21.dp), tint = MaterialTheme.colorScheme.secondary); Text(saved.preferences.routing.label, fontSize = 14.sp, fontWeight = FontWeight.SemiBold, modifier = Modifier.weight(1f).padding(start = 8.dp)); Icon(Icons.Outlined.ChevronRight, null, Modifier.size(18.dp)) }
                            Text(if (saved.preferences.routing == RoutingMode.ALL) "Всё через VPN" else "${saved.rules.size} правил", color = MaterialTheme.colorScheme.onSurfaceVariant, fontSize = 11.sp, modifier = Modifier.padding(top = 5.dp))
                        }
                    }
                }
                item {
                    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        Metric(Modifier.weight(1f), Icons.Outlined.South, "Загрузка", if (session.phase == Phase.ON) speed(session.down) else "—", Green)
                        Metric(Modifier.weight(1f), Icons.Outlined.North, "Отправка", if (session.phase == Phase.ON) speed(session.up) else "—", MaterialTheme.colorScheme.secondary)
                        Metric(Modifier.weight(1f), Icons.Outlined.Schedule, "Сессия", if (session.started > 0 && session.active) elapsed(now - session.started) else "—", MaterialTheme.colorScheme.onSurface)
                    }
                }
                item {
                    Row(Modifier.fillMaxWidth().clip(RoundedCornerShape(15.dp)).background(MaterialTheme.colorScheme.surfaceVariant.copy(alpha = .6f)).padding(12.dp), verticalAlignment = Alignment.CenterVertically) {
                        Icon(Icons.Outlined.Language, null, Modifier.size(18.dp), tint = MaterialTheme.colorScheme.secondary)
                        Text("Публичный IP", fontSize = 11.sp, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.weight(1f).padding(start = 8.dp))
                        Text(session.publicIp.ifEmpty { "—" }, fontSize = 12.sp, fontWeight = FontWeight.Medium)
                    }
                }
                item { Row(Modifier.fillMaxWidth().padding(top = 10.dp), verticalAlignment = Alignment.CenterVertically) { Text("Все серверы", fontSize = 18.sp, fontWeight = FontWeight.Bold, modifier = Modifier.weight(1f)); TextButton({ model.pingAll() }) { Icon(Icons.Outlined.Speed, null, Modifier.size(17.dp)); Text("Пинг", modifier = Modifier.padding(start = 5.dp)) } } }
                if (saved.nodes.isEmpty()) item { EmptyState(Icons.Outlined.Link, "Начните с подписки", "Вставьте ссылку провайдера или отсканируйте QR-код", "Добавить подписку", subscriptions) }
                items(saved.nodes, key = { "home-" + it.id }) { node -> ServerRow(node, node.id == saved.selected, node.id in saved.favorites, pingMap[node.id] ?: PingResult(), { model.select(node) }, { model.favorite(node) }, { model.ping(node) }) }
            }
        }) { contentPadding ->
            Box(Modifier.fillMaxSize().padding(contentPadding)) {
                WorldMap(saved.selectedNode?.country ?: "SE", saved.preferences.animations, session.phase == Phase.ON)
                Column(Modifier.align(Alignment.TopCenter).padding(top = 12.dp, start = 16.dp, end = 16.dp), horizontalAlignment = Alignment.CenterHorizontally) {
                    Text(when (session.phase) { Phase.ON -> "Вы под защитой"; Phase.STARTING -> "Прокладываем маршрут…"; Phase.RECONNECTING -> "Обновляем маршрут…"; Phase.STOPPING -> "Отключаем VPN…"; Phase.ERROR -> "Не удалось подключиться"; else -> "Свобода в одно касание" }, fontWeight = FontWeight.Bold, fontSize = 22.sp)
                    Text(when (session.phase) { Phase.ERROR -> session.message; Phase.ON -> if (saved.preferences.routing == RoutingMode.ALL) "Весь трафик идёт через VPN" else "VPN для сайтов и приложений по правилам"; Phase.STARTING -> "Проверяем сервер и создаём туннель"; Phase.RECONNECTING -> "Применяем выбранные настройки"; Phase.STOPPING -> "Завершаем подключение"; else -> "Выберите сервер и подключитесь" }, fontSize = 12.sp, color = if (session.phase == Phase.ERROR) Red else MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(top = 5.dp), maxLines = 2)
                }
                ConnectButton(session, saved.preferences.animations, toggle, Modifier.align(Alignment.BottomCenter).padding(bottom = 20.dp))
            }
        }
    }
}
@Composable private fun ConnectButton(session: Session, animations: Boolean, onClick: () -> Unit, modifier: Modifier) {
    val transition = rememberInfiniteTransition(label = "connection-pulse"); val animated by transition.animateFloat(.93f, 1.08f, infiniteRepeatable(tween(1900), RepeatMode.Reverse), label = "halo")
    val pulse = if (animations && session.active) animated else 1f
    Column(modifier, horizontalAlignment = Alignment.CenterHorizontally) {
        Box(Modifier.size(118.dp), contentAlignment = Alignment.Center) {
            Canvas(Modifier.fillMaxSize()) { drawCircle(Blue.copy(alpha = .09f), radius = size.minDimension / 2 * pulse); drawCircle(Blue.copy(alpha = .2f), radius = size.minDimension / 2 * .83f, style = Stroke(1.5.dp.toPx())) }
            Box(Modifier.size(88.dp).clip(CircleShape).background(Brush.linearGradient(listOf(Color(0xFF55B7FF), Blue, Color(0xFF235EDD)))).clickable(enabled = !session.busy, onClick = onClick), contentAlignment = Alignment.Center) {
                if (session.busy) CircularProgressIndicator(Modifier.size(44.dp), color = Color.White, strokeWidth = 2.dp) else Icon(Icons.Outlined.PowerSettingsNew, if (session.active) "Отключить VPN" else "Подключить VPN", Modifier.size(38.dp), tint = Color.White)
            }
        }
        Text(when (session.phase) { Phase.STARTING -> "Подключение…"; Phase.RECONNECTING -> "Обновление…"; Phase.STOPPING -> "Отключение…"; Phase.ON -> "Отключить"; else -> "Подключиться" }, fontSize = 14.sp, fontWeight = FontWeight.SemiBold)
    }
}
@Composable private fun SheetTile(modifier: Modifier, title: String, click: () -> Unit, content: @Composable ColumnScope.() -> Unit) {
    Column(modifier.clip(RoundedCornerShape(18.dp)).background(MaterialTheme.colorScheme.surfaceVariant.copy(alpha = .5f)).clickable(onClick = click).height(104.dp).padding(12.dp)) { Text(title, fontSize = 10.sp, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(bottom = 8.dp)); content() }
}
@Composable private fun Metric(modifier: Modifier, icon: ImageVector, title: String, value: String, color: Color) {
    Column(modifier.padding(vertical = 5.dp)) { Row(verticalAlignment = Alignment.CenterVertically) { Icon(icon, null, Modifier.size(13.dp), tint = MaterialTheme.colorScheme.onSurfaceVariant); Text(title, fontSize = 10.sp, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(start = 4.dp)) }; Text(value, fontSize = 18.sp, color = color, fontWeight = FontWeight.SemiBold, modifier = Modifier.padding(top = 6.dp)) }
}
@Composable fun PingText(ping: PingResult, click: () -> Unit) {
    val color = when { ping.millis != null -> if (ping.millis < 100) Green else if (ping.millis < 250) Amber else Red; ping.failed -> Red; else -> MaterialTheme.colorScheme.onSurfaceVariant }
    Row(Modifier.clip(RoundedCornerShape(6.dp)).clickable(onClick = click).padding(vertical = 4.dp), verticalAlignment = Alignment.CenterVertically) { Text(ping.label, fontSize = 11.sp, fontWeight = FontWeight.SemiBold, color = color); if (ping.running) CircularProgressIndicator(Modifier.padding(start = 5.dp).size(10.dp), color = color, strokeWidth = 1.dp) }
}
private fun cleanName(s: String) = s.replace(Regex("[\\x{1F1E6}-\\x{1F1FF}]"), "").trim()
@Composable fun Flag(code: String, modifier: Modifier = Modifier.size(34.dp, 24.dp)) {
    val context = LocalContext.current
    val bitmap = remember(code) { runCatching { context.assets.open("flags/$code.png").use { android.graphics.BitmapFactory.decodeStream(it) }.asImageBitmap() }.getOrNull() }
    if (bitmap != null) Image(bitmap, code, modifier.clip(RoundedCornerShape(4.dp))) else Icon(Icons.Outlined.Public, "Сервер", modifier, tint = MaterialTheme.colorScheme.secondary)
}
@Composable private fun StatusPill(text: String, color: Color) { Text(text, fontSize = 10.sp, color = color, fontWeight = FontWeight.SemiBold, modifier = Modifier.clip(CircleShape).background(color.copy(alpha = .12f)).padding(horizontal = 9.dp, vertical = 5.dp)) }
@Composable private fun ServerRow(node: Node, selected: Boolean, favorite: Boolean, ping: PingResult, select: () -> Unit, star: () -> Unit, probe: () -> Unit) {
    Surface(shape = RoundedCornerShape(18.dp), color = if (selected) MaterialTheme.colorScheme.primary.copy(alpha = .09f) else MaterialTheme.colorScheme.surface, border = BorderStroke(if (selected) 1.5.dp else 1.dp, if (selected) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.outline.copy(alpha = .45f)), modifier = Modifier.fillMaxWidth().clickable(onClick = select)) {
        Row(Modifier.padding(start = 13.dp, top = 12.dp, bottom = 12.dp, end = 4.dp), verticalAlignment = Alignment.CenterVertically) {
            Flag(node.country); Column(Modifier.weight(1f).padding(horizontal = 10.dp)) { Text(cleanName(node.name), fontSize = 14.sp, fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis); Text(if (node.unsupported.isEmpty()) "${node.protocol} · ${node.transport}" else "Доступен в Windows", fontSize = 10.sp, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(top = 4.dp)) }
            PingText(ping, probe); IconButton(star, Modifier.size(42.dp)) { Icon(if (favorite) Icons.Outlined.Star else Icons.Outlined.StarBorder, "Избранное", Modifier.size(19.dp), tint = if (favorite) Amber else MaterialTheme.colorScheme.onSurfaceVariant) }
        }
    }
}
@Composable private fun ServersScreen(saved: SavedState, model: MainViewModel) {
    var search by rememberSaveable { mutableStateOf("") }; var favorites by rememberSaveable { mutableStateOf(false) }; var methods by remember { mutableStateOf(false) }; var sortPing by rememberSaveable { mutableStateOf(false) }
    val ping by model.repo.pings.collectAsState()
    val nodes = saved.nodes.filter { (!favorites || it.id in saved.favorites) && it.name.contains(search, true) }.let { if (sortPing) it.sortedBy { n -> ping[n.id]?.millis ?: Int.MAX_VALUE } else it }
    LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(20.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
        item { PageTitle("Выбор сервера", "${saved.nodes.size} серверов · ${saved.subscriptions.size} подписок") }
        item { OutlinedTextField(search, { search = it }, Modifier.fillMaxWidth(), placeholder = { Text("Страна или название") }, leadingIcon = { Icon(Icons.Outlined.Search, null) }, singleLine = true, shape = RoundedCornerShape(16.dp)) }
        item { Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(6.dp), verticalAlignment = Alignment.CenterVertically) { FilterChip(favorites, { favorites = !favorites }, { Text("Избранное", fontSize = 11.sp) }); FilterChip(sortPing, { sortPing = !sortPing }, { Text("По пингу", fontSize = 11.sp) }); Spacer(Modifier.weight(1f)); IconButton({ model.pingAll() }) { Icon(Icons.Outlined.Speed, "Проверить пинг всех серверов") } } }
        item { Box { TextButton({ methods = true }) { Icon(Icons.Outlined.NetworkCheck, null, Modifier.size(18.dp)); Text(saved.preferences.ping.label, fontSize = 12.sp, modifier = Modifier.padding(start = 6.dp)); Icon(Icons.Outlined.ExpandMore, null) }; DropdownMenu(methods, { methods = false }) { PingMethod.entries.forEach { m -> DropdownMenuItem(text = { Text(m.label) }, onClick = { model.preferences(saved.preferences.copy(ping = m)); methods = false }) } } } }
        if (nodes.isEmpty()) item { EmptyState(Icons.Outlined.Public, "Серверов пока нет", "Добавьте подписку или измените фильтр") }
        items(nodes, key = { it.id }) { node -> ServerRow(node, node.id == saved.selected, node.id in saved.favorites, ping[node.id] ?: PingResult(), { model.select(node) }, { model.favorite(node) }, { model.ping(node) }) }
        item { Text("HTTPS проверяет ответ через VPN. TCP измеряет доступность адреса сервера. На каждую проверку — до 5 секунд.", fontSize = 11.sp, color = MaterialTheme.colorScheme.onSurfaceVariant) }
    }
}
@Composable private fun RulesScreen(saved: SavedState, model: MainViewModel, add: () -> Unit, edit: (Rule) -> Unit, presets: () -> Unit, routing: () -> Unit) {
    var search by rememberSaveable { mutableStateOf("") }
    LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(20.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        item { PageTitle("Ваши правила", "Сайты, приложения и готовые наборы") }
        item { ActionCard(Icons.Outlined.Tune, saved.preferences.routing.label, "Настройте, куда направлять трафик", routing) }
        item { Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) { Button(add, Modifier.weight(1f), shape = RoundedCornerShape(14.dp)) { Icon(Icons.Outlined.Add, null, Modifier.size(18.dp)); Text("Правило") }; OutlinedButton(presets, Modifier.weight(1f), shape = RoundedCornerShape(14.dp)) { Icon(Icons.Outlined.AutoAwesome, null, Modifier.size(18.dp)); Text("Наборы") } } }
        item { OutlinedTextField(search, { search = it }, Modifier.fillMaxWidth(), placeholder = { Text("Поиск по правилам") }, leadingIcon = { Icon(Icons.Outlined.Search, null) }, singleLine = true, shape = RoundedCornerShape(16.dp)) }
        if (saved.rules.isEmpty()) item { EmptyState(Icons.Outlined.Route, "Выберите свой маршрут", "Добавьте готовый набор или правило для отдельного сайта. В режиме «По правилам» остальной трафик идёт напрямую.") }
        items(saved.rules.filter { it.name.contains(search, true) || it.values.any { v -> v.contains(search, true) } }.sortedByDescending { it.created }, key = { it.id }) { r ->
            Surface(shape = RoundedCornerShape(17.dp), color = MaterialTheme.colorScheme.surface) { Row(Modifier.padding(12.dp), verticalAlignment = Alignment.CenterVertically) { Icon(if (r.kind == RuleKind.APP) Icons.Outlined.Apps else Icons.Outlined.Language, null, Modifier.size(22.dp), tint = MaterialTheme.colorScheme.secondary); Column(Modifier.weight(1f).padding(horizontal = 10.dp).clickable { edit(r) }) { Text(r.name, fontWeight = FontWeight.SemiBold, fontSize = 14.sp); Text("${r.kind.label} · ${r.values.joinToString()}", fontSize = 10.sp, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 2, overflow = TextOverflow.Ellipsis); StatusPill(if (r.vpn) "Через VPN" else "Напрямую", if (r.vpn) Green else Amber) }; IconButton({ edit(r) }, Modifier.size(36.dp)) { Icon(Icons.Outlined.Edit, "Изменить правило", Modifier.size(19.dp)) }; IconButton({ model.removeRule(r.id) }, Modifier.size(36.dp)) { Icon(Icons.Outlined.DeleteOutline, "Удалить правило", Modifier.size(19.dp)) } } }
        }
    }
}
@Composable private fun SubscriptionsScreen(saved: SavedState, model: MainViewModel, add: () -> Unit, scan: () -> Unit, importFile: () -> Unit) {
    LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(20.dp), verticalArrangement = Arrangement.spacedBy(14.dp)) {
        item { PageTitle("Подписки", "Все ваши VPN в одном месте") }
        item { Button(add, Modifier.fillMaxWidth().height(49.dp), shape = RoundedCornerShape(16.dp)) { Icon(Icons.Outlined.Add, null); Text("Добавить подписку", Modifier.padding(start = 8.dp)) } }
        item { Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) { OutlinedButton(scan, Modifier.weight(1f), shape = RoundedCornerShape(14.dp)) { Icon(Icons.Outlined.QrCodeScanner, null, Modifier.size(20.dp)); Text("QR-код", Modifier.padding(start = 6.dp)) }; OutlinedButton(importFile, Modifier.weight(1f), shape = RoundedCornerShape(14.dp)) { Icon(Icons.Outlined.UploadFile, null, Modifier.size(20.dp)); Text("Файл", Modifier.padding(start = 6.dp)) } } }
        if (saved.subscriptions.isEmpty()) item { EmptyState(Icons.Outlined.Link, "Ваша первая подписка", "Подойдёт ссылка провайдера, QR-код, список VPN ссылок, Base64, Clash или JSON конфигурация.") }
        items(saved.subscriptions, key = { it.id }) { sub ->
            Surface(shape = RoundedCornerShape(22.dp), color = MaterialTheme.colorScheme.surface, border = BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = .45f))) {
                Column(Modifier.padding(18.dp)) { Row(verticalAlignment = Alignment.CenterVertically) { Icon(Icons.Outlined.Link, null, Modifier.size(28.dp), tint = MaterialTheme.colorScheme.secondary); Column(Modifier.weight(1f).padding(start = 12.dp)) { Text(sub.name, fontSize = 17.sp, fontWeight = FontWeight.SemiBold); Text("${sub.nodes.count { it.unsupported.isEmpty() }} доступных · ${sub.nodes.size} всего", fontSize = 12.sp, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(top = 4.dp)) }; IconButton({ model.removeSubscription(sub.id) }) { Icon(Icons.Outlined.DeleteOutline, "Удалить подписку") } }; Text("Обновлено ${java.text.SimpleDateFormat("dd.MM HH:mm", Locale.ROOT).format(java.util.Date(sub.updated))}", fontSize = 11.sp, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(top = 16.dp)); TextButton({ model.refresh(sub) }) { Icon(Icons.Outlined.Refresh, null, Modifier.size(18.dp)); Text("Обновить серверы", Modifier.padding(start = 6.dp)) } }
            }
        }
        item { Text("Ссылки и ключи хранятся с шифрованием Android. Маршрутизация провайдера не заменяет ваши правила.", fontSize = 11.sp, color = MaterialTheme.colorScheme.onSurfaceVariant) }
    }
}
@Composable private fun SettingsScreen(p: Preferences, model: MainViewModel, addTile: () -> Unit) {
    val context = LocalContext.current; var logs by remember { mutableStateOf(false) }; val events by model.repo.logs.collectAsState()
    LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(20.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        item { SectionLabel("Оформление") }
        item { Surface(shape = RoundedCornerShape(20.dp), color = MaterialTheme.colorScheme.surface) { Column(Modifier.padding(16.dp)) { Text("Тема", fontWeight = FontWeight.SemiBold); Row(Modifier.fillMaxWidth().padding(top = 8.dp), horizontalArrangement = Arrangement.spacedBy(6.dp)) { ThemeChoice.entries.forEach { t -> FilterChip(p.theme == t, { model.preferences(p.copy(theme = t)) }, { Text(t.label, fontSize = 11.sp) }) } } } } }
        item { ToggleCard(Icons.Outlined.Animation, "Плавные анимации", "Движение карты и подсветка подключения", p.animations) { model.preferences(p.copy(animations = it)) } }
        item { SectionLabel("Подключение") }
        item { ToggleCard(Icons.Outlined.Wifi, "Доступ к локальной сети", "Принтеры, домашние устройства и LAN", p.allowLan) { model.preferences(p.copy(allowLan = it)) } }
        item { ActionCard(Icons.Outlined.Security, "Постоянный VPN и защита от утечек", "Включаются в системных настройках Android") { context.startActivity(Intent(Settings.ACTION_VPN_SETTINGS)) } }
        item { ActionCard(Icons.Outlined.ToggleOn, "Кнопка в шторке", "Включайте VPN из быстрых настроек", addTile) }
        item { SectionLabel("О приложении") }
        item { ActionCard(Icons.Outlined.ListAlt, "Журнал подключения", "События без адресов подписок и ключей") { logs = true } }
        item { ActionCard(Icons.Outlined.SystemUpdate, "Обновления", "Релизы Bebekon Android") { context.startActivity(Intent(Intent.ACTION_VIEW, android.net.Uri.parse("https://github.com/Bebekon12/BebekonClient/releases"))) } }
        item { Text("Bebekon VPN ${BuildConfig.VERSION_NAME}\nAndroid 10+ · sing-box 1.14.2\nVLESS · VMess · SS · Trojan · Hysteria / Hysteria2", fontSize = 12.sp, color = MaterialTheme.colorScheme.onSurfaceVariant, lineHeight = 20.sp) }
    }
    if (logs) AlertDialog(onDismissRequest = { logs = false }, title = { Text("Журнал") }, text = { LazyColumn { if (events.isEmpty()) item { Text("Событий пока нет") }; items(events) { Text(it, fontSize = 12.sp, modifier = Modifier.padding(vertical = 5.dp)) } } }, confirmButton = { TextButton({ logs = false }) { Text("Закрыть") } })
}
@Composable private fun ToggleCard(icon: ImageVector, title: String, subtitle: String, value: Boolean, change: (Boolean) -> Unit) {
    Surface(shape = RoundedCornerShape(20.dp), color = MaterialTheme.colorScheme.surface) { Row(Modifier.fillMaxWidth().padding(16.dp), verticalAlignment = Alignment.CenterVertically) { Icon(icon, null, Modifier.size(23.dp), tint = MaterialTheme.colorScheme.secondary); Column(Modifier.weight(1f).padding(horizontal = 12.dp)) { Text(title, fontWeight = FontWeight.SemiBold, fontSize = 14.sp); Text(subtitle, fontSize = 11.sp, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(top = 4.dp)) }; Switch(value, change) } }
}
@Composable private fun ActionCard(icon: ImageVector, title: String, subtitle: String, click: () -> Unit) {
    Surface(shape = RoundedCornerShape(20.dp), color = MaterialTheme.colorScheme.surface, modifier = Modifier.fillMaxWidth().clickable(onClick = click)) { Row(Modifier.padding(17.dp), verticalAlignment = Alignment.CenterVertically) { Icon(icon, null, Modifier.size(23.dp), tint = MaterialTheme.colorScheme.secondary); Column(Modifier.weight(1f).padding(horizontal = 12.dp)) { Text(title, fontSize = 14.sp, fontWeight = FontWeight.SemiBold); Text(subtitle, fontSize = 11.sp, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(top = 4.dp)) }; Icon(Icons.Outlined.ChevronRight, null, Modifier.size(20.dp), tint = MaterialTheme.colorScheme.onSurfaceVariant) } }
}
@Composable private fun SectionLabel(title: String) { Text(title.uppercase(), color = MaterialTheme.colorScheme.onSurfaceVariant, fontSize = 11.sp, fontWeight = FontWeight.SemiBold, modifier = Modifier.padding(top = 8.dp, bottom = 2.dp)) }
@Composable private fun PageTitle(title: String, subtitle: String) { Column(Modifier.padding(bottom = 8.dp)) { Text(title, fontSize = 25.sp, fontWeight = FontWeight.Bold); Text(subtitle, fontSize = 12.sp, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(top = 5.dp)) } }
@Composable private fun EmptyState(icon: ImageVector, title: String, subtitle: String, button: String? = null, click: () -> Unit = {}) { Column(Modifier.fillMaxWidth().padding(vertical = 32.dp, horizontal = 12.dp), horizontalAlignment = Alignment.CenterHorizontally) { Icon(icon, null, Modifier.size(40.dp), tint = MaterialTheme.colorScheme.secondary); Text(title, fontSize = 18.sp, fontWeight = FontWeight.SemiBold, modifier = Modifier.padding(top = 15.dp)); Text(subtitle, fontSize = 12.sp, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(top = 8.dp), textAlign = androidx.compose.ui.text.style.TextAlign.Center); if (button != null) TextButton(click) { Text(button) } } }
private fun speed(bytes: Long) = String.format(Locale.ROOT, "%.1f", bytes * 8.0 / 1_000_000) + " Мбит/с"
private fun elapsed(ms: Long) = (ms / 1000).coerceAtLeast(0).let { "%02d:%02d".format(it / 60, it % 60) }
