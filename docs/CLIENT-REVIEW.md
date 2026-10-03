# Review of comparable clients — 2026-10-04

This review covers documented mechanisms and source code, not an exhaustive audit of third-party clients. It informed the 0.1.12 fixes without copying another interface.

| Primary source | Mechanism reviewed | Bebekon assessment/change |
|---|---|---|
| [v2rayN SpeedtestService](https://github.com/2dust/v2rayN/blob/master/v2rayN/ServiceLib/Services/SpeedtestService.cs) | Separate TCP and real proxy tests; cancellation, bounded parallel batches, unfinished-result cleanup | Four explicit methods, shared five-second active deadline, per-method cache; fixed stale UI results after method changes/endpoint replacement |
| [sing-box URLTest](https://sing-box.sagernet.org/configuration/outbound/urltest/) | HTTP endpoint test, default gstatic generate_204, tolerance and idle test suspension | HTTPS GET is recommended/default; HTTPS HEAD available; TCP/ICMP clearly described as host/port reachability. Tests never select a different server automatically |
| [mihomo rules](https://wiki.metacubex.one/en/config/rules/) | Ordered first-match routing rules | Newest-first display is independent of execution priority; priority view explicitly enables drag reordering |
| [mihomo general configuration](https://wiki.metacubex.one/en/config/general/) | Selected-server persistence, process matching, protected local API | Existing stable identity-based subscription merge and pinned-server DNS retained; existing bearer-authenticated service-private loopback traffic API retained |
| [Clash Verge Rev releases](https://github.com/Clash-Verge-rev/clash-verge-rev/releases) | Current desktop client development context | Reviewed alongside the mechanisms above; no third-party UI or code copied |

Additional fixes found in our implementation: the home list had a nested scroll surface; the collapsed sidebar's inner width was smaller than its logo/status content; the routing dock had a shorter hover surface than the server dock; a virtual VPN adapter could be chosen as the local IP. Home now has one recycling virtualized list, responsive controls, equal dock geometry, and a physical-uplink address filter.

Validation covers local VLESS routing fixtures, rule/DNS precedence, refresh stability, serialized rule application, GET/HEAD verbs and failure/cancellation, loopback ICMP/TCP and cache separation, and a 1000-server UI fixture. These checks do not prove end-to-end behavior on every provider or replace live Windows TUN acceptance.
