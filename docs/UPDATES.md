# Application updates (0.1.8+)

Install 0.1.8 once over the existing application. Older versions do not contain an updater. Settings → Updates checks at startup (after 20 seconds) and every six hours. A newer release appears in a banner and optionally a tray notification. “Update and restart” downloads and verifies the package, disconnects VPN, upgrades the same directory and restarts the UI. Windows still requests UAC for the protected helper. No uninstall step is used; subscriptions, rules and settings remain in the user's encrypted state store.

This local build uses `E:\BebekonClient\dist\update.json`. New builds produced here appear without manually finding Setup. This works on this PC while that folder is available; it is not an Internet release service. Change the source in Settings → Updates to an HTTPS feed or another local update.json.

## Publishing

1. Increment Directory.Build.props and installer/setup.iss together.
2. Run `./build.ps1 -PublishFolder artifacts/release-updates`. It keeps versioned installers and atomically writes a signed dist/update.json only after the installer exists. Never replace an already published version with different content.
3. For Internet distribution, pass `-UpdateFeedUrl https://your-host/releases/update.json`. Upload the versioned installer first, then update.json in the same directory. Optionally pass `-InstallerUrl` for another HTTPS location, such as a GitHub Release asset. HTTPS-only redirects are supported. No hosting account is configured here.
4. Keep the feed URL stable and retain previous packages while clients may still download them. `-ReleaseNotes` supplies the user-visible description.

The RSA-3072 public key is embedded in the app. The private key stays outside the repository in `%LOCALAPPDATA%\BebekonReleaseKeys\release-key.dpapi`, encrypted for the publishing Windows user; it is never packaged. Back up the key and its Windows DPAPI recovery context securely before moving the build machine. The DPAPI file alone is not portable to another user. A missing/mismatched key fails signing, never silently rotates trust. `scripts/publish-update.ps1 -InitializeKey` initializes a new product only when no public key exists.

The signature binds version, installer location, size, SHA-256 and notes. Downloads enforce size/deadline limits and delete partial or invalid packages. The EXE is verified again and held read-only during launch. Equivalent/older versions are not offered. Setup signals readiness before the app exits, waits for that process, preserves the registered helper owner SID, then upgrades the helper. Cancelling before handoff keeps the app open; Setup does not force-kill it.

This is a full in-place upgrade, not a differential patch. See [Inno Setup command-line parameters](https://jrsoftware.org/ishelp/topic_setupcmdline.htm) for silent progress and no-reboot behavior. UAC is not bypassed. Automated smoke does not replace the user's running installation; full upgrade/UAC/rollback acceptance belongs on a separate machine or VM.
