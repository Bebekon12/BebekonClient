package com.bebekon.vpn

import android.app.NotificationManager
import android.content.Intent
import android.os.Build
import android.provider.Settings
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.*
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.compose.LocalLifecycleOwner

@Composable fun FullSettingsScreen(p: Preferences, model: MainViewModel, addTile: () -> Unit, routing: () -> Unit, applications: () -> Unit, presets: () -> Unit, subscriptions: () -> Unit) {
    val context = LocalContext.current
    var group by remember { mutableStateOf("Все") }; var query by remember { mutableStateOf("") }
    var logs by remember { mutableStateOf(false) }; var diagnostics by remember { mutableStateOf(false) }
    var routes by remember { mutableStateOf(false) }
    val events by model.repo.logs.collectAsState(); val state by model.saved.collectAsState()
    val routeEvents by model.repo.routingLogs.collectAsState()
    val manager = context.getSystemService(NotificationManager::class.java)
    var notificationsEnabled by remember { mutableStateOf(manager.areNotificationsEnabled()) }
    val permission = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { notificationsEnabled = manager.areNotificationsEnabled() }
    val lifecycle = LocalLifecycleOwner.current
    DisposableEffect(lifecycle) {
        val observer = LifecycleEventObserver { _, event -> if (event == Lifecycle.Event.ON_RESUME) notificationsEnabled = manager.areNotificationsEnabled() }
        lifecycle.lifecycle.addObserver(observer); onDispose { lifecycle.lifecycle.removeObserver(observer) }
    }
    fun visible(section: String, text: String) = (group == "Все" || group == section || query.isNotBlank()) && (query.isBlank() || text.contains(query, true))
    LazyColumn(Modifier.fillMaxSize().testTag("settings-list"), contentPadding = PaddingValues(20.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
        item { OutlinedTextField(query, { query = it }, Modifier.fillMaxWidth(), placeholder = { Text("Поиск настройки") }, leadingIcon = { Icon(Icons.Outlined.Search, null) }, singleLine = true, shape = RoundedCornerShape(16.dp)) }
        item { Row(Modifier.horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(7.dp)) { listOf("Все", "Оформление", "Подключение", "Проверки", "Уведомления", "Данные").forEach { s -> FilterChip(group == s, { group = s }, { Text(s, fontSize = 12.sp) }) } } }
        if (visible("Оформление", "Тема светлая тёмная система")) item { ChoiceSetting(Icons.Outlined.Palette, "Тема", "Тёмная, светлая или как в системе", p.theme, ThemeChoice.entries, { it.label }) { model.preferences(p.copy(theme = it)) } }
        if (visible("Оформление", "Плавные анимации")) item { ToggleCard(Icons.Outlined.Animation, "Плавные анимации", "Карта, световой луч и подсветка подключения", p.animations) { model.preferences(p.copy(animations = it)) } }
        if (visible("Оформление", "Местоположение карта дуга IP")) item { ToggleCard(Icons.Outlined.MyLocation, "Начало маршрута на карте", "Примерное место по обычному IP через ipwho.is. Без GPS, координаты не сохраняются", p.mapLocation) { model.preferences(p.copy(mapLocation = it)) } }
        if (visible("Подключение", "Режим маршрутизации")) item { ActionCard(Icons.Outlined.Route, "Режим маршрутизации", p.routing.label, routing) }
        if (visible("Подключение", "Приложения VPN напрямую исключения")) item { ActionCard(Icons.Outlined.Apps, "Приложения и исключения", "Через VPN / напрямую · ${effectiveAppRules(state.rules).size} правил", applications) }
        if (p.routing == RoutingMode.RULES && visible("Подключение", "Правила сайтов во всех приложениях")) item { ToggleCard(Icons.Outlined.Public, "Правила сайтов во всех приложениях", "Выключено: сайты обрабатываются только в выбранных приложениях и браузерах выбранных веб-приложений. Включено: сайты обрабатываются во всех приложениях; остальное идёт напрямую. «Напрямую» исключает приложение целиком, включая правила сайтов. Если выбраны только сайты, общий туннель нужен автоматически.", p.sitesInAllApps) { model.preferences(p.copy(sitesInAllApps = it)) } }
        if (visible("Подключение", "Готовые правила сайты")) item { ActionCard(Icons.Outlined.AutoAwesome, "Готовые правила", "Сервисы, сайты и наборы для России", presets) }
        if (visible("Подключение", "ChatGPT браузер веб-приложение")) item { ActionCard(Icons.Outlined.Public, "ChatGPT из браузера", "Добавить правила сайтов ChatGPT. Браузер нельзя исключать правилом «Напрямую»") { model.repo.presets().firstOrNull { it.first == "OpenAI / ChatGPT" }?.let { model.preset(it.second) } } }
        if (visible("Подключение", "Доступ к локальной сети LAN")) item { ToggleCard(Icons.Outlined.Wifi, "Доступ к локальной сети", "Принтеры, домашние устройства и LAN", p.allowLan) { model.preferences(p.copy(allowLan = it)) } }
        if (visible("Подключение", "Повтор подключения восстановление")) item { ToggleCard(Icons.Outlined.Refresh, "Повтор при ошибке подключения", "До 3 попыток через 5, 10 и 20 секунд. Без смены сервера", p.autoReconnect) { model.preferences(p.copy(autoReconnect = it)) } }
        if (visible("Подключение", "DNS резолвер Cloudflare Google")) item { ChoiceSetting(Icons.Outlined.Dns, "DNS-резолвер", "DoH: DNS VPN-трафика идёт через VPN. Исключённые приложения используют свою сеть", p.dnsResolver, DnsResolver.entries, { it.label }) { model.preferences(p.copy(dnsResolver = it)) } }
        if (visible("Подключение", "IPv6 IPv4 Яндекс совместимость")) item { ToggleCard(Icons.Outlined.Language, "IPv6 в туннеле", "По умолчанию используется IPv4 для совместимости. Включайте при работающем IPv6 в обычной сети и на VPN-сервере; иначе сайты могут не открываться. Приложения вне туннеля не затрагиваются.", p.ipv6) { model.preferences(p.copy(ipv6 = it)) } }
        if (visible("Подключение", "MTU размер пакета")) item { ChoiceSetting(Icons.Outlined.Tune, "MTU туннеля", "1400 — по умолчанию. 1280 может помочь в мобильной сети", p.mtu, listOf(1280, 1400, 1500), { it.toString() }) { model.preferences(p.copy(mtu = it)) } }
        if (visible("Подключение", "Постоянный VPN защита от утечек Kill Switch")) item { ActionCard(Icons.Outlined.Security, "Постоянный VPN и защита от утечек", "Настройки Android. Блокировка без VPN также блокирует приложения-исключения") { context.startActivity(Intent(Settings.ACTION_VPN_SETTINGS)) } }
        if (visible("Подключение", "Кнопка в шторке Quick Settings")) item { ActionCard(Icons.Outlined.ToggleOn, "Кнопка в шторке", "Включайте VPN из быстрых настроек", addTile) }
        if (visible("Подключение", "Батарея фоновая работа")) item { ActionCard(Icons.Outlined.BatteryFull, "Работа в фоне", "Системное управление батареей для Bebekon VPN") { context.startActivity(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, android.net.Uri.parse("package:${context.packageName}"))) } }
        if (visible("Проверки", "Метод проверки пинга HTTPS GET HEAD TCP")) item { ChoiceSetting(Icons.Outlined.Speed, "Метод проверки пинга", "HTTPS измеряет полный запрос через VPN; TCP — только доступность порта", p.ping, PingMethod.entries, { it.label }) { model.preferences(p.copy(ping = it)) } }
        if (visible("Проверки", "Адрес проверки Cloudflare Google")) item { ChoiceSetting(Icons.Outlined.Language, "Адрес проверки", "Если один сервис недоступен, выберите другой. Таймаут 5 секунд", p.pingTarget, PingTarget.entries, { it.label }) { model.preferences(p.copy(pingTarget = it)) } }
        if (visible("Проверки", "Проверить все серверы пинг")) item { ActionCard(Icons.Outlined.NetworkPing, "Проверить все серверы", "Все подписки · ${state.nodes.size} серверов", { model.pingAll() }) }
        if (visible("Проверки", "Проверить сайт DNS HTTPS диагностика")) item { ActionCard(Icons.Outlined.NetworkCheck, "Проверить сайт", "Сравнить прямой доступ и ответ через VPN") { model.siteCheck.value = SiteCheck(); diagnostics = true } }
        if (visible("Уведомления", "События подключения ошибки уведомления")) item { ToggleCard(Icons.Outlined.NotificationsActive, "События подключения", "Уведомления о подключении, отключении и ошибках", p.connectionNotifications) { model.preferences(p.copy(connectionNotifications = it)); if (it && Build.VERSION.SDK_INT >= 33 && !notificationsEnabled) permission.launch(android.Manifest.permission.POST_NOTIFICATIONS) } }
        if (visible("Уведомления", "Разрешение уведомлений звуки Android")) item { ActionCard(Icons.Outlined.Notifications, "Уведомления Android", if (notificationsEnabled) "Разрешены · настройте звуки и категории" else "Запрещены системой · нажмите, чтобы разрешить") { context.startActivity(Intent(Settings.ACTION_APP_NOTIFICATION_SETTINGS).putExtra(Settings.EXTRA_APP_PACKAGE, context.packageName)) } }
        if (visible("Уведомления", "Постоянное уведомление VPN")) item { Text("Постоянное уведомление VPN нужно для фоновой работы Android. В нём есть кнопка отключения; звук настраивается отдельно от событий.", color = MaterialTheme.colorScheme.onSurfaceVariant, fontSize = 12.sp, lineHeight = 17.sp) }
        if (visible("Данные", "Подписки импорт")) item { ActionCard(Icons.Outlined.Link, "Подписки", "Ссылки, QR-коды и файлы · ${state.subscriptions.size} подписок", subscriptions) }
        if (visible("Данные", "Обновить подписки")) item { ActionCard(Icons.Outlined.Sync, "Обновить все подписки", "Выбранный сервер сохраняется", { model.refreshAll() }) }
        if (visible("Данные", "Автоматическая проверка обновлений")) item { ToggleCard(Icons.Outlined.Update, "Проверять обновления при запуске", "Покажем окно: обновить или пропустить на этот раз", p.checkUpdates) { model.preferences(p.copy(checkUpdates = it)) } }
        if (visible("Данные", "Обновления установить версия")) item { ActionCard(Icons.Outlined.SystemUpdate, "Обновления", "Проверить и установить прямо из приложения") { context.updater.check() } }
        if (visible("Проверки", "Диагностика маршрутизации DNS Яндекс")) item { ToggleCard(Icons.Outlined.BugReport, "Диагностика маршрутизации", "Временно записывает домены, маршруты и ошибки в памяти телефона. Журнал может содержать посещённые сайты; данные доступа скрываются. Выключите после проверки.", p.routingDiagnostics) { model.repo.routingLogs.value = emptyList(); model.preferences(p.copy(routingDiagnostics = it)) } }
        if (visible("Проверки", "Журнал маршрутов DNS ошибки")) item { ActionCard(Icons.Outlined.Route, "Журнал маршрутов", if (p.routingDiagnostics) "Откройте проблемный сайт, затем скопируйте журнал · ${routeEvents.size}/200" else "Включите диагностику маршрутизации для записи") { routes = true } }
        if (visible("Данные", "Журнал подключения диагностика")) item { ActionCard(Icons.Outlined.ListAlt, "Журнал подключения", "События без адресов подписок и ключей") { logs = true } }
        if (visible("Данные", "Версия о приложении протоколы")) item { Text("Bebekon VPN ${BuildConfig.VERSION_NAME}\nAndroid 10+ · sing-box 1.14.2\nVLESS · VMess · SS · Trojan · Hysteria / Hysteria2\nНастройки сохраняются автоматически. Изменения сети применяются переподключением; активные запросы в этот момент могут прерваться.", fontSize = 12.sp, color = MaterialTheme.colorScheme.onSurfaceVariant, lineHeight = 19.sp) }
    }
    if (logs) AlertDialog(onDismissRequest = { logs = false }, title = { Text("Журнал") }, text = { LazyColumn { if (events.isEmpty()) item { Text("Событий пока нет") }; items(events) { Text(it, fontSize = 12.sp, modifier = Modifier.padding(vertical = 5.dp)) } } }, confirmButton = { TextButton({ logs = false }) { Text("Закрыть") } })
    if (diagnostics) SiteDiagnosticsDialog(model) { diagnostics = false }
    if (routes) AlertDialog(onDismissRequest = { routes = false }, title = { Text("Журнал маршрутов") }, text = {
        LazyColumn(Modifier.heightIn(max = 420.dp)) {
            if (routeEvents.isEmpty()) item { Text("Включите диагностику маршрутизации и повторите запрос в браузере. Записываются только новые подключения; журнал очищается при переключении диагностики.") }
            items(routeEvents) { Text(it, fontSize = 11.sp, modifier = Modifier.padding(vertical = 4.dp)) }
        }
    }, confirmButton = { TextButton({
        val clipboard = context.getSystemService(android.content.ClipboardManager::class.java)
        clipboard.setPrimaryClip(android.content.ClipData.newPlainText("Bebekon: маршруты", "Bebekon VPN ${BuildConfig.VERSION_NAME}\n${redactRoutingLog(events.joinToString("\n"), state)}\n${routeEvents.joinToString("\n")}"))
    }, enabled = routeEvents.isNotEmpty()) { Text("Скопировать") } }, dismissButton = { TextButton({ routes = false }) { Text("Закрыть") } })
}

@Composable private fun <T> ChoiceSetting(icon: ImageVector, title: String, subtitle: String, value: T, choices: List<T>, label: (T) -> String, choose: (T) -> Unit) {
    var expanded by remember { mutableStateOf(false) }
    Surface(shape = RoundedCornerShape(20.dp), color = MaterialTheme.colorScheme.surface) {
        Column(Modifier.fillMaxWidth().padding(16.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) { Icon(icon, null, tint = MaterialTheme.colorScheme.secondary); Text(title, Modifier.padding(start = 12.dp), fontSize = 14.sp, fontWeight = FontWeight.SemiBold) }
            Text(subtitle, Modifier.padding(top = 6.dp, bottom = 10.dp), fontSize = 11.sp, lineHeight = 16.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
            Box { OutlinedButton({ expanded = true }, Modifier.fillMaxWidth(), shape = RoundedCornerShape(13.dp)) { Text(label(value), Modifier.weight(1f), fontSize = 13.sp); Icon(Icons.Outlined.ExpandMore, null) }
                DropdownMenu(expanded, { expanded = false }) { choices.forEach { item -> DropdownMenuItem(text = { Text(label(item)) }, onClick = { expanded = false; choose(item) }) } }
            }
        }
    }
}
