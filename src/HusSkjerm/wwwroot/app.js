"use strict";
const $ = (id) => document.getElementById(id);
const NS = "http://www.w3.org/2000/svg";
const GRID = 10, W = 800, H = 440, MIN = 40;

let cfg = null, devices = [], readings = {}, images = [], floorId = null, roomId = null, dirty = false;

const uid = () => Math.random().toString(16).slice(2, 10) + Date.now().toString(16).slice(-4);
const snap = (v) => Math.round(v / GRID) * GRID;
const clamp = (v, lo, hi) => Math.max(lo, Math.min(hi, v));

function toast(msg, err = false) {
  const t = $("toast");
  t.textContent = msg; t.className = "toast" + (err ? " err" : "");
  clearTimeout(toast.h); toast.h = setTimeout(() => t.classList.add("hidden"), 4000);
}

async function api(url, opts = {}) {
  const res = await fetch(url, opts);
  if (!res.ok) {
    let msg = res.statusText;
    try { msg = (await res.json()).error || msg; } catch { /* ingen JSON */ }
    throw new Error(msg);
  }
  return res.status === 204 || res.status === 202 ? null : (res.headers.get("content-type")?.includes("json") ? res.json() : null);
}
const post = (url, body) => api(url, { method: "POST", headers: { "Content-Type": "application/json" }, body: body ? JSON.stringify(body) : undefined });
const guard = (fn) => async (...a) => { try { await fn(...a); } catch (e) { toast(e.message, true); } };

function markDirty(v = true) {
  dirty = v;
  $("dirty").classList.toggle("hidden", !v);
}

const floor = () => cfg.floors.find((f) => f.id === floorId);
const room = () => floor()?.rooms.find((r) => r.id === roomId);
const deviceName = (did) => devices.find((d) => d.did === did)?.name || did;

/* ---------- Faner ---------- */
document.querySelectorAll(".tabs button").forEach((b) => b.addEventListener("click", () => {
  document.querySelectorAll(".tabs button").forEach((x) => x.classList.toggle("active", x === b));
  document.querySelectorAll(".tab").forEach((t) => t.classList.toggle("hidden", t.id !== "tab-" + b.dataset.tab));
  if (b.dataset.tab === "preview") reloadCurrent();
  if (b.dataset.tab === "settings") loadStatus();
  if (b.dataset.tab === "rotation") renderSlides();
}));

/* ---------- Plantegning ---------- */
function renderFloors() {
  const sel = $("floorSelect");
  sel.innerHTML = "";
  cfg.floors.forEach((f) => sel.add(new Option(f.name, f.id)));
  if (!cfg.floors.some((f) => f.id === floorId)) floorId = cfg.floors[0]?.id ?? null;
  sel.value = floorId ?? "";
  renderPlan();
}

function renderPlan(withPanel = true) {
  const svg = $("svg");
  svg.innerHTML = "";
  for (let x = 0; x <= W; x += 50) svg.append(el("line", { x1: x, y1: 0, x2: x, y2: H, stroke: "#eee" }));
  for (let y = 0; y <= H; y += 50) svg.append(el("line", { x1: 0, y1: y, x2: W, y2: y, stroke: "#eee" }));
  const f = floor();
  if (!f) { if (withPanel) renderRoomPanel(); return; }
  for (const r of f.rooms) {
    const g = el("g", {});
    const rect = el("rect", { class: "room" + (r.id === roomId ? " sel" : ""), x: r.x, y: r.y, width: r.w, height: r.h });
    rect.addEventListener("pointerdown", (e) => startDrag(e, r, "move"));
    g.append(rect);
    const label = el("text", { x: r.x + 8, y: r.y + 20, "font-weight": "bold" });
    label.textContent = r.name || "(uten navn)";
    g.append(label);
    const t = roomTemp(r);
    const temp = el("text", { x: r.x + r.w / 2, y: r.y + r.h / 2 + 8, "text-anchor": "middle", "font-size": 24 });
    temp.textContent = t;
    g.append(temp);
    if (r.id === roomId) {
      for (const c of [["nw", r.x, r.y], ["ne", r.x + r.w, r.y], ["sw", r.x, r.y + r.h], ["se", r.x + r.w, r.y + r.h]]) {
        const h = el("circle", { class: "handle", cx: c[1], cy: c[2], r: 9, style: "cursor:" + c[0] + "-resize" });
        h.addEventListener("pointerdown", (e) => startDrag(e, r, c[0]));
        g.append(h);
      }
    }
    svg.append(g);
  }
  if (withPanel) renderRoomPanel();
}

function el(name, attrs) {
  const e = document.createElementNS(NS, name);
  for (const k in attrs) e.setAttribute(k, attrs[k]);
  return e;
}

