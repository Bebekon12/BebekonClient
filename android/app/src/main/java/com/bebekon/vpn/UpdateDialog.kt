package com.bebekon.vpn

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.SystemUpdate
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.Alignment
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import java.util.Locale

@Composable fun UpdateDialog(updater: AppUpdater) {
    val state by updater.state.collectAsState()
    val activity = androidx.activity.compose.LocalActivity.current
    LaunchedEffect(state.phase, state.installAfterDownload) { if (state.phase == UpdatePhase.READY && state.installAfterDownload && activity != null) updater.install(activity) }
    if (!state.visible) return
    val downloading = state.phase == UpdatePhase.DOWNLOADING
    AlertDialog(onDismissRequest = { if (!downloading) updater.dismiss() }, shape = RoundedCornerShape(26.dp), icon = { Icon(Icons.Outlined.SystemUpdate, null, tint = MaterialTheme.colorScheme.secondary, modifier = Modifier.size(32.dp)) },
        title = { Text(when (state.phase) { UpdatePhase.CHECKING -> "Проверяем обновления"; UpdatePhase.DOWNLOADING -> "Загружаем обновление"; UpdatePhase.CURRENT -> "Всё обновлено"; UpdatePhase.ERROR -> "Попробуем ещё раз"; UpdatePhase.READY, UpdatePhase.PERMISSION -> "Готово к установке"; else -> "Новая версия Bebekon" }, fontWeight = FontWeight.Bold) },
        text = { Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
            state.info?.let { Text("Версия ${it.version}", fontSize = 16.sp, fontWeight = FontWeight.SemiBold) }
            when (state.phase) {
                UpdatePhase.CHECKING -> Box(Modifier.fillMaxWidth().padding(14.dp), contentAlignment = Alignment.Center) { CircularProgressIndicator() }
                UpdatePhase.DOWNLOADING -> {
                    val total = state.info?.bytes ?: 1
                    LinearProgressIndicator(progress = { (state.received.toFloat() / total).coerceIn(0f, 1f) }, modifier = Modifier.fillMaxWidth())
                    Text(String.format(Locale.ROOT, "%.1f / %.1f МБ · %d%%", state.received / 1048576.0, total / 1048576.0, state.received * 100 / total), fontSize = 12.sp)
                    Text("После загрузки Android предложит установку. Ваши подписки и правила сохранятся.", fontSize = 12.sp)
                }
                UpdatePhase.PERMISSION -> Text("Разрешите Bebekon VPN устанавливать обновления в системных настройках. Это нужно только в первый раз.")
                UpdatePhase.AVAILABLE -> Text("Приложение само скачает и проверит APK. Затем подтвердите установку в Android — переходить в браузер не нужно.")
                else -> Text(state.message.ifEmpty { "APK проверен. Можно установить поверх текущей версии." })
            }
        } },
        confirmButton = { when (state.phase) {
            UpdatePhase.AVAILABLE -> Button(updater::download, shape = RoundedCornerShape(14.dp)) { Text("Обновить") }
            UpdatePhase.READY, UpdatePhase.PERMISSION -> Button({ activity?.let(updater::install) }, shape = RoundedCornerShape(14.dp)) { Text(if (state.phase == UpdatePhase.PERMISSION) "Разрешить установку" else "Установить") }
            UpdatePhase.ERROR -> Button({ if (state.info != null) updater.download() else updater.check() }) { Text("Повторить") }
            UpdatePhase.DOWNLOADING -> TextButton(updater::cancelDownload) { Text("Отменить загрузку") }
            else -> TextButton(updater::dismiss) { Text("Закрыть") }
        } }, dismissButton = { if (state.phase == UpdatePhase.AVAILABLE) TextButton(updater::dismiss) { Text("Позже") } })
}
