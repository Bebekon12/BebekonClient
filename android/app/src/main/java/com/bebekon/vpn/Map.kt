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
import kotlin.math.min

data class MapCountry(val code: String, val center: Offset, val rings: List<List<Offset>>)
fun parseWorldMap(text: String): List<MapCountry> = JSONArray(text.removePrefix("\uFEFF")).objects().map { o ->
    val coordinates = o.getJSONArray("coordinates")
    val polygons = if (o.getString("type") == "Polygon") listOf(coordinates) else (0 until coordinates.length()).map { coordinates.getJSONArray(it) }
    val rings = polygons.flatMap { polygon -> (0 until polygon.length()).map { polygon.getJSONArray(it) } }
        .map { ring -> (0 until ring.length()).map { val p = ring.getJSONArray(it); Offset(p.getDouble(0).toFloat(), p.getDouble(1).toFloat()) } }
    val code = if (o.getString("code") == "-99") when (o.optString("name")) { "France" -> "FR"; "Norway" -> "NO"; "Kosovo" -> "XK"; else -> "" } else o.getString("code")
    MapCountry(code, Offset(o.getDouble("x").toFloat(), o.getDouble("y").toFloat()), rings)
}

@Composable fun WorldMap(country: String, animations: Boolean, active: Boolean, origin: OriginPoint? = null) {
    val context = LocalContext.current
    val countries by produceState<List<MapCountry>>(emptyList()) {
        value = withContext(Dispatchers.IO) { parseWorldMap(context.assets.open("world.json").bufferedReader().use { it.readText() }) }
    }
    val selected = if (country.isBlank()) null else countries.firstOrNull { it.code == country }
    val span by animateFloatAsState(mapSpan(selected?.center?.x, origin), tween(if (animations) 650 else 0), label = "map-span")
    val longitude by animateFloatAsState(selected?.center?.x ?: 10f, tween(if (animations) 650 else 0), label = "map-longitude")
    val latitude by animateFloatAsState(selected?.let { ((if (origin == null) it.center.y else (it.center.y + origin.latitude) / 2) - 22f).coerceIn(-65f, 65f) } ?: 20f, tween(if (animations) 650 else 0), label = "map-latitude")
    var pan by remember(country) { mutableStateOf(Offset.Zero) }
    var zoom by remember(country) { mutableFloatStateOf(1f) }
    val flight = if (animations && active && selected != null) key(country) {
        val transition = rememberInfiniteTransition(label = "map-signal")
        transition.animateFloat(0f, 1f, infiniteRepeatable(tween(2400, easing = LinearEasing), RepeatMode.Restart), label = "connection-flight")
    } else remember { mutableFloatStateOf(1f) }
    val scheme = MaterialTheme.colorScheme
    val dark = scheme.background.luminance() < .5f
    val land = if (dark) Color(0xFF164B79) else Color(0xFFBADFFF)
    val border = if (dark) Color(0xFF29608F) else Color(0xFFEDF8FF)
    val marker = Color(0xFF169AFF)
    androidx.compose.foundation.layout.Box(Modifier.fillMaxSize().testTag("world-map")
        .semantics { contentDescription = "Карта мира" + if (selected != null) ". Страна сервера: $country" else "" }
        .pointerInput(country) { detectTransformGestures { _, move, scale, _ -> pan += move; zoom = (zoom * scale).coerceIn(.6f, 3.5f) } }
        .drawWithCache {
            val latSpan = if (origin != null && selected != null) abs(origin.latitude - selected.center.y) + 110f else 0f
            val factor = min(size.width / span, if (latSpan > 0) size.height / latSpan else Float.MAX_VALUE) * zoom
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
            val departure = origin?.let { project(Offset(longitude + shortestLongitude(it.longitude - longitude), it.latitude)) }
            val bend = if (center != null && departure != null) Offset((departure.x + center.x) * .5f, min(departure.y, center.y) - size.minDimension * .10f) else null
            val beam = if (center != null && bend != null && departure != null) Path().apply { moveTo(departure.x, departure.y); quadraticTo(bend.x, bend.y, center.x, center.y) } else null
            fun beamPoint(t: Float): Offset {
                val u = 1f - t
                return departure!! * (u * u) + bend!! * (2f * u * t) + center!! * (t * t)
            }
            onDrawBehind {
                drawRect(Brush.radialGradient(listOf(if (dark) Color(0xFF092B4C) else Color(0xFFE5F5FF), scheme.background), center = Offset(size.width * .5f, size.height * .5f), radius = size.maxDimension * .75f))
                projected.forEach { (code, path) ->
                    drawPath(path, if (selected != null && code == country) marker.copy(alpha = if (dark) .75f else .60f) else land)
                    drawPath(path, border, style = Stroke(.7f * density))
                }
                if (center != null) {
                    if (active && beam != null && departure != null) {
                        drawPath(beam, marker.copy(alpha = if (dark) .08f else .06f), style = Stroke(12f * density, cap = StrokeCap.Round))
                        drawPath(beam, marker.copy(alpha = .20f), style = Stroke(5f * density, cap = StrokeCap.Round))
                        drawPath(beam, if (dark) Color(0xFF41C5FF) else Color(0xFF087FFF), style = Stroke(1.6f * density, cap = StrokeCap.Round))
                        drawCircle(marker.copy(alpha = .16f), 12f * density, departure)
                        drawCircle(marker, 3.5f * density, departure)
                        drawCircle(Color.White, 1.7f * density, departure)
                        if (animations) {
                            val t = flight.value
                            val head = beamPoint(t)
                            val trail = Path().apply {
                                for (i in 0..16) {
                                    val point = beamPoint((t - .15f + .15f * i / 16).coerceAtLeast(0f))
                                    if (i == 0) moveTo(point.x, point.y) else lineTo(point.x, point.y)
                                }
                            }
                            drawPath(trail, Brush.linearGradient(listOf(Color.Transparent, if (dark) Color(0xFFBAF0FF) else Color(0xFF49B7FF)), start = beamPoint((t - .15f).coerceAtLeast(0f)), end = head), style = Stroke(3f * density, cap = StrokeCap.Round))
                            drawCircle(Brush.radialGradient(listOf(marker.copy(alpha = .8f), Color.Transparent), center = head, radius = 14f * density), 14f * density, head)
                            drawCircle(Color.White, 2.6f * density, head)
                        }
                    }
                    drawCircle(marker.copy(alpha = .18f), 22f * density, center)
                    if (animations && active) drawCircle(marker.copy(alpha = (1 - flight.value) * .5f), (8f + flight.value * 24f) * density, center, style = Stroke(1.2f * density))
                    drawCircle(marker, 8f * density, center)
                    drawCircle(Color.White, 8f * density, center, style = Stroke(1.5f * density))
                    drawCircle(Color.White, 2.5f * density, center)
                }
            }
        })
}
fun shortestLongitude(delta: Float): Float = ((delta + 540f) % 360f) - 180f
fun mapSpan(destination: Float?, origin: OriginPoint?): Float = if (destination == null) 330f else
    if (origin == null) 165f else (abs(shortestLongitude(origin.longitude - destination)) * 2.4f + 40f).coerceIn(165f, 440f)
