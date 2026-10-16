import { test, expect } from "@playwright/test";
import { launchCaptureBrowser } from "./launch-browser.mjs";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

async function launch() {
  const profile = fs.mkdtempSync(path.join(os.tmpdir(), "winshot-reliability-"));
  const browser = await launchCaptureBrowser(profile);
  try {
    const id = await browser.installExtension(process.env.WINSHOT_EXTENSION_PATH || fileURLToPath(new URL("../../dist/winshot-capture", import.meta.url)));
    const extension = (await browser.extensions()).get(id);
    const page = await browser.newPage();
    const errors = [];
    browser.on("targetcreated", async (target) => {
      const tab = target.type() === "page" ? await target.page() : null;
      tab?.on("pageerror", (error) => errors.push(error.message));
    });
    await page.goto("http://127.0.0.1:4173/body", { waitUntil: "networkidle0" });
    return { browser, extension, page, errors, async close() {
      await browser.close();
      fs.rmSync(profile, { recursive: true, force: true });
    } };
  } catch (error) {
    await browser.close();
    fs.rmSync(profile, { recursive: true, force: true });
    throw error;
  }
}

async function popup(run) {
  await run.page.bringToFront();
  const target = run.browser.waitForTarget((t) => t.type() === "page" && t.url().endsWith("/src/popup.html"));
  await run.extension.triggerAction(run.page);
  const page = await (await target).asPage();
  await page.waitForSelector("body[data-ready='true']");
  return page;
}

async function capture(run, mode = "full-page", pick = null) {
  const target = run.browser.waitForTarget((t) => t.type() === "page" && t.url().includes("/src/editor.html?id="), { timeout: 60_000 });
  const ui = await popup(run);
  await ui.click(pick ? "[data-picker='element']" : `[data-mode='${mode}']`);
  if (pick) {
    await run.page.waitForSelector("[data-winshot-picker='tip']");
    await run.page.mouse.click(pick.x, pick.y);
  }
  const editor = await (await target).asPage();
  await editor.waitForSelector("#state:not(:empty)");
  const record = await editor.evaluate(async () => {
    const store = await import(chrome.runtime.getURL("src/store.js"));
    return store.getRecord(new URL(location.href).searchParams.get("id"));
  });
  await ui.close().catch(() => {});
  return { editor, record };
}

async function magentaTiles(editor) {
  return editor.evaluate(async () => {
    const { getTiles } = await import(chrome.runtime.getURL("src/store.js"));
    const tiles = await getTiles(new URL(location.href).searchParams.get("id"));
    const hits = [];
    for (const tile of tiles) {
      const bitmap = await createImageBitmap(tile.blob);
      const canvas = new OffscreenCanvas(bitmap.width, bitmap.height);
      const context = canvas.getContext("2d");
      context.drawImage(bitmap, 0, 0);
      bitmap.close();
      const { data } = context.getImageData(0, 0, canvas.width, canvas.height);
      let count = 0;
      for (let i = 0; i < data.length; i += 16) {
        if (data[i] > 230 && data[i + 1] < 30 && data[i + 2] > 170) count++;
      }
      if (count > 100) hits.push(tile.index);
    }
    return hits;
  });
}

test("smooth-scroll pages restore their exact starting position and CSS", async () => {
  const run = await launch();
  try {
    await run.page.evaluate(() => {
      document.documentElement.style.scrollBehavior = "smooth";
      scrollTo({ top: 360, behavior: "instant" });
    });
    const { record } = await capture(run);
    expect(record.state, record.error).toBe("complete");
    expect(record.report.restoration).toBe("verified by read-back");
    expect(await run.page.evaluate(() => scrollY)).toBe(360);
    expect(await run.page.evaluate(() => document.documentElement.style.scrollBehavior)).toBe("smooth");
    expect(run.errors).toEqual([]);
  } finally { await run.close(); }
});

