#!/bin/sh
# MediaMTX tarafından her kamera path'i için çalıştırılır (MTX_PATH ve RTSP_PORT ortamdan gelir).
# /videos/<path>.mp4 varsa yeniden kodlamadan sonsuz döngüde yayınlar; yoksa test deseni üretir.
#
# Kullanım (mediamtx.yml): sh /fake-camera.sh [desen] [çözünürlük] [fps]
#   desen: ffmpeg lavfi kaynağı (testsrc, testsrc2, smptehdbars, life ...). Kameralar ızgarada ayırt edilsin
#   diye her kameraya farklı desen ve çözünürlük verilir.
set -eu

PATTERN="${1:-testsrc}"
SIZE="${2:-1280x720}"
RATE="${3:-25}"

VIDEO="/videos/${MTX_PATH}.mp4"
TARGET="rtsp://localhost:${RTSP_PORT}/${MTX_PATH}"

if [ -f "$VIDEO" ]; then
  echo "[fake-camera] ${MTX_PATH}: ${VIDEO} yayınlanıyor"
  exec ffmpeg -hide_banner -loglevel warning -re -stream_loop -1 -i "$VIDEO" \
    -map 0:v:0 -c copy -f rtsp -rtsp_transport tcp "$TARGET"
fi

# Gerçek kameralar gibi 2 saniyede bir anahtar kare (GOP = 2 × fps).
GOP=$((RATE * 2))
echo "[fake-camera] ${MTX_PATH}: video bulunamadı, ${PATTERN} ${SIZE}@${RATE} test deseni yayınlanıyor"
exec ffmpeg -hide_banner -loglevel warning -re \
  -f lavfi -i "${PATTERN}=size=${SIZE}:rate=${RATE}" \
  -vf format=yuv420p \
  -c:v libx264 -preset ultrafast -tune zerolatency -g "$GOP" -keyint_min "$GOP" \
  -f rtsp -rtsp_transport tcp "$TARGET"
