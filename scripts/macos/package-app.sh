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
MACOS_LIB_DIR="${MACOS_DIR}/lib"
RESOURCES_DIR="${CONTENTS_DIR}/Resources"
INFO_TEMPLATE="${PROJECT_DIR}/macos/Info.plist.template"
INFO_PLIST="${CONTENTS_DIR}/Info.plist"
ZIP_PATH="${DIST_ROOT}/VideoAnalysis-macos-${RUNTIME_IDENTIFIER}.zip"

if [[ "${RUNTIME_IDENTIFIER}" != "osx-arm64" && "${RUNTIME_IDENTIFIER}" != "osx-x64" ]]; then
  echo "Unsupported runtime identifier '${RUNTIME_IDENTIFIER}'. Expected osx-arm64 or osx-x64." >&2
  exit 1
fi

case "${RUNTIME_IDENTIFIER}" in
  osx-arm64)
    BUNDLED_FFMPEG_SOURCE="${REPO_ROOT}/tools/ffmpeg/macos-arm64/unpacked/ffmpeg"
    ;;
  osx-x64)
    BUNDLED_FFMPEG_SOURCE="${REPO_ROOT}/tools/ffmpeg/macos-x64/unpacked/ffmpeg"
    ;;
esac

NUGET_PACKAGES_DIR="${NUGET_PACKAGES:-${HOME}/.nuget/packages}"
LIBVLC_MAC_PACKAGE_DIR="${NUGET_PACKAGES_DIR}/videolan.libvlc.mac"

find_libvlc_runtime_dir() {
  local search_root="$1"
  if [[ ! -d "${search_root}" ]]; then
    return 1
  fi

  local libvlc_path
  local preferred_paths=()
  local fallback_paths=()
  while IFS= read -r libvlc_path; do
    if [[ "${libvlc_path}" == *"${RUNTIME_IDENTIFIER}"* ]]; then
      preferred_paths+=("${libvlc_path}")
    else
      fallback_paths+=("${libvlc_path}")
    fi
  done < <(find "${search_root}" -type f -name 'libvlc.dylib' 2>/dev/null)

  for libvlc_path in "${preferred_paths[@]}" "${fallback_paths[@]}"; do
    local runtime_dir
    runtime_dir="$(dirname "${libvlc_path}")"
    if [[ -f "${runtime_dir}/libvlccore.dylib" ]]; then
      echo "${runtime_dir}"
      return 0
    fi
  done

  return 1
}

copy_libvlc_runtime() {
  if [[ -f "${MACOS_LIB_DIR}/libvlc.dylib" && -f "${MACOS_LIB_DIR}/libvlccore.dylib" ]]; then
    return 0
  fi

  local runtime_dir=""
  if runtime_dir="$(find_libvlc_runtime_dir "${PUBLISH_DIR}")"; then
    echo "Copying LibVLC runtime from publish output: ${runtime_dir}"
  elif runtime_dir="$(find_libvlc_runtime_dir "${LIBVLC_MAC_PACKAGE_DIR}")"; then
    echo "Copying LibVLC runtime from NuGet cache: ${runtime_dir}"
  else
    echo "Unable to locate libvlc.dylib/libvlccore.dylib." >&2
    echo "Expected them in publish output or NuGet package: ${LIBVLC_MAC_PACKAGE_DIR}" >&2
    echo "Try running: dotnet restore \"${PROJECT_PATH}\" -r ${RUNTIME_IDENTIFIER}" >&2
    exit 1
  fi

  rm -rf "${MACOS_LIB_DIR}"
  mkdir -p "${MACOS_LIB_DIR}"
  cp -R "${runtime_dir}/." "${MACOS_LIB_DIR}/"

  if [[ ! -d "${MACOS_LIB_DIR}/plugins" ]]; then
    if [[ -d "${runtime_dir}/plugins" ]]; then
      cp -R "${runtime_dir}/plugins" "${MACOS_LIB_DIR}/plugins"
    elif [[ -d "$(dirname "${runtime_dir}")/plugins" ]]; then
      cp -R "$(dirname "${runtime_dir}")/plugins" "${MACOS_LIB_DIR}/plugins"
    fi
  fi

  if [[ ! -f "${MACOS_LIB_DIR}/libvlc.dylib" || ! -f "${MACOS_LIB_DIR}/libvlccore.dylib" ]]; then
    echo "LibVLC runtime copy failed: ${MACOS_LIB_DIR}/libvlc.dylib or libvlccore.dylib is missing." >&2
    exit 1
  fi
}

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
mkdir -p "${MACOS_DIR}" "${MACOS_LIB_DIR}" "${RESOURCES_DIR}"

echo "Copying publish output into app bundle..."
cp -R "${PUBLISH_DIR}/." "${MACOS_DIR}/"

chmod +x "${MACOS_DIR}/${APP_EXECUTABLE}"

if [[ ! -f "${MACOS_DIR}/ffmpeg" ]]; then
  if [[ ! -f "${BUNDLED_FFMPEG_SOURCE}" ]]; then
    echo "Bundled ffmpeg source not found: ${BUNDLED_FFMPEG_SOURCE}" >&2
    exit 1
  fi

  echo "Copying bundled ffmpeg into app bundle..."
  cp "${BUNDLED_FFMPEG_SOURCE}" "${MACOS_DIR}/ffmpeg"
fi

chmod +x "${MACOS_DIR}/ffmpeg"
if ! "${MACOS_DIR}/ffmpeg" -hide_banner -version >/dev/null 2>&1; then
  echo "Bundled ffmpeg exists but cannot be executed: ${MACOS_DIR}/ffmpeg" >&2
  echo "Run it manually on the Mac to inspect missing dylib dependencies." >&2
  exit 1
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

copy_libvlc_runtime

if [[ -f "${MACOS_LIB_DIR}/libvlc.dylib" && -f "${MACOS_LIB_DIR}/libvlccore.dylib" ]]; then
  echo "LibVLC runtime files detected in app bundle: ${MACOS_LIB_DIR}"
else
  echo "LibVLC runtime files were not detected in app bundle." >&2
  exit 1
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
