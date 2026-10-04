package com.bebekon.vpn

import androidx.compose.animation.core.*
import androidx.compose.foundation.gestures.detectTransformGestures
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.drawWithCache
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.*
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.json.JSONArray
import kotlin.math.abs

data class MapCountry(val code: String, val center: Offset, val rings: List<List<Offset>>)
fun parseWorldMap(text: String): List<MapCountry> = JSONArray(text.removePrefix("\uFEFF")).objects().map { o ->
    val coordinates = o.getJSONArray("coordinates")
    val polygons = if (o.getString("type") == "Polygon") listOf(coordinates) else (0 until coordinates.length()).map { coordinates.getJSONArray(it) }
    val rings = polygons.flatMap { polygon -> (0 until polygon.length()).map { polygon.getJSONArray(it) } }
        .map { ring -> (0 until ring.length()).map { val p = ring.getJSONArray(it); Offset(p.getDouble(0).toFloat(), p.getDouble(1).toFloat()) } }
    val code = if (o.getString("code") == "-99") when (o.optString("name")) { "France" -> "FR"; "Norway" -> "NO"; "Kosovo" -> "XK"; else -> "" } else o.getString("code")
    MapCountry(code, Offset(o.getDouble("x").toFloat(), o.getDouble("y").toFloat()), rings)
}

@Composable fun WorldMap(country: String, animations: Boolean, online: Boolean) {
    val context = LocalContext.current
    val countries by produceState<List<MapCountry>>(emptyList()) {
        value = withContext(Dispatchers.IO) { parseWorldMap(context.assets.open("world.json").bufferedReader().use { it.readText() }) }
    }
    val selected = countries.firstOrNull { it.code == country }
    val longitude by animateFloatAsState(selected?.center?.x ?: 10f, tween(if (animations) 650 else 0), label = "map-longitude")
    val latitude by animateFloatAsState(selected?.let { (it.center.y - 22f).coerceIn(-65f, 65f) } ?: 20f, tween(if (animations) 650 else 0), label = "map-latitude")
    var pan by remember(country) { mutableStateOf(Offset.Zero) }
    var zoom by remember(country) { mutableFloatStateOf(1f) }
    val transition = rememberInfiniteTransition(label = "map-signal")
    val pulse by transition.animateFloat(0f, 1f, infiniteRepeatable(tween(2100), RepeatMode.Restart), label = "ripple")
    val scheme = MaterialTheme.colorScheme
    val dark = scheme.background.luminance() < .5f
    val land = if (dark) Color(0xFF164B79) else Color(0xFFBADFFF)
    val border = if (dark) Color(0xFF29608F) else Color(0xFFEDF8FF)
    val marker = Color(0xFF169AFF)
    androidx.compose.foundation.layout.Box(Modifier.fillMaxSize().testTag("world-map")
        .semantics { contentDescription = "Карта мира" + if (selected != null) ". Страна сервера: $country" else "" }
        .pointerInput(country) { detectTransformGestures { _, move, scale, _ -> pan += move; zoom = (zoom * scale).coerceIn(.6f, 3.5f) } }
        .drawWithCache {
            val factor = size.width / (if (selected == null) 330f else 165f) * zoom
            fun project(p: Offset) = Offset((p.x - longitude) * factor + size.width * .5f, (latitude - p.y) * factor + size.height * .50f) + pan
            // Project geometry into screen coordinates; do not scale Canvas by a negative factor.
            val projected = countries.map { c -> c.code to Path().apply {
                fillType = PathFillType.EvenOdd
                c.rings.forEach { ring ->
                    ring.forEachIndexed { index, p ->
                        val point = project(p)
                        if (index == 0 || abs(p.x - ring[index - 1].x) > 180) moveTo(point.x, point.y) else lineTo(point.x, point.y)
                    }
                    close()
                }
            } }
            val center = selected?.let { project(it.center) }
            onDrawBehind {
                drawRect(Brush.radialGradient(listOf(if (dark) Color(0xFF092B4C) else Color(0xFFE5F5FF), scheme.background), center = Offset(size.width * .5f, size.height * .5f), radius = size.maxDimension * .75f))
                projected.forEach { (code, path) ->
                    drawPath(path, if (selected != null && code == country) marker.copy(alpha = if (dark) .75f else .60f) else land)
                    drawPath(path, border, style = Stroke(.7f * density))
                }
                if (center != null) {
                    drawCircle(marker.copy(alpha = .18f), 22f * density, center)
                    if (animations && online) drawCircle(marker.copy(alpha = (1 - pulse) * .5f), (8f + pulse * 24f) * density, center, style = Stroke(1.2f * density))
                    drawCircle(marker, 8f * density, center)
                    drawCircle(Color.White, 8f * density, center, style = Stroke(1.5f * density))
                    drawCircle(Color.White, 2.5f * density, center)
                }
            }
        })
}
