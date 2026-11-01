#!/usr/bin/env bash
# Docker Compose ile ayağa kaldırılmış sistemi uçtan uca doğrular. Tarayıcının kullandığı yol olan API ağ geçidi
# (GATEWAY_URL) üzerinden çalışır; servislerin kendi adresleri yalnızca sağlık kontrolü için kullanılır.
#   1) web paneli sayfaları ağ geçidinden sunulur
#   2) tüm kameralar akış servisinde Online olur ve canlı HLS yayınları ağ geçidinin /hls/ yolundan erişilebilir
#   3) kayıt servisi her kamera için parçaları diske yazar ve veritabanında aranabilir olur
#   4) kayıt parçası yeniden kodlanmamış, oynatılabilir bir H.264 MP4'tür
#   5) geri oynatma: zaman çizelgesi, parça akışı (HTTP Range), yeniden kodlamasız klip dışa aktarma, tek kare JPEG
#   6) kayıt servisi SIGKILL ile öldürülüp yeniden başlatıldığında açılış uzlaştırması tamamlanır
#   7) analiz servisi her kamerayı analiz eder; örnek videolarda insan ve araç olayları oluşur; olay arama (sayfalama)
#      ve tespit kareleri uçları çalışır (samples/videos boşsa kameralar test deseni yayınlar ve tespit adımları atlanır)
#   8) kamera yönetimi: ağ geçidi üzerinden kamera eklenir → kayıt ve analiz servisleri başlatır; If-Match çakışması
#      412, aynı kimlik 409 verir; kamera silinir → servisler durdurur
# Kullanım: SEGMENT_SECONDS=10 docker compose up -d && ./scripts/smoke-test.sh
set -euo pipefail

GATEWAY_URL="${GATEWAY_URL:-http://localhost:5000}"
STREAMING_URL="${STREAMING_URL:-http://localhost:5080}"
RECORDING_URL="${RECORDING_URL:-http://localhost:5081}"
ANALYSIS_URL="${ANALYSIS_URL:-http://localhost:5082}"
CAMERAS="${CAMERAS:-cam1 cam2 cam3 cam4}"
TIMEOUT="${TIMEOUT:-180}"
# Kamera yönetimi adımında geçici olarak eklenen kamera (cam1'in yayınını ikinci kez kullanır).
TEST_CAMERA="${TEST_CAMERA:-cam5}"
TEST_CAMERA_RTSP="${TEST_CAMERA_RTSP:-rtsp://mediamtx:8554/cam1}"

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

# Yalnızca HTTP durum kodunu yazar; curl'ün kalan argümanları olduğu gibi geçer.
http_status() {
  curl -s -o /dev/null -w '%{http_code}' "$@"
}

expect_status() {
  local description="$1" expected="$2" actual
  shift 2
  actual=$(http_status "$@")
  if [ "$actual" != "$expected" ]; then
    printf '❌ %s: HTTP %s bekleniyordu, %s geldi\n' "$description" "$expected" "$actual"
    return 1
  fi
  printf '  %s (HTTP %s) ✅\n' "$description" "$actual"
}

utc() {
  date -u -d "$1" +%Y-%m-%dT%H:%M:%SZ
}

# "kontrol && echo ✅ || fail $LINENO": set -e, && zincirindeki başarısızlıkta betiği durdurmadığı için açıkça çıkılır.
fail() {
  printf '❌ smoke-test.sh satır %s: doğrulama başarısız\n' "$1"
  exit 1
}

# Durum yanıtı alınabildi VE kamera listede yok (servis kapalıyken yanlışlıkla "kalktı" sayılmasın).
lacks_camera() {
  local body
  body=$(curl -fsS "$1") || return 1
  ! grep -q "\"cameraId\":\"$2\"" <<<"$body"
}

# Test yarıda kalırsa geçici kamera bir sonraki çalıştırmayı bozmasın.
cleanup() {
  curl -s -o /dev/null -X DELETE "$GATEWAY_URL/api/cameras/$TEST_CAMERA" || true
}
trap cleanup EXIT

wait_for "Akış servisi hazır" "curl -fsS $STREAMING_URL/health/ready"
wait_for "Kayıt servisi hazır" "curl -fsS $RECORDING_URL/health/ready"
wait_for "Ağ geçidi hazır" "curl -fsS $GATEWAY_URL/health/ready"

echo "🖥️  Web paneli (ağ geçidi)"
for page in / /playback.html /events.html /cameras.html /analysis.html; do
  curl -fsS "$GATEWAY_URL$page" | grep -q "<title>Gözcü VMS" && echo "  $page ✅" || fail $LINENO
