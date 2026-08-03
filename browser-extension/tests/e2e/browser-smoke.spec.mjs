import { test, expect } from "@playwright/test";

test("real browser renders and scrolls the capture corpus predictably", async ({ page }, testInfo) => {
  await page.goto("http://127.0.0.1:4173/sticky");
  await expect(page.locator("body")).toHaveAttribute("data-fixture", "sticky-fixed");
  const start = await page.locator("header").boundingBox();
  await page.evaluate(() => scrollTo(0, 1800));
  await expect.poll(() => page.evaluate(() => scrollY)).toBeGreaterThan(1700);
  const after = await page.locator("header").boundingBox();
  expect(Math.abs(after.y - start.y)).toBeLessThan(1);

  await page.goto("http://127.0.0.1:4173/virtual");
  const virtual = page.locator("#virtual");
  await virtual.evaluate((element) => { element.scrollTop = 8000; });
  await expect(virtual.locator(".virtual-row").first()).toContainText("VIRTUAL 0125");
  expect(["chrome-smoke", "edge-smoke"]).toContain(testInfo.project.name);
});
