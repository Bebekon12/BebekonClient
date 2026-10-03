# Bebekon VPN

Windows 10/11 x64, .NET 10, WPF, native UI. Default routing is **selected rules → VPN; everything else → Direct**.

## Build and install

Open Bebekon.sln in Rider. Install .NET SDK 10 (global.json pins 10.0.300). Run:

    .\build.ps1

The script verifies the pinned core archive, restores packages, runs tests, publishes both executables self-contained, compiles Inno Setup and produces:

- dist/BebekonVPN-Setup-x64.exe
- dist/BebekonVPN-Portable-x64.zip

No .NET runtime is needed on the destination PC. Build needs internet for NuGet and official downloads. Inno Setup 6.7.3 is bootstrapped per user into .tools when no compiler is installed. Override with -InnoCompiler PATH. -SkipInstaller produces only portable output.

Version 0.1.3 imports Ultima's JSON Xray subscriptions directly from the provider URL, in addition to plain/Base64 VLESS links. It reads VLESS connection parameters from each configuration and keeps the application's own routing/DNS rules. Version 0.1.2 fixed repeated tray menu openings and improved window buttons; 0.1.1 unified the dark palette, controls and dialogs. To publish while another build folder is in use, run `./build.ps1 -PublishFolder artifacts/release-ultima`; normal builds still use artifacts/release.

Setup installs into Program Files, registers a demand-start LocalSystem helper, assigns its pipe and service-start rights to the chosen Windows account, and creates shortcuts. If UAC uses a different administrator account, enter the ordinary user's Windows account on the owner page. Only helper installation/update and uninstallation need elevation; connecting from the UI does not. Upgrades preserve %LOCALAPPDATA% data.

The launch executable is **artifacts/release/Bebekon.App.exe** after building, or **Bebekon.App.exe** in the extracted portable ZIP. Keep its adjacent runtime, scripts and core folders together.

TUN is selected automatically at each launch. Proxy can still be selected manually for the current session. If the service is missing, the first connection automatically opens Windows' UAC prompt and installs the helper; confirm that prompt once. The UI waits for installation, then continues connecting. An older helper is upgraded once before connecting so it understands the new rule schema. Cancelling UAC produces a clear error and does not show Connected. With Setup, the helper is already installed, so connections need no elevation.

The portable helper/runtime is copied to a protected Program Files directory before service registration; its owner and ACL permit writes only to Administrators/SYSTEM. The original UI user's SID is passed before elevation, including when UAC uses a different administrator account. Manual fallback: run scripts/install-service.ps1 **once as administrator**, with -OwnerAccount 'COMPUTER\username' when elevating with another account. scripts/uninstall-service.ps1 removes the service and its protected copy. Portable means no app installer, not an unprivileged TUN driver.

## Development

    dotnet build Bebekon.sln -c Release
    dotnet run --project src/Bebekon.App -c Release
    dotnet test tests/Bebekon.Tests -c Release

For a dev connection, install the published service first. `Bebekon.Service.exe --console` is a local helper harness for tests; TUN still requires administrator rights. It uses a separate pipe `BebekonVPN.v1.test.<PID>` and runtime directory to isolate it from the installed service. The normal UI trusts only the Running own-process service registered with Windows SCM, matching its PID to the pipe server before sending any request. `Bebekon.App.exe --smoke` uses its own instance mutex and creates isolated synthetic UI fixtures and screenshots in artifacts/ui-smoke, never in the user's state.

Version 0.1.6 fixes a false service-identity rejection when the ordinary UI cannot inspect a LocalSystem process. No elevation is needed for the new SCM status check. `dotnet run --file tools/verify-service.cs -c Release` explicitly tests five authenticated status requests against an installed helper assigned to the current ordinary user. Add `-- --probe` to validate the saved profile and test HTTPS/IP through a temporary proxy core; it stops the core afterward and does not change Windows routes, proxy or saved state.

## Version 0.1.7

Home now shows live download/upload rates and totals, and the server and mode cards are clickable. Scroll down for all subscription servers, search by server/subscription, sort, favorites, individual/batch ping and refresh. The list is virtualized, and the wheel returns to the dashboard at its edges. The white-and-blue running snowman is used in the sidebar, EXE, tray and installer; its source and generation prompt are in resources/icons.

Refreshing subscriptions preserves the selected logical server, favorites and valid ping results across renaming/reordered parameters. A removed selection stays in the list instead of switching automatically. Real credential/endpoint changes apply once to the same logical selection. Recovery observes physical uplink changes and resume, ignores self-generated tunnel events, and requires two failed checks before restarting.

## Using it

