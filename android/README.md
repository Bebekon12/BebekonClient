# Bebekon VPN · Android

Native Kotlin/Jetpack Compose client for Android 10+ (API 29), using a separately compiled sing-box 1.14.2 library. No root is required. The system asks for VPN permission on first connection.

## Features

- Dark, light and system themes, local interactive map, draggable connection sheet and a separate bottom navigation bar.
- URL, text, file, share-intent and QR imports; plain links, Base64, Clash/Mihomo YAML, Xray JSON and sing-box outbound JSON.
- VLESS (TCP, WS, gRPC, HTTP, HTTPUpgrade, TLS/Reality), VMess, Shadowsocks, Trojan, Hysteria and Hysteria2.
- Stable server selection, favorites and TCP / HTTPS GET / HTTPS HEAD latency tests. HTTPS probes use the chosen VPN outbound and a five-second native context deadline.
- Full-device routing or rules for domains, keywords, Android packages, IP prefixes, GeoSite and GeoIP. Built-in service, Russia and admin presets. Windows process rules are intentionally omitted on Android.
- Native `VpnService` with a userspace gVisor packet stack, a foreground notification with Disconnect, live upload/download counters and public VPN IP.
- **Quick Settings tile**: Settings → «Кнопка в шторке». Android 13+ displays the system Add tile dialog; Android 10–12 use the shade's tile editor. The tile and app share the same serialized service state. First connection opens system VPN consent.
- Android Keystore AES-GCM encrypted subscription/settings storage. Provider configurations cannot replace app listeners, routing, certificate files or executable commands.
- System settings links for always-on VPN and Android's “Block connections without VPN” option.

This first mobile release does not yet include the Windows companion engines for **VLESS XHTTP or TrustTunnel**, or WireGuard. Such imported servers are clearly marked unavailable; the app does not silently convert their transport. Provider client restrictions, HWID binding and device limits still apply. ICMP is not advertised as a tunnel latency measurement.

## Android 0.1.1

Ultima's Xray JSON stores the human-readable server name in the profile's `remarks`, while the outbound tag is usually `proxy`. The importer now retains profile names (also `remark`, `ps`, and `name`) and country metadata; identity still depends only on connection parameters. HTTPS subscriptions with old generic placeholders are refreshed on launch, preserving selection and preferences.

The world map projects local country geometry into screen coordinates and highlights a country only when metadata is known. It no longer substitutes Sweden for unknown countries. France and Norway's Natural Earth sentinel codes are normalized for correct selection. The home screen uses a luminous circular connection button and four separate sheet cards for server, routing, traffic, and session/public IP. Charts contain measured traffic samples.

Rules → Applications supports searching by label/package, selecting multiple apps, VPN/direct actions, and actual installed-app icons. Saving a batch updates individual app rules atomically and reloads the VPN once. Application rules apply in **By rules** mode; the picker explains this when full-device routing is selected.

