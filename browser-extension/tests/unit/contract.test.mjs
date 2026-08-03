import test from "node:test";
import assert from "node:assert/strict";
import {
  axisPositions,
  buildTilePlan,
  createSession,
  restrictedPageReason,
  validateCoverage
} from "../../src/contract.js";
import { normalizeSettings, renderFileName, renderPdfTemplate } from "../../src/settings.js";
import { pdfPageSizePoints } from "../../src/exporters.js";

test("axis positions finish exactly at the final reachable scroll offset", () => {
  assert.deepEqual(axisPositions(2500, 1000, 100), [0, 900, 1500]);
  assert.deepEqual(axisPositions(800, 1000, 100), [0]);
});

test("tile plan covers vertical and horizontal content", () => {
  const plan = buildTilePlan({ width: 1800, height: 2500, viewportWidth: 1000, viewportHeight: 1000, overlap: 100 });
  assert.equal(plan.length, 6);
  assert.deepEqual(plan.at(-1), { x: 800, y: 1500 });
});

test("coverage validation accepts overlaps and rejects missing bands", () => {
  const full = validateCoverage(100, 200, [
    { destX: 0, destY: 0, widthCss: 100, heightCss: 120 },
    { destX: 0, destY: 100, widthCss: 100, heightCss: 100 }
  ]);
  assert.equal(full.complete, true);
  assert.equal(full.ratio, 1);

  const gap = validateCoverage(100, 200, [
    { destX: 0, destY: 0, widthCss: 100, heightCss: 80 },
    { destX: 0, destY: 100, widthCss: 100, heightCss: 100 }
  ]);
  assert.equal(gap.complete, false);
  assert.ok(gap.ratio < 1);
  assert.match(gap.gaps[0], /Gap/);
});

test("session contract has bounded local-first defaults", () => {
  const session = createSession({
    id: "capture-1",
    mode: "full-page",
    tab: { id: 4, windowId: 8, url: "https://example.test", title: "Fixture" }
  });
  assert.equal(session.protocolVersion, 1);
  assert.equal(session.limits.maxTiles, 400);
  assert.equal(session.limits.maxHeightCss, 200000);
  assert.equal(session.report.restoration, "pending");
});

test("restricted browser pages receive an honest fallback", () => {
  assert.match(restrictedPageReason("chrome://settings"), /visible page/i);
  assert.match(restrictedPageReason("file:///fixture.html"), /file URLs/i);
});

test("capture and PDF settings are bounded and filename templates are safe", () => {
  const settings = normalizeSettings({
    delayMs: 999999,
    maxDurationMs: 1,
    maxTiles: 99999,
    maxHeightCss: 9,
    batchMode: "visible",
    fileNameTemplate: "{title}:{mode}:{host}",
    pdf: { layout: "single", pageSize: "custom", customWidthIn: 0, customHeightIn: 500 }
  });
  assert.equal(settings.delayMs, 30000);
  assert.equal(settings.maxDurationMs, 30000);
  assert.equal(settings.maxTiles, 2000);
  assert.equal(settings.maxHeightCss, 1000);
  assert.equal(settings.batchMode, "visible");
  assert.deepEqual([settings.pdf.layout, settings.pdf.pageSize, settings.pdf.customWidthIn, settings.pdf.customHeightIn], ["single", "custom", 1, 200]);
  assert.equal(renderFileName(settings.fileNameTemplate, { sourceTitle: "Bad / title", sourceUrl: "https://example.test/path", mode: "visible" }), "Bad - title-visible-example.test");
  assert.equal(renderPdfTemplate("{title} {page}/{pages}", { title: "Fixture", page: 2, pages: 4 }), "Fixture 2/4");
});

test("PDF presets use their advertised physical page sizes", () => {
  assert.deepEqual(pdfPageSizePoints({ pageSize: "letter" }), { width: 612, height: 792 });
  assert.deepEqual(pdfPageSizePoints({ pageSize: "legal" }), { width: 612, height: 1008 });
  assert.deepEqual(pdfPageSizePoints({ pageSize: "a4" }), { width: 595.28, height: 841.89 });
  assert.deepEqual(pdfPageSizePoints({ pageSize: "custom", customWidthIn: 7, customHeightIn: 9 }), { width: 504, height: 648 });
  assert.equal(pdfPageSizePoints({ pageSize: "auto" }), null);
});
