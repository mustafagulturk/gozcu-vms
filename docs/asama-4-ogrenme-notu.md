# Aşama 4 öğrenme notu: olay arama ve web panel

Bu not, 4. aşamada ne yaptığımızı, neden öyle yaptığımızı ve mülakatta bu konularda sorulabilecek soruları özetler.
Önceki aşamaların notları ayrıca duruyor; sonda hepsini birleştiren bir mülakat hazırlık dokümanı yazılacak.

## 1. Kısaca ne yaptık?

| Parça | Ne yapıyor | Neden önemli |
|---|---|---|
| Ağ geçidi (`Gozcu.Gateway`, `:5000`) | Web panelini sunar, `/api/**` ve `/hls/**` isteklerini YARP ile ilgili servise iletir | Tarayıcı tek adres bilir; CORS yok, iç adresler dışarı sızmaz |
| Web paneli | Canlı, Kayıtlar, Olaylar, Kameralar, Analiz sayfaları, ortak gezinme | İlandaki "web tabanlı arayüz"; operatörün gerçekten kullandığı yer |
| Kamera yönetimi API'si | `/api/cameras` ile ekle, değiştir, sil, bağlantı testi; ETag / If-Match | Kameralar artık yapılandırma dosyasında değil, veritabanında |
| Dinamik kamera işleri | Kamera eklenince izleme, kayıt ve analiz **servis yeniden başlamadan** başlar; silinince durur | Gerçek VMS'te kamera eklemek için sistemi kapatamazsın |
| Olay arama | Kamera, kategori, zaman, güven ve süreye göre süzme; keyset sayfalama | "Dün 14:00–15:00 arası Otopark'ta araç" sorusunun cevabı |
| Kayıttan küçük resim | `frame.jpg`: olay anındaki kareyi kayıttan JPEG olarak çıkarır | Operatör listeye bakıp hangi olayın ilginç olduğunu görür |
| Tespit kutuları | Geri oynatmada videonun üstüne o andaki kutular çizilir; zaman çizelgesinde renkli olay şeritleri | Analiz sonucu ile kaydı aynı ekranda birleştirir |

Uçtan uca doğrulama: `scripts/smoke-test.sh` artık her şeyi ağ geçidi üzerinden sınar: panel sayfaları, `/hls`, olay
araması (cursor ile ikinci sayfa dahil), tespit kareleri, `frame.jpg`, ve çalışırken `cam5` ekleyip (kayıt ve analiz
servislerinde belirmesi), 409/400/412 hatalarını deneyip, silip (servislerden kalkması) kamera yaşam döngüsünü.

## 2. Temel kavramlar

### API ağ geçidi (API gateway) ve ters vekil (reverse proxy)
- Ters vekil, istemcinin isteğini kendi adına arka uçtaki bir sunucuya iletir. İstemci arka ucu görmez.
- API ağ geçidi bunun mikroservis sürümüdür: **yol tabanlı yönlendirme** (`/api/cameras/**` → akış servisi,
  `/api/playback/**` → kayıt servisi, `/api/analysis/**` → analiz servisi, `/hls/**` → MediaMTX).
- **YARP** (Yet Another Reverse Proxy), Microsoft'un .NET kütüphanesidir; rotalar ve kümeler (`Routes`, `Clusters`)
  `appsettings.json`'dan okunur, Compose'da ortam değişkeniyle ezilir
  (`ReverseProxy__Clusters__recording__Destinations__default__Address=http://recording:8080/`).
- Neden gerekti?
  - **Aynı köken (same-origin) politikası:** Tarayıcı, sayfanın geldiği köken (şema + alan + port) dışındaki bir adrese
    JavaScript ile istek atınca yanıtı okuyamaz; sunucu CORS başlıklarıyla izin vermelidir. Önceden canlı sayfa `:5080`,
    geri oynatma `:5081`, analiz `:5082`'deydi. Olaylar sayfası üç servisin verisini birleştirdiği için ya her servise
    CORS eklenecekti ya da tek köken olacaktı. Tek köken daha basit ve daha güvenli.
  - Servis adresleri sayfalara gömülmez (`PublicPlaybackUrl` gibi ayarlar gereksizleşti), port değişse panel bozulmaz.
  - İleride kimlik doğrulama, hız sınırlama, TLS sonlandırma tek yerde yapılır.
