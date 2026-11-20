(async () => {
  const select = selector => document.querySelector(selector);
  const status = select("#handoff-status");
  const message = select("#handoff-message");
  const retry = select("#handoff-retry");
  const close = select("#handoff-close");
  const post = route => fetch("./" + route, { method: "POST" });
  let waitingTimer;
  let poll;

  function showWaiting() {
    retry.hidden = true;
    close.hidden = false;
    message.textContent = "正在等待软件窗口…";
    waitingTimer = setTimeout(() => { status.hidden = false; }, 700);
  }

  function showError(text) {
    clearTimeout(waitingTimer);
    status.hidden = false;
    message.textContent = text;
    retry.hidden = false;
    close.hidden = false;
  }

  async function reveal() {
    showWaiting();
    try {
      if (!(await post("handoff")).ok) throw new Error("Handoff request failed");
    } catch {
      showError("启动器连接已中断，请重新打开片头。");
      return;
    }
    poll = setInterval(async () => {
      try {
        const response = await fetch("./status");
        const current = await response.json();
        if (current.error) {
          clearInterval(poll);
          showError(current.error);
        }
      } catch {
        clearInterval(poll);
      }
    }, 600);
  }

  retry.addEventListener("click", async () => {
    retry.hidden = true;
    try {
      if (!(await post("retry")).ok) throw new Error("Retry request failed");
      await reveal();
    } catch {
      showError("暂时无法重试，请重新打开片头。");
    }
  });
  close.addEventListener("click", () => { void post("dismiss").catch(() => {}); });

  let settings = { title: "启程", greeting: "欢迎回来", app: "" };
  try {
    const response = await fetch("./settings");
    if (response.ok) settings = await response.json();
  } catch {
    // The bundled welcome remains available if the settings request fails.
  }
  select("#welcome-title").textContent = settings.title;
  select("#welcome-destination").textContent = settings.app;
  window.xinglanIntro.play({ complete: reveal });
  select("#welcome-name").textContent = settings.greeting;
})();