test("sticky headers inside web components are captured once and restored", async () => {
  const run = await launch();
  try {
    await run.page.evaluate(() => {
      document.body.replaceChildren(document.createElement("article-view"));
      const root = document.querySelector("article-view").attachShadow({ mode: "open" });
      root.innerHTML = '<style>:host{display:block}header{position:sticky;top:0;height:80px;background:#ff00cc;z-index:5}.content{height:3600px;background:repeating-linear-gradient(#fff 0 60px,#ddd 60px 120px)}</style><header></header><div class="content"></div>';
    });
    const { editor, record } = await capture(run);
    expect(record.state, record.error).toBe("complete");
    expect(await magentaTiles(editor)).toHaveLength(1);
    expect(await run.page.evaluate(() => {
      const header = document.querySelector("article-view").shadowRoot.querySelector("header");
      return { position: getComputedStyle(header).position, inline: header.style.position };
    })).toEqual({ position: "sticky", inline: "" });
    expect(run.errors).toEqual([]);
  } finally { await run.close(); }
});

test("element picker finds a scrolling ancestor across a shadow boundary", async () => {
  const run = await launch();
  try {
    await run.page.evaluate(() => {
      document.body.innerHTML = '<div id="panel" style="margin:30px;width:760px;height:600px;overflow:auto"><article-view></article-view></div>';
      document.querySelector("article-view").attachShadow({ mode: "open" }).innerHTML = '<div style="height:3200px;background:repeating-linear-gradient(#fff 0 60px,#ddd 60px 120px)">PANEL CONTENT</div>';
      document.querySelector("#panel").scrollTop = 240;
    });
    const { record } = await capture(run, "element", { x: 200, y: 200 });
    expect(record.state, record.error).toBe("complete");
    expect(record.report.target).toBe("self-scroller");
    expect(record.report.dimensionsCss.height).toBe(3200);
    expect(await run.page.$eval("#panel", (node) => node.scrollTop)).toBe(240);
    expect(run.errors).toEqual([]);
  } finally { await run.close(); }
});

test("scroll-triggered images finish decoding before their tile is captured", async () => {
  const run = await launch();
  try {
    await run.page.setRequestInterception(true);
    run.page.on("request", async (request) => {
      if (!request.url().includes("slow-image.svg")) return request.continue();
      await new Promise((resolve) => setTimeout(resolve, 950));
      await request.respond({ contentType: "image/svg+xml", body: '<svg xmlns="http://www.w3.org/2000/svg" width="600" height="240"><path fill="#ff00cc" d="M0 0h600v240H0z"/></svg>' });
    });
    await run.page.evaluate(() => {
      document.body.innerHTML = '<div style="height:1200px"></div><img id="slow" width="600" height="240"><div style="height:2000px"></div>';
      new IntersectionObserver((entries, observer) => {
        if (entries.some((entry) => entry.isIntersecting)) {
          document.querySelector("#slow").src = "/slow-image.svg";
          observer.disconnect();
        }
      }).observe(document.querySelector("#slow"));
    });
    const { editor, record } = await capture(run);
    expect(record.state, record.error).toBe("complete");
    expect(await magentaTiles(editor)).toHaveLength(1);
    expect(run.errors).toEqual([]);
  } finally { await run.close(); }
});

for (const pending of [false, true]) {
  test(`shadow trees created while scrolling ${pending ? "report unfinished images as partial" : "wait for visible image pixels"}`, async () => {
    const run = await launch();
    try {
      await run.page.setRequestInterception(true);
      run.page.on("request", async request => {
        if (!request.url().includes("late-shadow-image.svg")) return request.continue();
        if (pending) return;
        await new Promise(resolve => setTimeout(resolve, 950));
        await request.respond({ contentType: "image/svg+xml", body: '<svg xmlns="http://www.w3.org/2000/svg" width="600" height="240"><path fill="#ff00cc" d="M0 0h600v240H0z"/></svg>' });
      });
      await run.page.evaluate(() => {
        document.body.innerHTML = '<div style="height:1200px"></div><lazy-card style="display:block;height:240px"></lazy-card><div style="height:1800px"></div>';
        const host = document.querySelector("lazy-card");
        new IntersectionObserver((entries, observer) => {
          if (!entries.some(entry => entry.isIntersecting)) return;
          // Attaching a shadow root to an existing host emits no light-DOM mutation.
          host.attachShadow({ mode: "open" }).innerHTML = '<img src="/late-shadow-image.svg" width="600" height="240">';
          observer.disconnect();
        }).observe(host);
      });
      const { editor, record } = await capture(run);
      expect(record.state, JSON.stringify(record.report)).toBe(pending ? "partial" : "complete");
      if (!pending) expect(await magentaTiles(editor)).toHaveLength(1);
      expect(record.report.restoration).toBe("verified by read-back");
      expect(await run.page.evaluate(() => document.querySelector("lazy-card").shadowRoot.querySelectorAll("[data-winshot-capture]").length)).toBe(0);
      expect(run.errors).toEqual([]);
    } finally { await run.close(); }
  });
}