- Tuzak: önek kaldırma (`PathRemovePrefix: /hls`). MediaMTX ilk HLS isteğine `302 Location: /cam1/index.m3u8?cookieCheck=1`
  döner. Bu **kök göreli** bir adres; tarayıcı `/hls` önekini kaybedip ağ geçidinin köküne gider ve 404 alır. Ağ geçidinde
  bir **yanıt dönüşümü** `Location` başlığına öneki geri ekliyor (`src/Gozcu.Gateway/Program.cs`). Bu hatayı duman testi
  yakaladı; birim testlerde sahte arka uç yönlendirme yapmadığı için görünmüyordu.

### Tek doğruluk kaynağı: kamera kataloğu
- 1–3. aşamada kameralar `appsettings`/ortam değişkeninden okunuyordu: yeni kamera = dosya düzenle + servisleri yeniden başlat.
- Şimdi **veritabanındaki `cameras` tablosu** tek doğruluk kaynağı (single source of truth). Sahibi akış servisi: yalnızca o
  yazar (CRUD API). Kayıt ve analiz servisleri **okur**.
- **Tohumlama (seeding):** İlk açılışta tablo boşsa Compose'daki 4 kamera yazılır. "Tablo boşsa tohumla" kuralı tehlikeli:
  operatör tüm kameraları silerse bir sonraki açılışta geri gelirler. Bu yüzden `app_markers` tablosunda "tohumlama yapıldı"
  işareti tutuluyor; tohumlama ömürde **bir kez** olur. Birden fazla örnek aynı anda açılırsa diye **advisory lock** ile.

### Değişikliği diğer servislere duyurmak: sinyal + periyodik yenileme
- Akış servisi her yazmadan sonra RabbitMQ'ya `camera.catalog.changed` yayınlar. Kayıt ve analiz servisleri kendilerine özel
  (sunucu adlı, exclusive, auto-delete) bir kuyrukla bu anahtara abone olur (`RabbitMqEventSubscriber`).
- Olay **içeriği taşımaz, yalnızca "yeniden oku" der**. Servis kataloğu veritabanından baştan okur. Neden?
  - Olaylar sırasız gelebilir ya da kaybolabilir (outbox yok, aracı kapalı olabilir). İçerik taşıyan olaylarda sıra/kayıp
    tutarsızlık yaratır; "yeniden oku" sinyali **idempotent**tir: iki kez gelse de, geç gelse de sonuç aynı.
  - Gerçek kaynak hep veritabanı; olay yalnızca gecikmeyi kısaltır.
- **Periyodik yenileme (30 sn)** emniyet ağıdır: olay kaybolsa da en geç 30 sn'de herkes aynı listeyi görür. Abonelik yeniden
  kurulduğunda da yenilenir. Bu desene bazen "notify + poll" ya da "level-triggered" uzlaştırma denir.
- Katalog sürümü yalnızca içerik **gerçekten** değişince artar; aynı listeyi tekrar okumak işleri yeniden başlatmaz.

### Uzlaştırma döngüsü (reconciliation loop): `CameraWorkerHost`
- Kubernetes denetleyicilerindeki fikir: **istenen durum** (katalogdaki etkin kameralar) ile **gerçek durum** (çalışan
  kamera işleri) karşılaştırılır, aradaki fark kapatılır:
  - katalogda var, çalışmıyor → başlat
  - çalışıyor, katalogda yok ya da devre dışı → durdur
  - adres değişmiş → **önce durdur, bitmesini bekle, sonra başlat** (aynı kamera için iki ffmpeg aynı dosyaya yazmasın)
- Durdurma: kamera başına `CancellationTokenSource` iptal edilir, görev beklenir. Kayıt servisinde bu ffmpeg'e `q` gönderir,
  son MP4 parçasının başlığı yazılır, dosya bozulmaz.
- Kendiliğinden biten iş 5 sn sonra yeniden başlatılır.
- Kayıt servisi yalnızca **adres** değişince yeniden başlar; kameranın adı değişti diye kayıt kesilmez.
- Üç servis aynı soyut sınıfı kullanır: `CameraMonitorService`, `RecorderService`, `AnalyzerService` yalnızca
  "bir kamera için ne çalıştırılır" kısmını yazar. Uzlaştırma mantığı tek yerde, birim testli (`CameraWorkerHostTests`).

