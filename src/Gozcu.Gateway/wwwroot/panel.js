// Gözcü VMS web paneli: ortak gezinme çubuğu ve sayfaların paylaştığı küçük yardımcılar (window.Gozcu).
// Tüm istekler aynı kökene (ağ geçidi) gider; mutlak servis adresi kullanılmaz.
"use strict";
(function () {
  const pages = [
    { key: "live", href: "/", label: "Canlı" },
    { key: "playback", href: "/playback.html", label: "Kayıtlar" },
    { key: "events", href: "/events.html", label: "Olaylar" },
    { key: "cameras", href: "/cameras.html", label: "Kameralar" },
    { key: "analysis", href: "/analysis.html", label: "Analiz" },
  ];

  // <header class="topbar" data-page="events"> içine marka ve gezinme bağlantıları eklenir; içindeki diğer öğeler korunur.
  function renderTopbar() {
    const header = document.querySelector("header.topbar");
    if (!header) return;
    const brand = document.createElement("a");
    brand.className = "brand";
    brand.href = "/";
    brand.textContent = "Gözcü VMS";
    const nav = document.createElement("nav");
    nav.setAttribute("aria-label", "Panel sayfaları");
    for (const page of pages) {
      const link = document.createElement("a");
      link.href = page.href;
      link.textContent = page.label;
      if (page.key === header.dataset.page) link.setAttribute("aria-current", "page");
      nav.appendChild(link);
    }
    header.prepend(brand, nav);
  }

  // API hatası: ProblemDetails gövdesi (title, detail, errors) varsa korunur.
  class ApiError extends Error {
    constructor(status, message, problem) {
      super(message);
      this.status = status;
      this.problem = problem;
    }
  }

  // JSON API çağrısı. body nesneyse JSON olarak gönderilir. 204'te null döner.
  async function api(path, { method = "GET", body, headers = {}, signal } = {}) {
    const init = { method, headers: { Accept: "application/json", ...headers }, signal };
    if (body !== undefined) {
      init.body = JSON.stringify(body);
      init.headers["Content-Type"] = "application/json";
    }
    const response = await fetch(path, init);
    if (!response.ok) {
      let problem = null;
      let message = `${response.status} ${response.statusText}`.trim();
      try {
        problem = await response.json();
        message = problem.detail || problem.title || message;
      } catch { /* gövde yok ya da JSON değil */ }
      throw new ApiError(response.status, message, problem);
    }
    if (response.status === 204) return null;
    const text = await response.text();
    return text ? JSON.parse(text) : null;
  }

  const pad = (n) => String(n).padStart(2, "0");
  // <input type="datetime-local"> değeri yerel saattir.
  function toLocalInput(ms) {
    const d = new Date(ms);
    return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`;
  }
  function fromLocalInput(value) {
    if (!value) return null;
    const ms = new Date(value).getTime();
    return Number.isFinite(ms) ? ms : null;
  }
  // Sorgu dizgisinde "+" sorun çıkarmasın diye her zaman "Z" biçimi.
  const iso = (ms) => new Date(Math.round(ms)).toISOString().replace(/\.\d{3}Z$/, "Z");

  function formatDuration(seconds) {
    const s = Math.max(0, Math.round(seconds)), h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60), r = s % 60;
    if (h > 0) return `${h} sa ${m} dk`;
    if (m > 0) return `${m} dk${r ? ` ${r} sn` : ""}`;
    return `${r} sn`;
  }

  // Tarayıcı depolaması yalnızca kişisel tercih içindir; gizli pencerede atabilir, sayfa onsuz da çalışır.
  const prefs = {
    get(key, fallback) {
      try { const v = localStorage.getItem(`gozcu.${key}`); return v === null ? fallback : JSON.parse(v); } catch { return fallback; }
    },
    set(key, value) {
      try { localStorage.setItem(`gozcu.${key}`, JSON.stringify(value)); } catch { /* yoksay */ }
    },
  };

  window.Gozcu = {
    $: (id) => document.getElementById(id),
    api,
    ApiError,
    pad,
    iso,
    toLocalInput,
    fromLocalInput,
    formatDuration,
    prefs,
    pct: (v) => `%${Math.round(v * 100)}`,
    timeFormat: new Intl.DateTimeFormat("tr-TR", { hour: "2-digit", minute: "2-digit", second: "2-digit" }),
    dateTimeFormat: new Intl.DateTimeFormat("tr-TR", { day: "2-digit", month: "2-digit", year: "numeric", hour: "2-digit", minute: "2-digit", second: "2-digit" }),
    categoryLabels: { Person: "İnsan", Vehicle: "Araç" },
    categoryColors: { Person: "#3fb950", Vehicle: "#f0883e" },
    labelNames: { person: "insan", bicycle: "bisiklet", car: "otomobil", motorcycle: "motosiklet", bus: "otobüs", truck: "kamyon" },
    healthLabels: { Online: "Çevrimiçi", Degraded: "Sorunlu", Offline: "Çevrimdışı", Connecting: "Bağlanıyor" },
    playbackLink: (cameraId, ms) => `/playback.html?camera=${encodeURIComponent(cameraId)}` + (ms != null ? `&time=${iso(ms)}` : ""),
  };

  renderTopbar();
})();
