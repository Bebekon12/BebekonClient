# Bebekon VPN

Windows 10/11 x64, .NET 10, WPF, native UI. Default routing is **selected rules → VPN; everything else → Direct**.

## Build and install

Open Bebekon.sln in Rider. Install .NET SDK 10 (global.json pins 10.0.300). Run:

    .\build.ps1

The script verifies the pinned core archive, restores packages, runs tests, publishes both executables self-contained, compiles Inno Setup and produces:

- dist/BebekonVPN-Setup-x64.exe
- dist/BebekonVPN-Portable-x64.zip

No .NET runtime is needed on the destination PC. Build needs internet for NuGet and official downloads. Inno Setup 6.7.3 is bootstrapped per user into .tools when no compiler is installed. Override with -InnoCompiler PATH. -SkipInstaller produces only portable output.

Version 0.1.2 fixes the collection-modified exception on repeated tray menu openings. Window buttons have larger icons, visible hover/pressed states, a red close hover and a changing maximize/restore icon. Version 0.1.1 unified the dark palette, controls and dialogs, with searchable settings categories. To publish while another build folder is in use, run `./build.ps1 -PublishFolder artifacts/release-fix`; normal builds still use artifacts/release.

Setup installs into Program Files, registers a demand-start LocalSystem helper, assigns its pipe and service-start rights to the chosen Windows account, and creates shortcuts. If UAC uses a different administrator account, enter the ordinary user's Windows account on the owner page. Only installation/uninstallation needs elevation; connecting from the UI does not. Upgrades preserve %LOCALAPPDATA% data.

The launch executable is **artifacts/release/Bebekon.App.exe** after building, or **Bebekon.App.exe** in the extracted portable ZIP. Keep its adjacent runtime, scripts and core folders together.

TUN is selected automatically at each launch. Proxy can still be selected manually for the current session. If the service is missing, the first connection automatically opens Windows' UAC prompt and installs the helper; confirm that prompt once. The UI waits for installation, then continues connecting. Cancelling UAC produces a clear error and does not show Connected. With Setup, the helper is already installed, so connections need no elevation.

The portable helper/runtime is copied to a protected Program Files directory before service registration; its owner and ACL permit writes only to Administrators/SYSTEM. The original UI user's SID is passed before elevation, including when UAC uses a different administrator account. Manual fallback: run scripts/install-service.ps1 **once as administrator**, with -OwnerAccount 'COMPUTER\username' when elevating with another account. scripts/uninstall-service.ps1 removes the service and its protected copy. Portable means no app installer, not an unprivileged TUN driver.

## Development

    dotnet build Bebekon.sln -c Release
    dotnet run --project src/Bebekon.App -c Release
    dotnet test tests/Bebekon.Tests -c Release

For a dev connection, install the published service first. `Bebekon.Service.exe --console` is a local helper harness for tests; TUN still requires administrator rights. It uses a separate pipe `BebekonVPN.v1.test.<PID>` and runtime directory to isolate it from the installed service. The normal UI trusts only the registered service binary. `Bebekon.App.exe --smoke` uses its own instance mutex and creates isolated synthetic UI fixtures and screenshots in artifacts/ui-smoke, never in the user's state.

## Using it

1. Add Ultima's subscription URL (HTTPS recommended), direct VLESS link, or a URL returning plain/Base64 VLESS links.
2. Choose a supported server. The client explicitly marks unsupported transports.
3. Open Rules, leave **By rules**, add OpenAI / ChatGPT, Claude / Anthropic and Telegram.exe.
4. Connect. A real HTTPS probe through the selected server must succeed before the UI shows Protected.

An off rule means **Direct**, not disabled. Higher rules win. Drag a rule row to reorder. Exact process paths are used when known; an executable name is the fallback when only a name is entered. Each profile owns its ordered list and default route. Import/export is this application's JSON, without subscription secrets.

Changes to server, profile, rule, routing mode, or saved DNS settings regenerate the core configuration and reconnect. Applying changes closes existing connections; it does not migrate sockets. Proxy mode configures Windows' per-user system proxy on 127.0.0.1:17890 and restores the prior settings on disconnect/next startup. Per-process routing requires TUN.

## Core and DNS

Pinned official stable **sing-box 1.14.2**, Windows amd64. Archive SHA256 is in core/version.json. Documentation snapshots in docs/ come from the exact v1.14.2 tag. The bundled executable is downloaded and checked by build.ps1. To update: review official tagged docs and release notes, change version/archive/hash, rerun core schema and routing tests and repeat the Windows acceptance matrix. Do not replace just the executable without validating fields.

