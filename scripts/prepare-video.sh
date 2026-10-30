#!/usr/bin/env bash
# Bir video dosyasını sahte kamera için hazırlar ve samples/videos/<kamera>.mp4 olarak kaydeder.
# Gerçek IP kameraları taklit etmek için: H.264, 1280x720, 25 fps, 2 sn'de bir anahtar kare, ses yok.
# Bu dönüştürme yalnızca bir kez yapılır; sistem çalışırken kamera akışı yeniden kodlanmaz.
#
# Kullanım: ./scripts/prepare-video.sh ~/Downloads/trafik.mp4 cam1
set -euo pipefail

if [ $# -ne 2 ]; then
  echo "Kullanım: $0 <girdi-video> <kamera-id>" >&2
  exit 1
fi

INPUT="$(cd "$(dirname "$1")" && pwd)/$(basename "$1")"
CAMERA="$2"
REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUTPUT_DIR="$REPO_ROOT/samples/videos"
mkdir -p "$OUTPUT_DIR"

ARGS=(-hide_banner -loglevel warning -y -i /in/input
  -an -vf "scale=1280:720:force_original_aspect_ratio=decrease,pad=1280:720:(ow-iw)/2:(oh-ih)/2,fps=25"
  -c:v libx264 -preset veryfast -profile:v main -pix_fmt yuv420p -g 50 -keyint_min 50 -sc_threshold 0
  -movflags +faststart "/out/$CAMERA.mp4")

if command -v ffmpeg >/dev/null 2>&1; then
  # Yerel ffmpeg: /in ve /out yerine gerçek yolları kullan.
  LOCAL_ARGS=("${ARGS[@]}")
  LOCAL_ARGS[5]="$INPUT"
  LOCAL_ARGS[${#LOCAL_ARGS[@]}-1]="$OUTPUT_DIR/$CAMERA.mp4"
  ffmpeg "${LOCAL_ARGS[@]}"
else
  echo "Yerel ffmpeg bulunamadı, MediaMTX imajındaki ffmpeg kullanılıyor"
  docker run --rm -v "$INPUT:/in/input:ro" -v "$OUTPUT_DIR:/out" \
    --entrypoint ffmpeg bluenviron/mediamtx:1.21.3-ffmpeg "${ARGS[@]}"
fi

echo "✅ $OUTPUT_DIR/$CAMERA.mp4 hazır. Yayına almak için: docker compose restart mediamtx"
