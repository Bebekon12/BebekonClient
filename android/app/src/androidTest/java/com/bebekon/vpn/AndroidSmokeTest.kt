package com.bebekon.vpn

import android.content.Context
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.test.platform.app.InstrumentationRegistry
import io.nekohasekai.libbox.Libbox
import androidx.compose.ui.graphics.toPixelMap
import androidx.activity.compose.setContent
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

class AndroidSmokeTest {
    @get:Rule val compose = createAndroidComposeRule<MainActivity>()
    private val context: Context get() = InstrumentationRegistry.getInstrumentation().targetContext
    private val uuid = "3bf154da-0ee6-4c45-b5f4-8512b4a2d2bd"
    private fun seed(link: String) { val node = SubscriptionParser.link(link); context.repo.update { SavedState(subscriptions = listOf(Subscription(name = "Test fixture", source = "", nodes = listOf(node))), selected = node.id) } }
    private fun verifyOtherAppTraffic(expectVpn: Boolean = true, stream: Boolean = false, url: String = "https://example.com/", routeMatch: String? = null, ipv4Checks: Boolean = false) {
        if (routeMatch != null) shell("logcat -c")
        val status = java.util.concurrent.atomic.AtomicInteger(0)
        val transport = java.util.concurrent.atomic.AtomicInteger(-1)
        val checks = java.util.concurrent.atomic.AtomicReference<android.os.Bundle>()
        val reply = android.os.Messenger(android.os.Handler(android.os.Looper.getMainLooper()) { checks.set(it.data); transport.set(it.arg2); status.set(it.arg1); true })
        val connection = object : android.content.ServiceConnection {
            override fun onServiceConnected(name: android.content.ComponentName?, binder: android.os.IBinder?) { android.os.Messenger(binder).send(android.os.Message.obtain().apply { replyTo = reply; arg2 = if (ipv4Checks) 2 else if (stream) 1 else 0; data = android.os.Bundle().apply { putString("url", url) } }) }
            override fun onServiceDisconnected(name: android.content.ComponentName?) = Unit
        }
        val intent = android.content.Intent().setComponent(android.content.ComponentName("com.bebekon.vpn.test", "com.bebekon.vpn.TrafficProbeService"))
        assertTrue(context.bindService(intent, connection, Context.BIND_AUTO_CREATE))
        try { compose.waitUntil(20_000) { status.get() != 0 }; assertEquals("Traffic from a separate Android UID", 200, status.get()); assertEquals("Actual Android VPN transport", if (expectVpn) 1 else 0, transport.get()); if (expectVpn) compose.waitUntil(5000) { VpnController.session.value.totalDown > 0 && VpnController.session.value.totalUp > 0 } } finally { context.unbindService(connection) }
        if (ipv4Checks) {
            val evidence = checks.get()
            assertTrue("Android VPN has no IPv6 address or DNS server", evidence.getBoolean("ipv4Only"))
            assertTrue("Native DNS still returns A records", evidence.getInt("aAnswers") > 0)
            assertEquals("Native DNS suppresses AAAA in IPv4 mode", 0, evidence.getInt("aaaaAnswers", -1))
            assertTrue("Android blocks cached literal IPv6 without falling through to physical networking", evidence.getBoolean("ipv6Blocked"))
        }
        if (routeMatch != null) {
            compose.waitUntil(5000) { shellOutput("logcat -d -s BebekonCoreTest:D *:S").contains(routeMatch) }
            context.cacheDir.resolve("routing-evidence.txt").appendText("Expected: $routeMatch\n" + shellOutput("logcat -d -s BebekonCoreTest:D *:S") + "\n")
        }
    }
    private fun shellOutput(command: String): String { val descriptor = InstrumentationRegistry.getInstrumentation().uiAutomation.executeShellCommand(command); return android.os.ParcelFileDescriptor.AutoCloseInputStream(descriptor).use { it.readBytes().toString(Charsets.UTF_8) } }
    private fun shell(command: String) { shellOutput(command) }
    @org.junit.After fun stopFixtureVpn() { if (VpnController.session.value.active) { VpnController.stop(context); compose.waitUntil(15_000) { VpnController.session.value.phase == Phase.OFF } } }
    @Test fun navigationAndBothThemes() {
        context.repo.update { it.copy(preferences = it.preferences.copy(theme = ThemeChoice.DARK)) }
        compose.onNodeWithText("Главная").assertIsDisplayed()
        compose.onNodeWithText("Серверы").performClick(); compose.onNodeWithText("Выбор сервера").assertIsDisplayed()
        compose.onNodeWithText("Правила").performClick(); compose.onNodeWithText("Ваши правила").assertIsDisplayed()
        compose.onNodeWithText("Подписки").performClick(); compose.onNodeWithText("Добавить подписку").assertIsDisplayed()
        compose.onNodeWithContentDescription("Настройки").performClick(); compose.onNodeWithText("Тёмная").performClick(); compose.onNodeWithText("Светлая").performClick()
        compose.waitUntil(5000) { context.repo.state.value.preferences.theme == ThemeChoice.LIGHT }
        compose.onNodeWithTag("settings-list").performScrollToNode(hasText("Кнопка в шторке")); compose.onNodeWithText("Кнопка в шторке").assertIsDisplayed()
        compose.onNodeWithTag("settings-list").performScrollToNode(hasText("Тема")); compose.onNodeWithText("Светлая").performClick(); compose.onNodeWithText("Тёмная").performClick(); compose.waitUntil(5000) { context.repo.state.value.preferences.theme == ThemeChoice.DARK }
        compose.onNodeWithText("Главная").performClick(); compose.onNodeWithContentDescription("Подключить VPN").assertIsDisplayed()
    }
    @Test fun retriesCanBeCancelledWithoutRestarting() {
        seed("vless://$uuid@127.0.0.1:9#Unavailable")
        VpnController.start(context)
        compose.waitUntil(20_000) { VpnController.session.value.phase == Phase.RECONNECTING }
        assertTrue(VpnController.session.value.message.contains("Повтор"))
        VpnController.stop(context); compose.waitUntil(10_000) { VpnController.session.value.phase == Phase.OFF }
        android.os.SystemClock.sleep(6500)
        assertEquals(Phase.OFF, VpnController.session.value.phase)
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
        compose.onNodeWithText("Приложения").performClick()
        compose.onNodeWithTag("app-search").performTextInput(apps.first().packageName)
        compose.waitUntil(5000) { compose.onAllNodesWithTag("app-${apps.first().packageName}").fetchSemanticsNodes().isNotEmpty() }
        compose.onNodeWithTag("app-${apps.first().packageName}").assertIsOn().performClick()
        compose.onNodeWithTag("save-apps").performClick()
        compose.waitUntil(5000) { context.repo.state.value.rules.size == 1 }
        assertEquals(listOf(apps.last().packageName), context.repo.state.value.rules.single().values)
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
                val yandex = kotlinx.coroutines.runBlocking { checkSite(context, SavedState(subscriptions = listOf(sub), selected = node.id), "https://ya.ru/") }
                android.util.Log.i("BebekonSiteTest", "Yandex country=${node.country} direct=${yandex.direct} vpn=${yandex.vpn}")
            }
        } finally { platform.close() }
    }
    @Test fun optionalUpdateDownloadAndSystemInstaller() {
        val path = InstrumentationRegistry.getArguments().getString("update_fixture") ?: return
        val fixture = java.io.File(path)
        val hash = java.security.MessageDigest.getInstance("SHA-256").let { digest -> fixture.inputStream().use { input -> val buffer = ByteArray(65536); while (true) { val count = input.read(buffer); if (count < 0) break; digest.update(buffer, 0, count) } }; digest.digest().joinToString("") { "%02x".format(it) } }
        val fixtureVersion = context.packageManager.getPackageArchiveInfo(fixture.absolutePath, 0)?.versionName ?: error("Update fixture APK metadata is missing")
        assertTrue("Use an update fixture newer than the installed client", AndroidRelease.newer(fixtureVersion, BuildConfig.VERSION_NAME))
        val release = json("name" to "Bebekon VPN · Android $fixtureVersion", "tag_name" to "v0.1.16", "assets" to array(listOf(json("name" to "Bebekon-Android.apk", "digest" to "sha256:$hash", "size" to fixture.length(), "browser_download_url" to "https://github.com/Bebekon12/BebekonClient/releases/download/v0.1.16/Bebekon-Android.apk")))).toString()
        val updater = AppUpdater(context, { release }, { fixture.inputStream() }, automatic = false)
        compose.runOnUiThread { compose.activity.setContent { androidx.compose.material3.MaterialTheme { UpdateDialog(updater) } }; updater.check() }
        compose.waitUntil(10_000) { updater.state.value.phase == UpdatePhase.AVAILABLE }
        compose.onNodeWithText("Позже").performClick()
        assertFalse(updater.state.value.visible)
        compose.runOnUiThread { updater.check() }
        compose.waitUntil(10_000) { updater.state.value.phase == UpdatePhase.AVAILABLE }
        compose.onNodeWithText("Обновить").performClick()
        compose.waitUntil(30_000) { updater.state.value.phase in listOf(UpdatePhase.READY, UpdatePhase.PERMISSION, UpdatePhase.ERROR) }
        assertNotEquals(updater.state.value.message, UpdatePhase.ERROR, updater.state.value.phase)
        val downloaded = context.cacheDir.resolve("updates/bebekon-update.apk")
        assertEquals(fixture.length(), downloaded.length())
        val intent = updateInstallIntent(context, downloaded)
        assertEquals("content", intent.data?.scheme)
        assertTrue(intent.flags and android.content.Intent.FLAG_GRANT_READ_URI_PERMISSION != 0)
        assertNotNull(context.contentResolver.openInputStream(intent.data!!)?.use { it.read() })
        assertTrue(runCatching { verifyUpdateApk(context, java.io.File(context.applicationInfo.sourceDir), updater.state.value.info!!) }.isFailure)
        // Leave Android's permission/installer screen without accepting installation during the suite.
        shell("input keyevent 4")
        downloaded.delete()
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
        context.repo.update { it.copy(preferences = it.preferences.copy(ipv6 = true)) }
        Libbox.checkConfig(CoreConfig.build(context.repo.state.value, context.repo::geo))
        val presets = context.repo.presets(); for ((_, rules) in presets) { context.repo.update { it.copy(rules = rules, preferences = it.preferences.copy(routing = RoutingMode.RULES)) }; Libbox.checkConfig(CoreConfig.build(context.repo.state.value, context.repo::geo)) }
        context.repo.update { it.copy(rules = listOf(Rule(name = "Browser", kind = RuleKind.APP, values = listOf("com.yandex.browser"))), preferences = it.preferences.copy(routing = RoutingMode.RULES)) }
        Libbox.checkConfig(CoreConfig.build(context.repo.state.value, context.repo::geo))
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
        context.cacheDir.resolve("routing-evidence.txt").delete()
        context.repo.routingLogs.value = emptyList()
        context.repo.update { it.copy(preferences = it.preferences.copy(routingDiagnostics = true)) }
        // Permission is granted only to this isolated emulator by the validation script.
        assertNull("Grant VPN consent on the isolated emulator first", android.net.VpnService.prepare(context))
        VpnController.start(context)
        compose.waitUntil(20_000) { VpnController.session.value.phase in listOf(Phase.ON, Phase.ERROR) }
        assertEquals(VpnController.session.value.message, Phase.ON, VpnController.session.value.phase)
        val site = kotlinx.coroutines.runBlocking { checkSite(context, context.repo.state.value, PingTarget.GOOGLE.url) }
        assertEquals("Direct diagnostic request", "HTTP 204", site.direct)
        assertEquals("VPN diagnostic request", "HTTP 204", site.vpn)
        verifyOtherAppTraffic()
        val firstBytes = VpnController.session.value.totalDown
        // App-only routing: a non-selected browser must retain its physical network and DNS.
        fun reloadRules(rules: List<com.bebekon.vpn.Rule>) {
            context.repo.update { it.copy(rules = rules, preferences = it.preferences.copy(routing = RoutingMode.RULES)) }
            VpnController.reload(context)
            compose.waitUntil(10_000) { VpnController.session.value.phase == Phase.RECONNECTING }
            compose.waitUntil(20_000) { VpnController.session.value.phase in listOf(Phase.ON, Phase.ERROR) }
            assertEquals(VpnController.session.value.message, Phase.ON, VpnController.session.value.phase)
        }
        reloadRules(listOf(com.bebekon.vpn.Rule(name = "Chrome", kind = RuleKind.APP, values = listOf("com.android.chrome"))))
        verifyOtherAppTraffic(expectVpn = false)
        // Stored app-only rules can become domain rules at runtime for recognizable WebAPKs.
        // That conversion previously widened the VPN to every unselected Android UID.
        assertEquals("com.android.chrome", webApp(context, "com.bebekon.webapkfixture")?.browser)
        reloadRules(listOf(com.bebekon.vpn.Rule(name = "Web app", kind = RuleKind.APP, values = listOf("com.bebekon.webapkfixture"))))
        verifyOtherAppTraffic(expectVpn = false)
        reloadRules(listOf(com.bebekon.vpn.Rule(name = "Web app", kind = RuleKind.APP, values = listOf("com.bebekon.webapkfixture")), com.bebekon.vpn.Rule(name = "Native app", kind = RuleKind.APP, values = listOf("com.bebekon.vpn.test"))))
        verifyOtherAppTraffic(); verifyOtherAppTraffic(stream = true)
        // A browser implicitly captured by a WebAPK must use VPN only for selected sites.
        // Probe from that browser's real separate UID, including a native-app mixed selection.
        val webProbe = com.bebekon.vpn.Rule(name = "Web probe", kind = RuleKind.APP, values = listOf("com.bebekon.webapkfixture.probe"))
        reloadRules(listOf(webProbe))
        verifyOtherAppTraffic(routeMatch = "domain_suffix=example.com => route(vpn)")
        verifyOtherAppTraffic(url = "https://example.org/", routeMatch = "package_name=com.bebekon.vpn.test => route(direct)")
        reloadRules(listOf(webProbe, com.bebekon.vpn.Rule(name = "Native Chrome", kind = RuleKind.APP, values = listOf("com.android.chrome"))))
        verifyOtherAppTraffic(routeMatch = "domain_suffix=example.com => route(vpn)")
        verifyOtherAppTraffic(url = "https://example.org/", routeMatch = "package_name=com.bebekon.vpn.test => route(direct)")
        reloadRules(listOf(webProbe, com.bebekon.vpn.Rule(name = "Explicit full browser", kind = RuleKind.APP, values = listOf("com.bebekon.vpn.test"))))
        verifyOtherAppTraffic(url = "https://example.org/", routeMatch = "package_name=com.bebekon.vpn.test => route(vpn)")
        compose.waitUntil(5000) { context.repo.routingLogs.value.any { it.contains("package_name=com.bebekon.vpn.test => route(vpn)") } }
        assertTrue(context.repo.routingLogs.value.size <= 200)
        assertFalse(context.repo.routingLogs.value.any { it.contains(uuid) })
        // Regression: adding a preset/site to an app selection must not put unchecked apps
        // into the VPN network (the Yandex/Gosuslugi report). Probe from a separate UID.
        reloadRules(listOf(com.bebekon.vpn.Rule(name = "Chrome", kind = RuleKind.APP, values = listOf("com.android.chrome")), com.bebekon.vpn.Rule(name = "VPN site", kind = RuleKind.DOMAIN, values = listOf("example.com"))))
        verifyOtherAppTraffic(expectVpn = false)
        val domain = com.bebekon.vpn.Rule(name = "VPN site", kind = RuleKind.DOMAIN, values = listOf("example.com"))
        // Reported failure: a shared site tunnel advertised IPv6 even when the
        // physical Wi-Fi could not dial it. Unmatched browser HTTPS must stay direct.
        context.repo.update { it.copy(preferences = it.preferences.copy(sitesInAllApps = true)) }
        reloadRules(listOf(domain))
        verifyOtherAppTraffic(url = "https://example.org/", routeMatch = "outbound/direct[direct]: outbound connection to", ipv4Checks = true)
        verifyOtherAppTraffic(routeMatch = "domain_suffix=example.com => route(vpn)", ipv4Checks = true)
        context.repo.update { it.copy(preferences = it.preferences.copy(sitesInAllApps = false)) }
        reloadRules(listOf(com.bebekon.vpn.Rule(name = "Selected app", kind = RuleKind.APP, values = listOf("com.bebekon.vpn.test")), domain))
        verifyOtherAppTraffic(); verifyOtherAppTraffic(stream = true)
        reloadRules(listOf(com.bebekon.vpn.Rule(name = "Selected app", kind = RuleKind.APP, values = listOf("com.bebekon.vpn.test"))))
        verifyOtherAppTraffic(); verifyOtherAppTraffic(stream = true)
        assertTrue("Session volume survives native counter reloads", VpnController.session.value.totalDown >= firstBytes)
        compose.onNodeWithTag("home-traffic").assertIsDisplayed()
        assertTrue("Home shows accumulated GB", compose.onAllNodesWithText(trafficGb(VpnController.trafficHistory.value.totalDown)).fetchSemanticsNodes().isNotEmpty())
        reloadRules(listOf(com.bebekon.vpn.Rule(name = "VPN site", kind = RuleKind.DOMAIN, values = listOf("example.com")), com.bebekon.vpn.Rule(name = "Direct app", kind = RuleKind.APP, values = listOf("com.bebekon.vpn.test"), vpn = false)))
        verifyOtherAppTraffic(expectVpn = false)
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