done
curl -fsS "$GATEWAY_URL/panel.css" | grep -q -- "--bg" && echo "  /panel.css ✅" || fail $LINENO
curl -fsS "$GATEWAY_URL/panel.js" | grep -q "Gozcu" && echo "  /panel.js ✅" || fail $LINENO

for cam in $CAMERAS; do
  wait_for "$cam çevrimiçi" "curl -fsS $GATEWAY_URL/api/cameras/$cam | grep -q '\"state\":\"Online\"'"
  wait_for "$cam canlı HLS yayını (ağ geçidi /hls/)" "curl -fsSL $GATEWAY_URL/hls/$cam/index.m3u8 | grep -q EXTM3U"
done
for cam in $CAMERAS; do
  wait_for "$cam: en az 2 kayıt parçası veritabanında" \
    "[ \$(curl -fsS '$GATEWAY_URL/api/recordings/segments?cameraId=$cam' | grep -o '\"filePath\"' | wc -l) -ge 2 ]"
done

segment=$(curl -fsS "$GATEWAY_URL/api/recordings/segments?cameraId=cam1&limit=1" | sed -E 's/.*"filePath":"([^"]+)".*/\1/')
echo "🔎 Parça kontrol ediliyor: $segment"
probe=$(docker compose exec -T recording ffprobe -v error -select_streams v:0 \
  -show_entries stream=codec_name:format=duration -of default=noprint_wrappers=1 "$segment")
echo "$probe"
grep -q "codec_name=h264" <<<"$probe"

echo "🎞️  Geri oynatma"
timeline=$(curl -fsS "$GATEWAY_URL/api/playback/cam1/timeline")
media_url=$(sed -E 's/.*"mediaUrl":"([^"]+)".*/\1/' <<<"$timeline")
first_start=$(sed -E 's/[^[]*"segments":\[\{[^}]*"start":"([^"]+)".*/\1/' <<<"$timeline")
# Sorgu dizgisinde "+" boşluğa dönüşeceği için zamanı "Z" biçimine çevir.
first_start=$(utc "$first_start")
echo "  zaman çizelgesi: $(grep -o '"mediaUrl"' <<<"$timeline" | wc -l) parça, ilk parça $first_start"
expect_status "parça akışı HTTP Range ile" 206 -H 'Range: bytes=0-1023' "$GATEWAY_URL$media_url"
curl -fsS "$GATEWAY_URL/api/playback/cam1/locate?time=$first_start" | grep -q '"offsetSeconds"' && echo "  saate göre konum bulma ✅" || fail $LINENO

# Kaydedilmiş aralığın ilk ~2 parçasını klip olarak dışa aktar ve yeniden kodlanmadığını doğrula.
clip_from="$first_start"
clip_to=$(utc "$first_start + $((2 * ${SEGMENT_SECONDS:-10})) seconds")
clip=$(mktemp --suffix=.mp4)
curl -fsS -o "$clip" "$GATEWAY_URL/api/playback/cam1/export?from=$clip_from&to=$clip_to"
clip_probe=$(ffprobe -v error -select_streams v:0 -show_entries stream=codec_name:format=duration -of default=noprint_wrappers=1 "$clip" 2>/dev/null \
  || docker run --rm -i --entrypoint ffprobe bluenviron/mediamtx:1.21.3-ffmpeg -v error -select_streams v:0 \
       -show_entries stream=codec_name:format=duration -of default=noprint_wrappers=1 - <"$clip")
rm -f "$clip"
echo "  klip ($clip_from → $clip_to): $(tr '\n' ' ' <<<"$clip_probe")"
grep -q "codec_name=h264" <<<"$clip_probe"

# Olay listesindeki küçük resimler: kayıttan tek JPEG kare (FF D8 FF ile başlar), uzun süre önbelleğe alınabilir.
frame_time=$(utc "$first_start + 2 seconds")
frame=$(mktemp --suffix=.jpg)
frame_headers=$(curl -fsS -D - -o "$frame" "$GATEWAY_URL/api/playback/cam1/frame.jpg?time=$frame_time&width=320" | tr -d '\r')
[ "$(head -c 3 "$frame" | od -An -tx1 | tr -d ' \n')" = "ffd8ff" ] && echo "  tek kare JPEG ($(stat -c %s "$frame") bayt) ✅" || fail $LINENO
rm -f "$frame"
grep -qi '^cache-control:.*max-age' <<<"$frame_headers" && echo "  kare önbellek başlığı ✅" || fail $LINENO
expect_status "kayıt olmayan anda kare" 404 "$GATEWAY_URL/api/playback/cam1/frame.jpg?time=2001-01-01T00:00:00Z&width=320"

