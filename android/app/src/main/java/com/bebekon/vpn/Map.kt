package com.bebekon.vpn

import androidx.compose.animation.core.*
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.gestures.detectTransformGestures
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.drawscope.withTransform
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalContext
import org.json.JSONArray
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

private data class MapCountry(val code: String, val center: Offset, val path: Path)
@Composable fun WorldMap(country: String, animations: Boolean, online: Boolean) {
    val context = LocalContext.current
    val countries by produceState<List<MapCountry>>(emptyList()) { value = withContext(Dispatchers.IO) { val data = JSONArray(context.assets.open("world.json").bufferedReader().use { it.readText().removePrefix("\uFEFF") }); data.objects().map { o ->
        val coordinates = o.getJSONArray("coordinates")
        val polygons = if (o.getString("type") == "Polygon") listOf(coordinates) else (0 until coordinates.length()).map { coordinates.getJSONArray(it) }
        val rings = polygons.flatMap { p -> (0 until p.length()).map { p.getJSONArray(it) } }.map { ring -> (0 until ring.length()).map { val point = ring.getJSONArray(it); Offset(point.getDouble(0).toFloat(), point.getDouble(1).toFloat()) } }
        val path = Path(); rings.forEach { ring -> ring.forEachIndexed { i, p -> if (i == 0) path.moveTo(p.x, p.y) else path.lineTo(p.x, p.y) }; path.close() }
        MapCountry(o.getString("code"), Offset(o.getDouble("x").toFloat(), o.getDouble("y").toFloat()), path)
    } } }
    val selected = countries.firstOrNull { it.code == country } ?: countries.firstOrNull { it.code == "SE" } ?: MapCountry("SE", Offset(15f, 62f), Path())
    val x by animateFloatAsState(selected.center.x, tween(if (animations) 700 else 0), label = "map-longitude")
    val y by animateFloatAsState(selected.center.y, tween(if (animations) 700 else 0), label = "map-latitude")
    var pan by remember(country) { mutableStateOf(Offset.Zero) }; var zoom by remember(country) { mutableFloatStateOf(1f) }
    val transition = rememberInfiniteTransition(label = "map-signal"); val pulse by transition.animateFloat(0f, 1f, infiniteRepeatable(tween(2100), RepeatMode.Restart), label = "ripple")
    val mapColor = MaterialTheme.colorScheme.secondary
    val stroke = MaterialTheme.colorScheme.outline
    Canvas(Modifier.fillMaxSize().pointerInput(country) { detectTransformGestures { _, move, scale, _ -> pan += move; zoom = (zoom * scale).coerceIn(.5f, 4f) } }) {
        val factor = size.width / 120f * zoom
        fun project(p: Offset) = Offset((p.x - x) * factor + size.width * .5f, (y - p.y) * factor + size.height * .47f) + pan
        // The map is local public-domain geometry: no trackers, API keys or online tile dependency.
        for (i in 0..(size.width / 24).toInt()) for (j in 0..(size.height / 24).toInt()) drawCircle(stroke.copy(alpha = .20f), radius = .7f, center = Offset(i * 24f, j * 24f))
        withTransform({ translate(size.width * .5f + pan.x, size.height * .47f + pan.y); scale(factor, -factor, pivot = Offset.Zero); translate(-x, -y) }) {
            countries.forEach { c ->
                drawPath(c.path, mapColor.copy(alpha = if (c.code == country) .30f else .085f))
                drawPath(c.path, stroke.copy(alpha = .65f), style = Stroke(.65f / factor))
            }
        }
        val center = project(selected.center)
        drawCircle(mapColor.copy(alpha = .12f), 28f, center)
        if (animations && online) drawCircle(mapColor.copy(alpha = (1 - pulse) * .5f), 10f + pulse * 45f, center, style = Stroke(2f))
        drawCircle(mapColor.copy(alpha = .30f), 16f, center); drawCircle(mapColor, 8f, center); drawCircle(Color.White, 3f, center)
    }
}
