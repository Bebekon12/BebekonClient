# Validation record — 2026-10-03

Environment: Windows 11 x64, .NET SDK 10.0.300, runtime 10.0.8, sing-box 1.14.2.

## Executed

- Release solution compilation.
- 150 parser/model/storage/config, signed-update and real-core tests, including JSON Xray subscription import, gRPC/Reality schema acceptance, parallel logging, multilingual country labels, the helper crash suite, elevation command boundaries and pipe/service identity checks.
- WPF smoke harness checks that startup selects TUN even after a saved Proxy session. Elevated registration uses the original user's SID and literal argument list; partial packages are rejected before elevation. Actual UAC acceptance/cancellation and protected service installation remain manual checks.
- Official `sing-box check` accepted generated TCP, Reality, gRPC, WebSocket, HTTPUpgrade configurations.
- Real local VLESS fixture confirmed: selected domain exits via VLESS; unmatched domain Direct; Entire PC default VPN; authenticated forced VPN probe; HTTP Host sniffing routes an IP-addressed request by its hostname.
- Helper detects core crash on the next status snapshot; killing the helper kills its child core through a Windows job object.
- Published self-contained helper responds over the ACL-protected Named Pipe (35.3MB Working Set / 8.3MB private, 0.00% idle CPU over 5s).
- All five WPF pages rendered at 100/125/150/175% pixel scale. These are render checks, not physical per-monitor DPI switch tests.
- 503-rule UI fixture realized only 3 list containers (recycling virtualization).
- Version 0.1.1: Settings categories, search across categories, empty results, scrolling and TUN/Proxy exclusivity are exercised by the WPF harness. All pages also render at 860×660, settings in English, and five native dialogs at 100/175% scale. Dialog application discovery skips inaccessible/reparse Start Menu folders. Smoke and console helper instances are isolated from the user's open app and installed service.
- Version 0.1.2: the tray menu is rebuilt 100 times without the collection-modified exception; old entries and submenus are disposed, and server selection/profile checks remain correct. Caption buttons are exercised for 46×36 hit areas, minimize, maximize/restore, corresponding icon/tooltips, Russian/English labels and close-to-tray behavior. The original disposal-during-enumeration code reproduced the reported exception in an isolated WinForms menu. Main-window and dialog close icons are rendered with the shared caption style.
- Version 0.1.3: the user-supplied Ultima URL returned HTTP 200 with an array of nine Xray configurations. Live import yielded nine supported VLESS nodes (eight gRPC, one TCP; TLS/Reality); official sing-box accepted all nine generated configurations. An isolated user-mode core completed a forced-VPN HTTPS request to gstatic with HTTP 204 in 1355 ms through the first gRPC/Reality node. The check does not change system proxy or routes, uses a separate local probe port, and removes temporary configurations/core processes. Subscription URL, credentials and endpoint addresses are omitted from this record and test fixtures. `dotnet run --file tools/verify-subscription.cs`, with BEBEKON_SUBSCRIPTION_URL supplied privately through the process environment, repeats this import/schema/probe check.
- Version 0.1.4: Windows Application event 1026 identified the exact-ping crash as an IOException from concurrent CoreProcess output callbacks appending to core.log. SafeLog now serializes instances per normalized path and handles filesystem access failures. Regression tests cover 1,600 parallel entries from 16 logger instances and recovery after an external exclusive file lock.
- Version 0.1.4: parallel exact checks of all nine supplied Ultima nodes completed successfully (HTTPS median 29–64 ms), followed by cancellation of another parallel scan. Exact probes run at concurrency 2, TCP at 6; no system proxy or routes are changed by these checks. All nine country labels resolved to a bundled PNG, including Russian/emoji labels and EU. No private endpoints or credentials are recorded here.
- Version 0.1.4 UI harness: real nine-node-style labels, all five screens and five dialogs, 100/125/150/175% renders, minimum window dimensions, odd server count with no empty card, rapid scan-mode changes/cancellation, actual checked/unchecked thumb positions, live accent colors, OLED background, reduced motion, animated sidebar final widths, and inline subscription import. Animations respect Windows client-area animation preferences; Home glow/rotation stops on unload, hidden/minimized windows or disabled animation.
- Version 0.1.4 framework-dependent Debug UI idle, disconnected synthetic fixture before any render captures or explicit GC: visible CPU 0.026%, hidden CPU 0.000% over 5 seconds each; visible Working Set 164.7 MB / private 116.4 MB. This is an ordinary idle measurement but not the published Release build or connected TUN scenario. The 90 MB UI target remains missed.
- Version 0.1.0 ordinary framework-dependent Release UI idle: 145.7 MB Working Set, 109.3 MB private, 0.174% machine-normalized CPU over 3 seconds.
- Version 0.1.0 published self-contained Release UI idle: 146.1MB Working Set, 108.1MB private, 0.03% CPU over 5 seconds. **90MB target missed**. Version 0.1.1/0.1.2 screenshot harness peak is higher and is not an idle measurement; a new ordinary idle benchmark is pending.
- Self-contained app launches without using an installed runtime; service runtime merging preserves WPF assemblies. Installer compilation and portable ZIP generation succeed; privileged installation itself is pending.

