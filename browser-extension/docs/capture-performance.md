# Reproducible capture performance checks

`tools/benchmark-capture.mjs` measures the actual extension popup → scrolling
capture → editor flow in an isolated Chrome profile. It does not use personal
browser profiles or replace native screenshot results. It records every attempt,
including partial/failed captures, rather than timing only a successful retry.

Build an extension first, or pass an immutable extracted extension package:

```bash
PUPPETEER_EXECUTABLE_PATH=/path/to/chrome \
  node browser-extension/tools/benchmark-capture.mjs \
  --extension browser-extension/dist/winshot-capture \
  --label candidate-complex --scenario complex --repeats 10 \
  --profile --output output/capture-benchmark
```

The local fixture is served by the tool itself on a random loopback port. It has
8,400+ DOM elements, a 5,600-pixel document, a fixed header and footer, and seven
colored blocks revealed by an intersection observer. The `complex` scenario makes
60 CSS variable writes on a wrapper containing the elements on each scroll. This
models a burst of scroll-driven animation writes without moving the final pixels.
The `static` scenario omits those writes. The `continuous` diagnostic keeps writing
six variables each animation frame; it is deliberately expected to remain partial
when the page never becomes stable, and its timings are not a successful-capture
performance claim.

Each fixture capture must be complete, restore the original scroll position,
remove capture styles/pickers, contain each colored block for exactly 48 rows,
and contain exactly 54 header rows and 46 footer rows in the assembled raster.
The tool also records the complete PNG's SHA-256 so a candidate's pixels can be
compared with the baseline. These checks exercise capture output, not merely DOM
position or a mocked browser API.

To measure a public page instead:

```bash
PUPPETEER_EXECUTABLE_PATH=/path/to/chrome \
  node browser-extension/tools/benchmark-capture.mjs \
  --extension browser-extension/dist/winshot-capture \
  --label candidate-lenis --url https://lenis.dev/ --repeats 3 \
  --output output/capture-benchmark
```

Public pages use a weaker, explicit check: complete recorded coverage, restored
scroll position/styles, and a rendered-pixel sanity check. Each tile is hashed and
its geometry retained. This is not an exact-content pixel oracle: advertisements,
media, live content, site updates and animation phases can change between runs.
HTTP errors/access challenges remain failed attempts and are not bypassed.

The `*-runs.json` files include per-call timestamps for real screenshot readback,
content-script messages (including each scroll/settle), and script injection.
These wrappers add trace entries but do not substitute pixels, clocks, delays or
responses. `--profile` collects a Chrome CPU profile for the first attempt only;
the sampling overhead applies to that attempt in both comparison cohorts.
`*-summary.json` contains browser/version, viewport, graphics test flags, fixture
and extension bundle hashes, successes/failures, and nearest-rank p50/p95.

For a useful comparison, keep the browser, hardware, viewport, graphics flags,
fixture hash and all non-performance extension files equal. Run cohorts without
other CPU-heavy browser tests. Retain failed attempts and compare pixels as well
as times. These are small-sample observations on the measured machine; p95 with
three samples is simply the slowest observation, and ten samples do not establish
a population-wide service guarantee. Repeating on Windows is necessary before
making a claim about Windows speed. Chrome's screenshot-call quota also sets a
lower bound for long pages; removing correctness waits to improve a number is not
an acceptable optimization.

## Recorded optimization check, 2026-10-16

The uncontended local comparison used Linux Chrome 156.0.8078.4, a physical
1,200 × 1,000 viewport, and default graphics flags. Both packages were the tested
0.1.10 build; the candidate replaced only `src/content.js` and `src/background.js`.
No editor changes were included. The controlled fixture and browser were identical
between cohorts.

| Cohort | Complete / attempts | Median end to end | Observed p95 | Median scroll/settle total |
| --- | ---: | ---: | ---: | ---: |
| Baseline | 10/10 | 4.451 s | 4.667 s | 1.767 s |
| Candidate | 10/10 | 3.977 s | 4.042 s | 0.955 s |

All 20 assembled PNGs had exactly the same SHA-256:
`1f40ec9ad44edc2b237834fcfd699b9ceabe281d4e2a8fa343457dce36a205f1`.
All capture-state, marker, fixed-element and restoration checks passed. The first
CPU profile in each cohort attributed 779 ms versus 30 ms of self time to the
mutation observer callback. A separate real-capture regression reduced wrapper
subtree scans from 750 to five for the same five bursts of 150 attribute writes,
while retaining identical correctness checks.

In these samples the final candidate was 10.6% faster at the median and 13.4%
faster at the observed p95. Native screenshot requests were at least 550.1 ms
apart with zero quota rejections, and still ran serially. The quota correction
starts the existing 550 ms interval when the native screenshot is requested,
rather than adding a fresh interval after readback finishes. A separate real
two-window regression verifies serialization, actual pixel output, minimum call
spacing, and the removal of that redundant post-readback wait.

This is a measured improvement on this fixture and machine, not a universal
website or Windows speed claim. The earlier content-only candidate reduced CPU
work but did not improve end-to-end time; its raw results are retained as
`candidate-cpu-only-*`. An initial baseline cohort overlapped other browser tests;
its results are retained as `baseline-initial-overlap-*` and excluded here.
Screenshot quota waits, compositor readback and scheduler variance still affect
total time. Current public-page results are kept in the evidence artifact rather
than used as a hard performance guarantee.

The `matched-capture-cohorts` job in the `browser-extension` GitHub workflow repeats ten attempts for each cohort on
Windows and Linux on extension pull requests, and the workflow can also be
started manually. Its two current-UI builds differ only in `src/content.js` and
`src/background.js`, with the baseline files pinned to commit
`3e50145d782db99637a5d23d463950fc84005d40`.
Windows uses the same software-rendering test flags for both cohorts; Linux uses
default flags. Each job publishes timings, exact PNG-hash comparison, restoration,
traces and CPU profiles. It fails for incorrect pixels or incompatible cohorts;
it records timing regressions without hiding or imposing a noisy runner-wide
speed threshold. This workflow does not change production browser graphics.
