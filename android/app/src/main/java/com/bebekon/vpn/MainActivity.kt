package com.bebekon.vpn

import android.app.StatusBarManager
import android.content.ComponentName
import android.content.Intent
import android.graphics.drawable.Icon
import android.net.VpnService
import android.os.Build
import android.os.Bundle
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.activity.viewModels
import com.journeyapps.barcodescanner.ScanContract
import com.journeyapps.barcodescanner.ScanOptions

class MainActivity : ComponentActivity() {
    private val model: MainViewModel by viewModels()
    private val permission = registerForActivityResult(ActivityResultContracts.StartActivityForResult()) { if (it.resultCode == RESULT_OK) startPrepared() else model.message.value = "Подключение отменено" }
    private val notifications = registerForActivityResult(ActivityResultContracts.RequestPermission()) { }
    private val scan = registerForActivityResult(ScanContract()) { it.contents?.let { text -> model.importText.value = text } }
    private val importFile = registerForActivityResult(ActivityResultContracts.OpenDocument()) { uri -> if (uri != null) model.task { contentResolver.openInputStream(uri)?.use { val bytes = it.readLimited(4 * 1024 * 1024); model.importText.value = bytes.toString(Charsets.UTF_8) } } }
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState); enableEdgeToEdge(); model.receive(intent)
        setContent { BebekonApp(model, ::toggle, ::addTile, { scan.launch(ScanOptions().setPrompt("Сканируйте QR-код VPN или подписки").setBeepEnabled(false).setOrientationLocked(false)) }, { importFile.launch(arrayOf("text/*", "application/json", "application/octet-stream")) }) }
        if (intent.getBooleanExtra("tile_connect", false) && savedInstanceState == null) { intent.removeExtra("tile_connect"); if (repo.state.value.selectedNode != null) toggle() else model.message.value = "Сначала добавьте подписку" }
    }
    override fun onNewIntent(intent: Intent) { super.onNewIntent(intent); setIntent(intent); model.receive(intent); if (intent.getBooleanExtra("tile_connect", false)) { intent.removeExtra("tile_connect"); toggle() } }
    private fun toggle() {
        if (VpnController.session.value.busy) return
        if (VpnController.session.value.active) { VpnController.stop(this); return }
        if (repo.state.value.selectedNode == null) { model.message.value = "Добавьте подписку на вкладке «Подписки»"; return }
        val consent = VpnService.prepare(this); if (consent != null) permission.launch(consent) else startPrepared()
    }
    private fun startPrepared() { if (Build.VERSION.SDK_INT >= 33 && checkSelfPermission(android.Manifest.permission.POST_NOTIFICATIONS) != android.content.pm.PackageManager.PERMISSION_GRANTED) notifications.launch(android.Manifest.permission.POST_NOTIFICATIONS); VpnController.start(this) }
    private fun addTile() {
        if (Build.VERSION.SDK_INT >= 33) getSystemService(StatusBarManager::class.java).requestAddTileService(ComponentName(this, VpnTileService::class.java), "Bebekon VPN", Icon.createWithResource(this, R.drawable.ic_vpn), mainExecutor) { result -> Toast.makeText(this, if (result == StatusBarManager.TILE_ADD_REQUEST_RESULT_TILE_ADDED || result == StatusBarManager.TILE_ADD_REQUEST_RESULT_TILE_ALREADY_ADDED) "Плитка добавлена" else "Плитку можно добавить через редактор шторки", Toast.LENGTH_LONG).show() }
        else Toast.makeText(this, "Откройте шторку → карандаш → перетащите Bebekon VPN в быстрые настройки", Toast.LENGTH_LONG).show()
    }
}
