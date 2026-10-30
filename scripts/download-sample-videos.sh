#!/usr/bin/env bash
# Sahte kameralar için örnek güvenlik kamerası videolarını indirir ve hazırlar (samples/videos/cam1..cam4.mp4).
# Kaynak: Intel IoT DevKit örnek videoları, CC BY 4.0 – https://github.com/intel-iot-devkit/sample-videos
# Videolar sabit, yukarıdan bakan kameralarla çekilmiştir; insan ve araç içerir (3. aşamadaki tespit için).
#
# Kullanım: ./scripts/download-sample-videos.sh && docker compose restart mediamtx
set -euo pipefail

BASE_URL="https://raw.githubusercontent.com/intel-iot-devkit/sample-videos/master"
REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
TMP_DIR="$(mktemp -d)"
trap 'rm -rf "$TMP_DIR"' EXIT

declare -A VIDEOS=(
  [cam1]=car-detection                  # otopark girişi, araçlar
  [cam2]=person-bicycle-car-detection   # otopark alanı, yaya + bisiklet + araç
  [cam3]=people-detection               # koridor, yürüyen insanlar
  [cam4]=one-by-one-person-detection    # lobi, tek tek geçen insanlar
)

for camera in cam1 cam2 cam3 cam4; do
  name="${VIDEOS[$camera]}"
  echo "⬇️  $camera ← $name.mp4"
  curl -fsSL -o "$TMP_DIR/$name.mp4" "$BASE_URL/$name.mp4"
  "$REPO_ROOT/scripts/prepare-video.sh" "$TMP_DIR/$name.mp4" "$camera" >/dev/null
done

echo "✅ Videolar samples/videos/ altında hazır. Yayına almak için: docker compose restart mediamtx"