## Version 0.1.5

- Admin preset: 42 screenshot entries, 36 VPN and 6 Direct; Direct exceptions precede existing broad Google routes. The store.supercell.com entry is a domain rule. Case variants of the Telegram application are preserved. Reapplication adds no duplicates; profiles and preset copies remain independent.
- All 29 embedded GeoSite/GeoIP rule sets and the admin preset passed official sing-box 1.14.2 schema validation. Sources are pinned SagerNet commits plus the dated official Telegram CIDR list; runtime uses inline sets, no downloads or user-writable paths.
- Real offline VLESS integration fixture proved GeoSite OpenAI → pinned second node (127.0.0.3), specific domain exception → Direct (127.0.0.1), other VPN rule/forced probe → selected node (127.0.0.2). Unit checks verify matching per-node DNS detours, missing/invalid server errors and extended profile round-trip.
- Ping quality tests cover null, 0, 100, 101, 200, 201 and failed values, and changes notify WPF bindings. UI verifies actual measured-label colors and full square 46×46 toolbar hit areas.
- WPF harness: all six editor types, optional name, Direct/VPN and specific/Auto server selections; rule choices ≥44px and submit buttons ≥44px. Owner blur/restoration verified; screenshots inspected. All five screens at 100/125/150/175% render scale and 860×660 minimum window. The home halo fits inside the hero; logo glow is present. Compact routing shortcuts leave a usable list viewport at minimum size even with a banner; the actual Add preset command is applied twice, then Save edits the same rule ID without changing its Direct action. Existing tray, settings, animation and virtualization checks retained.
- Published self-contained 0.1.5 UI passed the full WPF smoke suite. A separate disconnected idle process, before screen captures or explicit GC, measured 152.4 MB Working Set / 112.0 MB private and 0.000% visible/hidden machine-normalized CPU over 5 seconds each. The 90 MB UI target remains missed; real connected TUN memory remains unmeasured.
- A registered older helper is upgraded via the existing protected installer before connecting; version comparison is automated. Actual elevated upgrade remains part of manual acceptance.

## Version 0.1.6

- Reproduced the reported connection error from a medium-integrity Windows owner: GetNamedPipeServerProcessId succeeds, but OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION) on the installed LocalSystem helper fails with ERROR_ACCESS_DENIED (5). The pipe PID matches the Running own-process service PID returned by QueryServiceStatusEx using the owner's existing SERVICE_QUERY_STATUS permission.
- Replaced executable inspection with the authoritative SCM PID check before sending requests. Sixteen new tests cover the native 36-byte status layout, matching/zero/different PIDs, stopped/pending/paused states, driver/shared-process types and a real counterfeit pipe owned by the test process. No identity verification bypass or extra client privileges were added.
- The fixed client, running without elevation, completed five authenticated GetStatus requests to the actual installed protected LocalSystem helper (0.1.5). It then validated the saved server/profile, launched the real core, received forced-VPN HTTPS HTTP 204 and a valid egress IP, and stopped the core with Disconnected/no-core status. Reproduction: `dotnet run --file tools/verify-service.cs -c Release -- --probe`. Private connection details are not printed. The probe uses a separate authenticated local port, does not change Windows proxy/routes or saved user state, and requires an idle helper.
- SnowVPN's TUN is active on this machine. The installed-helper probe therefore used proxy mode; independent TUN/browser routing acceptance remains pending.
- Published self-contained 0.1.6 Core also completed five verified status requests to the installed LocalSystem helper without elevation. The published App passed the complete WPF smoke suite: five pages at 100/125/150/175% render scales, five dialogs, minimum size, tray/caption controls, settings, routing editor, admin preset and virtualization checks. Release tests: 129 passed, zero failed/skipped.

