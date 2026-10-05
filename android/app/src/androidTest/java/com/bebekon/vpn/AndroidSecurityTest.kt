package com.bebekon.vpn

import android.content.ComponentName
import android.content.Intent
import android.os.SystemClock
import androidx.test.platform.app.InstrumentationRegistry
import androidx.test.runner.lifecycle.ActivityLifecycleMonitorRegistry
import androidx.test.runner.lifecycle.Stage
import org.junit.Assert.*
import org.junit.Test

/** No Compose ActivityScenario: an attack deliberately crosses activity/task boundaries. */
class AndroidSecurityTest {
    private val instrumentation get() = InstrumentationRegistry.getInstrumentation()
    private val context get() = instrumentation.targetContext
    private fun shell(command: String): String = android.os.ParcelFileDescriptor.AutoCloseInputStream(
        instrumentation.uiAutomation.executeShellCommand(command)).use { it.readBytes().toString(Charsets.UTF_8) }
    private fun await(timeout: Long, condition: () -> Boolean) {
        val end = SystemClock.elapsedRealtime() + timeout
        while (!condition()) { check(SystemClock.elapsedRealtime() < end) { "Security test timed out" }; SystemClock.sleep(100) }
    }
    private fun attack() {
        shell("logcat -c")
        instrumentation.runOnMainSync {
            context.startActivity(Intent().setComponent(ComponentName("com.bebekon.vpn.test", "com.bebekon.vpn.TrafficProbeActivity"))
                .putExtra("attack_tile", true).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
        }
        await(5000) { shell("logcat -d -s BebekonSecurityTest:I *:S").contains("Private tile activity blocked=true") }
        SystemClock.sleep(1500)
    }
    @Test fun externalAppCannotStartOrStopVpnThroughTheLauncher() {
        val port = InstrumentationRegistry.getArguments().getString("fixture_port")!!.toInt()
        val node = SubscriptionParser.link("vless://3bf154da-0ee6-4c45-b5f4-8512b4a2d2bd@10.0.2.2:$port#Security%20fixture")
        context.repo.update { SavedState(subscriptions = listOf(Subscription(name = "Security fixture", source = "", nodes = listOf(node))), selected = node.id) }
        instrumentation.runOnMainSync { context.startActivity(Intent(context, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)) }
        try {
            await(5000) { var visible = false; instrumentation.runOnMainSync { visible = ActivityLifecycleMonitorRegistry.getInstance().getActivitiesInStage(Stage.RESUMED).any { it is MainActivity } }; visible }
            assertEquals(Phase.OFF, VpnController.session.value.phase)
            attack()
            assertEquals("An external intent cannot initiate a VPN connection", Phase.OFF, VpnController.session.value.phase)
            VpnController.start(context)
            await(20_000) { VpnController.session.value.phase in listOf(Phase.ON, Phase.ERROR) }
            assertEquals(VpnController.session.value.message, Phase.ON, VpnController.session.value.phase)
            val started = VpnController.session.value.started
            attack()
            assertEquals("An external intent cannot disconnect the VPN", Phase.ON, VpnController.session.value.phase)
            assertEquals(started, VpnController.session.value.started)
        } finally {
            VpnController.stop(context); await(15_000) { VpnController.session.value.phase == Phase.OFF }
            instrumentation.runOnMainSync {
                for (stage in listOf(Stage.RESUMED, Stage.STARTED, Stage.PAUSED, Stage.STOPPED))
                    ActivityLifecycleMonitorRegistry.getInstance().getActivitiesInStage(stage).toList().filter { it is MainActivity }.forEach { it.finish() }
            }
        }
    }
}
