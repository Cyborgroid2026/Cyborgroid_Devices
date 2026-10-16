#!/usr/bin/env bash
# Builds llama.cpp's llama-server — the local inference backend Cyborgroid drives.
# Needs network only to fetch the source; the resulting binary never touches the network.
#
# usage: build-engine.sh <src-dir> <install-prefix> [git-ref] [repo-url]
#   CYBORGROID_BACKEND=auto|cpu|cuda|vulkan   (default: auto)
set -euo pipefail

SRC="${1:?usage: build-engine.sh <src-dir> <install-prefix> [git-ref] [repo-url]}"
PREFIX="${2:?install prefix required}"
REF="${3:-master}"
REPO="${4:-https://github.com/ggml-org/llama.cpp.git}"
BACKEND="${CYBORGROID_BACKEND:-auto}"

if [ -d "$SRC/.git" ]; then
  echo "[engine] updating source ($REF)"
  git -C "$SRC" fetch --depth 1 origin "$REF"
  git -C "$SRC" checkout -q FETCH_HEAD
else
  echo "[engine] cloning $REPO ($REF)"
  mkdir -p "$(dirname "$SRC")"
  git clone --depth 1 --branch "$REF" "$REPO" "$SRC"
fi

if [ "$BACKEND" = auto ]; then
  if command -v nvcc >/dev/null 2>&1 || [ -x /usr/local/cuda/bin/nvcc ]; then
    BACKEND=cuda
  elif command -v glslc >/dev/null 2>&1 && [ -f /usr/include/vulkan/vulkan.h ]; then
    BACKEND=vulkan
  else
    BACKEND=cpu
  fi
fi
echo "[engine] backend: $BACKEND"

flags=(
  -DCMAKE_BUILD_TYPE=Release
  -DBUILD_SHARED_LIBS=OFF
  -DLLAMA_CURL=OFF          # no model-download code in the engine: it stays offline
  -DLLAMA_BUILD_TESTS=OFF
  -DGGML_NATIVE=ON
)
case "$BACKEND" in
  cuda)   flags+=(-DGGML_CUDA=ON); export PATH="/usr/local/cuda/bin:$PATH" ;;
  vulkan) flags+=(-DGGML_VULKAN=ON) ;;
  cpu)    ;;
  *) echo "unknown backend: $BACKEND" >&2; exit 2 ;;
esac

BUILD="$SRC/build-$BACKEND"
cmake -S "$SRC" -B "$BUILD" "${flags[@]}"
cmake --build "$BUILD" --target llama-server -j "$(nproc)"

install -Dm755 "$BUILD/bin/llama-server" "$PREFIX/bin/llama-server"
echo "$BACKEND" > "$PREFIX/BACKEND"
git -C "$SRC" rev-parse --short HEAD > "$PREFIX/VERSION"
echo "[engine] installed $PREFIX/bin/llama-server ($BACKEND, $(cat "$PREFIX/VERSION"))"
