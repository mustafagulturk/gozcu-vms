-- Analiz edilen ve en az bir nesne bulunan kareler. Boş kareler yazılmaz.
-- Kutular kaynak görüntüye göre 0..1 aralığında normalize edilir (çözünürlükten bağımsız).
CREATE TABLE detection_frames (
    id            bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    camera_id     text        NOT NULL,
    captured_at   timestamptz NOT NULL,
    inference_ms  real        NOT NULL,
    -- [{"label":"car","category":"Vehicle","confidence":0.91,"x":0.1,"y":0.2,"w":0.3,"h":0.4}, ...]
    detections    jsonb       NOT NULL
);

CREATE INDEX ix_detection_frames_camera_time ON detection_frames (camera_id, captured_at);

-- Saklama süresi dolan karelerin silinmesi (kameradan bağımsız "captured_at < sınır"). Satırlar zamana göre
-- eklendiği için BRIN dizini çok küçük kalır ve aralık taramasını yeterince daraltır.
CREATE INDEX ix_detection_frames_time_brin ON detection_frames USING brin (captured_at);

-- Olaylar: bir kategorinin bir kamerada görünmeye başlayıp kaybolduğu aralık.
-- Zaman çizelgesi işaretleri ve olay araması (4. aşama) bu tabloyu kullanır.
CREATE TABLE detection_events (
    id               uuid        PRIMARY KEY,
    camera_id        text        NOT NULL,
    category         text        NOT NULL,
    started_at       timestamptz NOT NULL,
    ended_at         timestamptz NULL,      -- NULL: olay hâlâ sürüyor
    max_count        integer     NOT NULL,
    peak_confidence  real        NOT NULL,
    frame_count      integer     NOT NULL,
    CONSTRAINT ck_detection_events_time CHECK (ended_at IS NULL OR ended_at >= started_at)
);

CREATE INDEX ix_detection_events_camera_time ON detection_events (camera_id, started_at);
CREATE INDEX ix_detection_events_open ON detection_events (camera_id) WHERE ended_at IS NULL;

-- Kamera belirtilmeden "en yeni olaylar" sorgusu.
CREATE INDEX ix_detection_events_time ON detection_events (started_at DESC);
