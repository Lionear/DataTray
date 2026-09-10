#!/usr/bin/env bash
#
# Builds the update.json manifest for one channel (SE-137, Fase 0): scans a directory of build assets,
# computes each one's SHA-256, and maps its filename to a per-RID key the app looks up. This manifest —
# refreshed every build and published as a release asset — is the source of truth the in-app updater
# checks, because a rolling tag (nightly/preview) never changes even when its assets do.
#
# Input is env, never argv splicing (SE-135 hardening):
#   CHANNEL        stable | preview | nightly. Required.
#   VERSION        full version stamp, e.g. 0.2.0-nightly.20260717.42. Required.
#   COMMIT         short commit the build was cut from. Required.
#   PUBLISHED_AT   ISO-8601 timestamp. Required.
#   ASSET_DIR      directory holding the build assets. Required.
#   DOWNLOAD_BASE  URL prefix the asset download links are built from (no trailing slash), e.g.
#                  https://github.com/Lionear/DataTray/releases/download/nightly. Required.
#   NOTES_FILE     markdown release notes to embed (optional; empty if unset/missing).
#   OUT            output path for update.json. Required.
set -euo pipefail

: "${CHANNEL:?}"; : "${VERSION:?}"; : "${COMMIT:?}"; : "${PUBLISHED_AT:?}"
: "${ASSET_DIR:?}"; : "${DOWNLOAD_BASE:?}"; : "${OUT:?}"

assets='{}'
for path in "$ASSET_DIR"/*; do
  [ -f "$path" ] || continue
  name="$(basename "$path")"

  # vpk names every artifact "<packId>-<channel>-<kind>", and our channel is "<rid>-<stream>" — so the
  # RID that used to sit in the filename now arrives inside the channel segment. The `kind` values are a
  # contract with the OLD client, not with us: "installer" is what makes it run the file with
  # /VERYSILENT (UpdateApplier.ApplyWindowsInstaller in the pre-SE-245 build). Changing win-*-setup to
  # kind=setup is the documented fallback if vpk's Setup.exe turns out not to tolerate those flags — the
  # old app then falls into its guided path and reveals the download instead of launching it.
  case "$name" in
    *-win-x64-*-Setup.exe)      key=win-x64-setup;   kind=installer ;;
    *-win-arm64-*-Setup.exe)    key=win-arm64-setup; kind=installer ;;
    *-win-x64-*-Portable.zip)   key=win-x64;         kind=zip ;;
    *-win-arm64-*-Portable.zip) key=win-arm64;       kind=zip ;;
    *-linux-x64-*.AppImage)     key=linux-x64;       kind=appimage ;;
    *-osx-arm64-*.dmg)          key=osx-arm64;       kind=dmg ;;
    # Velopack's own feeds and packages, the macOS .pkg, and the macOS portable zip (the old client
    # never had a key for that one — its macOS path is the .dmg). Skipped silently rather than warned
    # about: they are all expected to be here, and a warning that always fires is one nobody reads when
    # a genuinely unmapped asset shows up.
    update.json|releases.*.json|RELEASES*|*.nupkg|*-Setup.pkg|*-osx-*-Portable.zip) continue ;;
    *) echo "::warning::make-update-manifest: unmapped asset '$name' (skipped)"; continue ;;
  esac

  sha="$(sha256sum "$path" | cut -d' ' -f1)"
  size="$(stat -c%s "$path")"
  url="$DOWNLOAD_BASE/$name"

  assets="$(jq \
    --arg k "$key" --arg url "$url" --arg sha "$sha" --arg kind "$kind" --argjson size "$size" \
    '.[$k] = {url: $url, sha256: $sha, kind: $kind, size: $size}' <<<"$assets")"
done

notes=""
if [ -n "${NOTES_FILE:-}" ] && [ -f "$NOTES_FILE" ]; then
  notes="$(cat "$NOTES_FILE")"
fi

jq -n \
  --argjson schemaVersion 1 \
  --arg channel "$CHANNEL" \
  --arg version "$VERSION" \
  --arg commit "$COMMIT" \
  --arg publishedAt "$PUBLISHED_AT" \
  --arg notes "$notes" \
  --argjson assets "$assets" \
  '{schemaVersion: $schemaVersion, channel: $channel, version: $version, commit: $commit,
    publishedAt: $publishedAt, notes: $notes, assets: $assets}' > "$OUT"

echo "Wrote $OUT:"
cat "$OUT"
