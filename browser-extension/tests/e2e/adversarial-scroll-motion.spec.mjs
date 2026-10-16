import { test, expect } from "@playwright/test";
import { launchCaptureBrowser } from "./launch-browser.mjs";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

async function runFixture(setup, assertions, data = null) {
  const profile = fs.mkdtempSync(path.join(os.tmpdir(), "winshot-motion-"));
  const browser = await launchCaptureBrowser(profile);
  try {
    const id = await browser.installExtension(process.env.WINSHOT_EXTENSION_PATH || fileURLToPath(new URL("../../dist/winshot-capture", import.meta.url)));
    const extension = (await browser.extensions()).get(id);
    const page = await browser.newPage();
    await page.goto("http://127.0.0.1:4173/body");
    await page.evaluate(setup, data);
    const original = await page.evaluate(() => ({ x: scrollX, y: scrollY }));
    const popTarget = browser.waitForTarget(t => t.type() === "page" && t.url().endsWith("/src/popup.html"));
    await page.bringToFront();
    await extension.triggerAction(page);
    const popup = await (await popTarget).asPage();
    await popup.waitForSelector("[data-mode='full-page']");
    const editorTarget = browser.waitForTarget(t => t.type() === "page" && t.url().includes("/src/editor.html?id="), { timeout: 90_000 });
    await popup.click("[data-mode='full-page']");
    const editor = await (await editorTarget).asPage();
    await editor.waitForSelector("#state:not(:empty)");
    const record = await editor.evaluate(async () => (await import(chrome.runtime.getURL("src/store.js"))).getRecord(new URL(location.href).searchParams.get("id")));
    expect(record.report.dimensionsPx, record.error).toBeTruthy();
    await editor.waitForSelector("#preview img");
    const pixels = await editor.evaluate(async () => {
      const tiles = await (await import(chrome.runtime.getURL("src/store.js"))).getTiles(new URL(location.href).searchParams.get("id"));
      const width = Math.max(...tiles.map(t => t.destXPx + t.widthPx));
      const height = Math.max(...tiles.map(t => t.destYPx + t.heightPx));
      const context = new OffscreenCanvas(width, height).getContext("2d", { willReadFrequently: true });
      for (const tile of tiles.sort((a, b) => a.index - b.index)) {
        const bitmap = await createImageBitmap(tile.blob);
        context.drawImage(bitmap, tile.destXPx, tile.destYPx, tile.widthPx, tile.heightPx);
        bitmap.close();
      }
      const green = Array.from({ length: 6 }, (_, index) => [...context.getImageData(200, index * 800 + 280, 1, 1).data]);
      const magentaBands = [];
      for (let y = 0; y < height; y++) {
        const [r, g, b] = context.getImageData(100, y, 1, 1).data;
        if (r > 240 && g < 30 && b > 180) {
          const band = magentaBands.at(-1);
          if (band && band.end === y - 1) band.end = y;
          else magentaBands.push({ start: y, end: y });
        }
      }
      return { green, magentaBands, width, height };
    });
    expect(record.report.coverage.complete).toBe(true);
    expect(record.report.restoration).toBe("verified by read-back");
    expect(await page.evaluate(() => ({ x: scrollX, y: scrollY }))).toEqual(original);
    await assertions({ page, editor, record, pixels });
  } finally {
    await browser.close();
    fs.rmSync(profile, { recursive: true, force: true, maxRetries: 5, retryDelay: 100 });
  }
}

for (const mechanism of ["css", "web-animations"]) {
  test(`scroll-triggered ${mechanism} reveals remain visible in every exported tile`, async () => {
    await runFixture((mechanism) => {
      document.body.innerHTML = '<style>body{margin:0}.section{height:800px;padding:200px 100px}.reveal{width:320px;height:160px;background:rgb(26,180,92);opacity:0}.reveal.css.on{animation:appear .9s both}@keyframes appear{from{opacity:0}to{opacity:1}}</style>' +
        Array.from({ length: 6 }, () => '<section class="section"><div class="reveal"></div></section>').join("");
      const observer = new IntersectionObserver(entries => {
        for (const { target, isIntersecting } of entries) {
          if (!isIntersecting) continue;
          if (mechanism === "css") target.classList.add("css", "on");
          else target.animate([{ opacity: 0 }, { opacity: 1 }], { duration: 900, fill: "forwards" });
          observer.unobserve(target);
        }
      });
      document.querySelectorAll(".reveal").forEach(node => observer.observe(node));
    }, async ({ pixels, record }) => {
      expect(record.state).toBe("complete");
      for (const [index, pixel] of pixels.green.entries()) {
        expect(pixel.slice(0, 3), `revealed section ${index} must be present, got ${pixel}`).toEqual([26, 180, 92]);
      }
    }, mechanism);
  });
}

test("a header that becomes fixed during scrolling is not repeated in the stitched image", async () => {
  await runFixture(() => {
    document.body.innerHTML = '<style>body{margin:0}#slot{height:80px}#nav{height:80px;background:#ff00cc;width:100%;z-index:50}#nav.floating{position:fixed;top:0}#content{height:4800px;background:#fff}</style><div id="slot"><header id="nav"></header></div><main id="content"></main>';
    addEventListener("scroll", () => document.querySelector("#nav").classList.toggle("floating", scrollY > 500));
  }, async ({ pixels, record, page }) => {
    expect(record.state).toBe("complete");
    expect(pixels.magentaBands, JSON.stringify(pixels)).toHaveLength(1);
    expect(pixels.magentaBands[0]).toEqual({ start: 0, end: 79 });
    await page.waitForFunction(() => !document.querySelector("#nav").classList.contains("floating"));
    expect(await page.$eval("#nav", node => node.style.visibility)).toBe("");
  });
});

