# Night Track

The main screen follows the user's selected third concept: a midnight-blue dashboard with an alpine road and the existing running snowman mascot. The sidebar, controls, cards, settings and dialogs share a restrained blue palette. Active navigation uses a slim marker; the primary connection action remains large and explicit.

The illustration contains no interface text or controls. Connection state, selected server, routing mode, TUN/Proxy switches, traffic measurements and server rows are native WPF bindings. Traffic charts display up to 48 measured samples per direction; repeated timestamps are ignored, and disconnection clears the history. They have no independent timer.

The table includes the subscription, protocol, ping and favorite action. Card layout remains available. Home keeps navigation from the selected server, the routing-mode chooser, clickable ping and a scrollable list of servers from all subscriptions. No synthetic server-load data is displayed.

The blue theme is adopted once when migrating existing settings. Subsequent custom accent selections persist. OLED, Windows reduced-motion preferences and the app's animation/glow switches remain supported. Loading rotation and connection motion stop when hidden, minimized, unloaded or covered by a modal dialog.

The hero is embedded from `resources/design/night-track.png` and decoded to 1536 pixels wide. Its built-in ImageGen prompt and references are recorded in `resources/design/night-track.asset.json`. The sidebar scenery uses native vector geometry, and the existing application icon is retained.

The WPF smoke harness renders disconnected, connecting, connected and error states, English and minimum-window layouts, all pages and the shared dialogs. Its fixtures use isolated storage and do not install or connect a live VPN.