1. Add Ultima's subscription URL (HTTPS recommended), direct VLESS link, or a URL returning plain/Base64 VLESS links or JSON Xray configurations. The provider URL does not need to be converted to a different format.
2. Choose a supported server. The client explicitly marks unsupported transports.
3. Open Rules, leave **By rules**, add OpenAI / ChatGPT, Claude / Anthropic and Telegram.exe.
4. Connect. A real HTTPS probe through the selected server must succeed before the UI shows Protected.

Choose **Rules → Add preset → Правила админа** to add the 42 rules from the supplied screenshots: 36 VPN and 6 Direct entries. Direct exceptions are inserted first; applying the preset again does not duplicate it. `store.supercell.com` is represented as a domain suffix because it is a website, not an executable. The two case variants of Telegram.exe from the reference list are preserved.

The rule editor supports Application, GeoSite, Domain suffix, Domain keyword, GeoIP and IP-CIDR; a name is optional. Its List button selects running/installed applications or available embedded geo sets. **Auto — selected server** follows the main selection; a specific server routes both matching traffic and DNS through that node. Missing server references require editing the rule, with no silent fallback. Direct rules do not retain a VPN server selection. Per-rule server IDs are included in profile exports, so imported profiles may need their server selections updated. 13 service GeoSite sets and 15 country GeoIP sets plus Telegram IP ranges are bundled; sources/hashes and licenses are in resources/geo.

Measured pings use green (≤100 ms), orange (101–200 ms) and red (>200 ms); unmeasured values remain muted. Logo and connection halos respect the glow setting. Modal dialogs blur their owner and restore it on close; the connection animation pauses behind a modal. Text, icon and rule-choice buttons have independent minimum sizes.

An off rule means **Direct**, not disabled. Higher rules win. Drag a rule row to reorder. Exact process paths are used when known; an executable name is the fallback when only a name is entered. Each profile owns its ordered list and default route. Import/export is this application's JSON, without subscription secrets.

Changes to server, profile, rule, routing mode, or saved DNS settings regenerate the core configuration and reconnect. Rapid edits are coalesced into a serialized stop/start operation; edits during startup are applied afterward. Manual disconnect cancels pending reconfiguration, and stale recovery/IP/status results cannot overwrite a newer session. Applying changes closes existing connections; it does not migrate sockets. Proxy mode configures Windows' per-user system proxy on 127.0.0.1:17890 and restores the prior settings on disconnect/next startup. Per-process routing requires TUN.

