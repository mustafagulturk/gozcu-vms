-- 4. aşama: kameralar artık API ile yönetilir; veritabanı tek doğruluk kaynağıdır.
-- version: iyimser eşzamanlılık (HTTP ETag / If-Match). Her güncellemede bir artar.
ALTER TABLE cameras ADD COLUMN version integer NOT NULL DEFAULT 1;

-- Olay araması: kamera belirtilmeden kategoriye göre en yeni olaylar.
CREATE INDEX ix_detection_events_category_time ON detection_events (category, started_at DESC, id DESC);
