#!/usr/bin/env bash
# Cyborgroid installer — builds the Rust core, the C# CLI and the inference engine,
# then installs everything under /opt/cyborgroid with `cyborgroid` on the PATH.
#
# usage: sudo ./install.sh [--with-model] [--skip-engine] [--prefix DIR]
#   --with-model    also download the default model (~5GB, needs network)
#   --skip-engine   keep the installed engine instead of rebuilding it
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PREFIX=/opt/cyborgroid
CONF=/etc/cyborgroid/config.json
WITH_MODEL=0
SKIP_ENGINE=0

while [ $# -gt 0 ]; do
  case "$1" in
    --with-model)  WITH_MODEL=1 ;;
    --skip-engine) SKIP_ENGINE=1 ;;
    --prefix)      PREFIX="${2:?}"; shift ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
  shift
done

if [ "$(id -u)" -ne 0 ]; then
  echo "관리자 권한이 필요합니다:  sudo ./install.sh" >&2
  exit 1
fi

# Build as the invoking user (their ~/.dotnet and ~/.cargo), install as root.
BUILD_USER="${SUDO_USER:-root}"
BUILD_HOME="$(getent passwd "$BUILD_USER" | cut -d: -f6)"
as_user() {
  if [ "$BUILD_USER" != root ]; then
    sudo -u "$BUILD_USER" -H env \
      PATH="$BUILD_HOME/.dotnet:$BUILD_HOME/.cargo/bin:$PATH" \
      DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 "$@"
  else
    env DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 "$@"
  fi
}
for tool in cargo dotnet cmake git; do
  as_user bash -c "command -v $tool" >/dev/null || { echo "필요한 도구가 없습니다: $tool" >&2; exit 1; }
done

echo "[1/4] Rust core 빌드"
as_user cargo build --release --manifest-path "$HERE/core/Cargo.toml"

echo "[2/4] C# CLI 빌드"
as_user dotnet publish "$HERE/cli/Cyborgroid.Cli.csproj" -c Release -r linux-x64 \
  --self-contained true -p:PublishSingleFile=true -o "$HERE/build/cli"

echo "[3/4] $PREFIX 에 설치"
install -d "$PREFIX"/{bin,engine/bin,models,scripts,src}
install -m755 "$HERE/build/cli/cyborgroid" "$PREFIX/bin/cyborgroid"
install -m755 "$HERE/core/target/release/libcyborgroid_core.so" "$PREFIX/bin/libcyborgroid_core.so"
install -m755 "$HERE/scripts/build-engine.sh" "$PREFIX/scripts/build-engine.sh"
cat > /usr/local/bin/cyborgroid <<WRAP
#!/bin/sh
exec "$PREFIX/bin/cyborgroid" "\$@"
WRAP
chmod 755 /usr/local/bin/cyborgroid

echo "[4/4] 추론 엔진"
if [ "$SKIP_ENGINE" -eq 0 ] || [ ! -x "$PREFIX/engine/bin/llama-server" ]; then
  bash "$PREFIX/scripts/build-engine.sh" "$PREFIX/src/llama.cpp" "$PREFIX/engine"
else
  echo "  기존 엔진 유지 ($(cat "$PREFIX/engine/VERSION" 2>/dev/null || echo '?'))"
fi

if [ ! -f "$CONF" ]; then
  backend="$(cat "$PREFIX/engine/BACKEND" 2>/dev/null || echo cpu)"
  layers=0; [ "$backend" != cpu ] && layers=99
  "$PREFIX/bin/cyborgroid" init-config "$CONF" --home "$PREFIX" --gpu-layers "$layers" --app-source "$HERE"
else
  echo "  기존 설정 유지: $CONF"
fi
chmod 644 "$CONF"

if [ "$WITH_MODEL" -eq 1 ]; then
  CYBORGROID_CONFIG="$CONF" "$PREFIX/bin/cyborgroid" update --model
fi

echo
echo "설치 완료."
[ "$WITH_MODEL" -eq 1 ] || echo "  모델 받기 (네트워크 필요):  sudo cyborgroid update"
echo "  대화 시작 (오프라인 가능):   cyborgroid"
