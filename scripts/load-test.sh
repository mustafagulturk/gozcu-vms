#!/usr/bin/env bash
# Sistem yük testi: kamera sayısını adım adım artırıp her adımda servislerin kaynak kullanımını ve
# işin yetişip yetişmediğini ölçer. "Bu makinede kaç kamera?" sorusunun cevabını üretir.
#
# Her adımda:
#   1) N sahte kamera hazırlanır. Kamera K, samples/videos/cam((K-1)%4+1).mp4 dosyasını yayınlar (sabit bağlantı,
#      ek disk yok). MediaMTX yayını -c copy ile yaptığı için sahte kameraların CPU'su ölçümü bozmaz.
#   2) Sistem docker compose ile (ek bir override dosyasıyla) N kamerayla temiz veritabanı üzerinde başlatılır.
#   3) Isınma süresinden sonra ölçüm penceresi boyunca 5 sn'de bir `docker stats` örneklenir; pencerenin başında
#      ve sonunda servis API'lerinden sayaçlar okunur (analiz edilen/atılan kare, yazılan parça, kayıt gecikmesi).
#   4) Sonuç .loadtest/results.jsonl dosyasına bir satır olarak eklenir; en sonda Markdown tablo üretilir.
#
# Kullanım:
#   ./scripts/load-test.sh                               # tam sistem: 4 8 12 16 kamera
#   CAMERAS="16 32 64" SERVICES="streaming recording" ./scripts/load-test.sh   # yalnızca canlı izleme + kayıt
#   WARMUP=60 DURATION=120 ANALYSIS_FPS=2 ./scripts/load-test.sh
#
# Gereksinimler: Docker Compose v2, curl, jq, python3; imajlar önceden derlenmiş olmalı (docker compose build).
set -euo pipefail

cd "$(dirname "$0")/.."

CAMERAS="${CAMERAS:-4 8 12 16}"
SERVICES="${SERVICES:-streaming recording analysis}"
WARMUP="${WARMUP:-60}"
DURATION="${DURATION:-120}"
SAMPLE_EVERY="${SAMPLE_EVERY:-5}"
export SEGMENT_SECONDS="${SEGMENT_SECONDS:-10}"
export ANALYSIS_FPS="${ANALYSIS_FPS:-2}"
export ANALYSIS_MAX_CONCURRENCY="${ANALYSIS_MAX_CONCURRENCY:-2}"
LABEL="${LABEL:-$(echo "$SERVICES" | tr ' ' '+')}"
WORK=".loadtest"
RESULTS="${RESULTS:-$WORK/results.jsonl}"
REPORT="${REPORT:-$WORK/report.md}"

STREAMING_URL=http://localhost:5080
RECORDING_URL=http://localhost:5081
ANALYSIS_URL=http://localhost:5082

has_service() { [[ " $SERVICES " == *" $1 "* ]]; }

for video in samples/videos/cam{1,2,3,4}.mp4; do
  [[ -f "$video" ]] || { echo "❌ $video yok. Önce ./scripts/download-sample-videos.sh çalıştırın."; exit 1; }
done

mkdir -p "$WORK"
COMPOSE=(docker compose -f docker-compose.yml -f "$WORK/compose.loadtest.yml")

# N kameralık MediaMTX yapılandırması, video bağlantıları ve compose override dosyası üretir.
prepare() {
  local n="$1"
  rm -rf "$WORK/videos" && mkdir -p "$WORK/videos"
  {
    echo "# load-test.sh tarafından üretildi: $n sahte kamera"
    sed -n '/^logLevel:/,/^paths:/p' deploy/mediamtx/mediamtx.yml | sed 's/^logLevel: info/logLevel: warn/'
    for ((k = 1; k <= n; k++)); do
      ln "samples/videos/cam$(((k - 1) % 4 + 1)).mp4" "$WORK/videos/cam$k.mp4" 2>/dev/null \
        || cp "samples/videos/cam$(((k - 1) % 4 + 1)).mp4" "$WORK/videos/cam$k.mp4"
      printf '  cam%d:\n    runOnInit: sh /fake-camera.sh\n    runOnInitRestart: yes\n' "$k"
    done
  } > "$WORK/mediamtx.yml"

  local cameras=""
  for ((k = 1; k <= n; k++)); do
    cameras+="      Cameras__$((k - 1))__Id: cam$k"$'\n'
    cameras+="      Cameras__$((k - 1))__Name: Kamera $k"$'\n'
    cameras+="      Cameras__$((k - 1))__RtspUrl: rtsp://mediamtx:8554/cam$k"$'\n'
  done
  {
    echo "# load-test.sh tarafından üretildi: $n kamera"
    echo "services:"
    echo "  mediamtx:"
    echo "    volumes:"
    echo "      - ./$WORK/mediamtx.yml:/mediamtx.yml:ro"
    echo "      - ./$WORK/videos:/videos:ro"
    for svc in streaming recording analysis; do
      echo "  $svc:"
      echo "    environment:"
      printf '%s' "$cameras"
    done
  } > "$WORK/compose.loadtest.yml"
}

