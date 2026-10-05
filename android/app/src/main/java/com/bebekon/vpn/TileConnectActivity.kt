package com.bebekon.vpn

import android.content.Intent
import android.net.VpnService
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.result.contract.ActivityResultContracts

/** Private consent handoff from our system-protected Quick Settings service. */
class TileConnectActivity : ComponentActivity() {
    private val consent = registerForActivityResult(ActivityResultContracts.StartActivityForResult()) {
        if (it.resultCode == RESULT_OK) connect()
        finish()
    }
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        if (savedInstanceState != null) return // ActivityResultRegistry restores the pending consent.
        if (repo.state.value.selectedNode == null) {
            startActivity(Intent(this, MainActivity::class.java)); finish(); return
        }
        val request = VpnService.prepare(this)
        if (request != null) consent.launch(request) else { connect(); finish() }
    }
    private fun connect() {
        // A delayed permission result cannot disconnect a connection started meanwhile.
        if (!VpnController.session.value.active && VpnController.session.value.phase != Phase.STOPPING && repo.state.value.selectedNode != null)
            VpnController.start(this)
    }
}
