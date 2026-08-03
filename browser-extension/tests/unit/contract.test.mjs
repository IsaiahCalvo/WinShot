import test from "node:test";
import assert from "node:assert/strict";
import {
  axisPositions,
  buildTilePlan,
  createSession,
  restrictedPageReason,
  validateCoverage
} from "../../src/contract.js";

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
