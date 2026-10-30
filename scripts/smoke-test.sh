#!/usr/bin/env bash
# Docker Compose ile ayağa kaldırılmış sistemi uçtan uca doğrular:
#   1) tüm kameralar akış servisinde Online olur ve canlı HLS yayınları erişilebilir
#   2) kayıt servisi her kamera için parçaları diske yazar ve veritabanında aranabilir olur
#   3) kayıt parçası yeniden kodlanmamış, oynatılabilir bir H.264 MP4'tür
#   4) geri oynatma: zaman çizelgesi, parça akışı (HTTP Range) ve yeniden kodlamasız klip dışa aktarma
#   5) kayıt servisi SIGKILL ile öldürülüp yeniden başlatıldığında açılış uzlaştırması tamamlanır
# Kullanım: SEGMENT_SECONDS=10 docker compose up -d && ./scripts/smoke-test.sh
set -euo pipefail

STREAMING_URL="${STREAMING_URL:-http://localhost:5080}"
RECORDING_URL="${RECORDING_URL:-http://localhost:5081}"
HLS_BASE_URL="${HLS_BASE_URL:-http://localhost:8888}"
CAMERAS="${CAMERAS:-cam1 cam2 cam3 cam4}"
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
for cam in $CAMERAS; do
  wait_for "$cam çevrimiçi" "curl -fsS $STREAMING_URL/api/cameras/$cam | grep -q '\"state\":\"Online\"'"
  wait_for "$cam canlı HLS yayını" "curl -fsSL $HLS_BASE_URL/$cam/index.m3u8 | grep -q EXTM3U"
done
for cam in $CAMERAS; do
  wait_for "$cam: en az 2 kayıt parçası veritabanında" \
    "[ \$(curl -fsS '$RECORDING_URL/api/recordings/segments?cameraId=$cam' | grep -o '\"filePath\"' | wc -l) -ge 2 ]"
done

segment=$(curl -fsS "$RECORDING_URL/api/recordings/segments?cameraId=cam1&limit=1" | sed -E 's/.*"filePath":"([^"]+)".*/\1/')
echo "🔎 Parça kontrol ediliyor: $segment"
probe=$(docker compose exec -T recording ffprobe -v error -select_streams v:0 \
  -show_entries stream=codec_name:format=duration -of default=noprint_wrappers=1 "$segment")
echo "$probe"
grep -q "codec_name=h264" <<<"$probe"

echo "🎞️  Geri oynatma"
curl -fsS "$RECORDING_URL/" | grep -q "<title>" && echo "  geri oynatma sayfası ✅"
timeline=$(curl -fsS "$RECORDING_URL/api/playback/cam1/timeline")
media_url=$(sed -E 's/.*"mediaUrl":"([^"]+)".*/\1/' <<<"$timeline")
first_start=$(sed -E 's/[^[]*"segments":\[\{[^}]*"start":"([^"]+)".*/\1/' <<<"$timeline")
# Sorgu dizgisinde "+" boşluğa dönüşeceği için zamanı "Z" biçimine çevir.
first_start=$(date -u -d "$first_start" +%Y-%m-%dT%H:%M:%SZ)
echo "  zaman çizelgesi: $(grep -o '"mediaUrl"' <<<"$timeline" | wc -l) parça, ilk parça $first_start"
status=$(curl -s -o /dev/null -w '%{http_code}' -H 'Range: bytes=0-1023' "$RECORDING_URL$media_url")
[ "$status" = "206" ] && echo "  parça akışı HTTP Range ile ($status) ✅"
curl -fsS "$RECORDING_URL/api/playback/cam1/locate?time=$first_start" | grep -q '"offsetSeconds"' && echo "  saate göre konum bulma ✅"

# Kaydedilmiş aralığın ilk ~2 parçasını klip olarak dışa aktar ve yeniden kodlanmadığını doğrula.
clip_from="$first_start"
clip_to=$(date -u -d "$first_start + $((2 * ${SEGMENT_SECONDS:-10})) seconds" +%Y-%m-%dT%H:%M:%SZ)
clip=$(mktemp --suffix=.mp4)
curl -fsS -o "$clip" "$RECORDING_URL/api/playback/cam1/export?from=$clip_from&to=$clip_to"
clip_probe=$(ffprobe -v error -select_streams v:0 -show_entries stream=codec_name:format=duration -of default=noprint_wrappers=1 "$clip" 2>/dev/null \
  || docker run --rm -i --entrypoint ffprobe bluenviron/mediamtx:1.21.3-ffmpeg -v error -select_streams v:0 \
       -show_entries stream=codec_name:format=duration -of default=noprint_wrappers=1 - <"$clip")
rm -f "$clip"
echo "  klip ($clip_from → $clip_to): $(tr '\n' ' ' <<<"$clip_probe")"
grep -q "codec_name=h264" <<<"$clip_probe"

echo "💥 Kayıt servisi SIGKILL ile öldürülüyor (elektrik kesintisi benzetimi)"
sleep $(( ${SEGMENT_SECONDS:-10} / 2 ))
docker compose kill -s SIGKILL recording >/dev/null
docker compose up -d recording >/dev/null
wait_for "Kayıt servisi yeniden hazır" "curl -fsS $RECORDING_URL/health/ready"
wait_for "Açılış uzlaştırması tamamlandı" \
  "curl -fsS $RECORDING_URL/api/recordings/reconciliation | grep -q '\"state\":\"Completed\"'"
echo "  uzlaştırma raporu: $(curl -fsS "$RECORDING_URL/api/recordings/reconciliation")"
wait_for "Yeniden başlatmadan sonra kayıt sürüyor" \
  "curl -fsS $RECORDING_URL/api/recordings/status | grep -q '\"segmentsWritten\":[1-9]'"

echo
echo "Kamera durumu:   $(curl -fsS "$STREAMING_URL/api/cameras")"
echo "Kayıt durumu:    $(curl -fsS "$RECORDING_URL/api/recordings/status")"
echo
echo "✅ Duman testi başarılı"