### REST API tasarımı ve iyimser eşzamanlılık
- `GET/POST /api/cameras`, `GET/PUT/DELETE /api/cameras/{id}`, `POST /api/cameras/probe`.
- Durum kodları anlam taşır: **201** + `Location` (oluşturuldu), **400** ValidationProblem (alan alan hatalar: `id`, `name`,
  `rtspUrl`), **404**, **409** (aynı kimlik var), **412** (sürüm uyuşmadı), **204** (silindi).
- **İyimser eşzamanlılık (optimistic concurrency):** Kilit tutulmaz; her satırın `version` sütunu var, yanıtta
  `ETag: "3"` olarak döner. İstemci `PUT` ederken `If-Match: "3"` gönderir; SQL `UPDATE ... WHERE id = @id AND version = @v`
  çalışır, etkilenen satır 0 ise **412 Precondition Failed**. Böylece iki operatör aynı kamerayı aynı anda düzenlerse
  ikincisi birincinin değişikliğini **sessizce ezmez** ("kayıp güncelleme" / lost update problemi). Panel 412'de güncel
  hali gösterip formu korur.
- Kötümser (pessimistic) kilit (`SELECT ... FOR UPDATE`) HTTP'de uygun değil: kullanıcı formu dakikalarca açık tutabilir.

### Gizli bilgiyi korumak: RTSP parolası
- IP kamera adresleri çoğu zaman parola içerir: `rtsp://admin:12345@10.0.0.5/stream1`.
- API yanıtları, günlükler ve ffmpeg hata satırları parolayı `***` ile maskeler (`RtspUrlMasking`).
- Ama maskelenmiş adres geri gelirse? Düzenleme formunda adres boş bırakılırsa ya da `***` içerirse **kayıtlı parola
  korunur**. Böylece operatör kameranın adını değiştirmek için parolayı yeniden yazmak zorunda kalmaz ve parola
  tarayıcıya hiç inmez. `POST`'ta `***` içeren adres reddedilir.

### Keyset (imleç) sayfalama
- Klasik yöntem `ORDER BY started_at DESC LIMIT 50 OFFSET 5000`. Sorunları:
  1. Veritabanı atlanacak 5000 satırı yine de okur; derin sayfalar yavaşlar.
  2. Sayfalar arasında yeni olay gelirse her şey bir kayar: kullanıcı aynı olayı iki kez görür ya da birini kaçırır.
- **Keyset:** son görülen satırın anahtarını hatırla, "bundan küçük olanları" iste:
  `WHERE (started_at, id) < (@lastStartedAt, @lastId) ORDER BY started_at DESC, id DESC LIMIT 51`.
  - Dizinden doğrudan o noktaya atlanır; her sayfa aynı maliyette.
  - `id` **eşitlik bozucu** (tie-breaker): aynı mikro saniyede başlayan iki olay sıralamayı belirsiz yapmasın.
  - 51 satır okunur: 51. varsa "sonraki sayfa var" demektir, imleç 50.'den üretilir.
- İmleç istemciye **opak** verilir: `base64url("<unix mikro saniye>|<guid>")`. İstemci içini bilmez, sunucu biçimi
  değiştirebilir. Bozuk imleç 400 döner, istisna fırlatmaz.
- Bedeli: "sayfa 17'ye atla" yapılamaz. Olay listesinde zaten "daha fazla yükle" yeterli.
- Ölçüm (500 bin olay, 20 kamera, PostgreSQL 16): tüm sorgu biçimleri `ix_detection_events_time` dizinini kullanıyor,
  0,07–0,7 ms.
- **Dinamik WHERE:** `(@x IS NULL OR col = @x)` kalıbı yerine yalnızca verilen süzgeçler SQL'e eklenir; planlayıcı her sorgu
  biçimi için uygun dizini seçebilir. Değerler yine parametre olarak gider (SQL enjeksiyonu yok).

### Zaman aralığı kesişimi
- "10:00–11:00 arası olaylar" = bu aralıkla **kesişen** olaylar: `started_at < @to AND (ended_at IS NULL OR ended_at >= @from)`.
  09:55'te başlayıp 10:05'te biten olay da sonuçtadır. Süren olayın (`ended_at IS NULL`) süresi "şimdiye kadar" sayılır;
  "şimdi" `TimeProvider`'dan gelir, böylece test edilebilir.