Latency colors are product thresholds, not a network standard: **HTTPS GET/HEAD ≤300 ms green, ≤600 ms amber, otherwise red; TCP ≤100 ms green, ≤250 ms amber, otherwise red**. TCP tests the server socket; HTTPS includes DNS, the VPN handshake, target TLS and an HTTP response. They are not interchangeable RTT measurements (see [sing-box's URL test implementation](https://github.com/SagerNet/sing-box/blob/v1.14.2/common/urltest/urltest.go)). Servers offers Cloudflare (`cp.cloudflare.com/generate_204`, default) and Google (`www.gstatic.com/generate_204`); results from different targets are not mixed. Existing results retain their method and color during queued/running tests. A new method or target cancels the old queue and clears stale results. At most two native tests run concurrently, each with its existing five-second deadline. Connection validation tries the other HTTPS target if the first fails, so one blocked test site does not reject an otherwise usable connection; startup validation can take up to two five-second attempts.

## Build

Required: JDK 17, Go 1.26.8, Android SDK platform 36, NDK 28.2.13676358. Set `JAVA_HOME`, `ANDROID_HOME` and `ANDROID_NDK_HOME`, and add Go to PATH. Gradle 8.13 is pinned by the wrapper and SHA-256.

```powershell
./android/build-core.ps1
./android/gradlew.bat -p android :app:testDebugUnitTest :app:assembleDebug
```

The core script pins the official source commit and adds the small `core/bebekon.go` bridge for independent, deadline-bound VPN probes. It builds arm64-v8a, armeabi-v7a and x86_64. Generated AARs, SDKs, local settings and private signing keys are excluded from Git.

Release builds shrink code/resources. Provide `BEBEKON_ANDROID_KEYSTORE` and `BEBEKON_ANDROID_KEY_PASSWORD`, with alias `bebekon`, then run `:app:assembleRelease`. Keep the same signing key for every update; never put it in the repository. The build emits one universal `app-release.apk` with all three supported architectures. Publish it as `Bebekon-Android.apk` so users do not have to choose an architecture.

## Validation

JVM tests cover parsing, unsafe YAML rejection, provider-config isolation, encryption round-trip models, stable server identity and routing/DNS precedence. Instrumentation tests validate configurations against the actual native core, encrypted storage, navigation/themes, HTTPS traffic from a separate Android UID, live counters, rule reload and Quick Settings connect/disconnect through an isolated VLESS fixture. The optional `fixture_port` argument points to a loopback fixture exposed through the emulator's `10.0.2.2` alias; it never modifies the host's VPN settings.

Install the debug APK and test APK on an isolated emulator and grant the normal VPN consent before running `AndroidSmokeTest`. Without `fixture_port`, the live-connection case is not exercised.

`test-emulator.ps1 -FixtureExecutable <sing-box.exe>` runs the loopback fixture and the full suite on an already booted emulator. It rejects physical-device serials and installs only the app and its test companion.

## Licenses and provenance

Application/source: repository GPL-3.0-or-later license. Networking: [SagerNet/sing-box](https://github.com/SagerNet/sing-box), fixed commit in `core/dependencies.json`. Geo rules and flags are the same local resources as the desktop client. Map geometry derives from [Natural Earth](https://www.naturalearthdata.com/about/terms-of-use/) (public domain), simplified to geometry and country labels. The snowman is the existing Bebekon brand asset.

Android and Windows assets are published together in the [main GitHub release](https://github.com/Bebekon12/BebekonClient/releases/latest). Download the single [Bebekon-Android.apk](https://github.com/Bebekon12/BebekonClient/releases/latest/download/Bebekon-Android.apk) for Android 10+. Preserve the Windows release tag, installer, portable archive and signed `update.json` feed when adding Android assets: the Windows updater uses this same latest release. Android versions and signing keys remain independent. The `android-v0.1.0` source tag identifies the first mobile implementation; it does not require a separate release page.

## Release 0.1.0 verification

Validated on an isolated Android 15 / API 35 x86_64 emulator: 25 JVM tests, four instrumentation tests, and a separate HTTPS request from the test companion's UID through the installed, signed, R8-optimized release APK. The live test covers VLESS connection, traffic counters, rule reload, disconnect and the system Quick Settings toggle. Other protocol samples are parsed and checked by libbox; they have not all been exercised against live provider endpoints. Android 10 is the minimum supported API and is checked by Android Lint; a physical Android 10 device has not been tested here.

Both debug and release lint completed with zero errors. Release APK signatures and 16 KiB ZIP alignment were verified; the arm64 libbox ELF LOAD segments use 16 KiB alignment. Test companion services and activities are confined to `src/androidTest` and are absent from release APKs.

In-app automatic APK download/install is not included in this first mobile version. Settings links to GitHub releases; future APKs signed with the same private key can be installed over this version while retaining subscriptions and settings.

## Release 0.1.1 verification

All 30 JVM tests and seven instrumentation tests passed on Android 15 / API 35 x86_64. The suite checked visible map geometry, country metadata, app multi-selection/icons, encrypted storage, native protocol configurations, live VLESS traffic, rule reload and Quick Settings connect/disconnect. Optional diagnostics tested two real Ultima nodes using TCP and both HTTPS targets without importing a new provider device identity. Latency varied by target and route; these emulator measurements do not predict a phone's mobile-network latency.

The signed, R8-optimized APK installed over the published 0.1.0 APK and retained the encrypted subscription, selected server and light theme. A separate application's HTTPS request returned HTTP 200 through its VPN. Dark and light home screens were visually inspected. Debug/release Lint reported zero errors; the original signing certificate, universal ABI contents and 16 KiB APK alignment were verified. No physical phone was available for this validation.