test("Stop cancels a delayed capture before it starts", async () => {
  const run = await launch();
  try {
    const ui = await popup(run);
    await ui.$eval("details", (node) => { node.open = true; });
    await ui.select("#capture-delay", "3000");
    await ui.click("[data-mode='full-page']");
    await expect.poll(() => ui.$eval("#status", (node) => node.textContent)).toContain("seconds");
    await ui.click("#cancel");
    await expect.poll(() => ui.$eval("#status", (node) => node.textContent)).not.toContain("No active capture");
    await new Promise((resolve) => setTimeout(resolve, 3500));
    expect(run.browser.targets().filter((target) => target.url().includes("/src/editor.html?id="))).toHaveLength(0);
    expect(await ui.evaluate(async () => {
      const { listRecords } = await import(chrome.runtime.getURL("src/store.js"));
      return (await listRecords()).length;
    })).toBe(0);
  } finally { await run.close(); }
});

test("a visible image that never loads produces an honest partial capture", async () => {
  const run = await launch();
  try {
    await run.page.setRequestInterception(true);
    run.page.on("request", (request) => {
      if (!request.url().includes("pending-image.png")) request.continue();
    });
    await run.page.evaluate(() => {
      document.body.innerHTML = '<img src="/pending-image.png" width="600" height="240"><div style="height:1600px"></div>';
    });
    const { record } = await capture(run);
    expect(record.state).toBe("partial");
    expect(record.report.coverage.complete).toBe(true);
    expect(record.report.warnings.join(" ")).toMatch(/loading images/);
    expect(record.report.restoration).toBe("verified by read-back");
    expect(run.errors).toEqual([]);
  } finally { await run.close(); }
});

test("popup uses the desktop icon, keeps Stop visible, and responds immediately", async () => {
  const run = await launch();
  try {
    const ui = await popup(run);
    const initial = await ui.evaluate(() => {
      const icon = document.querySelector(".mark");
      const stop = document.querySelector("#cancel").getBoundingClientRect();
      return { iconLoaded: icon.complete && icon.naturalWidth === 32, height: innerHeight, stopBottom: stop.bottom, mainHeight: document.querySelector("main").clientHeight };
    });
    expect(initial.iconLoaded).toBe(true);
    expect(initial.height).toBeLessThanOrEqual(600);
    expect(initial.stopBottom).toBeLessThanOrEqual(initial.height);
    expect(initial.mainHeight).toBeGreaterThan(300);
    await ui.$eval("details", (node) => { node.open = true; });
    await ui.select("#capture-delay", "3000");
    await ui.click("[data-mode='full-page']");
    expect(await ui.$eval("[data-mode='full-page']", (node) => node.disabled)).toBe(true);
    expect(await ui.$eval("body", (node) => node.dataset.busy)).toBe("true");
    await ui.click("#cancel");
    await expect.poll(() => ui.$eval("body", (node) => node.dataset.busy)).toBe("false");
    expect(await ui.$eval("[data-mode='full-page']", (node) => node.disabled)).toBe(false);
    await ui.evaluate(() => { document.querySelector("details").open = false; document.querySelector("main").scrollTop = 0; });
    await ui.screenshot({ path: path.join(os.tmpdir(), "winshot-popup-after.png") });
    expect(run.errors).toEqual([]);
  } finally { await run.close(); }
});

