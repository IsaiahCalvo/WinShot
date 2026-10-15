import puppeteer from "puppeteer";

export async function launchCaptureBrowser(userDataDir, viewport = { width: 1200, height: 1000 }, { args = [] } = {}) {
  const browser = await puppeteer.launch({
    executablePath: await puppeteer.executablePath(),
    headless: true, enableExtensions: true, userDataDir,
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
      ...args
    ]
  });
  try {
    const [page] = await browser.pages();
    const client = await page.createCDPSession();
    const { windowId } = await client.send("Browser.getWindowForTarget");
    const bounds = await page.evaluate(({ width, height }) => ({
      width: outerWidth + width - innerWidth,
      height: outerHeight + height - innerHeight
    }), viewport);
    await client.send("Browser.setWindowBounds", { windowId, bounds });
    await page.waitForFunction(({ width, height }) => innerWidth === width && innerHeight === height, { timeout: 10_000 }, viewport);
    await client.detach();
    return browser;
  } catch (error) {
    await browser.close();
    throw error;
  }
}
