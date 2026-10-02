# Validation record — 2026-10-03

Environment: Windows 11 x64, .NET SDK 10.0.300, runtime 10.0.8, sing-box 1.14.2.

## Executed

- Release solution compilation.
- 49 parser/model/storage/config and real-core tests, including the helper crash suite and elevation command boundaries.
- WPF smoke harness checks that startup selects TUN even after a saved Proxy session. Elevated registration uses the original user's SID and literal argument list; partial packages are rejected before elevation. Actual UAC acceptance/cancellation and protected service installation remain manual checks.
- Official `sing-box check` accepted generated TCP, Reality, gRPC, WebSocket, HTTPUpgrade configurations.
- Real local VLESS fixture confirmed: selected domain exits via VLESS; unmatched domain Direct; Entire PC default VPN; authenticated forced VPN probe; HTTP Host sniffing routes an IP-addressed request by its hostname.
- Helper detects core crash on the next status snapshot; killing the helper kills its child core through a Windows job object.
- Published self-contained helper responds over the ACL-protected Named Pipe (35.3MB Working Set / 8.3MB private, 0.00% idle CPU over 5s).
- All five WPF pages rendered at 100/125/150/175% pixel scale. These are render checks, not physical per-monitor DPI switch tests.
- 503-rule UI fixture realized only 3 list containers (recycling virtualization).
- Version 0.1.1: Settings categories, search across categories, empty results, scrolling and TUN/Proxy exclusivity are exercised by the WPF harness. All pages also render at 860×660, settings in English, and five native dialogs at 100/175% scale. Dialog application discovery skips inaccessible/reparse Start Menu folders. Smoke and console helper instances are isolated from the user's open app and installed service.
- Version 0.1.2: the tray menu is rebuilt 100 times without the collection-modified exception; old entries and submenus are disposed, and server selection/profile checks remain correct. Caption buttons are exercised for 46×36 hit areas, minimize, maximize/restore, corresponding icon/tooltips, Russian/English labels and close-to-tray behavior. The original disposal-during-enumeration code reproduced the reported exception in an isolated WinForms menu. Main-window and dialog close icons are rendered with the shared caption style.
- Version 0.1.0 ordinary framework-dependent Release UI idle: 145.7 MB Working Set, 109.3 MB private, 0.174% machine-normalized CPU over 3 seconds.
- Version 0.1.0 published self-contained Release UI idle: 146.1MB Working Set, 108.1MB private, 0.03% CPU over 5 seconds. **90MB target missed**. Version 0.1.1/0.1.2 screenshot harness peak is higher and is not an idle measurement; a new ordinary idle benchmark is pending.
- Self-contained app launches without using an installed runtime; service runtime merging preserves WPF assemblies. Installer compilation and portable ZIP generation succeed; privileged installation itself is pending.

## Required manual acceptance — pending

No Ultima subscription was supplied. This session is not elevated. Do not label the following as passed:

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
