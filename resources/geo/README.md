# Bundled routing data

GeoSite and country GeoIP sets are decompiled from official SagerNet binary rule sets with sing-box 1.14.2. The application embeds their JSON rules as modern inline rule sets; no runtime downloads or user-writable rule paths are used by the privileged service.

- GeoSite: https://github.com/SagerNet/sing-geosite at `417ea33286a8c3a77fdce9b68ce25d58565dad43`.
- Country GeoIP: https://github.com/SagerNet/sing-geoip at `7fe82a879ad2666526730c195b55a6d8d9147908`.
- Telegram IPv4/IPv6: https://core.telegram.org/resources/cidr.txt retrieved 2026-10-03.

See `sources.json` for exact URLs and SHA-256 hashes. Upstream SagerNet license: `LICENSE-SagerNet` (GPL v3 or later); repository license terms also apply. Data changes over time: maintainer updates use `tools/update-geo.ps1` with reviewed pinned commits. The editor lists only bundled sets. Telegram is a service IP set, not a country code.
