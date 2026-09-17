// Regenerates revision-cloud-v17.json: the approved revision-cloud reference's OWN output,
// so RevisionCloudParityTests can diff the C# port against it numerically.
//
//   node tests/WinShot.Tests/Fixtures/generate-revision-cloud-v17.mjs //        tests/WinShot.Tests/Fixtures/revision-cloud-v17.json
//
// Needs node >= 22.18 (native TypeScript type stripping) and Survey checked out beside
// WinShot. The reference is vendored and must never be hand-edited to make a test pass;
// only replace it wholesale with a newer approved upstream revision, then rerun this.
import { writeFileSync } from 'node:fs';

const ref = await import('file:///C:/Users/icalvo/Survey/tests/fixtures/reference/cloud-v17.ts');

const rect = (x, y, w, h) => [
  { x, y }, { x: x + w, y }, { x: x + w, y: y + h }, { x, y: y + h },
];

const POLYGONS = {
  triangle: [{ x: 100, y: 100 }, { x: 300, y: 120 }, { x: 180, y: 320 }],
  concave: [
    { x: 100, y: 100 }, { x: 300, y: 100 }, { x: 300, y: 300 },
    { x: 200, y: 200 }, { x: 100, y: 300 },
  ],
  star: [
    { x: 200, y: 60 }, { x: 224, y: 150 }, { x: 320, y: 150 }, { x: 244, y: 206 },
    { x: 272, y: 300 }, { x: 200, y: 242 }, { x: 128, y: 300 }, { x: 156, y: 206 },
    { x: 80, y: 150 }, { x: 176, y: 150 },
  ],
  shortGapConvex: [
    { x: 100, y: 100 }, { x: 112, y: 98 }, { x: 126, y: 104 }, { x: 140, y: 100 },
    { x: 300, y: 140 }, { x: 280, y: 300 }, { x: 110, y: 280 },
  ],
  shortGapTight: [
    { x: 200, y: 100 }, { x: 206, y: 99 }, { x: 213, y: 101 }, { x: 219, y: 99 },
    { x: 226, y: 102 }, { x: 320, y: 220 }, { x: 140, y: 240 },
  ],
  counterClockwise: [
    { x: 100, y: 300 }, { x: 300, y: 300 }, { x: 300, y: 100 }, { x: 100, y: 100 },
  ],
  tiny: [{ x: 100, y: 100 }, { x: 106, y: 101 }, { x: 110, y: 107 }, { x: 103, y: 110 }],
};

const POLYLINES = {
  twoPoint: [{ x: 40, y: 40 }, { x: 320, y: 210 }],
  zigzag: [
    { x: 40, y: 260 }, { x: 120, y: 90 }, { x: 210, y: 250 },
    { x: 300, y: 80 }, { x: 380, y: 240 },
  ],
  shallow: [{ x: 20, y: 100 }, { x: 180, y: 108 }, { x: 340, y: 96 }],
};

// Bump -> scallop size, pdfAnnotationAppearance.cloudRadiusForIntensity:
//   size = min(80, 14 * bump)   (unitScale 1)
const bumpSize = (bump) => Math.min(80, 14 * bump);

const cases = [];
const push = (label, kind, points, size, depth = 12) =>
  cases.push({ label, kind, points, size, depth });

// Rectangles / ellipses / polygons over a spread of boxes, every Bump 1..20 on
// the headline box plus the reference suite's own size ladder elsewhere.
for (const bump of [1, 2, 3, 4, 5, 6, 8, 10, 13, 16, 20]) {
  for (const kind of ['rectangle', 'ellipse', 'polygon']) {
    push(`${kind} 300x200 bump=${bump}`, kind, rect(100, 100, 300, 200), bumpSize(bump));
  }
}
for (const [w, h] of [[300, 200], [40, 30], [600, 18], [25, 25], [120, 400], [1000, 700], [4, 4]]) {
  // A 1000x700 sheet at the smallest scallop fits ~1,700 crowns; three of those
  // per depth would be half the fixture file for no extra coverage, so the big
  // sheet only carries the sizes the small boxes cannot reach.
  for (const size of (w >= 1000 ? [60, 120] : [8, 14, 28, 60, 120])) {
    for (const depth of [2, 12, 25, 40]) {
      for (const kind of ['rectangle', 'ellipse', 'polygon']) {
        push(`${kind} ${w}x${h} size=${size} depth=${depth}`, kind, rect(100, 100, w, h), size, depth);
      }
    }
  }
}
for (const [name, points] of Object.entries(POLYGONS)) {
  for (const size of [10, 28, 55]) {
    for (const depth of [2, 12, 40]) {
      push(`polygon ${name} size=${size} depth=${depth}`, 'polygon', points, size, depth);
    }
  }
}
for (const [name, points] of Object.entries(POLYLINES)) {
  for (const size of [14, 28, 56]) {
    push(`polyline ${name} size=${size}`, 'polyline', points, size);
  }
}
// Degenerate / tiny.
push('rectangle 0.5x0.5 size=28', 'rectangle', rect(0, 0, 0.5, 0.5), 28);
push('ellipse 0.5x0.5 size=28', 'ellipse', rect(0, 0, 0.5, 0.5), 28);
push('polygon tiny bump=20', 'polygon', POLYGONS.tiny, bumpSize(20));
push('rectangle 1x120 size=28', 'rectangle', rect(0, 0, 1, 120), 28);
push('polyline 2 coincident points', 'polyline', [{ x: 50, y: 50 }, { x: 50, y: 50 }], 28);

const out = cases.map(({ label, kind, points, size, depth }) => {
  const shape = ref.makeShape(kind, points, 'x', { size, depth });
  const runs = ref.cloudRuns(shape, new Map(), 0, false);
  return {
    label,
    kind,
    size,
    depth,
    points: points.map((p) => [p.x, p.y]),
    runs: runs.map((r) => ({ id: r.id, d: r.d })),
  };
});

writeFileSync(process.argv[2], JSON.stringify(out));
console.log('cases', out.length, 'commands',
  out.reduce((s, c) => s + c.runs.reduce((t, r) => t + (r.d ? r.d.split(/ (?=[MC] )/).length : 0), 0), 0));
