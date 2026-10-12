#!/usr/bin/env bash
# Builds pcbridge.apk with the plain Android SDK command-line tools (no Gradle needed).
# Usage: build.sh <versionName> [output.apk]
# Needs: ANDROID_HOME (or ANDROID_SDK_ROOT) with build-tools and platforms;android-34, plus a JDK (javac, keytool).
set -euo pipefail

VERSION="${1:-1.0.0}"
HERE="$(cd "$(dirname "$0")" && pwd)"
OUT_APK="${2:-$HERE/build/pcbridge.apk}"
SDK="${ANDROID_HOME:-${ANDROID_SDK_ROOT:-}}"
PLATFORM_API="${PLATFORM_API:-34}"
BT_VERSION="${BUILD_TOOLS:-34.0.0}"

[ -n "$SDK" ] || { echo "ANDROID_HOME is not set" >&2; exit 1; }
BT="$SDK/build-tools/$BT_VERSION"
ANDROID_JAR="$SDK/platforms/android-$PLATFORM_API/android.jar"
[ -f "$ANDROID_JAR" ] || { echo "Missing $ANDROID_JAR" >&2; exit 1; }
[ -x "$BT/aapt2" ] || { echo "Missing $BT/aapt2" >&2; exit 1; }

# versionCode: major*1000000 + minor*1000 + patch (e.g. 1.0.42 -> 1000042)
IFS=. read -r MAJ MIN PAT <<<"$VERSION"
VERSION_CODE=$(( ${MAJ:-1} * 1000000 + ${MIN:-0} * 1000 + ${PAT:-0} ))

W="$HERE/build"
rm -rf "$W"
mkdir -p "$W/gen" "$W/classes" "$W/dex"

echo "== aapt2 compile/link"
"$BT/aapt2" compile --dir "$HERE/res" -o "$W/res.zip"
"$BT/aapt2" link -o "$W/base.apk" -I "$ANDROID_JAR" \
  --manifest "$HERE/AndroidManifest.xml" --java "$W/gen" \
  --min-sdk-version 26 --target-sdk-version "$PLATFORM_API" \
  --version-code "$VERSION_CODE" --version-name "$VERSION" \
  "$W/res.zip"

echo "== javac"
find "$HERE/src" "$W/gen" -name '*.java' > "$W/sources.txt"
javac --release 11 -encoding UTF-8 -nowarn -classpath "$ANDROID_JAR" -d "$W/classes" @"$W/sources.txt"

echo "== d8"
(cd "$W/classes" && jar cf "$W/classes.jar" .)
"$BT/d8" --release --min-api 26 --lib "$ANDROID_JAR" --output "$W/dex" "$W/classes.jar"

echo "== package"
cp "$W/base.apk" "$W/unsigned.apk"
(cd "$W/dex" && zip -q -j "$W/unsigned.apk" classes.dex)
"$BT/zipalign" -f -p 4 "$W/unsigned.apk" "$W/aligned.apk"

echo "== sign"
KS="$W/signing.jks"
keytool -genkeypair -keystore "$KS" -storepass pcbridge -keypass pcbridge -alias pcbridge \
  -keyalg RSA -keysize 2048 -validity 10000 -dname "CN=Emulate PC Bridge" >/dev/null 2>&1
mkdir -p "$(dirname "$OUT_APK")"
"$BT/apksigner" sign --ks "$KS" --ks-pass pass:pcbridge --key-pass pass:pcbridge --out "$OUT_APK" "$W/aligned.apk"
"$BT/apksigner" verify "$OUT_APK"
echo "Built $OUT_APK ($VERSION, code $VERSION_CODE)"
