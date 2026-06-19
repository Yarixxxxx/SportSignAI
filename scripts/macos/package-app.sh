#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"

PROJECT_PATH="${REPO_ROOT}/frontend/VideoAnalysis.App/VideoAnalysis.App.csproj"
PROJECT_DIR="${REPO_ROOT}/frontend/VideoAnalysis.App"
CONFIGURATION="${CONFIGURATION:-Release}"
RUNTIME_IDENTIFIER="${RUNTIME_IDENTIFIER:-osx-arm64}"
FRAMEWORK="${FRAMEWORK:-net10.0}"
APP_NAME="${APP_NAME:-Video Analysis}"
APP_EXECUTABLE="${APP_EXECUTABLE:-VideoAnalysis.App}"
BUNDLE_IDENTIFIER="${BUNDLE_IDENTIFIER:-com.videoanalysis.hockeymvp}"
APP_VERSION="${APP_VERSION:-1.0.0}"
DIST_ROOT="${REPO_ROOT}/dist/macos"
PUBLISH_DIR="${PROJECT_DIR}/bin/${CONFIGURATION}/${FRAMEWORK}/${RUNTIME_IDENTIFIER}/publish"
BUNDLE_DIR="${DIST_ROOT}/VideoAnalysis.app"
CONTENTS_DIR="${BUNDLE_DIR}/Contents"
MACOS_DIR="${CONTENTS_DIR}/MacOS"
RESOURCES_DIR="${CONTENTS_DIR}/Resources"
INFO_TEMPLATE="${PROJECT_DIR}/macos/Info.plist.template"
INFO_PLIST="${CONTENTS_DIR}/Info.plist"
ZIP_PATH="${DIST_ROOT}/VideoAnalysis-macos-${RUNTIME_IDENTIFIER}.zip"

if [[ "${RUNTIME_IDENTIFIER}" != "osx-arm64" && "${RUNTIME_IDENTIFIER}" != "osx-x64" ]]; then
  echo "Unsupported runtime identifier '${RUNTIME_IDENTIFIER}'. Expected osx-arm64 or osx-x64." >&2
  exit 1
fi

echo "Publishing ${PROJECT_PATH} (${CONFIGURATION}, ${RUNTIME_IDENTIFIER})..."
rm -rf "${PUBLISH_DIR}"
dotnet publish "${PROJECT_PATH}" -c "${CONFIGURATION}" -r "${RUNTIME_IDENTIFIER}" --self-contained true

if [[ ! -d "${PUBLISH_DIR}" ]]; then
  echo "Publish output not found: ${PUBLISH_DIR}" >&2
  exit 1
fi

if [[ ! -f "${PUBLISH_DIR}/${APP_EXECUTABLE}" ]]; then
  echo "Published executable not found: ${PUBLISH_DIR}/${APP_EXECUTABLE}" >&2
  exit 1
fi

mkdir -p "${DIST_ROOT}"
rm -rf "${BUNDLE_DIR}"
mkdir -p "${MACOS_DIR}" "${RESOURCES_DIR}"

echo "Copying publish output into app bundle..."
cp -R "${PUBLISH_DIR}/." "${MACOS_DIR}/"

chmod +x "${MACOS_DIR}/${APP_EXECUTABLE}"

if [[ -f "${MACOS_DIR}/ffmpeg" ]]; then
  chmod +x "${MACOS_DIR}/ffmpeg"
fi

if [[ ! -f "${INFO_TEMPLATE}" ]]; then
  echo "Info.plist template not found: ${INFO_TEMPLATE}" >&2
  exit 1
fi

sed \
  -e "s|{{APP_DISPLAY_NAME}}|${APP_NAME}|g" \
  -e "s|{{APP_EXECUTABLE}}|${APP_EXECUTABLE}|g" \
  -e "s|{{BUNDLE_IDENTIFIER}}|${BUNDLE_IDENTIFIER}|g" \
  -e "s|{{APP_NAME}}|${APP_NAME}|g" \
  -e "s|{{APP_VERSION}}|${APP_VERSION}|g" \
  "${INFO_TEMPLATE}" > "${INFO_PLIST}"

if [[ -f "${PROJECT_DIR}/Assets/app-icon.png" ]]; then
  cp "${PROJECT_DIR}/Assets/app-icon.png" "${RESOURCES_DIR}/"
fi

if find "${MACOS_DIR}" -maxdepth 3 \( -name 'libvlc*.dylib' -o -name 'libvlc*.framework' \) | grep -q .; then
  echo "LibVLC runtime files detected in publish output."
else
  echo "Warning: LibVLC runtime files were not detected in ${PUBLISH_DIR}." >&2
  echo "The app bundle was created, but video playback may not work on macOS until LibVLC runtime is included." >&2
fi

echo "mpv runtime is not required for macOS builds; playback uses LibVLC on this platform."

rm -f "${ZIP_PATH}"
if command -v ditto >/dev/null 2>&1; then
  ditto -c -k --sequesterRsrc --keepParent "${BUNDLE_DIR}" "${ZIP_PATH}"
  echo "Created distributable archive: ${ZIP_PATH}"
else
  echo "ditto not found; skipped zip archive creation."
fi

echo
echo "App bundle ready:"
echo "  ${BUNDLE_DIR}"
echo
echo "Next checks:"
echo "  1. Open the app bundle on macOS."
echo "  2. Verify video playback."
echo "  3. Verify export and ffmpeg execution."
