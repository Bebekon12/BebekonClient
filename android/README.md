# Bebekon VPN · Android

Native Kotlin/Jetpack Compose client for Android 10+ (API 29), using a separately compiled sing-box 1.14.2 library. No root is required. The system asks for VPN permission on first connection.

## Features

- Version 0.1.10 adds `ipapi.is` to the HTTPS map lookup providers and parses the anonymous country-name/lat/lon response. Resolved countries drive Home/list flags without renaming or persisting changes to profiles. HTTP 403/429 backoff, echoed-IP checks, cancellation and idle destination retries are supported. Map gestures and dimensions are unchanged.
- Version 0.1.9 excludes core/TrustTunnel startup from displayed HTTPS latency while retaining the five-second total probe budget. Generic server names can resolve approximate destination coordinates/country using the VPN public IP; map lookup remains optional and coordinates stay in memory. Back returns Servers, Rules, Subscriptions and Settings to Home, then backgrounds the task from Home.
- Dark, light and system themes, local interactive map, draggable connection sheet and a separate bottom navigation bar.
- URL, text, file, share-intent and QR imports; plain links, Base64, Clash/Mihomo YAML, Xray JSON and sing-box outbound JSON.
- VLESS (TCP, WS, gRPC, HTTP, HTTPUpgrade, TLS/Reality), VMess, Shadowsocks, Trojan, Hysteria and Hysteria2.
- Stable server selection, favorites and TCP / HTTPS GET / HTTPS HEAD latency tests. HTTPS probes use the chosen VPN outbound and a five-second native context deadline.
- Full-device routing or rules for domains, keywords, Android packages, IP prefixes, GeoSite and GeoIP. Built-in service, Russia and admin presets. Windows process rules are intentionally omitted on Android.
- Native `VpnService` with a userspace gVisor packet stack, a foreground notification with Disconnect, live upload/download counters and public VPN IP.
- **Quick Settings tile**: Settings → «Кнопка в шторке». Android 13+ displays the system Add tile dialog; Android 10–12 use the shade's tile editor. The tile and app share the same serialized service state. First connection opens system VPN consent.
- Android Keystore AES-GCM encrypted subscription/settings storage. Provider configurations cannot replace app listeners, routing, certificate files or executable commands.
- System settings links for always-on VPN and Android's “Block connections without VPN” option.
- In-app update prompts, APK download with progress, SHA-256 and signing-certificate verification, then the normal Android installation confirmation.
- Settings → «Проверить сайт» compares a direct HTTPS response with the selected VPN without changing the active connection or rules.

This first mobile release does not yet include the Windows companion engines for **VLESS XHTTP or TrustTunnel**, or WireGuard. Such imported servers are clearly marked unavailable; the app does not silently convert their transport. Provider client restrictions, HWID binding and device limits still apply. ICMP is not advertised as a tunnel latency measurement.

## Android 0.1.1

Ultima's Xray JSON stores the human-readable server name in the profile's `remarks`, while the outbound tag is usually `proxy`. The importer now retains profile names (also `remark`, `ps`, and `name`) and country metadata; identity still depends only on connection parameters. HTTPS subscriptions with old generic placeholders are refreshed on launch, preserving selection and preferences.

The world map projects local country geometry into screen coordinates and highlights a country only when metadata is known. It no longer substitutes Sweden for unknown countries. France and Norway's Natural Earth sentinel codes are normalized for correct selection. The home screen uses a luminous circular connection button and four separate sheet cards for server, routing, traffic, and session/public IP. Charts contain measured traffic samples.

Rules → Applications supports searching by label/package, selecting multiple apps, VPN/direct actions, and actual installed-app icons. Saving a batch updates individual app rules atomically and reloads the VPN once. Application rules apply in **By rules** mode; the picker explains this when full-device routing is selected.