for (const scale of [0.5, 1.25]) {
  test(`a 2D scrolling panel at scale ${scale} captures every edge and restores both axes`, async () => {
    const run = await launch();
    try {
      const pick = await run.page.evaluate((scale) => {
        document.body.innerHTML = `<div style="margin:30px;transform:scale(${scale});transform-origin:0 0"><div id="panel" style="width:500px;height:300px;overflow:auto;border:4px solid #333"><div style="width:900px;height:1600px;background:repeating-linear-gradient(90deg,#fff 0 60px,#ddd 60px 120px)"><div style="position:relative;top:1480px;height:120px;background:#ff00cc">BOTTOM EDGE</div></div></div></div>`;
        const panel = document.querySelector("#panel");
        panel.scrollTo(80, 160);
        const box = panel.getBoundingClientRect();
        return { x: box.x + 40, y: box.y + 40 };
      }, scale);
      const { editor, record } = await capture(run, "element", pick);
      expect(record.state, JSON.stringify(record.report)).toBe("complete");
      expect(record.report.dimensionsPx).toEqual({ width: 900 * scale, height: 1600 * scale });
      expect(record.report.coverage.complete).toBe(true);
      expect(record.report.restoration).toBe("verified by read-back");
      expect((await magentaTiles(editor)).length).toBeGreaterThan(0);
      expect(await run.page.$eval("#panel", (panel) => [panel.scrollLeft, panel.scrollTop])).toEqual([80, 160]);
      expect(run.errors).toEqual([]);
    } finally { await run.close(); }
  });
}

test("classic scrollbar gutters do not stretch or leak into the captured page", async () => {
  const run = await launch();
  try {
    const contentWidth = await run.page.evaluate(() => {
      const style = document.createElement("style");
      style.textContent = 'html{overflow-y:scroll}html::-webkit-scrollbar{width:24px;background:#00ff00}body{height:2400px;background:linear-gradient(to right,#fff calc(100% - 8px),#ff00cc 0)}';
      document.head.append(style);
      document.body.replaceChildren();
      return document.documentElement.clientWidth;
    });
    const { editor, record } = await capture(run);
    expect(record.state, JSON.stringify(record.report)).toBe("complete");
    expect(record.report.dimensionsPx.width).toBe(contentWidth);
    expect(record.report.dimensionsPx.height).toBe(2400);
    expect(await editor.evaluate(async () => {
      const { getTiles } = await import(chrome.runtime.getURL("src/store.js"));
      const [tile] = await getTiles(new URL(location.href).searchParams.get("id"));
      const bitmap = await createImageBitmap(tile.blob);
      const canvas = new OffscreenCanvas(bitmap.width, bitmap.height);
      const context = canvas.getContext("2d");
      context.drawImage(bitmap, 0, 0); bitmap.close();
      return [...context.getImageData(canvas.width - 3, 100, 1, 1).data];
    })).toEqual([255, 0, 204, 255]);
    expect(run.errors).toEqual([]);
  } finally { await run.close(); }
});

test("Entire page finds the main scrolling panel in a fixed-height application shell", async () => {
  const run = await launch();
  try {
    await run.page.evaluate(() => {
      document.body.innerHTML = '<header style="height:60px">APP TOOLBAR</header><aside style="position:fixed;top:60px;bottom:0;width:220px">SIDEBAR</aside><main id="app" style="margin-left:220px;height:calc(100vh - 60px);overflow:auto"><div style="height:3200px;background:repeating-linear-gradient(#fff 0 60px,#ddd 60px 120px)">SCROLLING CONTENT</div></main>';
      document.querySelector("#app").scrollTop = 240;
    });
    const { record } = await capture(run);
    expect(record.state, JSON.stringify(record.report)).toBe("complete");
    expect(record.report.target).toBe("self-scroller");
    expect(record.report.dimensionsCss.height).toBe(3200);
    expect(record.report.warnings.join(" ")).toMatch(/main scrolling area/);
    expect(await run.page.$eval("#app", (element) => element.scrollTop)).toBe(240);
    expect(run.errors).toEqual([]);
  } finally { await run.close(); }
});

