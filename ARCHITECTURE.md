# Architecture

    WPF App (ordinary user, lazy pages, MVVM)
       → local Named Pipe (framed messages, owner ACL, SID check)
       → Windows Service (LocalSystem, demand start)
       → sing-box 1.14.2 (checked config, child job)
       → TUN → ordered rules → VLESS VPN or Direct

App owns the DPAPI-protected subscription, servers, profiles and settings. Service owns the active privileged core and networking lifetime. Core is the only VLESS/TUN implementation; C# does not implement VPN cryptography.

Core library is shared by the UI, service and tests. ConfigGenerator maps human rule kinds to domain_suffix, domain_keyword, process_path/process_name and ip_cidr. One ordered list drives routing and compatible DNS matching; it is never partitioned into a list of VPN rules followed by Direct exceptions. FINAL=Direct by default; FINAL=VPN only for explicit Entire PC mode. Protocol sniff and DNS capture precede the list, while the authenticated probe inbound is always forced through VPN.

The app sends validated **models**, not user-selected paths or raw sing-box configs, across the privileged boundary. The service generates a protected, fixed runtime config and checks it before launch. No HTTP control API is exposed. Its state tracks a real child process and its startup signal; the UI additionally requires a successful outbound HTTP probe. A job closes all child cores if the parent unexpectedly terminates.

Connection commands serialize through a semaphore. User edits debounce for 650ms and trigger controlled reconnects. Stats/status refresh once per second only while connected. Disconnected UI has no service polling; the helper stops itself after 45 seconds without active VPN or commands. Server pings run on opening Servers or explicit request with max concurrency 6, a 3-minute cache per mode, cancellation when leaving the page. Lists recycle only visible containers. Vector flags and icons need no web engine.

User state and profiles are distinct from runtime core configuration. Profile exports contain no provider UUIDs or subscription URLs. The user runtime mirror remains encrypted. Service runtime is accessible only to administrators and SYSTEM, removed on shutdown, and never handed to an external API. Core log output is summarized rather than saved verbatim to prevent destination/secret disclosure.

Proxy is an alternative mode for proxy-aware applications. Its mixed inbound is localhost-only; the UI snapshots existing proxy settings with DPAPI and restores them. It cannot substitute for process-based TUN routing. The authenticated dynamic probe port is separate from the user system proxy and bypasses user rules so both ping and IP reporting measure the chosen VPN server.

Setup performs service registration once with elevation, binds access to the intended Windows account and grants that user service-start/query rights. Core/app/service binaries live under Program Files. The portable build includes the same helper installation scripts; privileged service execution requires a trusted installation directory.
