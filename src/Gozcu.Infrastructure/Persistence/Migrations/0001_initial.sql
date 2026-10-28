-- Kameralar: 1. aşamada yapılandırmadan senkronize edilir, ileride API servisi yönetecek.
CREATE TABLE cameras (
    id          text        PRIMARY KEY,
    name        text        NOT NULL,
    rtsp_url    text        NOT NULL,
    enabled     boolean     NOT NULL DEFAULT true,
    created_at  timestamptz NOT NULL DEFAULT now(),
    updated_at  timestamptz NOT NULL DEFAULT now()
);

-- Kayıt parçaları: her satır diskte yeniden kodlanmadan yazılmış bir MP4 dosyası.
CREATE TABLE recording_segments (
    id          bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    camera_id   text        NOT NULL,
    file_path   text        NOT NULL UNIQUE,
    started_at  timestamptz NOT NULL,
    ended_at    timestamptz NOT NULL,
    size_bytes  bigint      NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT now(),
    deleted_at  timestamptz NULL,
    CONSTRAINT ck_recording_segments_time CHECK (ended_at >= started_at)
);

-- Saate göre geri oynatma sorgusu: belirli kameranın belirli aralıktaki parçaları.
CREATE INDEX ix_recording_segments_camera_time
    ON recording_segments (camera_id, started_at)
    WHERE deleted_at IS NULL;
