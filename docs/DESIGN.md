# Night Track

The main screen follows the user's selected third concept: a midnight-blue dashboard with an alpine road and the existing running snowman mascot. The sidebar, controls, cards, settings and dialogs share a restrained blue palette. Active navigation uses a slim marker; the primary connection action remains large and explicit.

The illustration contains no interface text or controls. Connection state, selected server, routing mode, TUN/Proxy switches, traffic measurements and server rows are native WPF bindings. Traffic charts display up to 48 measured samples per direction; repeated timestamps are ignored, and disconnection clears the history. They have no independent timer.

The table includes the subscription, protocol, ping and favorite action. Card layout remains available. Home keeps navigation from the selected server, the routing-mode chooser, clickable ping and a scrollable list of servers from all subscriptions. Home uses one virtualized scroll surface containing the overview and every server row. The shared toolbar wraps onto two rows at narrower widths. The selected server and routing mode share geometry and hover styling. Public VPN IP and the physical uplink’s local/private IP are labelled separately; the local address tooltip lists adapter and IPv4/IPv6 addresses. No synthetic server-load data is displayed.

The blue theme is adopted once when migrating existing settings. Subsequent custom accent selections persist. OLED, Windows reduced-motion preferences and the app's animation/glow switches remain supported. Loading rotation and connection motion stop when hidden, minimized, unloaded or covered by a modal dialog.

The hero is embedded from `resources/design/night-track.png` and decoded to 1536 pixels wide. Its built-in ImageGen prompt and references are recorded in `resources/design/night-track.asset.json`. The sidebar scenery uses native vector geometry, and the application/sidebar/tray/installer icon adds a dark navy trilby to the existing white/blue mascot; its built-in ImageGen edit prompt is recorded in `resources/icons/snowman.asset.json`. `scripts/build-icon.ps1` converts that PNG to a seven-size ICO.

The WPF smoke harness renders disconnected, connecting, connected and error states, English and minimum-window layouts, all pages and the shared dialogs. Its fixtures use isolated storage and do not install or connect a live VPN.
Rules show newest entries first without changing execution priority. The priority view retains drag reordering; entries without a creation timestamp retain their relative legacy priority. The [client review](CLIENT-REVIEW.md) records the source-backed behavior decisions. Settings uses the shared regular gear geometry, and the 76px sidebar keeps its logo/glow and status badge within bounds.