Latency colors are product thresholds, not a network standard: **HTTPS GET/HEAD ≤300 ms green, ≤600 ms amber, otherwise red; TCP ≤100 ms green, ≤250 ms amber, otherwise red**. TCP tests the server socket; HTTPS includes DNS, the VPN handshake, target TLS and an HTTP response. They are not interchangeable RTT measurements (see [sing-box's URL test implementation](https://github.com/SagerNet/sing-box/blob/v1.14.2/common/urltest/urltest.go)). Servers offers Cloudflare (`cp.cloudflare.com/generate_204`, default) and Google (`www.gstatic.com/generate_204`); results from different targets are not mixed. Existing results retain their method and color during queued/running tests. A new method or target cancels the old queue and clears stale results. At most four native tests run concurrently, each with its existing five-second deadline. Connection validation tries the other HTTPS target if the first fails, so one blocked test site does not reject an otherwise usable connection; startup validation can take up to two five-second attempts.

## Android 0.1.2

The app checks the common GitHub release at startup and offers «Обновить» or «Позже». Settings → «Проверить обновления» runs a manual check. The APK downloads inside the app with progress; the downloader checks the exact size, SHA-256, package identity, newer version code and the existing signing certificate before sharing it with Android's installer through a restricted FileProvider. On first use, Android may ask to allow installation from Bebekon VPN; installation still requires the system confirmation. Android 0.1.1 must be updated to 0.1.2 manually once to get this updater. Subscriptions and settings are retained when installing with the same signing key.

Rules → Applications starts with the effective saved app rules checked. VPN and direct assignments have separate tabs; removing a check removes that app's rule. Saving preserves domain rules and unchanged app-rule priorities, and reloads an active VPN once. When only app rules select VPN traffic, Android's allowed-application TUN list excludes other apps from both VPN transport and DNS. The tunnel default and resolver fallback are VPN because Android's DNS resolver or isolated app processes can lack original package metadata. In mixed site/app mode, explicit Direct apps use Android's disallowed-app list and bypass the tunnel entirely. Android's system «Block connections without VPN» option blocks such exceptions; the client reports that conflict before connection rather than silently blocking them.

Recognizable Chromium WebAPKs carry requests through their host browser; their stored app rule is translated to a domain rule in the runtime configuration. Stored rules are unchanged. The app picker explains this, and Settings provides a ChatGPT site preset. A browser explicitly excluded from the tunnel cannot also carry a VPN web-app route. Native installed apps retain normal app routing; browser identity is not assumed for the reported native-app failure.

The site diagnostic sends a HEAD request both directly and through the selected VPN and distinguishes HTTP responses, timeouts, DNS and TLS failures. It checks the connection and response headers, not a browser's whole page or its private DNS/QUIC behavior. A successful diagnostic does not establish the cause of a browser-specific failure.

The home map draws a luminous blue arc from the approximate ordinary-IP location to the selected country during connection and while online. The optional HTTPS lookup to ipwho.is is bound to a validated non-VPN Android network, uses a 30-minute cache and stores coordinates only in memory; no GPS permission is requested. Settings can disable it. Without a successful lookup the country remains visible without an invented departure point. A bright signal travels along the arc and the destination pulses; changing the country moves the endpoint with the map projection. Unknown countries have no invented destination. With animations disabled, the arc remains static; disconnecting removes it. The drawing uses the existing dark/light palettes and remains attached to the destination when the map is zoomed or panned.

Home card labels/values are vertically centered. Received/sent volume appears in GB per session, even when current speed is zero. Samples are collected by the running service while other screens are open, retaining up to 120 seconds per direction. Pull up the sheet for larger charts. Reloading rules restarts native counters but preserves accumulated session bytes; a new connection starts a new session, and disconnect keeps the previous charts until then.

Settings offers searchable categories for themes, motion, location, routing/app rules, presets, LAN, three delayed startup retries, DoH resolver, MTU, Android always-on/blocking options, background battery settings, Quick Settings, ping method/target, site diagnostics, connection-event notifications, subscriptions, update checking and the log. The required low-priority foreground VPN notification remains independent from optional connection/error event notifications. Startup/retry can be cancelled from Home or Quick Settings without a delayed reconnection restarting it.

## Android 0.1.3

App filtering is calculated from the stored app rules **before** recognizable WebAPKs are converted to runtime domain rules. Previously that conversion could switch an apparently app-only selection to a shared tunnel and expose VPN transport/DNS to unchecked applications. WebAPK launchers map to their host browser in the system allowlist; native selected apps retain their own packages. Other applications retain the physical network. A WebAPK shares networking with its browser and cannot have a separate OS network identity.

Adding site/service rules to a selection of native apps no longer silently captures the rest of the device. Site rules apply within the selected apps. Settings → «Правила сайтов во всех приложениях» explicitly enables a shared tunnel; Direct apps remain excluded. Site-only configurations still use the shared tunnel as required. Shared site mode keeps unmatched DNS on the direct resolver; matching site/app DNS rules still select their appropriate resolver. The log records which system scope was actually established without subscription URLs or keys. This matches the separation of app filters and routing described in [Happ's official app-management documentation](https://github.com/HappDev/happ_su/blob/main/dev-docs/app-management.md), using Android's `addAllowedApplication` / `addDisallowedApplication` APIs.

Server lists default to live ping ordering (green, amber, red, unknown). Home uses the same ordering; results rise as each probe finishes without changing the selected server. Four Android probes run concurrently, with the existing five-second native deadline per active probe. Waiting in the queue is separate from that deadline. Failed peers cannot stall the whole scan indefinitely.

The isolated validation setup includes a tiny **test-only WebAPK metadata fixture**, in addition to a separate-UID HTTPS/streaming companion. It exercises app-only stored rules that become site rules at runtime, mixed native/WebAPK app selections, and unselected-app physical networking. The fixture APK is never included in public release assets.

## Android 0.1.5: IPv4 compatibility

The affected phone's route journal confirmed correct ownership (`com.yandex.browser`), TLS domain (`yandex.ru`) and Direct selection, followed by literal IPv6 dials on `wlan0` failing with `network is unreachable` or timeout. The previous configuration always advertised a synthetic IPv6 TUN address and accepted AAAA answers even when the physical network could not reach those destinations. `prefer_ipv4` does not suppress explicit AAAA queries or change an already resolved literal IPv6 destination.

Android now defaults to an IPv4-only TUN with DNS strategy `ipv4_only`. Settings → Connection → «IPv6 в туннеле» restores dual-stack TUN/DNS when both the physical network and VPN server support it; changing the option reloads the active service. Existing saved settings without this field migrate to the compatibility default without altering app/site rules. Disabled IPv6 is blocked by Android for captured applications: no IPv6 address, route, DNS server or `allowFamily(AF_INET6)` is added, so it cannot fall through to the physical network. Applications outside the OS allowlist remain on their own network. The outer VPN endpoint's domain resolver keeps an explicit dual-stack preference independently of the apps' tunnel family.

This addresses the demonstrated IPv6 failure mechanism. The user's exact phone/browser session still requires confirmation after installing the patch; no physical user device is connected to this workspace. IPv6-only destinations inside the tunnel require enabling the IPv6 setting.

## Build

### TrustTunnel (Android 0.1.7)

TrustTunnel is a native transport alongside sing-box: a private, authenticated loopback SOCKS bridge carries TCP/UDP through the official TrustTunnel client, while sing-box owns Android's TUN, DNS and existing app/site rules. HTTP/2, HTTP/3 (QUIC) and automatic selection are supported. Both the endpoint certificate hostname and trust chain are verified; custom SNI does not disable hostname verification. Imported pinned certificates remain available when editing a profile. Provider routing/listeners are never applied.

Add or edit an endpoint from Servers → **TrustTunnel · ввести вручную** or Subscriptions → **TrustTunnel без ссылки**. Fields include address (default 443), certificate domain, optional SNI, username, password and protocol. `tt://` v0–v2 links, endpoint TOML/JSON and HTTPS subscriptions (including Basic authorization) use the same validated profile. Credentials stay in encrypted local storage. Initial TLS/authentication must complete before sending traffic; HTTPS probes share one five-second budget with native startup.

Build the additional native libraries before Gradle, using Docker Desktop's Linux engine:

```powershell
./android/trusttunnel/build.ps1
```

The script builds all three APK architectures, API 29+, with NDK r30 and 16 KiB ELF alignment. TrustTunnel Client v1.1.7 is pinned to `170609c24ca865819fed68437b01c013049bc3fa`. Its nghttp2 dependency is replaced by upstream **1.68.1**, checksum checked; the included patch only exports two existing function declarations needed by TrustTunnel. Build sources and this adapter are included in the release core-source archive. Generated libraries and the reusable Docker build cache are excluded from Git.

The notification and Quick Settings tile use a monochrome snowman. Android's own VPN/key indicator beside the clock is controlled by the OS and cannot be replaced by the app. The connected button has a bright blue/cyan rim; the disconnected button is muted blue. Its continuous animation runs only while connected and animations are enabled.

Required: JDK 17, Go 1.26.8, Android SDK platform 36, NDK 28.2.13676358. Set `JAVA_HOME`, `ANDROID_HOME` and `ANDROID_NDK_HOME`, and add Go to PATH. Gradle 8.13 is pinned by the wrapper and SHA-256.

```powershell
./android/build-core.ps1
./android/gradlew.bat -p android :app:testDebugUnitTest :app:assembleDebug :app:assembleDebugAndroidTest :webapp-fixture:assembleDebug
```

The core script pins the official source commit, updates dependency security versions from `../core/security-pins.json` and adds the small `core/bebekon.go` bridge for independent, deadline-bound VPN probes. It builds arm64-v8a, armeabi-v7a and x86_64. Generated AARs, SDKs, local settings and private signing keys are excluded from Git. The source archive supplied with releases contains the exact modified module manifests and bridge.

Security changes in 0.1.6 reject certificate-verification bypass and certificate-file plugin options, isolate Quick Settings consent in a private Activity, bound import complexity/download duration, redact diagnostic secrets and recheck APK SHA-256 immediately before installation. See [the security audit](../docs/SECURITY-AUDIT.md) for attack scenarios, evidence and remaining limits.

Release builds shrink code/resources. Provide `BEBEKON_ANDROID_KEYSTORE` and `BEBEKON_ANDROID_KEY_PASSWORD`, with alias `bebekon`, then run `:app:assembleRelease`. Keep the same signing key for every update; never put it in the repository. Increment both Android's numeric `versionCode` and semantic `versionName`. The build emits one universal `app-release.apk` with all three supported architectures. Publish it as `Bebekon-Android.apk` so users do not have to choose an architecture. The common latest stable release title must include `Android X.Y.Z`, and its single `Bebekon-Android.apk` asset must have GitHub's SHA-256 digest; this is the Android updater's publication contract. Preserve these Android assets/title when publishing a subsequent Windows release. Android version comparisons are independent of the Windows release tag.

## Validation

JVM tests cover parsing, unsafe YAML rejection, provider-config isolation, encryption round-trip models, stable server identity and routing/DNS precedence. Instrumentation tests validate configurations against the actual native core, encrypted storage, navigation/themes, HTTPS traffic from a separate Android UID, live counters, rule reload and Quick Settings connect/disconnect through an isolated VLESS fixture. The optional `fixture_port` argument points to a loopback fixture exposed through the emulator's `10.0.2.2` alias; it never modifies the host's VPN settings.

Install the debug APK and test APK on an isolated emulator and grant the normal VPN consent before running `AndroidSmokeTest`. Without `fixture_port`, the live-connection case is not exercised.

`test-emulator.ps1 -FixtureExecutable <sing-box.exe>` runs the loopback fixture and the full suite on an already booted emulator. It rejects physical-device serials and installs only the app and its test companion. Optional `-ProviderFixture <private subscription JSON>` runs provider diagnostics without publishing credentials. Optional `-UpdateFixture <newer APK signed with the debug key>` exercises the update popup, download, package/signature verification and system permission/installer handoff without accepting installation during the suite. Temporary device fixture files are removed on exit.

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

## Release 0.1.2 verification

All 45 JVM tests and nine instrumentation tests passed on the disposable Android 15 / API 35 x86_64 emulator. Actual traffic from a separate Android UID used the physical network when unselected, VPN when selected, and the physical network again when explicitly excluded in mixed site/app mode. A fixture destination reachable only through VPN delivered a nine-second stream without interruption. Rule reload preserved session time and accumulated byte totals; Home exposed GB counters. Retry cancellation remained disconnected after the delayed attempt's scheduled time. Navigation through both themes, the expanded lazy Settings list, checked app selection/icons, native configuration parsing and encrypted storage also passed.

The optional Ultima probes and direct/VPN HEAD diagnostic exercised real provider outbounds without changing the provider device identity. The updater test offered a synthetic newer release, downloaded its full APK, checked size/hash/package/signature, and reached the system permission/installer handoff without accepting installation. It did not publish or install that future fixture APK. No physical Yandex/ChatGPT installation was available; the exact phone failure remains unconfirmed even though the modeled routing defects are fixed.

Debug and release Lint reported zero errors (18 advisory warnings). APK signature continuity, Android 10 minimum API, all three ABIs and 16 KiB ZIP alignment were verified. The app-only/mixed TUN tests used Android's real VpnService in the emulator, not mocked transport flags. Test components remain confined to the separate instrumentation APK.

The final signed, R8-optimized 0.1.2 APK installed over the previously published signed 0.1.1 and retained the encrypted subscription, selected Sweden fixture and light theme. The separately installed test companion then reported actual VPN transport and HTTPS HTTP 200 through the release APK. Light Home with the real approximate-IP arc and measured traffic spikes was visually inspected; idle speed remains zero while the chart retains those earlier spikes.

## Release 0.1.3 verification

All 47 JVM tests passed, including preserved original app scope before WebAPK conversion, explicit shared site mode, direct DNS fallback and live ping sorting. Debug and release Lint report zero errors and 18 advisory warnings. The original certificate, universal ABIs and 16 KiB APK ZIP alignment were checked. The release, instrumentation and separate WebAPK metadata fixture APKs compile; the fixture is never a public download.

Initial Android 15 emulator startup failed at Windows' commit limit. After the user freed memory, the expanded real-tunnel suite reported OK (9 tests): selected native apps and streams used VPN, unchecked native/WebAPK companion UIDs stayed physical, and site rules did not silently broaden the app allowlist. Optional provider and future-update fixtures were not supplied, so those two methods returned without external checks.

The actual signed 0.1.2 APK reproduced the WebAPK-only scope defect (unchecked companion reported VPN transport). Upgrading in place to the final signed 0.1.3 retained the encrypted fixture subscription, selected server and rule; the same companion reported physical transport with HTTP 200 while VPN remained connected. The earlier client displayed the new public update offer. No physical phone was attached, so the exact Yandex/Gosuslugi error still needs confirmation after updating.

## Release 0.1.4 verification

49 JVM tests passed; debug/release Lint reported zero errors and 18 advisory warnings. The final real-tunnel suite reported OK (9 tests), with two optional external-input methods returning early. A test WebAPK identifies a separate companion UID as its host browser: native route logs confirm VPN for the matched site and Direct for an unmatched HTTPS page, including mixed native-app selection. Explicit whole-browser selection retains VPN. The signed R8 universal APK retains the original certificate and 16 KiB alignment; updating over signed 0.1.3 preserved encrypted fixture data and the app rule. Its optional route journal populated and Copy was checked. No exact physical Yandex failure was reproduced; the journal provides evidence for that remaining investigation. Full evidence and tool limitations are recorded in docs/VALIDATION.md.

## Release 0.1.5 verification

52 JVM tests passed; debug/release Lint reported zero errors and 18 advisory warnings. The final real-service suite passed OK (9 tests), 159.942 seconds, with two optional external-input methods returning early. Fresh native logs distinguish the shared-mode Direct HTTPS request from the matched VPN site. From a separate captured UID, A DNS returns addresses, AAAA returns none, and a literal IPv6 connection fails immediately. Android's actual release VPN link has an IPv4 address/DNS and an unreachable IPv6 default route, with bypass disabled. Signed R8 0.1.5 installed over published 0.1.4, preserved the encrypted fixture subscription/selected node/light theme and carried a companion HTTPS request with HTTP 200. The default-off IPv6 setting was visually inspected. Physical phone confirmation and real dual-stack IPv6 forwarding remain outside this validation.

## Release 0.1.7 verification

Native TrustTunnel HTTP/2 and HTTP/3 were tested against the official endpoint, including authenticated UDP, certificate-host rejection, Android TUN from a separate UID, disconnect and manual profile editing. The original app/site routing suite and 63 JVM tests pass. The compact map keeps existing Home/button dimensions, distinguishes departure/destination size and attaches the country caption to the server. Native dependency license notices are bundled in assets/licenses/trusttunnel; security limitations and device coverage are recorded in docs/VALIDATION.md and docs/SECURITY-AUDIT.md.

## Release 0.1.8 verification

Subscription eye buttons hide nodes from Home, Servers and batch ping while retaining the active connection and authored route destinations. The hidden state persists and survives subscription refresh. All 64 JVM tests pass. The actual Compose Home fixture passed for Moscow→Sweden/USA routes after a 20% map zoom reduction and origin clearance around the unchanged power button. The signed universal R8 APK retains the published certificate and 16 KiB ZIP alignment. Release Lint has zero errors and 19 advisory warnings; no physical phone was attached. Detailed validation is in docs/VALIDATION.md.