function roomTemp(r) {
  const vals = r.sensors.map((d) => readings[d]?.temperature).filter((v) => v != null);
  return vals.length ? (vals.reduce((a, b) => a + b, 0) / vals.length).toFixed(1).replace(".", ",") + "°" : "–";
}

function startDrag(e, r, mode) {
  e.preventDefault(); e.stopPropagation();
  const svg = $("svg");
  const select = roomId !== r.id;
  roomId = r.id;
  const p0 = pt(e), o = { x: r.x, y: r.y, w: r.w, h: r.h };
  svg.setPointerCapture(e.pointerId);
  const move = (ev) => {
    const p = pt(ev), dx = p.x - p0.x, dy = p.y - p0.y;
    let { x, y, w, h } = o;
    if (mode === "move") { x = o.x + dx; y = o.y + dy; }
    else {
      if (mode.includes("w")) { x = o.x + dx; w = o.w - dx; }
      if (mode.includes("e")) w = o.w + dx;
      if (mode.includes("n")) { y = o.y + dy; h = o.h - dy; }
      if (mode.includes("s")) h = o.h + dy;
      if (w < MIN) { if (mode.includes("w")) x -= MIN - w; w = MIN; }
      if (h < MIN) { if (mode.includes("n")) y -= MIN - h; h = MIN; }
    }
    x = snap(x); y = snap(y); w = Math.max(MIN, snap(w)); h = Math.max(MIN, snap(h));
    r.w = Math.min(w, W); r.h = Math.min(h, H);
    r.x = clamp(x, 0, W - r.w); r.y = clamp(y, 0, H - r.h);
    markDirty(); renderPlan(false);
  };
  const up = (ev) => {
    svg.removeEventListener("pointermove", move);
    svg.removeEventListener("pointerup", up);
    svg.removeEventListener("pointercancel", up);
    try { svg.releasePointerCapture(ev.pointerId); } catch { /* allerede frigitt */ }
  };
  svg.addEventListener("pointermove", move);
  svg.addEventListener("pointerup", up);
  svg.addEventListener("pointercancel", up);
  if (select) renderPlan();
}

function pt(e) {
  const svg = $("svg"), p = svg.createSVGPoint();
  p.x = e.clientX; p.y = e.clientY;
  return p.matrixTransform(svg.getScreenCTM().inverse());
}

$("svg").addEventListener("pointerdown", (e) => {
  if (e.target === $("svg") || e.target.tagName === "line") { roomId = null; renderPlan(); }
});

function renderRoomPanel() {
  const p = $("roomPanel"), r = room();
  if (!r) { p.innerHTML = '<p class="muted">Velg et rom for å redigere det. Dra for å flytte, dra i hjørnene for å endre størrelse.</p>'; return; }
  p.innerHTML = "<h2>Rom</h2>";
  const name = document.createElement("label");
  name.textContent = "Navn";
  const input = document.createElement("input");
  input.value = r.name;
  input.addEventListener("input", () => { r.name = input.value;   markDirty(); renderPlan(false); });
  name.append(input);
  p.append(name);
  const h = document.createElement("h2"); h.textContent = "Sensorer"; p.append(h);
  if (!devices.length) {
    const m = document.createElement("p"); m.className = "muted"; m.textContent = "Ingen enheter. Synkroniser under Aqara-fanen."; p.append(m);
  }
  for (const d of devices) {
    const l = document.createElement("label"); l.className = "check";
    const cb = document.createElement("input"); cb.type = "checkbox"; cb.checked = r.sensors.includes(d.did);
    cb.addEventListener("change", () => {
      r.sensors = cb.checked ? [...r.sensors, d.did] : r.sensors.filter((x) => x !== d.did);
      markDirty(); renderPlan(false);
    });
    const rd = readings[d.did];
    l.append(cb, `${d.name}${rd?.temperature != null ? " (" + rd.temperature.toFixed(1) + "°)" : ""}`);
    p.append(l);
  }
  const del = document.createElement("button"); del.className = "danger"; del.textContent = "Slett rom";
  del.addEventListener("click", () => { floor().rooms = floor().rooms.filter((x) => x !== r); roomId = null; markDirty(); renderPlan(); });
  p.append(del);
}

