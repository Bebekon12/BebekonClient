# Architecture

    WPF App (ordinary user, lazy pages, MVVM)
       → local Named Pipe (framed messages, owner ACL, SID check)
       → Windows Service (LocalSystem, demand start)
       → sing-box 1.14.2 (checked config, child job)
       → TUN → ordered rules → VLESS VPN or Direct

App owns the DPAPI-protected subscription, servers, profiles and settings. Service owns the active privileged core and networking lifetime. Core is the only VLESS/TUN implementation; C# does not implement VPN cryptography.

Theme.xaml owns the shared palette and native control templates. IconView renders cached outline geometry; SettingRow gives searchable settings a common layout. View filtering stays local to presentation, while switches bind to the shared view model. TUN and Proxy always select one mode. Dialogs use the same tokens, keyboard focus, Escape/cancel and native window chrome. UI rendering and console helper tests have separate instance/pipe identities and cannot operate on the installed service.

Core library is shared by the UI, service and tests. ConfigGenerator maps human rule kinds to domain_suffix, domain_keyword, process_path/process_name, ip_cidr and modern rule_set references. One ordered list drives routing and compatible DNS matching. The admin preset inserts its Direct exceptions first when applied; configuration generation preserves the resulting user order. FINAL=Direct by default; FINAL=VPN only for explicit Entire PC mode. Protocol sniff and DNS capture precede the list, while the authenticated probe inbound is always forced through VPN.

GeoCatalog reads only trusted embedded headless rule sets; the privileged service does not load user-writable data files or download geo data. RoutingRule.ServerId optionally pins a rule to a supported subscription node. ConnectSpec includes only referenced extra nodes, which are validated again by the service and mapped to generated outbound/DNS tags. Missing references are errors; the probe remains on the main selected server. RuleEditor builds a validated model only on Save. PresetCatalog creates independent rules, preserves intentional screenshot entries and prevents repeated application from duplicating existing rules.

The app sends validated **models**, not user-selected paths or raw sing-box configs, across the privileged boundary. The service generates a protected, fixed runtime config and checks it before launch. No HTTP control API is exposed. Its state tracks a real child process and its startup signal; the UI additionally requires a successful outbound HTTP probe. A job closes all child cores if the parent unexpectedly terminates.

Before sending any pipe request, the UI compares the kernel-reported pipe server PID with the registered helper's PID from Windows Service Control Manager. The helper must be Running and SERVICE_WIN32_OWN_PROCESS; zero/different PIDs and stopped/pending/shared services are rejected. SCM registration is protected by administrator permissions. The client needs only the owner's existing SERVICE_QUERY_STATUS right, not access to the LocalSystem process or service configuration. Server-side pipe ACL and impersonated-owner SID checks remain in place.

Connection commands serialize through a semaphore. User edits debounce for 650ms and trigger controlled reconnects. Stats/status refresh once per second only while connected. Disconnected UI has no service polling; the helper stops itself after 45 seconds without active VPN or commands. Server pings run on opening Servers or explicit request with concurrency 2 for exact VPN and 6 for TCP, a 3-minute cache per mode, cancellation when leaving the page. Lists recycle only visible containers. Bundled PNG flags and cached vector icons need no web engine.

User state and profiles are distinct from runtime core configuration. Profile exports contain no provider UUIDs or subscription URLs. The user runtime mirror remains encrypted. Service runtime is accessible only to administrators and SYSTEM, removed on shutdown, and never handed to an external API. Core log output is summarized rather than saved verbatim to prevent destination/secret disclosure.

Proxy is an alternative mode for proxy-aware applications. Its mixed inbound is localhost-only; the UI snapshots existing proxy settings with DPAPI and restores them. It cannot substitute for process-based TUN routing. The authenticated dynamic probe port is separate from the user system proxy and bypasses user rules so both ping and IP reporting measure the chosen VPN server.

TUN is selected on UI startup, including after a saved Proxy session. Before connecting, ServiceInstaller checks the registered helper version. If absent or older, it launches the bundled installation script through Windows UAC with an explicit argument list and the original UI owner's SID. A portable upgrade stops the old helper before replacing its loaded files. The UI waits for success, checks version/registration/owner and then starts the demand-start service. UAC cancellation produces an error; normal connections to a current helper do not request elevation.

Setup performs service registration once with elevation, binds access to the intended Windows account and grants that user service-start/query rights. Core/app/service binaries live under Program Files. Portable registration copies the helper/runtime to a fixed Program Files directory, rejects reparse points and applies Administrators/SYSTEM ownership/write permissions before registering LocalSystem execution. The user-facing EXE remains in the extracted folder and runs under the ordinary account.
