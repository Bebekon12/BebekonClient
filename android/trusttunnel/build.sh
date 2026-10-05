#!/usr/bin/env bash
set -euo pipefail
cd /work
export CONAN_HOME=/work/conan2-cache
export ANDROID_NDK_HOME=/work/android-ndk-r30
export TT_SOURCE=/work/TrustTunnelClient
if [ ! -d "$ANDROID_NDK_HOME" ]; then
  curl --fail --location --retry 3 -o ndk.zip https://dl.google.com/android/repository/android-ndk-r30-linux.zip
  echo '5107f898313790e449e87eee2183d9a20602dee9  ndk.zip' | sha1sum --check -
  unzip -q ndk.zip && rm ndk.zip
fi
if [ ! -d "$TT_SOURCE" ]; then
  git clone --depth 1 --branch v1.1.7 https://github.com/TrustTunnel/TrustTunnelClient.git "$TT_SOURCE"
fi
test "$(git -C "$TT_SOURCE" rev-parse HEAD)" = 170609c24ca865819fed68437b01c013049bc3fa
if [ ! -f /work/bootstrap-complete ]; then
  conan profile detect --force
  printf '\n[conf]\ntools.build:jobs=2\n' >> "$CONAN_HOME/profiles/default"
  python3 "$TT_SOURCE/scripts/bootstrap_conan_deps.py"
  touch /work/bootstrap-complete
fi
mkdir -p /work/adapter
cp /input/bridge.cpp /input/CMakeLists.txt /input/CMakePresets.json /input/Makefile /work/adapter/
cp "$TT_SOURCE/conanfile.py" /work/adapter/conanfile.py
conan export /input/conan/nghttp2 --user=bebekon --channel=security
python3 - <<'PY'
from pathlib import Path
for p in (Path('/work/adapter/conanfile.py'), Path('/work/TrustTunnelClient/conanfile.py')):
    p.write_text(p.read_text().replace('"nghttp2/1.56.0@adguard/oss", transitive_headers=True', '"nghttp2/1.68.1@bebekon/security", transitive_headers=True, force=True').replace('"nghttp2/1.68.1", transitive_headers=True', '"nghttp2/1.68.1@bebekon/security", transitive_headers=True'))
PY
cd /work/adapter
for TT_ABI in ${TT_ABIS:-x86_64 arm64-v8a armeabi-v7a}; do
  export TT_ABI
  make
  mkdir -p "/output/$TT_ABI"
  cp "build/$TT_ABI/libbebekon_trusttunnel.so" "/output/$TT_ABI/"
  "$ANDROID_NDK_HOME/toolchains/llvm/prebuilt/linux-x86_64/bin/llvm-strip" --strip-unneeded "/output/$TT_ABI/libbebekon_trusttunnel.so"
done
