package com.bebekon.vpn

import android.content.Context
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.test.platform.app.InstrumentationRegistry
import io.nekohasekai.libbox.Libbox
import androidx.compose.ui.graphics.toPixelMap
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
    @Test fun mapShowsLandAndSelectedCountry() {
        val geometry = parseWorldMap(context.assets.open("world.json").bufferedReader().use { it.readText() })
        assertTrue(geometry.any { it.code == "FR" }); assertTrue(geometry.any { it.code == "NO" })
        seed("vless://$uuid@vpn.example:443#Sweden")
        compose.waitUntil(10_000) { compose.onAllNodesWithContentDescription("Карта мира. Страна сервера: SE").fetchSemanticsNodes().isNotEmpty() }
        val pixels = compose.onNodeWithTag("world-map").captureToImage().toPixelMap()
        var land = 0
        for (y in 0 until pixels.height step 3) for (x in 0 until pixels.width step 3) {
            val color = pixels[x, y]
            if (kotlin.math.abs(color.red - 22 / 255f) < .025f && kotlin.math.abs(color.green - 75 / 255f) < .025f && kotlin.math.abs(color.blue - 121 / 255f) < .025f) land++
        }
        assertTrue("Country geometry must be visible, not just the marker", land > pixels.width * pixels.height / 9 * .02f)
    }
    @Test fun appPickerSupportsSeveralAppsAndIcons() {
        val apps = installedApps(context).take(2)
        assertEquals(2, apps.size)
        context.repo.update { SavedState(preferences = Preferences(routing = RoutingMode.RULES)) }
        compose.onNodeWithText("Правила").performClick()
        compose.onNodeWithText("Приложения").performClick()
        for (app in apps) {
            compose.onNodeWithTag("app-search").performTextClearance()
            compose.onNodeWithTag("app-search").performTextInput(app.packageName)
            compose.waitUntil(5000) { compose.onAllNodesWithTag("app-${app.packageName}").fetchSemanticsNodes().isNotEmpty() }
            compose.onNodeWithTag("app-${app.packageName}").performClick()
        }
        compose.onNodeWithTag("save-apps").performClick()
        compose.waitUntil(5000) { context.repo.state.value.rules.size == 2 }
        assertEquals(apps.map { it.packageName }.toSet(), context.repo.state.value.rules.flatMap { it.values }.toSet())
        assertTrue(context.repo.state.value.rules.all { it.kind == RuleKind.APP && it.vpn })
        compose.waitUntil(5000) { compose.onAllNodesWithContentDescription("Иконка приложения").fetchSemanticsNodes().size >= 2 }
    }
    @Test fun optionalProviderLatencyDiagnostic() {
        val path = InstrumentationRegistry.getArguments().getString("provider_fixture") ?: return
        val sub = Subscription(name = "Diagnostic", source = "", nodes = SubscriptionParser.parse(java.io.File(path).readText()))
        assertTrue(sub.nodes.any { it.country.isNotEmpty() })
        assertTrue(sub.nodes.none { it.name == "proxy" })
        NativeCore.setup(context)
        val platform = AndroidPlatform(context)
        try {
            for (node in sub.nodes.filter { it.unsupported.isEmpty() }.take(2)) {
                val tcp = runCatching { Libbox.bebekonTCPProbe(node.host, node.port, platform) }.getOrNull()
                val https = runCatching { NativeCore.probe(context, SavedState(subscriptions = listOf(sub), selected = node.id), "GET").millis }.getOrNull()
                val google = runCatching { NativeCore.probe(context, SavedState(subscriptions = listOf(sub), selected = node.id), "GET", PingTarget.GOOGLE.url).millis }.getOrNull()
                android.util.Log.i("BebekonLatencyTest", "Country=${node.country} TCP=$tcp Cloudflare=$https Google=$google")
            }
        } finally { platform.close() }
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
