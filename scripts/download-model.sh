#!/usr/bin/env bash
# Nesne tespiti için YOLOX ONNX modelini indirir ve SHA-256 özetini doğrular (<hedef>/<ad>.onnx).
# Kaynak: Megvii YOLOX 0.1.1rc0 sürümü, Apache-2.0 – https://github.com/Megvii-BaseDetection/YOLOX
# Model dosyaları git'e eklenmez (models/.gitignore). Ayrıntılar: models/README.md
#
# Kullanım: ./scripts/download-model.sh [yolox_nano|yolox_tiny|yolox_s]   (varsayılan: yolox_tiny)
# Hedef klasör MODELS_DIR ile değiştirilebilir (varsayılan: <depo kökü>/models). Docker imajında betik tek başına
# kopyalanıp şöyle çalıştırılır: MODELS_DIR=/models bash /download-model.sh yolox_tiny
# Yalnızca bash, curl ve sha256sum gerektirir.
set -euo pipefail

BASE_URL="https://github.com/Megvii-BaseDetection/YOLOX/releases/download/0.1.1rc0"
NAME="${1:-yolox_tiny}"

case "$NAME" in
  yolox_nano) SHA256=c789161ed43c8269fcd4e67c67eeeb4e80c622da2eb296a20bc6007bd18a0b7d ;;
  yolox_tiny) SHA256=427cc366d34e27ff7a03e2899b5e3671425c262ea2291f88bb942bc1cc70b0f7 ;;
  yolox_s)    SHA256=c5c2d13e59ae883e6af3b45daea64af4833a4951c92d116ec270d9ddbe998063 ;;
  *)
    echo "❌ Bilinmeyen model: $NAME. Geçerli seçenekler: yolox_nano, yolox_tiny, yolox_s" >&2
    exit 1
    ;;
esac

if [ -n "${MODELS_DIR:-}" ]; then
  TARGET_DIR="$MODELS_DIR"
else
  TARGET_DIR="$(cd "$(dirname "$0")/.." && pwd)/models"
fi

TARGET="$TARGET_DIR/$NAME.onnx"
mkdir -p "$TARGET_DIR"

verify() {
  echo "$SHA256  $1" | sha256sum -c --status
}

if [ -f "$TARGET" ] && verify "$TARGET"; then
  echo "✅ $NAME.onnx zaten mevcut ve özeti doğru: $TARGET"
  exit 0
fi

TMP_FILE="$TARGET.part"
trap 'rm -f "$TMP_FILE"' EXIT

echo "⬇️  $NAME.onnx indiriliyor: $BASE_URL/$NAME.onnx"
curl -fSL --retry 3 -o "$TMP_FILE" "$BASE_URL/$NAME.onnx"

if ! verify "$TMP_FILE"; then
  echo "❌ SHA-256 doğrulaması başarısız: indirilen dosya beklenen özetle ($SHA256) eşleşmiyor" >&2
  exit 1
fi

mv "$TMP_FILE" "$TARGET"
echo "✅ Model hazır: $TARGET"