Appearance settings apply immediately: four coordinated accent palettes, smooth interaction/page/sidebar animations, connection glow and an OLED black background. Motion respects the Windows animation preference and can be disabled. State-bound looping animations stop when their view is hidden, minimized or unloaded. Country flags are bundled locally from [Flagpedia / FlagCDN](https://flagpedia.net/download/api), recognizing ISO codes, emoji and Russian/English provider labels; a flag reflects the provider label, not verified geolocation. Subscriptions can be pasted directly into the inline field or added through the named-subscription dialog.
## Core and DNS

Pinned official stable **sing-box 1.14.2**, Windows amd64. Archive SHA256 is in core/version.json. Documentation snapshots in docs/ come from the exact v1.14.2 tag. The bundled executable is downloaded and checked by build.ps1. To update: review official tagged docs and release notes, change version/archive/hash, rerun core schema and routing tests and repeat the Windows acceptance matrix. Do not replace just the executable without validating fields.

- TUN captures IPv4 and IPv6; auto_detect_interface binds outbound traffic to the real uplink.
- DNS hijack and 300ms HTTP/TLS/QUIC sniffing precede the ordered user routes.
- Direct and VPN DNS are separate typed HTTPS servers. Domain DNS rules follow the user order; selective final DNS is Direct, entire-PC final DNS is VPN.
- Native Windows DNS protection uses strict_route; Docker/VM compatibility relaxes strict_route.
- GeoSite and GeoIP use trusted embedded inline rule sets, without legacy geoip/geosite fields or deprecated inbound sniff configuration. No MITM, certificate bypass, telemetry or geolocation service.
- Exact ping (the default) is a median of three small HTTPS requests through an authenticated local SOCKS5 probe into an isolated real VLESS outbound. TCP (port) measures TCP connection time; another active VPN may intercept that handshake and understate remote latency. The caches are distinct, 3-minute TTL, concurrency 2 for exact probes and 6 for TCP. Every active measurement has one five-second deadline, including startup and all three requests; queued probes are labelled separately. Failures/timeouts are red. Click a ping on Home or a server card to recalculate it. Checks can be cancelled. Nodes never show invented latency.
- VPN IP comes from api.ipify.org through a forced VPN probe inbound, irrespective of user rules. This shares only the egress IP with the IP-check endpoint. No subscription/UUID is sent to it. IP lookup runs outside the connection gate. Live speed and session totals come from actual core byte counters, including VPN and Direct routes. Samples refresh once per second; missing/stale counters show “—”, and disconnect resets them.

## Storage and security

%LOCALAPPDATA%/BebekonVPN/state.dpapi contains the whole user state protected with DPAPI CurrentUser. Runtime mirror: runtime/sing-box.dpapi. Logs: logs/app.log, logs/core.log, logs/service.log. Rotation: 1MB, 3 archives. Raw core messages are not persisted, because they can contain private destinations; diagnostics intentionally disclose less detail.

Service runtime and service/core logs are in %PROGRAMDATA%/BebekonVPN, protected to SYSTEM/Administrators. The service writes its own fixed sing-box.json and removes it on disconnect/core exit. It never accepts raw JSON, executable paths, arbitrary config paths or command-line arguments from the UI. Local pipe ACL allows only the installation owner and SYSTEM, denies network logons, and additionally checks the impersonated SID. The UI verifies the pipe server's PID against the registered Running own-process service using SCM query-status access. Core children belong to kill-on-close jobs. Traffic sampling uses a service-private loopback Clash API port and a new 256-bit bearer secret per session. The secret stays in service memory/protected runtime config, never IPC, user state or logs. Only aggregate counters cross IPC; no web dashboard is configured.

## Validation and current limits

Measured on Windows 11 x64: version 0.1.5 Release self-contained UI in an ordinary disconnected idle process before render captures, without Working Set trimming. The helper figure is the earlier isolated helper benchmark; actual connected TUN memory is still pending:

| State/process | Working Set | Private memory | CPU |
|---|---:|---:|---:|
| UI, disconnected idle | 152.4 MB | 112.0 MB | 0.000% over 5s |
| Helper harness, no core | 35.3 MB | 8.3 MB | 0.00% over 5s |
| Connected UI + service + core | pending | pending | pending |

The desired ≤90MB UI target is **not met**. No process is hidden or trimmed. CPU is normalized to the whole machine. Screenshot rendering allocates extra surfaces and is excluded from idle measurements. Real provider/TUN combined memory is unmeasured.

135 automated tests cover plain/Base64/JSON Xray subscription parsing, models, DPAPI, priority, generated config checks, actual loopback VLESS traffic, HTTP Host sniffing, helper/core crash detection, job cleanup, elevation command boundaries and service identity rejection cases. A supplied Ultima subscription was also imported live: nine supported nodes, nine configurations accepted by the official core, and a successful forced-VPN HTTPS probe through gRPC/Reality. Version 0.1.6 also verified authenticated commands and a live saved-profile HTTPS/IP probe through the installed LocalSystem helper from an ordinary UI identity. Full TUN/browser acceptance remains in docs/VALIDATION.md.

Known limits:

- XHTTP, mKCP and unfamiliar VLESS extensions are shown as unsupported; current official stable transport support is followed.
- Plain/Base64 VLESS and JSON Xray VLESS subscriptions are supported. JSON imports server settings, not provider listeners, routes or DNS. Unsupported connection options are marked on the affected node. YAML, sing-box provider JSON and other VPN protocols are not supported. Redirected subscription URLs are rejected to avoid leaking secret URLs.
- TLS ECH hides SNI; encrypted names cannot be sniffed. Browser DoH plus ECH can prevent domain selection. No promise to infer names from arbitrary encrypted traffic.
- The basic Telegram preset covers websites; the admin preset also includes Telegram.exe and the official Telegram IP ranges. Process DNS from the Windows DNS Client service cannot always be attributed to the original program; use TUN and domain rules alongside process rules where needed.
- Domain-only routing does not cover hard-coded IPs without an observable hostname. Add IP/subnet or application rules.
- Full-VPN is not a kill switch after a core crash; Windows resumes its normal route when the TUN closes.
- RAM targets are goals; any missed target is reported, without trimming Working Sets or hiding processes.
- Sleep/wake and availability changes trigger status/probe verification and a controlled reconnect when needed. Physical adapters, DoH/ECH behavior, UAC installation, upgrade and clean-machine testing need the manual matrix.
- Unsigned personal build: no Authenticode signing key was provided.
- UI Russian/English navigation is supported; some validation/detail strings currently remain Russian.

sing-box is GPLv3; its license is distributed in core/LICENSE. If redistributing this application/core, provide the corresponding sources and comply with dependency licenses. This repository is provided under GPL-3.0-or-later (LICENSE).
