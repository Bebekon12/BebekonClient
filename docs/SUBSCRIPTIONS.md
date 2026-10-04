# Subscription formats and device binding

Bebekon VPN 0.1.14 accepts direct VLESS, VMess, Shadowsocks, Trojan, Hysteria and Hysteria 2 / `hy2` links, mixed newline-separated links, plain or URL-safe Base64 subscription text, Xray JSON profiles/arrays, sing-box JSON outbounds and Clash/Mihomo YAML or JSON containing inline `proxies`. Add the provider URL on Subscriptions; converting it through an external service is unnecessary. Pasted configuration text uses the same importer. A new subscription has an empty optional name: saving without a name derives it from the URL hostname (or uses the protocol / Subscription for pasted text); editing retains the existing name.

Supported protocols:

- VLESS: TCP, TLS/Reality, gRPC, WebSocket, HTTP/2, HTTPUpgrade and **XHTTP** (`auto`, `packet-up`, `stream-up`, `stream-one`). XHTTP uses bundled official Xray through authenticated loopback bridges; routing/DNS/counters remain in sing-box. TLS `pcs` certificate SHA-256 pinning and `vcn` verification names are supported. XHTTP insecure TLS and packetaddr encoding are rejected rather than silently downgraded. Common `extra` headers, padding, POST/stream limits and XMUX tuning are validated. Separate `downloadSettings` and other unrecognized tuning are explicitly rejected.
- VMess: AEAD and legacy alterId, supported ciphers, TCP/TLS, gRPC, WebSocket, HTTP/2 and HTTPUpgrade. Standard Base64 JSON links and UUID URI links are accepted.
- Shadowsocks: SIP002 Base64 or percent-encoded credentials, legacy full Base64 links, classic AEAD and Shadowsocks 2022 ciphers; built-in `obfs-local` / `v2ray-plugin` options. External plugin executables are forbidden.
- Trojan: password authentication, TLS, TCP, gRPC, WebSocket, HTTP/2 and HTTPUpgrade.
- Hysteria 1 / 2: UDP/QUIC, authentication, bandwidth, supported obfuscation and port hopping. Hysteria 1 defaults missing bandwidth to 100 Mbps; Hysteria 2 retains BBR when bandwidth is absent. Explicit insecure TLS in these new protocol imports is retained and visibly labeled `TLS без проверки`; it is never enabled automatically. Existing VLESS URI certificate-verification restrictions remain.

 Names, endpoints, UUIDs, SNI, browser fingerprint, flow, ALPN, transport path/Host/service name and UDP flags/packet encoding are retained. A valid nil UUID is accepted; a wildcard destination such as `0.0.0.0` is an informational placeholder and cannot connect. Unsupported transport/security options remain visible on the affected server with a reason; unknown inline protocols stay visible with an unsupported reason; external `proxy-providers` give an explicit import error. HTTP/1 camouflage is distinct from HTTP/2 and is not silently converted. Hysteria fake TCP, certificate `pinSHA256`, ECH, Realm, external plugins, arbitrary dialer chains and unsupported advanced options are not silently downgraded. TCP/ICMP probes test endpoint reachability only; HTTPS GET tests the actual selected protocol, including QUIC and XHTTP, with the existing five-second budget.

## Provider rules

A full Clash profile can also include DNS, TUN options, proxy groups and routing rules. Import extracts servers and retains Bebekon's own rules/settings. It does not run the provider configuration or fetch its external files. For example, `MATCH,Provider` is a catch-all route to a provider selector; choosing **Entire PC** in Bebekon expresses that intent. **By rules** retains the user's Direct default and selected VPN exceptions. Refresh preserves the selected server and favorites through the existing merge logic.

## HWID

Some providers enforce device binding. Their response can be HTTP 200 with dummy servers and instructions to enable HWID, rather than an ordinary HTTP error. The subscription client now sends the standard `x-hwid`, `x-device-os` and `x-ver-os` headers over HTTPS. Loopback HTTP endpoints also receive them for integration tests; public HTTP endpoints do not receive device identifiers. Use HTTPS with device-bound providers.

The HWID is `win-` plus 32 hexadecimal characters derived using SHA-256 from a canonical Windows MachineGuid and the fixed Bebekon application namespace. The raw Windows identifier, computer name, hardware serials and MAC addresses are not sent. The namespace stays fixed across versions: refreshing, elevation, changing the Windows account and reinstalling Bebekon on the same Windows installation retain the same ID. Reinstalling Windows can change it. A device registered by another application can occupy a separate provider device slot; remove obsolete devices in the provider's account interface if needed. HWID is never rotated automatically to circumvent limits.

Device-limit and unsupported-HWID response headers are checked before parsing, including HTTP 404 responses. These show actionable messages instead of format errors. Wildcard-only placeholder lists are rejected even when the provider omits these headers. Arbitrary provider announcements are never interpreted as commands.

The request identifies itself as `clash.meta BebekonVPN/<version>` for Mihomo format compatibility. Providers can still restrict allowed clients, subscription status and device count. Download limits cover the entire response (20 seconds, 4 MiB); YAML depth/event/alias traversal is bounded, cyclic aliases and duplicate fields are rejected, and parser errors omit credentials. Redirects remain disabled.

## Sources

- [sing-box native outbounds](https://sing-box.sagernet.org/configuration/outbound/) and [Hysteria 2 URI specification](https://v2.hysteria.network/docs/developers/URI-Scheme/).
- [Pinned Xray 26.3.27](https://github.com/XTLS/Xray-core/releases/tag/v26.3.27) and its [XHTTP configuration implementation](https://github.com/XTLS/Xray-core/blob/v26.3.27/infra/conf/transport_internet.go); MPL-2.0 license included in `core/LICENSE-Xray`.

- [Mihomo VLESS fields](https://wiki.metacubex.one/en/config/proxies/vless/), [TLS fields](https://wiki.metacubex.one/en/config/proxies/tls/) and [transport options](https://wiki.metacubex.one/en/config/proxies/transport/).
- [Mihomo VLESS implementation](https://github.com/MetaCubeX/mihomo/blob/Meta/adapter/outbound/vless.go): UDP defaults and transport distinctions.
- [Remnawave's HWID contract for app developers](https://github.com/remnawave/panel/blob/main/docs/features/hwid-device-limit.md): required/optional headers, accepted identity format and device-limit response flags.
- [sing-box VLESS](https://sing-box.sagernet.org/configuration/outbound/vless/) and [pinned core implementation](https://github.com/SagerNet/sing-box/blob/v1.14.2/protocol/vless/outbound.go): packet encoding and schema mapping.
- [YamlDotNet 18.1.0](https://www.nuget.org/packages/YamlDotNet/18.1.0), MIT license included in `licenses/YamlDotNet.txt`.
