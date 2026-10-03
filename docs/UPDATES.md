# Application updates (0.1.8+)

Install 0.1.8 once over the existing application. Older versions do not contain an updater. Settings → Updates checks at startup (after 20 seconds) and every six hours. A newer release appears in a banner and optionally a tray notification. “Update and restart” downloads and verifies the package, disconnects VPN, upgrades the same directory and restarts the UI. Windows still requests UAC for the protected helper. No uninstall step is used; subscriptions, rules and settings remain in the user's encrypted state store.

Version 0.1.9+ defaults to `https://github.com/Bebekon12/BebekonClient/releases/latest/download/update.json`. Each release contains a signed manifest and a versioned installer; the manifest refers to the immutable version tag, so publishing a later release cannot redirect an in-progress download to the wrong installer. Version 0.1.8 used `E:\BebekonClient\dist\update.json`; that feed also offers the next release, after which an unset source follows the new bundled GitHub default. Explicit user-selected channels remain unchanged. The source can be changed in Settings → Updates.

## Publishing

1. Increment Directory.Build.props and installer/setup.iss together.
2. Run `./build.ps1 -PublishFolder artifacts/release-updates`. It keeps versioned installers and atomically writes a signed dist/update.json only after the installer exists. Never replace an already published version with different content.
3. The default build targets Bebekon12/BebekonClient on GitHub. Create a draft release for the matching version tag, attach the versioned Setup, portable ZIP, update.json and checksums, and publish it as latest only after all uploads succeed. For another host, pass `-UpdateFeedUrl https://your-host/releases/update.json` and optionally `-InstallerUrl`. HTTPS-only redirects are supported.
4. Keep the feed URL stable and retain previous packages while clients may still download them. `-ReleaseNotes` supplies the user-visible description.

The RSA-3072 public key is embedded in the app. The private key stays outside the repository in `%LOCALAPPDATA%\BebekonReleaseKeys\release-key.dpapi`, encrypted for the publishing Windows user; it is never packaged. Back up the key and its Windows DPAPI recovery context securely before moving the build machine. The DPAPI file alone is not portable to another user. A missing/mismatched key fails signing, never silently rotates trust. `scripts/publish-update.ps1 -InitializeKey` initializes a new product only when no public key exists.

The signature binds version, installer location, size, SHA-256 and notes. Downloads enforce size/deadline limits and delete partial or invalid packages. The EXE is verified again and held read-only during launch. Equivalent/older versions are not offered. Setup signals readiness before the app exits, waits for that process, preserves the registered helper owner SID, then upgrades the helper. Cancelling before handoff keeps the app open; Setup does not force-kill it.

This is a full in-place upgrade, not a differential patch. See [Inno Setup command-line parameters](https://jrsoftware.org/ishelp/topic_setupcmdline.htm) for silent progress and no-reboot behavior. UAC is not bypassed. Automated smoke does not replace the user's running installation; full upgrade/UAC/rollback acceptance belongs on a separate machine or VM.