wait_until() {
  local description="$1" check="$2" timeout="${3:-240}" deadline=$((SECONDS + ${3:-240}))
  printf '  ⏳ %s' "$description"
  until eval "$check" >/dev/null 2>&1; do
    if ((SECONDS > deadline)); then printf ' ❌ (%ss)\n' "$timeout"; return 1; fi
    printf '.'; sleep 3
  done
  printf ' ✅\n'
}

# Servis API'lerinden anlık sayaçlar (JSON).
snapshot() {
  local streaming='[]' recording='{}' analysis='{}'
  has_service streaming && streaming=$(curl -fsS "$STREAMING_URL/api/cameras" || echo '[]')
  has_service recording && recording=$(curl -fsS "$RECORDING_URL/api/recordings/status" || echo '{}')
  has_service analysis && analysis=$(curl -fsS "$ANALYSIS_URL/api/analysis/status" || echo '{}')
  jq -n --argjson s "$streaming" --argjson r "$recording" --argjson a "$analysis" \
    '{t: now, streaming: $s, recording: $r, analysis: $a}'
}

# /proc/stat'tan toplam ve boşta CPU jiffy'leri: makine geneli CPU kullanımı için.
host_cpu() { awk '/^cpu /{idle=$5+$6; total=0; for(i=2;i<=NF;i++) total+=$i; print total, idle}' /proc/stat; }

run_step() {
  local n="$1"
  echo "▶ $n kamera ($SERVICES)"
  prepare "$n"
  "${COMPOSE[@]}" down -v --remove-orphans >/dev/null 2>&1 || true
  # RabbitMQ ilk açılışta sağlık denetimini geç geçebilir; bağımlılık hatasında bir kez daha dene.
  "${COMPOSE[@]}" up -d --no-build mediamtx postgres rabbitmq $SERVICES >/dev/null 2>&1 \
    || { sleep 20; "${COMPOSE[@]}" up -d --no-build mediamtx postgres rabbitmq $SERVICES >/dev/null; }

  has_service streaming && wait_until "akış servisi hazır" "curl -fsS $STREAMING_URL/health/ready"
  has_service recording && wait_until "kayıt servisi hazır" "curl -fsS $RECORDING_URL/health/ready"
  has_service analysis && wait_until "analiz servisi hazır" "curl -fsS $ANALYSIS_URL/health/ready"
  has_service recording && wait_until "tüm kameralar kayıtta" \
    "[ \$(curl -fsS $RECORDING_URL/api/recordings/status | jq '[.recorders[] | select(.state==\"Recording\")] | length') -eq $n ]"

  echo "  🔥 ısınma ${WARMUP}s"
  sleep "$WARMUP"

  local before after stats="$WORK/stats.txt" cpu0 cpu1
  before=$(snapshot)
  cpu0=$(host_cpu)
  : > "$stats"
  local end=$((SECONDS + DURATION))
  echo "  📏 ölçüm ${DURATION}s"
  while ((SECONDS < end)); do
    docker stats --no-stream --format '{{.Name}}\t{{.CPUPerc}}\t{{.MemUsage}}' >> "$stats"
    sleep "$SAMPLE_EVERY"
  done
  cpu1=$(host_cpu)
  after=$(snapshot)

  printf '%s' "$before" > "$WORK/before.json"
  printf '%s' "$after" > "$WORK/after.json"
  python3 - "$n" "$LABEL" "$ANALYSIS_FPS" "$stats" "$cpu0" "$cpu1" "$(nproc)" "$SEGMENT_SECONDS" "$WORK" \
    >> "$RESULTS" <<'PY'
import json, re, sys, statistics as st
n, label, fps, stats_path, cpu0, cpu1, ncpu, segment_seconds, work = sys.argv[1:10]
n, fps, ncpu, segment_seconds = int(n), float(fps), int(ncpu), int(segment_seconds)
before = json.load(open(f"{work}/before.json", encoding="utf-8"))
after = json.load(open(f"{work}/after.json", encoding="utf-8"))
elapsed = after["t"] - before["t"]

def mib(text):
    value, unit = re.match(r"([\d.]+)\s*([KMG]i?B)", text).groups()
    return float(value) * {"KiB": 1 / 1024, "MiB": 1, "GiB": 1024, "kB": 1 / 1024, "MB": 1, "GB": 1024}[unit]

services = {}
for line in open(stats_path, encoding="utf-8"):
    name, cpu, mem = line.rstrip("\n").split("\t")
    m = re.match(r"gozcu-vms-(\w+)-\d+", name)
    if not m:
        continue
    s = services.setdefault(m.group(1), {"cpu": [], "mem": []})
    s["cpu"].append(float(cpu.rstrip("%")))
    s["mem"].append(mib(mem.split("/")[0].strip()))
services = {k: {"cpu": round(st.mean(v["cpu"]), 1), "cpu_max": round(max(v["cpu"]), 1),
                "mem_mib": round(max(v["mem"]))} for k, v in services.items()}

(t0, i0), (t1, i1) = (map(int, cpu0.split()), map(int, cpu1.split()))
host_cpu = round(100 * (1 - (i1 - i0) / max(1, t1 - t0)), 1)

result = {"label": label, "cameras": n, "duration_s": round(elapsed), "cpus": ncpu, "segment_seconds": segment_seconds,
          "host_cpu_pct": host_cpu, "services": services}

if after["streaming"]:
    result["streaming_online"] = sum(1 for c in after["streaming"] if (c.get("state") or (c.get("health") or {}).get("state")) == "Online")

if after["recording"]:
    rb = {r["cameraId"]: r for r in before["recording"]["recorders"]}
    ra = {r["cameraId"]: r for r in after["recording"]["recorders"]}
    written = sum(ra[c]["segmentsWritten"] - rb.get(c, {"segmentsWritten": 0})["segmentsWritten"] for c in ra)
    result["recording"] = {
        "recording": sum(1 for r in ra.values() if r["state"] == "Recording"),
        "restarts": sum(r["restartCount"] for r in ra.values()),
        "segments_written": written,
        "segments_expected": round(n * elapsed / segment_seconds),
        "index_latency_avg_ms": after["recording"]["metrics"]["averageLatencyMs"],
        "index_latency_max_ms": after["recording"]["metrics"]["maxLatencyMs"],
    }

if after["analysis"]:
    ab = {c["cameraId"]: c for c in before["analysis"]["cameras"]}
    aa = {c["cameraId"]: c for c in after["analysis"]["cameras"]}
    analyzed = sum(aa[c]["framesAnalyzed"] - ab[c]["framesAnalyzed"] for c in aa)
    received = sum(aa[c]["framesReceived"] - ab[c]["framesReceived"] for c in aa)
    dropped = sum(aa[c]["framesDropped"] - ab[c]["framesDropped"] for c in aa)
    per_camera = [(aa[c]["framesAnalyzed"] - ab[c]["framesAnalyzed"]) / elapsed for c in aa]
    inference = [c["averageInferenceMs"] for c in aa.values() if c["averageInferenceMs"]]
    result["analysis"] = {
        "target_fps": n * fps,
        "analyzed_fps": round(analyzed / elapsed, 2),
        "received_fps": round(received / elapsed, 2),
        "drop_pct": round(100 * dropped / max(1, received), 1),
        "min_camera_fps": round(min(per_camera), 2),
        "inference_avg_ms": round(st.mean(inference), 1) if inference else None,
        "writer": after["analysis"].get("writer"),
    }

ok = True
if "streaming_online" in result: ok &= result["streaming_online"] == n
if "recording" in result: ok &= result["recording"]["recording"] == n and \
    result["recording"]["segments_written"] >= 0.9 * result["recording"]["segments_expected"]
if "analysis" in result: ok &= result["analysis"]["analyzed_fps"] >= 0.9 * result["analysis"]["target_fps"]
result["keeps_up"] = bool(ok)
print(json.dumps(result, ensure_ascii=False))
PY
  tail -1 "$RESULTS" | jq -c '{cameras, host_cpu_pct, keeps_up, analysis: (.analysis // {} | {analyzed_fps, target_fps, drop_pct}), recording: (.recording // {} | {segments_written, segments_expected})}'
}