- TUN captures IPv4 and IPv6; auto_detect_interface binds outbound traffic to the real uplink.
- DNS hijack and 300ms HTTP/TLS/QUIC sniffing precede the ordered user routes.
- Direct and VPN DNS are separate typed HTTPS servers. Domain DNS rules follow the user order; selective final DNS is Direct, entire-PC final DNS is VPN.
- Native Windows DNS protection uses strict_route; Docker/VM compatibility relaxes strict_route.
- No geoip/geosite or deprecated inbound sniff configuration. No MITM, certificate bypass, telemetry or geolocation service.
- Exact ping is a median of three small HTTPS requests through an authenticated local SOCKS5 probe into an isolated real VLESS outbound. Fast ping is median TCP connect time; the caches are distinct, 3-minute TTL, concurrency 6. Nodes never show invented latency.
- VPN IP comes from api.ipify.org through a forced VPN probe inbound, irrespective of user rules. This shares only the egress IP with the IP-check endpoint. No subscription/UUID is sent to it. Throughput stays “—”: no fabricated speed or location.

## Storage and security

%LOCALAPPDATA%/BebekonVPN/state.dpapi contains the whole user state protected with DPAPI CurrentUser. Runtime mirror: runtime/sing-box.dpapi. Logs: logs/app.log, logs/core.log, logs/service.log. Rotation: 1MB, 3 archives. Raw core messages are not persisted, because they can contain private destinations; diagnostics intentionally disclose less detail.

Service runtime and service/core logs are in %PROGRAMDATA%/BebekonVPN, protected to SYSTEM/Administrators. The service writes its own fixed sing-box.json and removes it on disconnect/core exit. It never accepts raw JSON, executable paths, arbitrary config paths or command-line arguments from the UI. Local pipe ACL allows only the installation owner and SYSTEM, denies network logons, and additionally checks the impersonated SID. The UI verifies the pipe server's executable against the registered service image. Core children belong to kill-on-close jobs.

## Validation and current limits

Measured on Windows 11 x64, version 0.1.0 Release self-contained, without Working Set trimming. Version 0.1.2 has passed the expanded rendering and tray/caption checks; a new ordinary idle memory benchmark has not been run:

| State/process | Working Set | Private memory | CPU |
|---|---:|---:|---:|
| UI, disconnected idle | 146.1 MB | 108.1 MB | 0.03% over 5s |
| Helper harness, no core | 35.3 MB | 8.3 MB | 0.00% over 5s |
| Connected UI + service + core | pending | pending | pending |

The desired ≤90MB UI target is **not met**. No process is hidden or trimmed. CPU is normalized to the whole machine. Screenshot rendering allocates extra surfaces and is excluded from idle measurements. Real provider/TUN combined memory is unmeasured.

49 automated tests cover parsing, models, DPAPI, priority, generated config checks, actual loopback VLESS traffic, HTTP Host sniffing, helper/core crash detection, job cleanup and elevation command boundaries. Measured results and remaining manual checks are in docs/VALIDATION.md. A passing build and local VLESS fixture are **not** proof of the Ultima/TUN/browser acceptance scenario.

Known limits:

- XHTTP, mKCP and unfamiliar VLESS extensions are shown as unsupported; current official stable transport support is followed.
- Plain VLESS subscriptions only; JSON/YAML provider formats are rejected explicitly. Redirected subscription URLs are rejected to avoid leaking secret URLs.
- TLS ECH hides SNI; encrypted names cannot be sniffed. Browser DoH plus ECH can prevent domain selection. No promise to infer names from arbitrary encrypted traffic.
- Telegram presets cover websites; add Telegram.exe for its native/IP connections. Process DNS from the Windows DNS Client service cannot always be attributed to the original program; use TUN and domain rules alongside process rules where needed.
- Domain-only routing does not cover hard-coded IPs without an observable hostname. Add IP/subnet or application rules.
- Full-VPN is not a kill switch after a core crash; Windows resumes its normal route when the TUN closes.
- RAM targets are goals; any missed target is reported, without trimming Working Sets or hiding processes.
- Sleep/wake and availability changes trigger status/probe verification and a controlled reconnect when needed. Physical adapters, DoH/ECH behavior, UAC installation, upgrade and clean-machine testing need the manual matrix.
- Unsigned personal build: no Authenticode signing key was provided.
- UI Russian/English navigation is supported; some validation/detail strings currently remain Russian.

sing-box is GPLv3; its license is distributed in core/LICENSE. If redistributing this application/core, provide the corresponding sources and comply with dependency licenses. This repository is provided under GPL-3.0-or-later (LICENSE).
