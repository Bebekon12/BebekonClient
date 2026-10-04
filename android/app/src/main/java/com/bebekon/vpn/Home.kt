@file:OptIn(androidx.compose.material3.ExperimentalMaterial3Api::class)
package com.bebekon.vpn

import androidx.compose.animation.core.*
import androidx.compose.foundation.*
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.*
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.*
import kotlinx.coroutines.delay
import java.util.Locale

private val SignalGreen = Color(0xFF20D884)
private val SignalRed = Color(0xFFFF687A)

@Composable fun HomeScreen(saved: SavedState, session: Session, model: MainViewModel, toggle: () -> Unit, servers: () -> Unit, routing: () -> Unit, subscriptions: () -> Unit) {
    val pings by model.repo.pings.collectAsState()
    var now by remember { mutableLongStateOf(System.currentTimeMillis()) }
    val down = remember(session.started) { mutableStateListOf<Long>() }
    val up = remember(session.started) { mutableStateListOf<Long>() }
    LaunchedEffect(session.started) { while (true) { now = System.currentTimeMillis(); delay(1000) } }
    LaunchedEffect(session.down, session.up, session.phase) {
        if (session.phase == Phase.ON) { down.add(session.down); up.add(session.up); if (down.size > 28) down.removeAt(0); if (up.size > 28) up.removeAt(0) }
        else if (!session.active) { down.clear(); up.clear() }
    }
    BoxWithConstraints(Modifier.fillMaxSize()) {
        val rowHeight = (maxHeight * .103f).coerceIn(48.dp, 62.dp)
        val peek = rowHeight * 4 + 58.dp
        val markerLabelTop = ((maxHeight - peek) * .5f - maxWidth * (22f / 165f) - 20.dp).coerceAtLeast(68.dp)
        val node = saved.selectedNode
        BottomSheetScaffold(sheetPeekHeight = peek, sheetContainerColor = MaterialTheme.colorScheme.surface,
            sheetTonalElevation = 0.dp, sheetShadowElevation = 4.dp,
            sheetShape = RoundedCornerShape(topStart = 28.dp, topEnd = 28.dp),
            sheetDragHandle = { Box(Modifier.padding(top = 8.dp, bottom = 8.dp).size(44.dp, 4.dp).clip(CircleShape).background(MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = .65f))) },
            containerColor = MaterialTheme.colorScheme.background, sheetContent = {
                LazyColumn(Modifier.fillMaxWidth(), contentPadding = PaddingValues(start = 12.dp, end = 12.dp, bottom = 16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    item {
                        DashboardCard(Modifier.fillMaxWidth().height(rowHeight).clickable(onClick = servers)) {
                            Flag(node?.country.orEmpty(), Modifier.size(42.dp, 30.dp))
                            Column(Modifier.weight(1f).padding(horizontal = 12.dp)) {
                                Text(node?.name?.let(::displayName) ?: "Выбрать сервер", fontSize = 17.sp, fontWeight = FontWeight.Bold, maxLines = 1, overflow = TextOverflow.Ellipsis)
                                if (node != null) PingText(pings[node.id] ?: PingResult(), { model.ping(node) })
                                else Text("Добавьте вашу подписку", fontSize = 11.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
                            }
                            Icon(Icons.Outlined.ChevronRight, null, tint = MaterialTheme.colorScheme.onSurfaceVariant)
                        }
                    }
                    item {
                        DashboardCard(Modifier.fillMaxWidth().height(rowHeight).clickable(onClick = routing)) {
                            DashboardIcon(Icons.Outlined.SwapHoriz)
                            Column(Modifier.weight(1f).padding(horizontal = 12.dp)) {
                                Text("Маршрутизация", fontSize = 11.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
                                Text(saved.preferences.routing.label, fontSize = 15.sp, fontWeight = FontWeight.SemiBold, modifier = Modifier.padding(top = 3.dp))
                            }
                            Icon(Icons.Outlined.ChevronRight, null, tint = MaterialTheme.colorScheme.onSurfaceVariant)
                        }
                    }
                    item {
                        DashboardCard(Modifier.fillMaxWidth().height(rowHeight).testTag("home-traffic")) {
                            TrafficValue(Modifier.weight(1f), Icons.Outlined.South, "Загрузка", session.down, down, session.phase == Phase.ON)
                            DashboardDivider()
                            TrafficValue(Modifier.weight(1f), Icons.Outlined.North, "Отправка", session.up, up, session.phase == Phase.ON)
                        }
                    }
                    item {
                        DashboardCard(Modifier.fillMaxWidth().height(rowHeight)) {
                            DashboardValue(Modifier.weight(1f), Icons.Outlined.Schedule, "Сессия", if (session.started > 0 && session.active) "%02d:%02d".format((now - session.started) / 60000, (now - session.started) / 1000 % 60) else "—")
                            DashboardDivider()
                            DashboardValue(Modifier.weight(1f), Icons.Outlined.Language, "Публичный IP", session.publicIp.ifEmpty { "—" }, 12.sp)
                        }
                    }
                    item { Row(Modifier.fillMaxWidth().padding(top = 10.dp), verticalAlignment = Alignment.CenterVertically) { Text("Все серверы", fontSize = 18.sp, fontWeight = FontWeight.Bold, modifier = Modifier.weight(1f)); TextButton({ model.pingAll() }) { Icon(Icons.Outlined.Speed, null, Modifier.size(17.dp)); Text("Пинг", Modifier.padding(start = 5.dp)) } } }
                    if (saved.nodes.isEmpty()) item { Button(subscriptions, Modifier.fillMaxWidth(), shape = RoundedCornerShape(14.dp)) { Text("Добавить подписку") } }
                    items(saved.nodes, key = { "home-" + it.id }) { server -> ServerRow(server, server.id == saved.selected, server.id in saved.favorites, pings[server.id] ?: PingResult(), { model.select(server) }, { model.favorite(server) }, { model.ping(server) }) }
                }
            }) { contentPadding ->
            Box(Modifier.fillMaxSize().padding(contentPadding).clip(RoundedCornerShape(0.dp))) {
                WorldMap(node?.country.orEmpty(), saved.preferences.animations, session.phase == Phase.ON)
                Column(Modifier.align(Alignment.TopCenter).padding(top = 8.dp, start = 16.dp, end = 16.dp), horizontalAlignment = Alignment.CenterHorizontally) {
                    Surface(shape = RoundedCornerShape(22.dp), color = MaterialTheme.colorScheme.background.copy(alpha = .88f), border = BorderStroke(1.dp, MaterialTheme.colorScheme.secondary.copy(alpha = .28f))) {
                        Column(Modifier.padding(horizontal = 18.dp, vertical = 9.dp), horizontalAlignment = Alignment.CenterHorizontally) {
                            Row(verticalAlignment = Alignment.CenterVertically) {
                                val statusColor = if (session.phase == Phase.ON) SignalGreen else if (session.phase == Phase.ERROR) SignalRed else MaterialTheme.colorScheme.secondary
                                Box(Modifier.size(9.dp).clip(CircleShape).background(statusColor))
                                Text(when (session.phase) { Phase.ON -> "Подключено"; Phase.STARTING -> "Подключение…"; Phase.RECONNECTING -> "Обновляем маршрут…"; Phase.STOPPING -> "Отключение…"; Phase.ERROR -> "Ошибка подключения"; else -> "Готов к подключению" }, Modifier.padding(start = 8.dp), fontSize = 17.sp, fontWeight = FontWeight.Bold)
                            }
                            Text(when (session.phase) { Phase.ON -> if (saved.preferences.routing == RoutingMode.ALL) "Трафик через VPN" else "VPN для выбранных приложений и сайтов"; Phase.ERROR -> session.message; else -> "Ваш маршрут. Ваш интернет." }, fontSize = 11.sp, maxLines = 2, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(top = 3.dp))
                        }
                    }
                }
                if (node != null && node.country.isNotEmpty()) Surface(Modifier.align(Alignment.TopEnd).padding(top = markerLabelTop, end = 14.dp).widthIn(max = 180.dp), shape = RoundedCornerShape(14.dp), color = MaterialTheme.colorScheme.background.copy(alpha = .94f), border = BorderStroke(1.dp, MaterialTheme.colorScheme.secondary.copy(alpha = .7f))) {
                    Row(Modifier.padding(horizontal = 10.dp, vertical = 2.dp), verticalAlignment = Alignment.CenterVertically) {
                        Text(displayName(node.name), fontSize = 11.sp, fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f, fill = false).padding(end = 6.dp))
                        PingText(pings[node.id] ?: PingResult(), { model.ping(node) })
                    }
                }
                ConnectButton(session, saved.preferences.animations, toggle, Modifier.align(Alignment.BottomCenter).padding(bottom = 7.dp))
            }
        }
    }
}

@Composable private fun ConnectButton(session: Session, animations: Boolean, onClick: () -> Unit, modifier: Modifier) {
    val transition = rememberInfiniteTransition(label = "connection-halo")
    val animated by transition.animateFloat(.92f, 1.05f, infiniteRepeatable(tween(1800), RepeatMode.Reverse), label = "halo")
    val pulse = if (animations && session.active) animated else 1f
    Box(modifier.size(168.dp), contentAlignment = Alignment.Center) {
        Canvas(Modifier.fillMaxSize()) {
            val center = Offset(size.width / 2, size.height / 2)
            drawCircle(Brush.radialGradient(listOf(Color(0xFF169AFF).copy(alpha = .36f), Color.Transparent), center = center, radius = size.minDimension / 2), size.minDimension / 2 * pulse)
            drawCircle(Color(0xFF3CBCFF).copy(alpha = .65f), size.minDimension * .438f, style = Stroke(1.5.dp.toPx()))
            drawCircle(Color(0xFF95E3FF).copy(alpha = .45f), size.minDimension * .405f, style = Stroke(1.dp.toPx()))
        }
        Box(Modifier.size(134.dp).clip(CircleShape).background(Brush.verticalGradient(listOf(Color(0xFF138FFF), Color(0xFF0054ED))))
            .border(1.5.dp, Color(0xFF67CAFF), CircleShape)
            .clickable(enabled = !session.busy, role = Role.Button, onClick = onClick)
            .semantics { contentDescription = if (session.active) "Отключить VPN" else "Подключить VPN" }, contentAlignment = Alignment.Center) {
            Column(horizontalAlignment = Alignment.CenterHorizontally) {
                if (session.busy) CircularProgressIndicator(Modifier.size(43.dp), color = Color.White, strokeWidth = 2.5.dp)
                else Icon(Icons.Outlined.PowerSettingsNew, null, Modifier.size(49.dp), tint = Color.White)
                Text(when (session.phase) { Phase.STARTING -> "Подключение…"; Phase.RECONNECTING -> "Обновление…"; Phase.STOPPING -> "Отключение…"; Phase.ON -> "Отключить"; else -> "Подключить" }, fontSize = 14.sp, fontWeight = FontWeight.SemiBold, color = Color.White, modifier = Modifier.padding(top = 7.dp))
            }
        }
    }
}

@Composable private fun DashboardCard(modifier: Modifier, content: @Composable RowScope.() -> Unit) {
    Surface(modifier, shape = RoundedCornerShape(17.dp), color = MaterialTheme.colorScheme.surfaceVariant.copy(alpha = .68f), border = BorderStroke(1.dp, MaterialTheme.colorScheme.secondary.copy(alpha = .12f))) {
        Row(Modifier.padding(horizontal = 12.dp, vertical = 6.dp), verticalAlignment = Alignment.CenterVertically, content = content)
    }
}
@Composable private fun DashboardIcon(icon: ImageVector) {
    Box(Modifier.size(34.dp).clip(CircleShape).background(MaterialTheme.colorScheme.secondary.copy(alpha = .10f)), contentAlignment = Alignment.Center) { Icon(icon, null, Modifier.size(23.dp), tint = MaterialTheme.colorScheme.secondary) }
}
@Composable private fun DashboardDivider() { Box(Modifier.padding(horizontal = 8.dp).width(1.dp).height(32.dp).background(MaterialTheme.colorScheme.secondary.copy(alpha = .22f))) }
@Composable private fun DashboardValue(modifier: Modifier, icon: ImageVector, label: String, value: String, fontSize: TextUnit = 16.sp) {
    Row(modifier, verticalAlignment = Alignment.CenterVertically) {
        DashboardIcon(icon)
        Column(Modifier.weight(1f).padding(start = 8.dp)) {
            Text(label, fontSize = 10.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
            Text(value, fontSize = fontSize, fontWeight = FontWeight.Bold, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.padding(top = 4.dp))
        }
    }
}
@Composable private fun TrafficValue(modifier: Modifier, icon: ImageVector, label: String, bytes: Long, samples: List<Long>, online: Boolean) {
    Row(modifier, verticalAlignment = Alignment.CenterVertically) {
        DashboardIcon(icon)
        Column(Modifier.weight(1f).padding(start = 8.dp)) {
            Text(label, fontSize = 10.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
            Row(verticalAlignment = Alignment.Bottom) {
                Text(if (online) String.format(Locale.ROOT, "%.1f", bytes * 8.0 / 1_000_000) else "—", fontSize = 16.sp, fontWeight = FontWeight.Bold)
                if (online) Text(" Мбит/с", fontSize = 9.sp, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(bottom = 2.dp))
            }
            Sparkline(samples, Modifier.fillMaxWidth().height(10.dp))
        }
    }
}
@Composable private fun Sparkline(samples: List<Long>, modifier: Modifier) {
    val color = MaterialTheme.colorScheme.secondary
    Canvas(modifier) {
        if (samples.size < 2) { drawLine(color.copy(alpha = .3f), Offset(0f, size.height - 1), Offset(size.width, size.height - 1), 1f); return@Canvas }
        val max = samples.max().coerceAtLeast(1024).toFloat()
        val path = Path(); samples.forEachIndexed { i, v -> val x = i * size.width / (samples.size - 1); val y = size.height - (v / max * (size.height - 1)); if (i == 0) path.moveTo(x, y) else path.lineTo(x, y) }
        drawPath(path, color, style = Stroke(1.5.dp.toPx(), cap = StrokeCap.Round, join = StrokeJoin.Round))
    }
}
private fun displayName(value: String) = value.replace(Regex("[\\x{1F1E6}-\\x{1F1FF}]"), "").trim()
