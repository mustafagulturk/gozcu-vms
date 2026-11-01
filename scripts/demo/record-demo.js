// Demo GIF kaydı: çalışan sistemin web sayfalarını Playwright ile gezer, ekran videosu kaydeder ve
// ffmpeg ile README için küçük bir GIF'e çevirir (iki geçiş: önce palet üret, sonra paletle kodla).
//
// Kullanım (sistem docker compose ile ayaktayken, en az 2-3 dakika kayıt birikmiş olmalı):
//   npm i -D playwright   # veya önceden kurulu Playwright
//   node scripts/demo/record-demo.js docs/images/demo.gif
//
// Ortam değişkenleri: STREAMING_URL, RECORDING_URL, ANALYSIS_URL, GATEWAY_URL (4. aşamanın birleşik paneli varsa
// onu da gezer), HLS_JS_PATH (cdnjs'e erişimi olmayan ortamlarda hls.js'i yerel dosyadan sunmak için).
const { chromium } = require('playwright');
const { execFileSync } = require('node:child_process');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

const output = path.resolve(process.argv[2] || 'docs/images/demo.gif');
const urls = {
  streaming: process.env.STREAMING_URL || 'http://localhost:5080',
  recording: process.env.RECORDING_URL || 'http://localhost:5081',
  analysis: process.env.ANALYSIS_URL || 'http://localhost:5082',
  gateway: process.env.GATEWAY_URL,
};
const size = { width: 1280, height: 720 };
const pause = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

(async () => {
  const videoDir = fs.mkdtempSync(path.join(os.tmpdir(), 'gozcu-demo-'));
  const browser = await chromium.launch();
  const context = await browser.newContext({ viewport: size, recordVideo: { dir: videoDir, size } });
  if (process.env.HLS_JS_PATH) {
    const body = fs.readFileSync(process.env.HLS_JS_PATH);
    await context.route(/hls(\.min)?\.js/, (route) => route.fulfill({ body, contentType: 'application/javascript' }));
  }
  const page = await context.newPage();

  // 1) Canlı izleme: dört kamera ızgarası.
  await page.goto(urls.streaming, { waitUntil: 'networkidle' });
  await pause(6000);

  // 2) Analiz: son analiz edilen karelerde insan ve araç kutuları.
  await page.goto(urls.analysis, { waitUntil: 'networkidle' });
  await pause(6000);

  // 3) Geri oynatma: kamera seç, zaman çizelgesinde geçmiş bir ana tıkla, 2x hızda oynat.
  await page.goto(urls.recording, { waitUntil: 'networkidle' });
  await pause(1500);
  await page.selectOption('#camera', 'cam2').catch(() => {});
  await page.click('#now').catch(() => {});
  await pause(1500);
  const timeline = await page.$('#timeline');
  if (timeline) {
    const box = await timeline.boundingBox();
    await page.mouse.click(box.x + box.width * 0.35, box.y + box.height / 2);
  }
  await pause(1000);
  await page.click('#speeds button:nth-child(2)').catch(() => {});
  await pause(5000);

  // 4) (Varsa) 4. aşamanın birleşik paneli: olay arama.
  if (urls.gateway) {
    await page.goto(urls.gateway, { waitUntil: 'networkidle' });
    await pause(5000);
  }

  const video = page.video();
  await context.close();
  await browser.close();
  const webm = await video.path();

  // ffmpeg: 8 fps, 960 px genişlik, ortak palet. Boyut README için ~5-8 MB civarında kalır.
  const palette = path.join(videoDir, 'palette.png');
  const filters = 'fps=8,scale=960:-1:flags=lanczos';
  execFileSync('ffmpeg', ['-y', '-loglevel', 'error', '-i', webm, '-vf', `${filters},palettegen=stats_mode=diff`, palette]);
  execFileSync('ffmpeg', ['-y', '-loglevel', 'error', '-i', webm, '-i', palette, '-lavfi',
    `${filters}[x];[x][1:v]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle`, output]);
  console.log(`GIF hazır: ${output} (${(fs.statSync(output).size / 1024 / 1024).toFixed(1)} MB)`);
})().catch((error) => {
  console.error(error);
  process.exit(1);
});
