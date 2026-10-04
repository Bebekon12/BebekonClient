package com.bebekon.vpn

import android.content.Context
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.test.platform.app.InstrumentationRegistry
import io.nekohasekai.libbox.Libbox
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

class AndroidSmokeTest {
    @get:Rule val compose = createAndroidComposeRule<MainActivity>()
    private val context: Context get() = InstrumentationRegistry.getInstrumentation().targetContext
    private val uuid = "3bf154da-0ee6-4c45-b5f4-8512b4a2d2bd"
    private fun seed(link: String) { val node = SubscriptionParser.link(link); context.repo.update { SavedState(subscriptions = listOf(Subscription(name = "Test fixture", source = "", nodes = listOf(node))), selected = node.id) } }
    private fun verifyOtherAppTraffic() {
        val status = java.util.concurrent.atomic.AtomicInteger(0)
        val reply = android.os.Messenger(android.os.Handler(android.os.Looper.getMainLooper()) { status.set(it.arg1); true })
        val connection = object : android.content.ServiceConnection {
            override fun onServiceConnected(name: android.content.ComponentName?, binder: android.os.IBinder?) { android.os.Messenger(binder).send(android.os.Message.obtain().apply { replyTo = reply }) }
            override fun onServiceDisconnected(name: android.content.ComponentName?) = Unit
        }
        val intent = android.content.Intent().setComponent(android.content.ComponentName("com.bebekon.vpn.test", "com.bebekon.vpn.TrafficProbeService"))
        assertTrue(context.bindService(intent, connection, Context.BIND_AUTO_CREATE))
        try { compose.waitUntil(15_000) { status.get() != 0 }; assertEquals("HTTPS from a separate Android UID", 200, status.get()); compose.waitUntil(5000) { VpnController.session.value.totalDown > 0 && VpnController.session.value.totalUp > 0 } } finally { context.unbindService(connection) }
    }
    private fun shell(command: String) { val descriptor = InstrumentationRegistry.getInstrumentation().uiAutomation.executeShellCommand(command); android.os.ParcelFileDescriptor.AutoCloseInputStream(descriptor).use { it.readBytes() } }
    @org.junit.After fun stopFixtureVpn() { if (VpnController.session.value.active) { VpnController.stop(context); compose.waitUntil(15_000) { VpnController.session.value.phase == Phase.OFF } } }
    @Test fun navigationAndBothThemes() {
        compose.onNodeWithText("Главная").assertIsDisplayed()
        compose.onNodeWithText("Серверы").performClick(); compose.onNodeWithText("Выбор сервера").assertIsDisplayed()
        compose.onNodeWithText("Правила").performClick(); compose.onNodeWithText("Ваши правила").assertIsDisplayed()
        compose.onNodeWithText("Подписки").performClick(); compose.onNodeWithText("Добавить подписку").assertIsDisplayed()
        compose.onNodeWithContentDescription("Настройки").performClick(); compose.onNodeWithText("Светлая").performClick()
        compose.waitUntil(5000) { context.repo.state.value.preferences.theme == ThemeChoice.LIGHT }
        compose.onNodeWithText("Кнопка в шторке").assertIsDisplayed()
        compose.onNodeWithText("Тёмная").performClick(); compose.waitUntil(5000) { context.repo.state.value.preferences.theme == ThemeChoice.DARK }
        compose.onNodeWithText("Главная").performClick(); compose.onNodeWithContentDescription("Подключить VPN").assertIsDisplayed()
    }
    @Test fun allNativeProtocolConfigsValidate() {
        NativeCore.setup(context)
        val samples = listOf(
            "vless://$uuid@vpn.example:443?security=tls&type=ws&path=%2Fvpn",
            "vless://$uuid@vpn.example:443?security=reality&type=tcp&flow=xtls-rprx-vision&sni=example.com&pbk=2xAwHsReZhHwVAuqEciHiIJZG-yXDQYmCy_iIr3fyjk&sid=01234567&fp=chrome",
            "trojan://secret@vpn.example:443?sni=example.com",
            "ss://YWVzLTEyOC1nY206c2VjcmV0@vpn.example:8388",
            "hysteria://vpn.example:443?auth=secret&upmbps=100&downmbps=100",
            "hy2://secret@vpn.example:443?obfs=salamander&obfs-password=obfs"
        )
        for (link in samples) { seed(link); Libbox.checkConfig(CoreConfig.build(context.repo.state.value, context.repo::geo)) }
        val presets = context.repo.presets(); for ((_, rules) in presets) { context.repo.update { it.copy(rules = rules, preferences = it.preferences.copy(routing = RoutingMode.RULES)) }; Libbox.checkConfig(CoreConfig.build(context.repo.state.value, context.repo::geo)) }
    }
    @Test fun encryptedStorageAndStableRefresh() {
        seed("vless://$uuid@vpn.example:443#Original")
        val id = context.repo.state.value.selected
        context.repo.add(context.repo.load("vless://$uuid@vpn.example:443#Renamed", "Renamed"))
        assertEquals(id, context.repo.state.value.selected)
        val encrypted = context.filesDir.resolve("settings.enc").readBytes().toString(Charsets.ISO_8859_1)
        assertFalse(encrypted.contains(uuid)); assertFalse(encrypted.contains("vpn.example"))
        assertEquals(context.repo.state.value, Repository(context).state.value)
    }
    @Test fun vpnConnectReloadAndStop() {
        val args = InstrumentationRegistry.getArguments()
        val port = args.getString("fixture_port")?.toIntOrNull() ?: return
        seed("vless://$uuid@10.0.2.2:$port#Local%20fixture")
        // Permission is granted only to this isolated emulator by the validation script.
        assertNull("Grant VPN consent on the isolated emulator first", android.net.VpnService.prepare(context))
        VpnController.start(context)
        compose.waitUntil(20_000) { VpnController.session.value.phase in listOf(Phase.ON, Phase.ERROR) }
        assertEquals(VpnController.session.value.message, Phase.ON, VpnController.session.value.phase)
        verifyOtherAppTraffic()
        val firstStarted = VpnController.session.value.started
        context.repo.update { it.copy(rules = listOf(Rule(name = "Fixture site", kind = RuleKind.DOMAIN, values = listOf("example.com"))), preferences = it.preferences.copy(routing = RoutingMode.RULES)) }
        VpnController.reload(context)
        compose.waitUntil(10_000) { VpnController.session.value.phase == Phase.RECONNECTING }
        compose.waitUntil(20_000) { VpnController.session.value.phase in listOf(Phase.ON, Phase.ERROR) }
        assertEquals(VpnController.session.value.message, Phase.ON, VpnController.session.value.phase)
        assertEquals(firstStarted, VpnController.session.value.started)
        verifyOtherAppTraffic()
        VpnController.stop(context); compose.waitUntil(15_000) { VpnController.session.value.phase == Phase.OFF }
        shell("cmd statusbar add-tile com.bebekon.vpn/.VpnTileService")
        shell("cmd statusbar expand-settings")
        android.service.quicksettings.TileService.requestListeningState(context, android.content.ComponentName(context, VpnTileService::class.java))
        // Adding a tile binds its service asynchronously in SystemUI.
        android.os.SystemClock.sleep(1500)
        shell("cmd statusbar click-tile com.bebekon.vpn/.VpnTileService")
        compose.waitUntil(20_000) { VpnController.session.value.phase in listOf(Phase.ON, Phase.ERROR) }
        assertEquals("Quick Settings connects the real service", Phase.ON, VpnController.session.value.phase)
        shell("cmd statusbar click-tile com.bebekon.vpn/.VpnTileService")
        compose.waitUntil(15_000) { VpnController.session.value.phase == Phase.OFF }
        shell("cmd statusbar collapse")
    }
}