$("floorSelect").addEventListener("change", (e) => { floorId = e.target.value; roomId = null; renderPlan(); });
$("addRoom").addEventListener("click", () => {
  const f = floor(); if (!f) return;
  const r = { id: uid(), name: "Nytt rom", x: 20, y: 20, w: 160, h: 120, sensors: [] };
  f.rooms.push(r); roomId = r.id; markDirty(); renderPlan();
});
$("addFloor").addEventListener("click", () => {
  const name = prompt("Navn på etasjen:"); if (!name) return;
  const f = { id: uid(), name, rooms: [] };
  cfg.floors.push(f); cfg.slides.push({ id: uid(), type: "Floor", floorId: f.id, enabled: true });
  floorId = f.id; roomId = null; markDirty(); renderFloors();
});
$("renameFloor").addEventListener("click", () => {
  const f = floor(); if (!f) return;
  const name = prompt("Nytt navn:", f.name); if (!name) return;
  f.name = name; markDirty(); renderFloors();
});
$("deleteFloor").addEventListener("click", () => {
  const f = floor(); if (!f || !confirm(`Slette «${f.name}» med alle rom?`)) return;
  cfg.floors = cfg.floors.filter((x) => x !== f);
  cfg.slides = cfg.slides.filter((s) => s.floorId !== f.id);
  roomId = null; markDirty(); renderFloors();
});

/* ---------- Rotasjon ---------- */
function slideTitle(s) {
  if (s.type === "Floor") return "Plantegning: " + (cfg.floors.find((f) => f.id === s.floorId)?.name ?? "?");
  if (s.type === "Weather") return "Temperatur og vær";
  return "Bilde";
}

function renderSlides() {
  const ul = $("slideList");
  ul.innerHTML = "";
  cfg.slides.forEach((s, i) => {
    const li = document.createElement("li");
    const img = document.createElement("img");
    img.loading = "lazy"; img.alt = "";
    img.src = s.type === "Image" ? "/api/images/" + encodeURIComponent(s.image) : "/api/preview/" + s.id + ".png?t=" + (dirty ? "" : Date.now());
    if (dirty && s.type !== "Image") img.removeAttribute("src");
    const name = document.createElement("span"); name.className = "name"; name.textContent = slideTitle(s);
    const on = document.createElement("label"); on.className = "check";
    const cb = document.createElement("input"); cb.type = "checkbox"; cb.checked = s.enabled;
    cb.addEventListener("change", () => { s.enabled = cb.checked; markDirty(); });
    on.append(cb, "Aktiv");
    const sec = document.createElement("input"); sec.type = "number"; sec.min = 1; sec.placeholder = "std";
    sec.title = "Sekunder (tom = standard)"; sec.value = s.seconds ?? "";
    sec.addEventListener("input", () => { s.seconds = sec.value ? Number(sec.value) : null; markDirty(); });
    const mk = (txt, fn, cls) => { const b = document.createElement("button"); b.textContent = txt; if (cls) b.className = cls; b.addEventListener("click", fn); return b; };
    const swap = (j) => { if (j < 0 || j >= cfg.slides.length) return; [cfg.slides[i], cfg.slides[j]] = [cfg.slides[j], cfg.slides[i]]; markDirty(); renderSlides(); };
    li.append(img, name, on, sec,
      mk("↑", () => swap(i - 1)), mk("↓", () => swap(i + 1)),
      mk("Vis nå", guard(async () => {
        if (dirty) throw new Error("Lagre først.");
        await post("/api/show/" + s.id); toast("Sendt til skjermen (tar ca. 20 s).");
      })),
      mk("Fjern", () => { cfg.slides.splice(i, 1); markDirty(); renderSlides(); }, "danger"));
    ul.append(li);
  });
}

$("rotationSeconds").addEventListener("input", (e) => { cfg.settings.rotationSeconds = Number(e.target.value) || 10; markDirty(); });
$("paused").addEventListener("change", (e) => { cfg.settings.paused = e.target.checked; markDirty(); });
$("addWeather").addEventListener("click", () => { cfg.slides.push({ id: uid(), type: "Weather", enabled: true }); markDirty(); renderSlides(); });

/* ---------- Bilder ---------- */
async function loadImages() {
  images = await api("/api/images");
  const grid = $("imageGrid");
  grid.innerHTML = "";
  for (const name of images) {
    const f = document.createElement("figure");
    const img = document.createElement("img"); img.src = "/api/images/" + encodeURIComponent(name); img.alt = "";
    const inRot = cfg.slides.some((s) => s.type === "Image" && s.image === name);
    const add = document.createElement("button");
    add.textContent = inRot ? "I rotasjon ✓" : "Legg i rotasjon"; add.disabled = inRot;
    add.addEventListener("click", () => { cfg.slides.push({ id: uid(), type: "Image", image: name, enabled: true }); markDirty(); loadImages(); });
    const del = document.createElement("button"); del.textContent = "Slett"; del.className = "danger";
    del.addEventListener("click", guard(async () => {
      if (!confirm("Slette bildet?")) return;
      if (dirty) throw new Error("Lagre endringene først.");
      await api("/api/images/" + encodeURIComponent(name), { method: "DELETE" });
      cfg = await api("/api/config"); await loadImages();
    }));
    f.append(img, add, del);
    grid.append(f);
  }
}

