(() => {
  const intro = document.querySelector("#intro");
  const canvas = document.querySelector("#cinema");
  const ctx = canvas.getContext("2d");
  const particleCanvas = document.querySelector("#portrait-particles");
  const particleCtx = particleCanvas.getContext("2d");
  const workspace = document.querySelector("#workspace");
  const skip = document.querySelector("#skip-intro");
  const number = document.querySelector("#scan-number");
  const steps = Array.from(document.querySelectorAll(".scan-steps li"));
  const reducedMotion = matchMedia("(prefers-reduced-motion: reduce)");
  const palette = [
    "#f7fbff", "#c5d8ed", "#a6bfdc", "#89a8ca",
    "#6c8fb6", "#567ba3", "#42668c", "#294b70"
  ];
  const TAU = Math.PI * 2;
  const durationMs = 13550;
  const particlePixelBudget = 2_500_000;
  const art = new Image();
  let portrait = [];
  let width = 0;
  let height = 0;
  let frame;
  let hideTimer;
  let running = false;
  let startTime;
  let audio;
  let onFinish;
  let completionTimer;
  let displayedProgress = -1;

  function randomGenerator(seed) {
    return () => {
      seed = (Math.imul(seed, 1664525) + 1013904223) >>> 0;
      return seed / 4294967296;
    };
  }

  function makePortrait() {
    const buffer = document.createElement("canvas");
    buffer.width = 384;
    buffer.height = 216;
    const sampler = buffer.getContext("2d", { willReadFrequently: true });
    sampler.drawImage(art, 0, 0, buffer.width, buffer.height);
    const { data } = sampler.getImageData(0, 0, buffer.width, buffer.height);
    const lightness = new Float32Array(buffer.width * buffer.height);
    for (let i = 0; i < lightness.length; i++) {
      const pixel = i * 4;
      lightness[i] = (data[pixel] * 0.27 + data[pixel + 1] * 0.55 + data[pixel + 2] * 0.18) / 255;
    }
    const random = randomGenerator(41817);
    const groups = palette.map(() => []);
    const edges = new Float32Array(lightness.length);
    const probabilities = new Float32Array(lightness.length);
    let expected = 0;
    for (let y = 0; y < buffer.height; y++) {
      const row = y * buffer.width;
      for (let x = 0; x < buffer.width; x++) {
        const shade = 1 - lightness[row + x];
        // Follow shadows and fine edges so a pale background leaves the portrait visible.
        const horizontal = lightness[row + Math.min(x + 1, buffer.width - 1)]
          - lightness[row + Math.max(x - 1, 0)];
        const vertical = lightness[Math.min(y + 1, buffer.height - 1) * buffer.width + x]
          - lightness[Math.max(y - 1, 0) * buffer.width + x];
        const edge = Math.abs(horizontal) + Math.abs(vertical);
        edges[row + x] = edge;
        const probability = Math.min(1, 0.008 + shade ** 1.78 * 0.9 + Math.min(0.26, edge * 1.65));
        probabilities[row + x] = probability;
        expected += probability;
      }
    }
    const density = Math.min(1, 16_500 / expected);
    for (let y = 0; y < buffer.height; y++) {
      for (let x = 0; x < buffer.width; x++) {
        const index = y * buffer.width + x;
        if (random() > probabilities[index] * density) continue;
        const shade = 1 - lightness[index];
        const tone = shade * 1.6 + edges[index] * 0.35;
        const band = Math.min(palette.length - 1, Math.floor(tone * palette.length));
        const u = (x + random()) / buffer.width;
        const v = (y + random()) / buffer.height;
        const angle = random() * TAU;
        const radius = 0.56 + random() * 0.49;
        const directionX = Math.cos(angle);
        groups[band].push(
          u, v, directionX * radius, Math.sin(angle) * radius, directionX,
          (random() - 0.5) * 1.3, 0.62 + random() * (1.2 + shade)
        );
      }
    }
    portrait = groups.map(group => Float32Array.from(group));
  }

  art.addEventListener("load", () => {
    try {
      makePortrait();
    } catch {
      // Browsers can restrict pixel reads when the HTML is opened as a file.
    }
  }, { once: true });
  art.src = "./assets/pearl.webp";

  function syncCanvas() {
    width = canvas.clientWidth;
    height = canvas.clientHeight;
    if (!width || !height) return;
    const density = Math.min(devicePixelRatio || 1, 1.6);
    if (ctx) {
      canvas.width = Math.round(width * density);
      canvas.height = Math.round(height * density);
      ctx.setTransform(density, 0, 0, density, 0, 0);
    }
    if (particleCtx) {
      const particleDensity = Math.min(density, Math.sqrt(particlePixelBudget / (width * height)));
      particleCanvas.width = Math.round(width * particleDensity);
      particleCanvas.height = Math.round(height * particleDensity);
      particleCtx.setTransform(particleDensity, 0, 0, particleDensity, 0, 0);
    }
  }

  window.addEventListener("resize", () => { if (running) syncCanvas(); });
  const randomStar = randomGenerator(741);
  const stars = Array.from({ length: 154 }, () => ({
    x: randomStar(), y: randomStar(), phase: randomStar() * TAU, size: 0.36 + randomStar() ** 3 * 1.7
  }));

  function background(t, strength) {
    if (strength > 0) {
      const x = width / 2;
      const y = height * 0.48;
      const radius = Math.min(width, height);
      const gradient = ctx.createRadialGradient(x, y, 0, x, y, radius * 0.73);
      gradient.addColorStop(0, `rgba(150, 185, 220, ${0.35 * strength})`);
      gradient.addColorStop(0.46, `rgba(132, 168, 205, ${0.21 * strength})`);
      gradient.addColorStop(1, "#9db9d600");
      ctx.fillStyle = gradient;
      ctx.fillRect(0, 0, width, height);
    }
    ctx.fillStyle = "#789bbf";
    for (const star of stars) {
      ctx.globalAlpha = (0.25 + 0.21 * Math.sin(t * 1.15 + star.phase)) * (0.4 + 0.6 * strength);
      ctx.fillRect(star.x * width, star.y * height, star.size, star.size);
    }
    ctx.globalAlpha = 1;
  }

  const scannerRadius = () => Math.min(width * 0.27, height * 0.27, 265);

  function ring(t, progress, opacity) {
    const x = width / 2;
    const y = height * 0.48;
    const radius = scannerRadius();
    ctx.save();
    ctx.translate(x, y);
    ctx.globalAlpha = opacity;
    ctx.strokeStyle = "#628cb580";
    ctx.lineWidth = 0.7;
    for (const ratio of [0.635, 0.68, 0.77, 0.88, 0.98, 1.065]) {
      ctx.beginPath();
      ctx.arc(0, 0, radius * ratio, 0, TAU);
      ctx.stroke();
    }
    for (let i = 0; i < 108; i++) {
      const a = i / 108 * TAU + t * 0.1;
      const long = i % 9 === 0;
      const inner = radius * (long ? 1.012 : 1.026);
      const outer = radius * (long ? 1.13 : 1.047);
      ctx.strokeStyle = long ? "#52799fc4" : "#6186ab7c";
      ctx.beginPath();
      ctx.moveTo(Math.cos(a) * inner, Math.sin(a) * inner);
      ctx.lineTo(Math.cos(a) * outer, Math.sin(a) * outer);
      ctx.stroke();
    }
    const rails = [
      { radius: 0.93, count: 12, rate: 0.13, thickness: 4.8, color: "#95b6d6be" },
      { radius: 0.82, count: 7, rate: -0.26, thickness: 2, color: "#5f89b5e0" },
      { radius: 1.10, count: 4, rate: -0.16, thickness: 1.4, color: "#7597bcae" }
    ];
    for (const rail of rails) {
      ctx.strokeStyle = rail.color;
      ctx.lineWidth = rail.thickness;
      for (let i = 0; i < rail.count; i++) {
        const a = i / rail.count * TAU + t * rail.rate;
        ctx.beginPath();
        ctx.arc(0, 0, radius * rail.radius, a, a + TAU / rail.count * (i % 3 === 0 ? 0.38 : 0.63));
        ctx.stroke();
      }
    }
    ctx.lineWidth = 2.35;
    ctx.strokeStyle = "#497eaf";
    ctx.shadowColor = "#80b5e6";
    ctx.shadowBlur = 15;
    ctx.lineCap = "round";
    ctx.beginPath();
    ctx.arc(0, 0, radius, -Math.PI / 2, -Math.PI / 2 + progress * TAU);
    ctx.stroke();
    ctx.restore();

    const sweepY = y - radius + progress * radius * 2;
    const sweep = ctx.createLinearGradient(x - radius * 1.5, 0, x + radius * 1.5, 0);
    sweep.addColorStop(0, "#7ba5cd00");
    sweep.addColorStop(0.5, "#6a96c3c0");
    sweep.addColorStop(1, "#7ba5cd00");
    ctx.globalAlpha = opacity;
    ctx.fillStyle = sweep;
    ctx.fillRect(x - radius * 1.5, sweepY, radius * 3, 1);
    ctx.globalAlpha = 1;
  }

  const clamp = n => Math.max(0, Math.min(1, n));
  const smooth = n => {
    const k = clamp(n);
    return k * k * (3 - 2 * k);
  };

  function drawPortrait(t, opacity) {
    if (!particleCtx || !portrait.length) return;
    particleCanvas.style.opacity = opacity;
    if (opacity === 0) return;

    const p = clamp(t / 4.15);
    const easing = smooth(p);
    const remaining = 1 - easing;
    const ratio = Math.max(width / art.naturalWidth, height / art.naturalHeight);
    const dw = art.naturalWidth * ratio;
    const dh = art.naturalHeight * ratio;
    const anchorX = width * 0.5 * remaining + (width - dw) * 0.68 * easing;
    const anchorY = height * 0.48 * remaining + (height - dh) * 0.46 * easing;
    const radius = scannerRadius();
    const rotation = t * remaining * 0.79;
    const orbitCos = Math.cos(rotation) * radius * remaining;
    const orbitSin = Math.sin(rotation) * radius * remaining;
    const swirl = Math.sin(p * Math.PI) * remaining ** 0.6 * radius;
    const drift = smooth((t - 2.65) / 1.2) * 5.5;
    const driftCos = Math.cos(t * 1.9) * drift;
    const driftSin = Math.sin(t * 1.9) * drift;
    const portraitWidth = dw * easing;
    const portraitHeight = dh * easing;
    particleCtx.clearRect(0, 0, width, height);

    // Keep the points drifting as the portrait fades into the welcome image.
    portrait.forEach((group, band) => {
      particleCtx.fillStyle = palette[band];
      particleCtx.globalAlpha = 0.32 + band * 0.096;
      particleCtx.beginPath();
      for (let i = 0; i < group.length; i += 7) {
        const orbitX = group[i + 2];
        const orbitY = group[i + 3];
        const x = anchorX + group[i] * portraitWidth
          + orbitX * (orbitCos + driftCos) - orbitY * (orbitSin + driftSin) + group[i + 4] * swirl * 0.52;
        const y = anchorY + group[i + 1] * portraitHeight
          + orbitX * (orbitSin - driftSin) + orbitY * (orbitCos + driftCos) + group[i + 5] * swirl;
        const size = group[i + 6];
        particleCtx.rect(x, y, size, size);
      }
      particleCtx.fill();
    });
  }

  function updateScan(progress) {
    const current = Math.floor(progress * 100);
    if (current === displayedProgress) return;
    displayedProgress = current;
    number.firstChild.textContent = String(current).padStart(2, "0");
    intro.style.setProperty("--scan", `${current}%`);
    steps.forEach((step, i) => step.toggleAttribute("data-active", progress > i / 3));
  }

  function animate(now) {
    const t = (now - startTime) / 1000;
    let stage = "splash";
    if (t >= 1.35) stage = "scan";
    if (t >= 6.55) stage = "particles";
    if (t >= 10.7) stage = "welcome";
    const stageChanged = intro.dataset.stage !== stage;
    if (stageChanged) intro.dataset.stage = stage;
    if (now - startTime >= durationMs) {
      finish();
      return;
    }
    if (ctx && (stage !== "welcome" || stageChanged)) ctx.clearRect(0, 0, width, height);
    if (stage === "splash" && ctx) background(t, 0.28);
    if (stage === "scan") {
      const progress = clamp((t - 1.48) / 4.86);
      updateScan(progress);
      if (ctx) {
        background(t, 0.64 + progress * 0.22);
        ring(t, progress, smooth((t - 1.35) / 0.75));
      }
    }
    if (stage === "particles") {
      const pt = t - 6.55;
      if (ctx) {
        background(t, 0.72 * (1 - smooth(pt / 1.2)));
        if (pt < 0.9) ring(t, 1, (1 - smooth(pt / 0.9)) * 0.7);
      }
      drawPortrait(pt, smooth(pt / 0.9) * 0.97);
    }
    if (stage === "welcome") drawPortrait(t - 6.55, 0.97 * (1 - smooth((t - 10.7) / 1.45)));
    frame = requestAnimationFrame(animate);
  }

  function startSound() {
    if (!window.AudioContext) return;
    try {
      const context = new AudioContext();
      audio = context;
      const now = context.currentTime;
      const master = context.createGain();
      master.gain.value = 0.16;
      master.connect(context.destination);
      const notes = [
        [55, 0, 11.5, 0.30], [82.4, 1.3, 8.7, 0.22], [130.8, 2.1, 6.2, 0.11],
        [261.6, 4.1, 1.2, 0.18], [329.6, 6.5, 2.2, 0.22], [392, 7.05, 2.3, 0.18],
        [523.3, 10.7, 2.45, 0.19], [659.3, 10.9, 2.2, 0.15], [784, 11.2, 2.05, 0.12]
      ];
      for (const [pitch, offset, length, volume] of notes) {
        const oscillator = context.createOscillator();
        const envelope = context.createGain();
        oscillator.type = "sine";
        oscillator.frequency.value = pitch;
        envelope.gain.setValueAtTime(0.0001, now + offset);
        envelope.gain.exponentialRampToValueAtTime(volume, now + offset + Math.min(length * 0.2, 0.45));
        envelope.gain.exponentialRampToValueAtTime(0.0001, now + offset + length);
        oscillator.connect(envelope).connect(master);
        oscillator.start(now + offset);
        oscillator.stop(now + offset + length);
      }
      context.resume().catch(() => {});
    } catch {
      // Audio support is optional.
    }
  }

  function finish({ immediate = false, focus = false } = {}) {
    running = false;
    cancelAnimationFrame(frame);
    clearTimeout(hideTimer);
    clearTimeout(completionTimer);
    if (audio) {
      audio.close().catch(() => {});
      audio = undefined;
    }
    if (onFinish) {
      const complete = onFinish;
      onFinish = undefined;
      skip.disabled = true;
      intro.setAttribute("data-handoff", "");
      complete();
      return;
    }
    intro.dataset.stage = "done";
    intro.inert = true;
    workspace.inert = false;
    if (immediate) intro.hidden = true;
    else hideTimer = setTimeout(() => { intro.hidden = true; }, 490);
    if (focus) document.querySelector("#prompt").focus({ preventScroll: true });
  }

  function play({ name = "", sound = false, startup = false, complete } = {}) {
    onFinish = complete;
    if (startup && reducedMotion.matches) {
      finish({ immediate: true });
      return;
    }
    clearTimeout(hideTimer);
    displayedProgress = -1;
    updateScan(0);
    intro.hidden = false;
    intro.inert = false;
    workspace.inert = true;
    document.querySelector("#welcome-name").textContent = name ? `欢迎回来，${name}` : "欢迎回来";
    particleCanvas.style.opacity = 0;
    syncCanvas();
    startTime = performance.now();
    running = true;
    intro.dataset.stage = "splash";
    if (!startup) skip.focus({ preventScroll: true });
    if (sound) startSound();
    if (complete) completionTimer = setTimeout(finish, durationMs);
    frame = requestAnimationFrame(animate);
  }

  skip.addEventListener("click", () => finish({ focus: true }));
  document.addEventListener("keydown", event => {
    if (running && event.key === "Escape") finish({ focus: true });
  });
  window.xinglanIntro = { play, finish };
})();
