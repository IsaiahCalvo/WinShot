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

test("first scrolling tile rejects a stale browser paint before declaring complete", async () => {
  const run = await launch();
  try {
    await run.page.goto("http://127.0.0.1:4173/sticky", { waitUntil: "networkidle0" });
    await run.page.evaluate(async () => {
      document.querySelector("header").style.position = "static";
      scrollTo(0, 255);
      await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
    });
    const worker = await (await run.browser.waitForTarget(target => target.type() === "service_worker")).worker();
    await worker.evaluate(async () => {
      const [tab] = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
      const original = chrome.tabs.captureVisibleTab;
      const stale = await original.call(chrome.tabs, tab.windowId, { format: "png" });
      let calls = 0;
      chrome.tabs.captureVisibleTab = (...args) => ++calls === 1 ? Promise.resolve(stale) : original.apply(chrome.tabs, args);
    });
    await run.page.evaluate(() => document.querySelector("header").style.removeProperty("position"));
    const { editor, record } = await capture(run);
    expect(record.state, JSON.stringify(record.report)).toBe("complete");
    const firstPixel = await editor.evaluate(async () => {
      const { getTile } = await import(chrome.runtime.getURL("src/store.js"));
      const tile = await getTile(new URL(location.href).searchParams.get("id"), 0);
      const image = await createImageBitmap(tile.blob);
      const ctx = new OffscreenCanvas(image.width, image.height).getContext("2d");
      ctx.drawImage(image, 0, 0); image.close();
      return [...ctx.getImageData(5, 5, 1, 1).data];
    });
    expect(firstPixel).toEqual([255, 207, 51, 255]);
    expect(record.report.seamConfidence.minimum).toBe(1);
    expect(record.report.restoration).toBe("verified by read-back");
  } finally { await run.close(); }
});

for (const height of [0, 1]) test(`a ${height}px fixed wrapper with visible absolute children is captured once`, async () => {
  const run = await launch();
  try {
    await run.page.evaluate(height => {
      document.body.innerHTML = `<div id="nav" style="position:fixed;left:0;top:0;width:0;height:${height}px;z-index:10"><div style="position:absolute;left:0;top:0;width:600px;height:80px;background:#ff00cc;visibility:visible!important">FIXED NAV</div></div><div style="height:3200px;background:#ddd"></div>`;
    }, height);
    const { editor, record } = await capture(run);
    expect(record.state, JSON.stringify(record.report)).toBe("complete");
    expect(await magentaTiles(editor)).toHaveLength(1);
    expect(await run.page.$eval("#nav", node => ({ opacity: node.style.opacity, visibility: node.style.visibility }))).toEqual({ opacity: "", visibility: "" });
  } finally { await run.close(); }
});

test("GSAP reveals finish while hidden paused menus stay hidden and playback is restored", async () => {
  const run = await launch();
  try {
    await run.page.goto("http://127.0.0.1:4173/gsap", { waitUntil: "networkidle0" });
    const { editor, record } = await capture(run);
    expect(record.state, JSON.stringify(record.report)).toBe("complete");
    expect(await magentaTiles(editor)).toHaveLength(0);
    const greens = await editor.evaluate(async () => {
      const { getTiles } = await import(chrome.runtime.getURL("src/store.js"));
      const tiles = await getTiles(new URL(location.href).searchParams.get("id"));
      const ctx = new OffscreenCanvas(1200,4800).getContext("2d");
      for(const tile of tiles){const image=await createImageBitmap(tile.blob);ctx.drawImage(image,tile.destXPx,tile.destYPx);image.close();}
      return Array.from({length:6},(_,i)=>[...ctx.getImageData(200,i*800+280,1,1).data].slice(0,3));
    });
    expect(greens).toEqual(Array.from({length:6},()=>[26,180,92]));
    await run.page.bringToFront();
    const after=await run.page.evaluate(()=>({menu:window.menu.paused(),hidden:getComputedStyle(document.querySelector('#menu')).visibility,user:window.userPaused.paused(),time:window.userPaused.time(),root:gsap.globalTimeline.paused(),spinner:window.spinner.totalTime()}));
    expect(after).toMatchObject({menu:true,hidden:'hidden',user:true,time:.4,root:false});
    await expect.poll(()=>run.page.evaluate(()=>window.spinner.totalTime())).toBeGreaterThan(after.spinner+.05);
    expect(record.report.restoration).toBe("verified by read-back");
  } finally { await run.close(); }
});

