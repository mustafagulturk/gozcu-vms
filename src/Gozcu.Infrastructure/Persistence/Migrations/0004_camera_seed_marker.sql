-- 4. aşama: yapılandırmadaki kameralar veritabanına yalnızca bir kez tohum olarak yazılır. Bu tablo tohumlamanın
-- yapıldığını (ya da tablo zaten dolu olduğu için atlandığını) kaydeder; böylece tüm kameralar API ile silinse bile
-- sonraki açılışta yapılandırmadaki liste geri gelmez.
CREATE TABLE app_markers (
    name        text        PRIMARY KEY,
    created_at  timestamptz NOT NULL DEFAULT now()
);
