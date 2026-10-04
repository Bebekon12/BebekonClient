# Android dependency notices

The application follows the repository's GPL-3.0-or-later license. sing-box and the Bebekon libbox additions are distributed with the corresponding source tree and reproducible build instructions. The pinned upstream commit, toolchain, build tags and map provenance are in `../core/dependencies.json`.

- sing-box / sing / sing-tun: SagerNet, GPL-3.0-or-later; see the upstream source's individual notices and `sing-box-NOTICE.txt`.
- gVisor, including SagerNet's pinned fork: The gVisor Authors, Apache-2.0. Version is pinned by the upstream sing-box `go.mod` and `go.sum`.
- AndroidX and Jetpack Compose: The Android Open Source Project and AndroidX contributors, Apache-2.0.
- SnakeYAML Engine: SnakeYAML contributors, Apache-2.0.
- ZXing and ZXing Android Embedded: ZXing authors and JourneyApps contributors, Apache-2.0.
- Kotlin runtime and kotlinx.coroutines: JetBrains and contributors, Apache-2.0.
- Natural Earth map geometry: public domain. Geometry is stored locally; no map server is contacted.
- Flags, GeoSite/GeoIP presets and the Bebekon snowman reuse the existing desktop repository resources and their accompanying notices.

Full GPL-3.0 and Apache-2.0 license texts are included here and in the APK assets. All module versions and transitive source dependencies can be reproduced from the Gradle declarations and the pinned core's `go.mod` / `go.sum`.