test("a layout that collapses after the first tile is recaptured without stale pixels or tiles", async () => {
  const run = await launch();
  try {
    await run.page.evaluate(() => {
      document.body.innerHTML = '<div id="ad" style="height:200px;background:#888"></div><div style="height:3600px;background:repeating-linear-gradient(#000 0 40px,#fff 40px 80px)"></div>';
      addEventListener('scroll',()=>{if(scrollY>500)document.querySelector('#ad').style.height='0px';});
    });
    const { editor, record } = await capture(run);
    expect(record.state, JSON.stringify(record.report)).toBe("complete");
    expect(record.report.dimensionsPx.height).toBe(3600);
    const actual = await editor.evaluate(async () => {
      const { getTiles } = await import(chrome.runtime.getURL("src/store.js"));
      const tiles=await getTiles(new URL(location.href).searchParams.get('id'));
      const first=tiles.find(t=>t.index===0),image=await createImageBitmap(first.blob),ctx=new OffscreenCanvas(image.width,image.height).getContext('2d');ctx.drawImage(image,0,0);image.close();
      return{count:tiles.length,pixel:[...ctx.getImageData(10,10,1,1).data]};
    });
    expect(actual).toEqual({count:record.report.tileCount,pixel:[0,0,0,255]});
    expect(record.report.restoration).toBe("verified by read-back");
  } finally { await run.close(); }
});

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


for (const height of ["0px", "100vh"]) test(`nested fixed header and footer survive a ${height} wrapper independently`, async () => {
  const run=await launch();
  try {
    await run.page.evaluate(height=>{
      document.body.innerHTML=`<div id="wrapper" style="position:fixed;top:0;left:0;width:100%;height:${height};z-index:3"><div id="head" style="position:fixed;top:0;left:0;width:100%;height:60px;background:#ff00cc"></div><div id="normal" style="position:absolute;top:64px;left:0;width:100%;height:40px;background:#0000ff;visibility:visible"></div><div id="foot" style="position:fixed;bottom:0;left:0;width:100%;height:60px;background:#00c8df"></div></div><article style="height:3600px;background:#eee"></article>`;
    },height);
    const {editor,record}=await capture(run);
    expect(record.state,JSON.stringify(record.report)).toBe('complete');
    const counts=await editor.evaluate(async()=>{
      const s=await import(chrome.runtime.getURL('src/store.js')),r=await s.getRecord(new URL(location.href).searchParams.get('id'));
      const canvas=new OffscreenCanvas(r.report.dimensionsPx.width,r.report.dimensionsPx.height),c=canvas.getContext('2d');
      for(const t of await s.getTiles(r.id)){const b=await createImageBitmap(t.blob);c.drawImage(b,t.destXPx,t.destYPx);b.close();}
      const pixels=c.getImageData(1100,0,1,canvas.height).data,counts={head:0,foot:0,normal:0};
      for(let i=0;i<pixels.length;i+=4){const key=[...pixels.slice(i,i+3)].join(',');if(key==='255,0,204')counts.head++;if(key==='0,200,223')counts.foot++;if(key==='0,0,255')counts.normal++;}
      return counts;
    });
    expect(counts).toEqual({head:60,foot:60,normal:40});
    expect(await run.page.evaluate(()=>['wrapper','head','foot','normal'].map(id=>document.getElementById(id).style.visibility))).toEqual(['','','','visible']);
    expect(record.report.restoration).toBe('verified by read-back');
  } finally {await run.close();}
});

test("GSAP Infinity repeats created during scrolling remain live after capture",async()=>{
  const run=await launch();
  try {
    await run.page.goto('http://127.0.0.1:4173/gsap',{waitUntil:'networkidle0'});
    await run.page.evaluate(()=>addEventListener('scroll',()=>{if(scrollY>500&&!window.pulse)window.pulse=gsap.to('#spinner',{opacity:.3,repeat:Infinity,yoyo:true,duration:.4});}));
    const {record}=await capture(run);
    expect(record.state,JSON.stringify(record.report)).toBe('complete');
    await run.page.bringToFront();
    expect(await run.page.evaluate(()=>({infinite:window.pulse.repeat()===Infinity,paused:window.pulse.paused(),root:gsap.globalTimeline.paused()}))).toEqual({infinite:true,paused:false,root:false});
    const time=await run.page.evaluate(()=>window.pulse.totalTime());
    await expect.poll(()=>run.page.evaluate(()=>window.pulse.totalTime())).toBeGreaterThan(time+.05);
    expect(record.report.restoration).toBe('verified by read-back');
  } finally {await run.close();}
});

test("GSAP invalidated tween restoration still wakes the ticker and restores other animations",async()=>{
  const run=await launch();
  try {
    await run.page.goto('http://127.0.0.1:4173/gsap',{waitUntil:'networkidle0'});
    await run.page.evaluate(()=>{
      window.badTween=gsap.to({value:0},{value:10,duration:30});
      const time=window.badTween.totalTime;
      window.badTween.totalTime=function(...args){if(args.length&&window.throwDuringRestore)throw new Error('Invalidated test tween');return time.apply(this,args);};
      addEventListener('scroll',()=>{if(scrollY>500)window.throwDuringRestore=true;});
    });
    const {record}=await capture(run);
    expect(record.state,JSON.stringify(record.report)).toBe('complete');
    expect(record.report.restoration).toContain('GSAP animation playback');
    await run.page.bringToFront();
    expect(await run.page.evaluate(()=>gsap.globalTimeline.paused())).toBe(false);
    const time=await run.page.evaluate(()=>window.spinner.totalTime());
    await expect.poll(()=>run.page.evaluate(()=>window.spinner.totalTime())).toBeGreaterThan(time+.05);
  } finally {await run.close();}
});