## Version 0.1.7

- Six new core tests verify refresh identity/favorites/ping preservation, removal without automatic replacement, credential rotation, independent subscriptions, the five-second silent-peer deadline/cancellation, and actual bidirectional traffic counters. The real sing-box fixture downloads 256 KiB through its proxy, observes nonzero upload/download rates and totals, and confirms unauthenticated statistics access returns HTTP 401. Helper integration also receives traffic snapshots via IPC.
- WPF connection regressions use synthetic storage and fake IPC: unchanged refresh makes no restart; three rapid site additions produce one restart containing all rules; the connection stays stable beyond five seconds; a stale failing recovery cannot restart the new session; manual off cancels a queued update; edits during startup apply afterward; cancelling startup remains disconnected. Transitions never overlap, and traffic direction/unit conversion is checked.
- Home checks cover the embedded full server list, navigation from its server card, routing-mode dialog action and ping-click result. All five pages render at 100/125/150/175% and minimum window size; existing rule editor, tray, captions and animation checks remain. The snowman source is generated with built-in image_gen, exported to a seven-size ICO and embedded as a decoded-size PNG for the sidebar. Prompt provenance: resources/icons/snowman.asset.json.
- The website report is covered by deterministic connection/routing regressions. Physical TUN/browser reproduction with the user's live website, adapter changes and ECH/DoH remains in manual acceptance below; simulated IPC is not labelled as a live TUN check.

## Version 0.1.8

Version 0.1.8 adds 15 update tests (150 total): signed local feed and package download; equal/older version rejection; wrong signing key/tampered payload rejection; truncated, oversized and corrupted package cleanup; cancellation cleanup; unsafe package paths and non-HTTPS sources. WPF smoke checks disabled ping opacity, the Updates category, update banner/actions at minimum size, and the real signed current-version feed in the published build. The Inno update branch compiles. Full UAC handoff and installed-file replacement are not performed against the user's running app; they remain part of installer acceptance below.

## Required manual acceptance — pending

Ultima import and a user-mode forced-VPN HTTPS probe passed. This session is not elevated; real TUN routing and the following acceptance checks remain pending. Do not label them as passed:

1. Install Setup on clean Windows 11; verify no runtime prerequisite, one installation UAC, none per connect. Verify pipe access from another Windows user is denied.
2. Ultima: add only ip2location.com → VPN, default Direct. `curl.exe https://ifconfig.me/ip` must retain ISP IP; ip2location.com must show VPN IP. Compare with VPN off.
3. Repeat with browser DoH, TLS and QUIC. Repeat with ECH disabled; document ECH limitation separately.
4. Telegram.exe → VPN and generic browser traffic → Direct.
5. discord.exe Direct above discord.com VPN: desktop direct, browser domain VPN.
6. Entire PC: ifconfig.me must show VPN egress; check IPv6 and DNS captures.
7. Kill sing-box: error status, no surviving child/network state. Local helper job/crash behavior is automated; privileged TUN cleanup is pending.
8. Sleep/wake, Wi-Fi loss/reconnect, Wi-Fi→Ethernet and new Wi-Fi network. Check actual egress and reconnect status.
9. Record connected UI + service + core Working Sets and CPU (all processes included). Target ≤150–180MB is **unmeasured**.
10. Installer upgrade preserves state; uninstall stops helper/core and offers data removal. Portable first connection automatically requests UAC once; cancellation produces an error; acceptance registers the protected helper, even with a different administrator account. No further UAC on subsequent connections; ordinary user cannot write helper binaries. Portable helper unregistration.

`scripts/measure-memory.ps1` measures real processes without trimming memory. Save output with the relevant connection state and test duration.
