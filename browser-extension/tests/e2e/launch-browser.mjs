import puppeteer from "puppeteer";

export async function launchCaptureBrowser(userDataDir, viewport = { width: 1200, height: 1000 }, { args = [], headless = true } = {}) {
  const browser = await puppeteer.launch({
    executablePath: await puppeteer.executablePath(),
    headless, enableExtensions: true, userDataDir,
    // captureVisibleTab reads the physical surface, not CDP's emulated viewport.
    // Emulation can claim 1000px while Chrome actually returns only 913px,
    // especially on extension-created batch tabs. Resize the real window instead.
    defaultViewport: null,
    args: [
      `--window-size=${viewport.width},${viewport.height}`,
      ...(viewport.deviceScaleFactor ? [`--force-device-scale-factor=${viewport.deviceScaleFactor}`] : []),
      // GPU-less Windows CI must render the fixture's WebGL surface, not silently
      // assert against a blank canvas. Opt in only for the isolated fixture run.
      ...(process.env.WINSHOT_TEST_SOFTWARE_GL === "1"
        ? ["--use-gl=angle", "--use-angle=swiftshader", "--enable-unsafe-swiftshader"]
        : []),
      // SwiftShader on GPU-less Windows runners must also use CPU composition.
      // The GPU compositor can stall readback despite a fully painted canvas.
      ...(process.env.WINSHOT_TEST_SOFTWARE_COMPOSITING === "1" ||
          (process.env.WINSHOT_TEST_SOFTWARE_GL === "1" && process.env.WINSHOT_TEST_SOFTWARE_COMPOSITING !== "0")
        ? ["--disable-gpu-compositing"] : []),
      ...args
    ]
  });
  let page, client, windowId, bounds;
  try {
    [page] = await browser.pages();
    client = await page.createCDPSession();
    ({ windowId } = await client.send("Browser.getWindowForTarget"));
    bounds = await page.evaluate(({ width, height }) => ({
      width: outerWidth + width - innerWidth,
      height: outerHeight + height - innerHeight
    }), viewport);
    await client.send("Browser.setWindowBounds", { windowId, bounds });
    await page.waitForFunction(({ width, height }) => innerWidth === width && innerHeight === height, { timeout: 10_000 }, viewport);
    await client.detach();
    return browser;
  } catch (error) {
    let diagnosticTimer;
    const details = await Promise.race([
      Promise.allSettled([
        page ? page.evaluate(() => ({
          inner: { width: innerWidth, height: innerHeight },
          outer: { width: outerWidth, height: outerHeight },
          screen: { width: screen.width, height: screen.height, availWidth: screen.availWidth, availHeight: screen.availHeight },
          devicePixelRatio
        })) : Promise.reject(new Error("No initial page")),
        client && windowId !== undefined ? client.send("Browser.getWindowBounds", { windowId }) : Promise.reject(new Error("No native browser window"))
      ]).then(results => results.map(result => result.status === "fulfilled" ? result.value : { error: result.reason?.message || String(result.reason) })),
      new Promise(resolve => { diagnosticTimer = setTimeout(() => resolve({ error: "Window diagnostics timed out after 2 seconds" }), 2000); })
    ]);
    clearTimeout(diagnosticTimer);
    const diagnostic = { headless, requestedViewport: viewport, requestedBounds: bounds, actual: details };
    await browser.close();
    throw new Error(`${error.message}\nChrome window sizing diagnostics: ${JSON.stringify(diagnostic)}`, { cause: error });
  }
}
