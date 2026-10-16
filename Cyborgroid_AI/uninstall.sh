#!/usr/bin/env bash
# Removes Cyborgroid. Downloaded models live under the prefix and are removed too.
# usage: sudo ./uninstall.sh [--purge] [--prefix DIR]   (--purge also deletes /etc/cyborgroid)
set -euo pipefail
PREFIX=/opt/cyborgroid
PURGE=0
while [ $# -gt 0 ]; do
  case "$1" in
    --purge)  PURGE=1 ;;
    --prefix) PREFIX="${2:?}"; shift ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
  shift
done
[ "$(id -u)" -eq 0 ] || { echo "sudo ./uninstall.sh 로 실행하세요" >&2; exit 1; }

du -sh "$PREFIX" 2>/dev/null || true
read -r -p "$PREFIX (모델 포함) 를 삭제할까요? [y/N] " ok
[ "$ok" = y ] || [ "$ok" = Y ] || { echo "취소했습니다."; exit 0; }

rm -f /usr/local/bin/cyborgroid
rm -rf "$PREFIX"
[ "$PURGE" -eq 1 ] && rm -rf /etc/cyborgroid
echo "삭제 완료. 대화 기록은 각 사용자의 ~/.local/share/cyborgroid 에 남아 있습니다."