test("captures in two browser windows share Chrome's screenshot rate limit", async () => {
  const run = await launch();
  try {
    await run.page.goto("http://127.0.0.1:4173/frame", { waitUntil: "networkidle0" });
    const worker = await (await run.browser.waitForTarget((target) => target.type() === "service_worker")).worker();
    const results = await worker.evaluate(async () => {
      const [first] = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
      const secondWindow = await chrome.windows.create({ url: "http://127.0.0.1:4173/frame", type: "normal", focused: false, width: 1200, height: 1000 });
      const secondId = secondWindow.tabs[0].id;
      const deadline = Date.now() + 10_000;
      let second;
      while (Date.now() < deadline) {
        second = await chrome.tabs.get(secondId);
        if (second.status === "complete" && second.url?.endsWith("/frame")) break;
        await new Promise((resolve) => setTimeout(resolve, 50));
      }
      return Promise.all([first, second].map((tab) => globalThis.__winshotTest.runCapture("full-page", null, tab, { openEditor: false })));
    });
    expect(results.map((result) => result.state), JSON.stringify(results)).toEqual(["complete", "complete"]);
    expect(run.errors).toEqual([]);
  } finally { await run.close(); }
});

test("wall-clock adjustments cannot break Chrome's screenshot rate limit", async () => {
  const run = await launch();
  try {
    const worker = await (await run.browser.waitForTarget((target) => target.type() === "service_worker")).worker();
    const result = await worker.evaluate(async () => {
      const originalNow = Date.now;
      const originalCapture = chrome.tabs.captureVisibleTab;
      let offset = 0;
      const times = [];
      Date.now = () => originalNow() + offset;
      chrome.tabs.captureVisibleTab = async (...args) => {
        times.push(performance.now());
        const pixels = await originalCapture.apply(chrome.tabs, args);
        offset += 600_000;
        return pixels;
      };
      try {
        const [tab] = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
        const capture = await globalThis.__winshotTest.runCapture("full-page", null, tab, { openEditor: false });
        return { capture, times };
      } finally {
        Date.now = originalNow;
        chrome.tabs.captureVisibleTab = originalCapture;
      }
    });
    expect(result.capture.state, JSON.stringify(result)).toBe("complete");
    for (let index = 1; index < result.times.length; index++) {
      expect(result.times[index] - result.times[index - 1]).toBeGreaterThanOrEqual(500);
    }
    expect(run.errors).toEqual([]);
  } finally { await run.close(); }
});

test("transient screenshot readback retries once while permission errors fail immediately", async () => {
  const run = await launch();
  try {
    const worker = await (await run.browser.waitForTarget((target) => target.type() === "service_worker")).worker();
    const result = await worker.evaluate(async () => {
      const original = chrome.tabs.captureVisibleTab;
      const [tab] = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
      let attempts = 0;
      try {
        chrome.tabs.captureVisibleTab = async (...args) => {
          if (++attempts === 1) throw new Error("Failed to capture tab: image readback failed");
          return original.apply(chrome.tabs, args);
        };
        const recovered = await globalThis.__winshotTest.runCapture("visible", null, tab, { openEditor: false });
        const readbackAttempts = attempts;
        attempts = 0;
        chrome.tabs.captureVisibleTab = async () => { attempts++; throw new Error("Cannot access contents of url: missing host permission"); };
        const denied = await globalThis.__winshotTest.runCapture("visible", null, tab, { openEditor: false });
        return { recovered, readbackAttempts, denied, permissionAttempts: attempts };
      } finally { chrome.tabs.captureVisibleTab = original; }
    });
    expect(result.recovered.state, JSON.stringify(result)).toBe("complete");
    expect(result.readbackAttempts).toBe(2);
    expect(result.denied.state).toBe("failed");
    expect(result.permissionAttempts).toBe(1);
    expect(run.errors).toEqual([]);
  } finally { await run.close(); }
});