$("upload").addEventListener("change", guard(async (e) => {
  const form = new FormData();
  for (const f of e.target.files) form.append("files", f);
  await api("/api/images", { method: "POST", body: form });
  e.target.value = "";
  toast("Lastet opp.");
  await loadImages();
}));

/* ---------- Aqara ---------- */
async function loadDevices() {
  [devices, readings] = await Promise.all([api("/api/aqara/devices"), api("/api/readings")]);
  const list = $("deviceList");
  list.innerHTML = devices.length ? "" : '<p class="muted">Ingen enheter ennå.</p>';
  for (const d of devices) {
    const r = readings[d.did];
    const row = document.createElement("div"); row.className = "dev";
    row.innerHTML = '<span class="n"></span><span></span>';
    row.children[0].textContent = `${d.name} (${d.model})`;
    row.children[1].textContent = r ? `${r.temperature?.toFixed(1) ?? "–"}° / ${r.humidity?.toFixed(0) ?? "–"}%` : "";
    list.append(row);
  }
  const sel = $("outdoorSensor"), cur = cfg.settings.outdoorSensorId ?? "";
  sel.innerHTML = ""; sel.add(new Option("(bruk yr.no)", ""));
  devices.forEach((d) => sel.add(new Option(d.name, d.did)));
  sel.value = cur;
}

$("aqaraCode").addEventListener("click", guard(async () => { await post("/api/aqara/code"); toast("Kode sendt. Sjekk e-post/SMS."); }));
$("aqaraToken").addEventListener("click", guard(async () => { await post("/api/aqara/token", { code: $("aqaraCodeInput").value.trim() }); toast("Innlogget mot Aqara."); await loadStatus(); }));
$("aqaraSync").addEventListener("click", guard(async () => { await post("/api/aqara/sync"); await loadDevices(); renderPlan(); toast("Enheter synkronisert."); }));
$("refreshSensors").addEventListener("click", guard(async () => { await post("/api/sensors/refresh"); await loadDevices(); toast("Sensorer oppdatert."); }));
$("refreshWeather").addEventListener("click", guard(async () => { await post("/api/weather/refresh"); toast("Vær oppdatert."); await loadStatus(); }));

async function loadStatus() {
  const s = await api("/api/status");
  $("statusBox").textContent = JSON.stringify(s, null, 2);
  $("aqaraStatus").textContent = !s.aqara.configured ? "Mangler AppId/KeyId/AppKey i appsettings.json."
    : s.aqara.hasToken ? "Innlogget." : "Ikke innlogget – følg trinnene under.";
}

/* ---------- Innstillinger ---------- */
const bind = (id, key, parse) => $(id).addEventListener("input", (e) => { cfg.settings[key] = parse(e.target.value); markDirty(); });
bind("location", "location", (v) => v);
bind("latitude", "latitude", (v) => (v === "" ? null : Number(v)));
bind("longitude", "longitude", (v) => (v === "" ? null : Number(v)));
bind("alertLow", "alertLow", Number);
bind("alertHigh", "alertHigh", Number);
$("outdoorSensor").addEventListener("change", (e) => { cfg.settings.outdoorSensorId = e.target.value || null; markDirty(); });

function fillSettings() {
  const s = cfg.settings;
  $("rotationSeconds").value = s.rotationSeconds; $("paused").checked = s.paused;
  $("location").value = s.location ?? ""; $("latitude").value = s.latitude ?? ""; $("longitude").value = s.longitude ?? "";
  $("alertLow").value = s.alertLow; $("alertHigh").value = s.alertHigh;
}

function reloadCurrent() { $("currentImg").src = "/api/current.png?t=" + Date.now(); }
$("reloadCurrent").addEventListener("click", reloadCurrent);

$("save").addEventListener("click", guard(async () => {
  cfg = await api("/api/config", { method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify(cfg) });
  markDirty(false); fillSettings(); renderFloors(); toast("Lagret.");
}));

window.addEventListener("beforeunload", (e) => { if (dirty) e.preventDefault(); });

(async () => {
  try {
    cfg = await api("/api/config");
    fillSettings();
    await loadDevices();
    renderFloors();
    await loadImages();
    await loadStatus();
    setInterval(guard(async () => { if (!document.hidden) { readings = await api("/api/readings"); renderPlan(false); } }), 60000);
  } catch (e) { toast(e.message, true); }
})();