test("CSS view-timeline reveals are fully visible and their scroll animations are restored", async () => {
  await runFixture(() => {
    if (!CSS.supports("animation-timeline", "view()")) throw new Error("This test browser must support view timelines");
    document.body.innerHTML = '<style>body{margin:0}.section{height:800px;padding:200px 100px}.reveal{width:320px;height:160px;background:rgb(26,180,92);animation:reveal linear both;animation-timeline:view();animation-range:entry 0% cover 60%}@keyframes reveal{from{opacity:0;transform:translateY(30px)}to{opacity:1;transform:translateY(0)}}</style>' +
      Array.from({ length: 6 }, () => '<section class="section"><div class="reveal"></div></section>').join("");
  }, async ({ page, record, pixels }) => {
    expect(record.state, JSON.stringify(record.report)).toBe("complete");
    for (const pixel of pixels.green) expect(pixel.slice(0, 3)).toEqual([26, 180, 92]);
    expect(await page.evaluate(() => document.getAnimations().filter(animation => animation.timeline instanceof ViewTimeline).length)).toBe(6);
    expect(await page.evaluate(() => document.getAnimations().filter(animation => animation.timeline instanceof DocumentTimeline).length)).toBe(0);
  });
});

test("a fixed footer inserted after scrolling appears once at the true bottom", async () => {
  await runFixture(() => {
    document.body.innerHTML = '<style>body{margin:0;height:4800px;background:#fff}footer{position:fixed;bottom:0;left:0;width:100%;height:80px;background:#ff00cc;z-index:50}</style>';
    addEventListener("scroll", () => {
      if (scrollY > 600 && !document.querySelector("footer")) document.body.append(document.createElement("footer"));
    });
  }, async ({ pixels, record, page }) => {
    expect(record.state).toBe("complete");
    expect(pixels.magentaBands).toEqual([{ start: 4720, end: 4799 }]);
    expect(await page.$eval("footer", node => node.style.visibility)).toBe("");
  });
});

test("a fixed overlay that was hidden by the page stays hidden throughout capture", async () => {
  await runFixture(() => {
    document.body.innerHTML = '<style>body{margin:0;height:4800px;background:white}#hidden{position:fixed;top:0;left:0;width:100%;height:80px;background:#ff00cc;visibility:hidden}</style><aside id="hidden"></aside>';
  }, async ({ pixels, record, page }) => {
    expect(record.state).toBe("complete");
    expect(pixels.magentaBands).toEqual([]);
    expect(await page.$eval("#hidden", node => getComputedStyle(node).visibility)).toBe("hidden");
  });
});

test("repeating Web Animations resume after capture while user-paused effects stay paused", async () => {
  await runFixture(() => {
    document.body.innerHTML = '<style>body{margin:0;height:4800px}.box{width:500px;height:150px;background:#26b45c}</style><div id="running" class="box"></div><div id="paused" class="box"></div>';
    const running = document.querySelector("#running").animate([{ transform: "translateX(0px)" }, { transform: "translateX(25px)" }], { duration: 1000, iterations: Infinity });
    running.id = "running";
    const paused = document.querySelector("#paused").animate([{ opacity: 0 }, { opacity: 1 }], { duration: 1000, fill: "both" });
    paused.id = "paused"; paused.pause(); paused.currentTime = 400;
  }, async ({ record, page }) => {
    expect(record.state).toBe("complete");
    const before = await page.evaluate(() => document.getAnimations().map(a => ({ id: a.id, state: a.playState, time: a.currentTime })));
    expect(before.find(a => a.id === "running").state).toBe("running");
    expect(before.find(a => a.id === "paused")).toMatchObject({ state: "paused", time: 400 });
    await new Promise(resolve => setTimeout(resolve, 150));
    const after = await page.evaluate(() => document.getAnimations().map(a => ({ id: a.id, state: a.playState, time: a.currentTime })));
    expect(after.find(a => a.id === "running").time).toBeGreaterThan(before.find(a => a.id === "running").time + 50);
    expect(after.find(a => a.id === "paused")).toMatchObject({ state: "paused", time: 400 });
  });
});

for (const change of ["fragment", "history-path"]) {
  test(`scroll-driven ${change} URL updates do not masquerade as a document navigation`, async () => {
    await runFixture(change => {
      document.body.innerHTML = '<style>body{margin:0;height:4800px;background:white}</style>';
      addEventListener("scroll", () => {
        const page = Math.floor(scrollY / 700);
        const url = change === "fragment" ? `#section-${page}` : `/article/page-${page}`;
        history.replaceState({ page }, "", url);
      });
    }, async ({ record }) => {
      expect(record.state, JSON.stringify(record.report)).toBe("complete");
      expect(record.report.tileCount).toBeGreaterThan(4);
      expect(record.report.warnings.join(" ")).toMatch(/URL.*document identity/i);
    }, change);
  });
}

test("lazy embedded documents may load during an outer-page capture without aborting it", async () => {
  await runFixture(() => {
    document.body.innerHTML = '<style>body{margin:0;height:4800px;background:white}iframe{position:absolute;top:1400px;left:500px;width:500px;height:250px}</style>';
    addEventListener("scroll", () => {
      if (scrollY > 600 && !document.querySelector("iframe")) {
        const frame = document.createElement("iframe"); frame.src = "/frame"; document.body.append(frame);
      }
    });
  }, async ({ record }) => { expect(record.state, JSON.stringify(record.report)).toBe("complete"); });
});
