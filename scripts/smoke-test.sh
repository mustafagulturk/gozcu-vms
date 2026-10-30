#!/usr/bin/env bash
# Docker Compose ile ayağa kaldırılmış sistemi uçtan uca doğrular:
#   1) cam1 akış servisinde Online olur
#   2) canlı HLS yayını erişilebilir
#   3) kayıt servisi parçaları diske yazar ve veritabanında aranabilir olur
#   4) kayıt parçası yeniden kodlanmamış, oynatılabilir bir H.264 MP4'tür
# Kullanım: SEGMENT_SECONDS=10 docker compose up -d && ./scripts/smoke-test.sh
set -euo pipefail

STREAMING_URL="${STREAMING_URL:-http://localhost:5080}"
RECORDING_URL="${RECORDING_URL:-http://localhost:5081}"
HLS_URL="${HLS_URL:-http://localhost:8888/cam1/index.m3u8}"
TIMEOUT="${TIMEOUT:-180}"

wait_for() {
  local description="$1" check="$2" deadline=$((SECONDS + TIMEOUT))
  printf '⏳ %s' "$description"
  until eval "$check" >/dev/null 2>&1; do
    if (( SECONDS > deadline )); then
      printf '\n❌ %s (%ss içinde gerçekleşmedi)\n' "$description" "$TIMEOUT"
      return 1
    fi
    printf '.'
    sleep 2
  done
  printf ' ✅\n'
}

wait_for "Akış servisi hazır" "curl -fsS $STREAMING_URL/health/ready"
wait_for "Kayıt servisi hazır" "curl -fsS $RECORDING_URL/health/ready"
wait_for "cam1 çevrimiçi" "curl -fsS $STREAMING_URL/api/cameras/cam1 | grep -q '\"state\":\"Online\"'"
wait_for "Canlı HLS yayını" "curl -fsSL $HLS_URL | grep -q EXTM3U"
wait_for "En az 2 kayıt parçası veritabanında" \
  "[ \$(curl -fsS '$RECORDING_URL/api/recordings/segments?cameraId=cam1' | grep -o '\"filePath\"' | wc -l) -ge 2 ]"

segment=$(curl -fsS "$RECORDING_URL/api/recordings/segments?cameraId=cam1&limit=1" | sed -E 's/.*"filePath":"([^"]+)".*/\1/')
echo "🔎 Parça kontrol ediliyor: $segment"
probe=$(docker compose exec -T recording ffprobe -v error -select_streams v:0 \
  -show_entries stream=codec_name:format=duration -of default=noprint_wrappers=1 "$segment")
echo "$probe"
grep -q "codec_name=h264" <<<"$probe"

echo
echo "Kamera durumu:   $(curl -fsS "$STREAMING_URL/api/cameras/cam1")"
echo "Kayıt durumu:    $(curl -fsS "$RECORDING_URL/api/recordings/status")"
echo
echo "✅ Duman testi başarılı"