trap '"${COMPOSE[@]}" down -v --remove-orphans >/dev/null 2>&1 || true' EXIT

for n in $CAMERAS; do
  run_step "$n"
done

python3 - "$RESULTS" > "$REPORT" <<'PY'
import json, sys
rows = [json.loads(l) for l in open(sys.argv[1], encoding="utf-8") if l.strip()]
def svc(r, name):
    s = r["services"].get(name)
    return f'%{s["cpu"]:.0f} / {s["mem_mib"]} MiB' if s else "–"
print("| Senaryo | Kamera | Makine CPU | MediaMTX | Akış | Kayıt | Analiz | Analiz hızı (hedef) | Atılan kare | Parça (beklenen) | Kayıt gecikmesi ort./en çok | Yetişiyor mu |")
print("|---|---|---|---|---|---|---|---|---|---|---|---|")
for r in rows:
    a, rec = r.get("analysis"), r.get("recording")
    print("| {} | {} | %{} | {} | {} | {} | {} | {} | {} | {} | {} | {} |".format(
        r["label"], r["cameras"], r["host_cpu_pct"], svc(r, "mediamtx"), svc(r, "streaming"), svc(r, "recording"),
        svc(r, "analysis"),
        f'{a["analyzed_fps"]} fps ({a["target_fps"]:g})' if a else "–",
        f'%{a["drop_pct"]}' if a else "–",
        f'{rec["segments_written"]} ({rec["segments_expected"]})' if rec else "–",
        f'{rec["index_latency_avg_ms"]} / {rec["index_latency_max_ms"]} ms' if rec else "–",
        "✅" if r["keeps_up"] else "❌"))
PY
echo "📄 $REPORT"
cat "$REPORT"