### Tespit kutularını kayıtla eşlemek
- Analiz servisi her tespitli kareyi `captured_at` zamanıyla saklar (3. aşama). Geri oynatma sayfası duvar saatini biliyor
  (parça başlangıcı + `currentTime`, 2. aşama). İkisini birleştirmek için:
  - Oynatma anının 30 sn öncesi ile 120 sn sonrası arasındaki kareler `/api/analysis/cameras/{id}/frames` ile **eskiden
    yeniye** yüklenir (en fazla 15 dk'lık aralık, sunucu sınırı).
  - Her `timeupdate`/animasyon karesinde **ikili arama** ile en yakın kare bulunur; ±0,5 sn'den uzaksa kutu çizilmez
    (2 fps'de kareler 0,5 sn arayla).
  - Kutular videonun üstündeki `<canvas>`'a çizilir. `object-fit: contain` siyah şeritleri hesaba katılır; kutular 0..1
    normalize olduğu için video hangi boyutta gösterilirse gösterilsin doğru yere düşer.
- Zaman çizelgesinde olaylar kategori rengiyle şerit olarak çizilir; tıklayınca olaydan 2 sn önce oynatılır (ön kayıt payı).

### Kayıttan tek kare (küçük resim)
- `GET /api/playback/{cam}/frame.jpg?time=...&width=320`: o anı içeren parça bulunur, ffmpeg
  `-ss <ofset> -i parça.mp4 -frames:v 1 -vf scale=320:-2 -q:v 5 -f image2pipe pipe:1` ile JPEG üretir.
  - `-ss` girişten **önce**: ffmpeg en yakın anahtar kareye atlar, sonra çözer; hızlıdır.
  - Çıktı stdout'tan ikili okunur (`ProcessOutputReader`); mevcut süreç çalıştırıcı satır satır metin okuduğu için
    JPEG'e uymuyordu. Boyut sınırı var.
  - En fazla 2 eşzamanlı ffmpeg (`SemaphoreSlim`): 50 kartlık liste 50 süreç açmasın. `Cache-Control: max-age=86400`:
    kayıt değişmez, tarayıcı tekrar istemez.
- Neden önceden üretip saklamadık? Olay başına dosya + saklama politikası + tutarlılık derdi. İsteğe bağlı üretim basit ve
  kayıt zaten diskte. Bedeli: "videoyu yalnızca analiz servisi çözer" ilkesine küçük, bilinçli bir istisna.

## 3. Koda nereden bakılır?

| Konu | Dosya |
|---|---|
| Ağ geçidi, rotalar, HLS yönlendirme düzeltmesi | `src/Gozcu.Gateway/Program.cs`, `src/Gozcu.Gateway/appsettings.json` |
| Panel sayfaları | `src/Gozcu.Gateway/wwwroot/` (`index.html`, `playback.html`, `events.html`, `cameras.html`, `analysis.html`, `panel.js`, `panel.css`) |
| Kamera CRUD API'si, ETag/If-Match | `src/Gozcu.StreamingService/Cameras/CameraManagementEndpoints.cs`, `CameraModels.cs` |
| Tek seferlik tohumlama | `src/Gozcu.StreamingService/Cameras/CameraSeedService.cs`, `Migrations/0004_camera_seed_marker.sql` |
| Depo, sürümlü güncelleme | `src/Gozcu.Infrastructure/Persistence/CameraRepository.cs` |
| Katalog, yenileme, abone | `src/Gozcu.Infrastructure/Cameras/CameraCatalog.cs`, `CameraCatalogRefresher.cs`, `Messaging/RabbitMqEventSubscriber.cs` |
| Uzlaştırma döngüsü | `src/Gozcu.Infrastructure/Cameras/CameraWorkerHost.cs` |
| Doğrulama, parola maskeleme | `src/Gozcu.Contracts/Cameras/CameraValidation.cs`, `RtspUrlMasking.cs` |
| Bağlantı testi | `src/Gozcu.Media/Probing/FfprobeStreamProbe.cs` |
| Olay arama, imleç | `src/Gozcu.Infrastructure/Persistence/EventSearch.cs`, `DetectionRepository.SearchEventsAsync`, `src/Gozcu.AnalysisService/Api/EventSearchEndpoints.cs` |
| Kayıttan JPEG | `src/Gozcu.RecordingService/Playback/FrameEndpoints.cs`, `FrameExtractor.cs`, `FfmpegCommands.JpegFrame` |
| Testler | `tests/Gozcu.UnitTests/Cameras/`, `Analysis/EventSearchParametersTests.cs`, `tests/Gozcu.IntegrationTests/Cameras/`, `Analysis/EventSearchIntegrationTests.cs`, `PlaybackFrameIntegrationTests.cs`, `Gateway/` |

## 4. Bilerek verdiğimiz kararlar ve bedelleri

- **Ayrı bir "API servisi" yazmadık; her veri sahibinde kalır.** Kamera akış servisinde, olaylar analizde, kayıtlar kayıt
  servisinde. Ağ geçidi yalnızca yönlendirir, iş mantığı içermez. Bedeli: olay kartındaki küçük resim için tarayıcı iki
  servise ayrı istek atar (olay listesi + frame.jpg). Birleştirme (BFF / aggregation) gerekirse ağ geçidinde yapılabilir.
- **Ön yüz çerçevesiz (vanilla JS).** Derleme adımı yok, sayfalar statik. Beş sayfa için React gereksiz bağımlılık olurdu;
  büyürse bileşen kütüphanesine geçilir.
- **Kayıt ve analiz servisleri açılışta veritabanını bekler.** Önceden yapılandırmadan hemen kayda başlıyorlardı. Son
  bilinen katalog diske önbelleklenmiyor; veritabanı yokken yeni açılan kayıt servisi kayda başlamaz. Gerçek sistemde
  yerel önbellek eklenmeli.
- **Kimlik doğrulama yok.** Panel ve API herkese açık. Sonraki adım ağ geçidinde OIDC/JWT ve rol (izleyici/operatör/yönetici).
- **Harf duyarsız kimlik tekrarı sorguda kontrol ediliyor**, benzersiz dizinde değil (`lower(id)` üzerinde unique index
  daha sağlam olurdu).
- **frame.jpg bir gün önbellekte.** Saklama politikası parçayı silerse tarayıcı eski kareyi göstermeye devam edebilir.

## 5. Mülakat soruları (kısa yanıtlarıyla)

1. **Neden API ağ geçidi koydun? CORS ile de olmaz mıydı?**
   Olurdu, ama her servise CORS ayarı, sayfalara servis adresleri ve tarayıcıya açılan dört port gerekirdi. Ağ geçidiyle tek
   köken, tek giriş noktası; kimlik doğrulama, TLS, hız sınırı tek yerde. Bedeli: ek bir atlama (hop) ve tek hata noktası
   (yatay ölçeklenerek çözülür).
2. **Ters vekilde önek kaldırınca nelere dikkat edilir?**
   Arka ucun ürettiği kök göreli adresler (yönlendirme `Location`, HTML'deki mutlak yollar, çerez `Path`) öneki bilmez.
   Biz MediaMTX'in 302 `Location` başlığını yeniden yazdık.
3. **Yeni kamera eklenince kayıt servisi nasıl haberdar oluyor? Mesaj kaybolursa?**
   RabbitMQ'da `camera.catalog.changed` sinyali gelir, servis kataloğu veritabanından yeniden okur. Mesaj kaybolursa 30 sn'lik
   periyodik yenileme yakalar. Sinyal içerik taşımadığı için sıra ve tekrar sorun değil (idempotent).
4. **Neden olayın içine kamera bilgisini koymadın?**
   O zaman olaylar sıralı ve eksiksiz gelmek zorunda olurdu; outbox olmadan bunu garanti edemeyiz. Kaynak veritabanı,
   olay yalnızca "bak" der. "Event-carried state transfer" yerine "event notification" seçtik.
5. **Kamera adresi değişince ne olur? Aynı kamera için iki ffmpeg çalışabilir mi?**
   Uzlaştırma döngüsü eski işi iptal eder ve **bitmesini bekler**, sonra yenisini başlatır. Kayıtta ffmpeg'e `q` gönderilir,
   son parça düzgün kapanır. Aynı anda iki süreç olmaz.
6. **İyimser ve kötümser eşzamanlılık farkı? Neden iyimser?**
   Kötümser: düzenlemeden önce kilit al, başkası bekler. İyimser: kilit yok, yazarken sürüm kontrol et, çakışırsa reddet.
   Çakışma nadir ve kullanıcı formu uzun süre açık tuttuğu için iyimser uygun. HTTP'de ETag + If-Match → 412.
7. **409 ile 412 farkı?**
   409 Conflict: kaynağın mevcut durumuyla çelişen istek (aynı kimlikle ikinci kamera). 412 Precondition Failed: istemcinin
   koyduğu ön koşul (`If-Match`) tutmadı.
8. **OFFSET sayfalamanın sorunu ne? Keyset'in bedeli ne?**
   Derin sayfada atlanan satırlar da okunur ve yeni veri gelince sayfalar kayar. Keyset dizinden doğrudan devam eder ve
   kaymaz; bedeli rastgele sayfaya atlanamaması ve sıralama anahtarının benzersiz olması gerekmesi (`id` eşitlik bozucu).
9. **İmleci neden base64 yapıp opak verdin?**
   İstemci içine güvenip kendisi üretmesin; sunucu biçimi değiştirebilsin. Bozuk imleç doğrulanıp 400 döner.
10. **"10:00–11:00 arası olaylar" sorgusunu nasıl yazarsın?**
    Aralıkla kesişenler: `started_at < to AND (ended_at IS NULL OR ended_at >= from)`. Yalnızca `started_at BETWEEN` yazmak
    10:00'dan önce başlayıp süren olayları kaçırır.
11. **Geri oynatmada kutuyu doğru kareye nasıl denk getiriyorsun?**
    Duvar saati = parça başlangıcı + `currentTime`. Tespit kareleri zaman damgalı ve sıralı yüklenir; ikili aramayla en yakını
    bulunur, ±0,5 sn dışındaysa çizilmez. Kutular normalize olduğu için video boyutundan bağımsız.
12. **Kutular kayıtla neden tam örtüşmeyebilir?**
    Analiz 2 fps; kareler arasında nesne hareket eder. Ayrıca analiz servisinin kare zamanı ile kayıt parçasının zamanı farklı
    ffmpeg süreçlerinden gelir (birkaç yüz ms kayma). Kesin eşleme için kayıt ve analizin aynı PTS'i paylaşması gerekir.
13. **Olay listesinde 50 küçük resim 50 ffmpeg açar mı?**
    Hayır: en fazla 2 eşzamanlı (semafor), tarayıcı `loading=lazy` ile yalnız görünenleri ister, yanıt 1 gün önbellekte.
14. **RTSP parolasını nasıl koruyorsun?**
    Yanıtta, günlükte ve ffmpeg hata metninde maskeleniyor; düzenlemede boş ya da maskeli adres gelirse kayıtlı parola korunuyor.
    Veritabanında ise düz metin; sonraki adım şifreleme (ör. ASP.NET Data Protection) ya da gizli bilgi kasası.
15. **Tüm kameraları silip servisi yeniden başlatırsam config'deki kameralar geri gelir mi?**
    Hayır. Tohumlama ömürde bir kez yapılır, işaret `app_markers` tablosunda; eşzamanlı açılışlara karşı advisory lock var.
16. **Sistemde kimlik doğrulama olsaydı nereye koyardın?**
    Ağ geçidine: OIDC ile giriş, JWT doğrulama, rol tabanlı yetki (izleyici yalnız okur, operatör kamera düzenler). Arka uç
    servisleri iç ağda kalır ya da ağ geçidinden gelen kimlik başlığına güvenir (mTLS ile).

## 6. Kendin dene

```bash
./scripts/download-sample-videos.sh
docker compose up --build
# http://localhost:5000            → panel (Canlı)
# http://localhost:5000/events.html → olay arama; bir karta tıkla, kayıt o andan kutularla oynar

# Çalışırken kamera ekle (servisler yeniden başlamadan izlenir, kaydedilir, analiz edilir):
curl -i -X POST localhost:5000/api/cameras -H 'Content-Type: application/json' \
  -d '{"id":"cam5","name":"Deneme","rtspUrl":"rtsp://mediamtx:8554/cam1"}'
curl -s localhost:5000/api/recordings/status | grep cam5

# Eski sürümle güncellemeyi dene → 412
curl -i -X PUT localhost:5000/api/cameras/cam5 -H 'If-Match: "99"' -H 'Content-Type: application/json' \
  -d '{"name":"Yeni ad","enabled":true}'

# Olay araması ve sonraki sayfa
curl -s 'localhost:5000/api/analysis/events/search?category=Person&limit=5'
curl -s 'localhost:5000/api/analysis/events/search?category=Person&limit=5&cursor=<nextCursor>'

curl -i -X DELETE localhost:5000/api/cameras/cam5
```