for(const fault of ['readback','unstable']) test(`a ${fault} recapture keeps the original partial pixels and removes staging tiles`,async()=>{
  const run=await launch();
  try {
    await run.page.evaluate(()=>{
      document.body.innerHTML='<div id="ad" style="height:200px;background:#888"></div><div style="height:3600px;background:repeating-linear-gradient(#000 0 40px,#fff 40px 80px)"></div>';
      addEventListener('scroll',()=>{if(scrollY>500)document.querySelector('#ad').style.height='0px';});
    });
    const worker=await(await run.browser.waitForTarget(t=>t.type()==='service_worker'&&t.url().endsWith('/src/background.js'))).worker();
    await worker.evaluate(fault=>{
      const send=chrome.tabs.sendMessage.bind(chrome.tabs),image=chrome.tabs.captureVisibleTab.bind(chrome.tabs);let prepares=0;
      chrome.tabs.sendMessage=async(...args)=>{
        const message=args[1];
        if(message.type==='WINSHOT_PREPARE'&&++prepares===2){
          const db=await new Promise((resolve,reject)=>{const r=indexedDB.open('winshot-captures');r.onsuccess=()=>resolve(r.result);r.onerror=()=>reject(r.error);});
          const tiles=await new Promise((resolve,reject)=>{const r=db.transaction('tiles').objectStore('tiles').index('byCapture').getAll(IDBKeyRange.only(message.sessionId));r.onsuccess=()=>resolve(r.result);r.onerror=()=>reject(r.error);});db.close();
          globalThis.originalHashes=await Promise.all(tiles.map(async t=>Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256',await t.blob.arrayBuffer()))).join(',')));
        }
        const result=await send(...args);
        if(prepares>1&&fault==='unstable'&&message.type==='WINSHOT_SCROLL'){globalThis.recaptureFaultReached=true;return{...result,stable:false,warnings:['Forced recapture readiness failure']};}
        return result;
      };
      chrome.tabs.captureVisibleTab=(...args)=>{if(prepares>1&&fault==='readback'){globalThis.recaptureFaultReached=true;return Promise.reject(new Error('Missing host permission during recapture'));}return image(...args);};
    },fault);
    const {editor,record}=await capture(run);
    expect(record.state,JSON.stringify(record.report)).toBe('partial');
    expect(record.report.layoutRetry.keptOriginal).toBe(true);
    expect(record.report.warnings.join(' ')).toContain('original partial capture was kept');
    const actual=await editor.evaluate(async()=>{
      const s=await import(chrome.runtime.getURL('src/store.js')),tiles=await s.getTiles(new URL(location.href).searchParams.get('id'));
      const hashes=await Promise.all(tiles.map(async t=>Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256',await t.blob.arrayBuffer()))).join(',')));
      const db=await new Promise((resolve,reject)=>{const r=indexedDB.open('winshot-captures');r.onsuccess=()=>resolve(r.result);r.onerror=()=>reject(r.error);});
      const keys=await new Promise((resolve,reject)=>{const r=db.transaction('tiles').objectStore('tiles').getAllKeys();r.onsuccess=()=>resolve(r.result);r.onerror=()=>reject(r.error);});db.close();
      return{hashes,staged:keys.filter(k=>String(k[0]).includes('-retry-')).length};
    });
    expect(await worker.evaluate(()=>globalThis.recaptureFaultReached)).toBe(true);
    expect(actual.hashes).toEqual(await worker.evaluate(()=>globalThis.originalHashes));
    expect(actual.staged).toBe(0);
  } finally {await run.close();}
});


test("an old main-world animation bridge cannot intercept an upgraded capture",async()=>{
  const run=await launch();
  try {
    await run.page.goto('http://127.0.0.1:4173/gsap',{waitUntil:'networkidle0'});
    await run.page.evaluate(()=>{
      window[Symbol.for('winshot.page-effects.v1')]=true;window.legacyRequests=0;
      addEventListener('message',event=>{if(event.source===window&&event.data?.source==='winshot-effects-request'){window.legacyRequests++;postMessage({source:'winshot-effects-response',nonce:event.data.nonce,result:{error:'Obsolete page bridge'}},'*');}});
    });
    const {record}=await capture(run);
    expect(record.state,JSON.stringify(record.report)).toBe('complete');
    expect(record.report.restoration).toBe('verified by read-back');
    expect(await run.page.evaluate(()=>window.legacyRequests)).toBe(0);
  } finally {await run.close();}
});