echo "💥 Kayıt servisi SIGKILL ile öldürülüyor (elektrik kesintisi benzetimi)"
sleep $(( ${SEGMENT_SECONDS:-10} / 2 ))
docker compose kill -s SIGKILL recording >/dev/null
docker compose up -d recording >/dev/null
wait_for "Kayıt servisi yeniden hazır" "curl -fsS $RECORDING_URL/health/ready"
wait_for "Açılış uzlaştırması tamamlandı" \
  "curl -fsS $GATEWAY_URL/api/recordings/reconciliation | grep -q '\"state\":\"Completed\"'"
echo "  uzlaştırma raporu: $(curl -fsS "$GATEWAY_URL/api/recordings/reconciliation")"
wait_for "Yeniden başlatmadan sonra kayıt sürüyor" \
  "curl -fsS $GATEWAY_URL/api/recordings/status | grep -q '\"segmentsWritten\":[1-9]'"

echo "🧠 Nesne tespiti"
wait_for "Analiz servisi hazır (model yüklendi)" "curl -fsS $ANALYSIS_URL/health/ready"
for cam in $CAMERAS; do
  wait_for "$cam analiz ediliyor" \
    "curl -fsS $GATEWAY_URL/api/analysis/status | grep -q '\"cameraId\":\"$cam\"[^]]*\"state\":\"Analyzing\"'"
done
snapshot=$(mktemp --suffix=.bmp)
curl -fsS -o "$snapshot" "$GATEWAY_URL/api/analysis/cameras/cam1/snapshot.bmp"
[ "$(head -c 2 "$snapshot")" = "BM" ] && echo "  son kare görüntüsü (BMP, $(stat -c %s "$snapshot") bayt) ✅" || fail $LINENO
rm -f "$snapshot"

# Olay arama ve tespit kareleri uçları (örnek video olmasa da yanıt biçimi doğrulanır).
curl -fsS "$GATEWAY_URL/api/analysis/events/search?limit=5" | grep -q '"items":\[' && echo "  olay arama yanıtı ✅" || fail $LINENO
expect_status "olay aramada geçersiz parametre" 400 "$GATEWAY_URL/api/analysis/events/search?limit=0"
frames_from=$(utc "now - 5 minutes")
frames_to=$(utc "now")
curl -fsS "$GATEWAY_URL/api/analysis/cameras/cam3/frames?from=$frames_from&to=$frames_to" | grep -q '^\[' && echo "  tespit kareleri yanıtı ✅" || fail $LINENO
expect_status "15 dakikadan uzun kare aralığı" 400 \
  "$GATEWAY_URL/api/analysis/cameras/cam3/frames?from=$(utc "now - 1 hour")&to=$frames_to"

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
if ls "$REPO_ROOT"/samples/videos/*.mp4 >/dev/null 2>&1; then
  wait_for "İnsan olayı oluştu" "curl -fsS '$GATEWAY_URL/api/analysis/events?category=Person&limit=1' | grep -q '\"category\":\"Person\"'"
  wait_for "Araç olayı oluştu" "curl -fsS '$GATEWAY_URL/api/analysis/events?category=Vehicle&limit=1' | grep -q '\"category\":\"Vehicle\"'"
  wait_for "Tespit kareleri veritabanında" "curl -fsS '$GATEWAY_URL/api/analysis/detections?cameraId=cam3&limit=1' | grep -q '\"label\":\"person\"'"
  wait_for "Biten olay RabbitMQ'ya yayınlandı" \
    "curl -fsS $GATEWAY_URL/api/analysis/status | grep -Eq '\"eventsPublished\":[1-9]'"

  # Olay arama: kamera + kategori filtresi, ad katalogdan gelir; limit=1 ile ikinci sayfa cursor ile alınır.
  wait_for "Olay aramada en az 2 insan olayı" \
    "[ \$(curl -fsS '$GATEWAY_URL/api/analysis/events/search?category=Person&limit=2' | grep -o '\"category\":\"Person\"' | wc -l) -ge 2 ]"
  page1=$(curl -fsS "$GATEWAY_URL/api/analysis/events/search?category=Person&limit=1")
  grep -q '"cameraName":"' <<<"$page1" && echo "  olay arama: kamera adı ✅" || fail $LINENO
  cursor=$(sed -nE 's/.*"nextCursor":"([^"]+)".*/\1/p' <<<"$page1")
  [ -n "$cursor" ] || { echo "❌ olay aramada nextCursor yok: $page1"; exit 1; }
  first_id=$(sed -E 's/.*"items":\[\{"id":"([^"]+)".*/\1/' <<<"$page1")
  page2=$(curl -fsS "$GATEWAY_URL/api/analysis/events/search?category=Person&limit=1&cursor=$cursor")
  grep -q '"category":"Person"' <<<"$page2" && ! grep -q "\"id\":\"$first_id\"" <<<"$page2" && echo "  olay arama: cursor ile ikinci sayfa ✅" || fail $LINENO
  # Aralık (en fazla 15 dk) her denemede yeniden hesaplanır: son 10 dakika.
  wait_for "Tespit kareleri (frames) insan içeriyor" \
    "curl -fsS \"$GATEWAY_URL/api/analysis/cameras/cam3/frames?from=\$(utc 'now - 10 minutes')&to=\$(utc now)\" | grep -q '\"category\":\"Person\"'"
