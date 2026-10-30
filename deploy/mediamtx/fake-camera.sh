#!/bin/sh
# MediaMTX tarafından her kamera path'i için çalıştırılır (MTX_PATH ve RTSP_PORT ortamdan gelir).
# /videos/<path>.mp4 varsa yeniden kodlamadan sonsuz döngüde yayınlar; yoksa test deseni üretir.
set -eu

VIDEO="/videos/${MTX_PATH}.mp4"
TARGET="rtsp://localhost:${RTSP_PORT}/${MTX_PATH}"

if [ -f "$VIDEO" ]; then
  echo "[fake-camera] ${MTX_PATH}: ${VIDEO} yayınlanıyor"
  exec ffmpeg -hide_banner -loglevel warning -re -stream_loop -1 -i "$VIDEO" \
    -map 0:v:0 -c copy -f rtsp -rtsp_transport tcp "$TARGET"
fi

echo "[fake-camera] ${MTX_PATH}: video bulunamadı, test deseni yayınlanıyor"
exec ffmpeg -hide_banner -loglevel warning -re \
  -f lavfi -i "testsrc=size=1280x720:rate=25" \
  -c:v libx264 -preset ultrafast -tune zerolatency -pix_fmt yuv420p -g 50 -keyint_min 50 \
  -f rtsp -rtsp_transport tcp "$TARGET"
