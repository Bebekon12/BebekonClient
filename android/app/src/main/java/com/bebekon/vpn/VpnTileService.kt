package com.bebekon.vpn

import android.app.PendingIntent
import android.content.Intent
import android.net.VpnService
import android.os.Build
import android.service.quicksettings.Tile
import android.service.quicksettings.TileService
import kotlinx.coroutines.*

/** Quick Settings and the app use the same controller and the same real service state. */
class VpnTileService : TileService() {
    private var watching: Job? = null
    private fun render(s: Session) {
        qsTile?.apply { label = "Bebekon VPN"; state = when { s.busy -> Tile.STATE_UNAVAILABLE; s.phase == Phase.ON -> Tile.STATE_ACTIVE; else -> Tile.STATE_INACTIVE }; subtitle = when (s.phase) { Phase.ON -> s.server; Phase.STARTING, Phase.RECONNECTING -> "Подключение…"; Phase.STOPPING -> "Отключение…"; Phase.ERROR -> "Ошибка подключения"; else -> "Отключён" }; updateTile() }
    }
    override fun onTileAdded() { super.onTileAdded(); render(VpnController.session.value) }
    override fun onStartListening() {
        super.onStartListening(); watching?.cancel()
        render(VpnController.session.value)
        watching = CoroutineScope(Dispatchers.Main).launch { VpnController.session.collect(::render) }
    }
    override fun onStopListening() { watching?.cancel(); watching = null; super.onStopListening() }
    @android.annotation.SuppressLint("StartActivityAndCollapseDeprecated") // Guarded fallback for Android 10–13; PendingIntent overload starts at API 34.
    override fun onClick() {
        super.onClick()
        if (VpnController.session.value.busy) return
        if (VpnController.session.value.active) { VpnController.stop(this); return }
        unlockAndRun {
            if (VpnService.prepare(this) != null || repo.state.value.selectedNode == null) {
                val intent = Intent(this, MainActivity::class.java).putExtra("tile_connect", true).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                if (Build.VERSION.SDK_INT >= 34) startActivityAndCollapse(PendingIntent.getActivity(this, 3, intent, PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)) else @Suppress("DEPRECATION") startActivityAndCollapse(intent)
            } else VpnController.start(this)
        }
    }
}