else
  echo "  ⚠️  samples/videos boş: test desenlerinde nesne yok, tespit adımları atlandı (./scripts/download-sample-videos.sh)"
fi

echo "📷 Kamera yönetimi (ağ geçidi üzerinden)"
cleanup
curl -fsS -X POST "$GATEWAY_URL/api/cameras/probe" -H 'Content-Type: application/json' \
  -d "{\"rtspUrl\":\"$TEST_CAMERA_RTSP\"}" | grep -q '"ok":true' && echo "  bağlantı testi (probe) ✅" || fail $LINENO
expect_status "$TEST_CAMERA eklendi" 201 -X POST "$GATEWAY_URL/api/cameras" -H 'Content-Type: application/json' \
  -d "{\"id\":\"$TEST_CAMERA\",\"name\":\"Duman testi\",\"rtspUrl\":\"$TEST_CAMERA_RTSP\"}"
expect_status "aynı kimlik ikinci kez" 409 -X POST "$GATEWAY_URL/api/cameras" -H 'Content-Type: application/json' \
  -d "{\"id\":\"$TEST_CAMERA\",\"name\":\"Duman testi\",\"rtspUrl\":\"$TEST_CAMERA_RTSP\"}"
expect_status "geçersiz kamera" 400 -X POST "$GATEWAY_URL/api/cameras" -H 'Content-Type: application/json' \
  -d '{"id":"","name":"","rtspUrl":"http://yanlis"}'
etag=$(curl -fsS -D - -o /dev/null "$GATEWAY_URL/api/cameras/$TEST_CAMERA" | tr -d '\r' | sed -nE 's/^[Ee][Tt][Aa][Gg]: *//p')
[ -n "$etag" ] && echo "  ETag: $etag ✅" || fail $LINENO
wait_for "$TEST_CAMERA çevrimiçi" "curl -fsS $GATEWAY_URL/api/cameras/$TEST_CAMERA | grep -q '\"state\":\"Online\"'"
wait_for "$TEST_CAMERA kayıt servisinde" "curl -fsS $GATEWAY_URL/api/recordings/status | grep -q '\"cameraId\":\"$TEST_CAMERA\"'"
wait_for "$TEST_CAMERA analiz servisinde" "curl -fsS $GATEWAY_URL/api/analysis/status | grep -q '\"cameraId\":\"$TEST_CAMERA\"'"
expect_status "eski sürümle güncelleme (If-Match)" 412 -X PUT "$GATEWAY_URL/api/cameras/$TEST_CAMERA" \
  -H 'Content-Type: application/json' -H 'If-Match: "999999"' -d '{"name":"Çakışma","enabled":true}'
expect_status "güncel sürümle ad değişikliği" 200 -X PUT "$GATEWAY_URL/api/cameras/$TEST_CAMERA" \
  -H 'Content-Type: application/json' -H "If-Match: $etag" -d '{"name":"Duman testi 2","enabled":true}'
curl -fsS "$GATEWAY_URL/api/cameras/$TEST_CAMERA" | grep -q '"name":"Duman testi 2"' && echo "  ad güncellendi, adres korundu ✅" || fail $LINENO
expect_status "$TEST_CAMERA silindi" 204 -X DELETE "$GATEWAY_URL/api/cameras/$TEST_CAMERA"
expect_status "silinen kamera" 404 "$GATEWAY_URL/api/cameras/$TEST_CAMERA"
wait_for "$TEST_CAMERA kayıt servisinden kalktı" "lacks_camera $GATEWAY_URL/api/recordings/status $TEST_CAMERA"
wait_for "$TEST_CAMERA analiz servisinden kalktı" "lacks_camera $GATEWAY_URL/api/analysis/status $TEST_CAMERA"
trap - EXIT

echo
echo "Kamera durumu:   $(curl -fsS "$GATEWAY_URL/api/cameras")"
echo "Kayıt durumu:    $(curl -fsS "$GATEWAY_URL/api/recordings/status")"
echo "Son olaylar:     $(curl -fsS "$GATEWAY_URL/api/analysis/events/search?limit=3")"
echo
echo "✅ Duman testi başarılı"
