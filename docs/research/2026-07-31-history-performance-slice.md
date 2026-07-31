# WinShot history scalability slice

Date: 2026-07-31
Branch: `feature/cleanshot-history-performance`
Base: `b473cc1` (`Harden local capture history`)

## Outcome

History now opens a bounded first page of 200 cards even when the local library retains
5,000 files. `Load more` appends the next 200. This keeps the existing grouped WrapPanel
appearance without creating every card and decoding every thumbnail at startup.

The full history remains a lightweight in-memory file model, so the slice preserves:

- day grouping and Screenshots / Videos / GIFs filters;
- live watcher refresh and manual refresh;
- keyboard selection, visible focus, Enter, Delete, Space preview, and preview stepping;
- Copy, Edit, Pin, Open, Reveal, Delete, and drag-out;
- local-only storage and the existing retention limit.

Refreshes preserve the selected file and the number of pages already exposed. A superseded
or cancelled refresh builds its replacement off-screen and cannot partially clear the last
complete page. Deletes backfill the current page while more matching history exists.

No dependency, cloud service, proprietary asset, settings migration, or installed-app change
was introduced.

## Deterministic scalability coverage

The performance contract is model/count based instead of a brittle stopwatch threshold:

- 200 retained entries expose exactly 200 cards and no `Load more` state.
- 5,000 retained entries expose 200 cards initially, then 400 after one page request.
- the full count remains 5,000 while visual-card exposure stays bounded;
- a cancelled 5,000-entry replacement leaves the prior complete 200-entry page untouched;
- refresh preserves a selected item beyond the first page and the prior page depth;
- filtering keeps day-group metadata and delete backfills the visible page.

The sanitized large-state render harness also loads 5,000 real local fixture files into the
window, verifies `ItemsList` contains 200 entries, clicks `Load more`, and verifies 400.

## Verification

- Focused History tests: 29 passed, 0 failed.
- Full Release suite: 345 passed, 0 failed.
- Release build: succeeded, 0 warnings, 0 errors.
- Self-contained ReadyToRun `win-x64` publish: succeeded.
- Candidate `WinShot.dll` SHA-256:
  `4BA5D26989EC533952FE3EB00D399A869F014062CB2F5DFD6025959A47216953`.
- `git diff --check`: passed.
- Runtime UI verification: isolated WPF History render harness passed for normal and
  5,000-file states; the installed singleton was not stopped or replaced.

The same publish command was run against an archive of base `b473cc1` and the candidate:

| Measurement | Base | Candidate | Delta |
| --- | ---: | ---: | ---: |
| Published files | 480 | 480 | 0 |
| Total bytes | 342,417,554 | 342,436,122 | +18,568 (+0.0054%) |

## Sanitized evidence

Normal state:

- `docs/evidence/history-performance/20260731/normal/history-normal-idle.png`
- `docs/evidence/history-performance/20260731/normal/history-normal-keyboard-focus.png`

Large state:

- `docs/evidence/history-performance/20260731/large/history-large-idle.png`
- `docs/evidence/history-performance/20260731/large/history-large-keyboard-focus.png`
- `docs/evidence/history-performance/20260731/large/history-large-after-load-more.png`

Evidence SHA-256:

| File | SHA-256 |
| --- | --- |
| Normal idle | `F205F785015D01205136F15E5E7AA124AF43BBFBA1E7974BEEE7F96D5B624141` |
| Normal keyboard focus | `D09BF1BBDAFE4FDE66B5423BDFAB56A518A6F4A0ADF04F8EE101884ADE38ABB6` |
| Large idle, 200 of 5,000 | `9C7DEF0657ABAAC8A6E7AD1E5A979C5342D37754E6052F5C54F6D131C884DA52` |
| Large keyboard focus | `5E3917537823780E6A26197963D56DAA513AE54FF4F5466E95DCFC919A360919` |
| Large after load more, 400 of 5,000 | `9E628CB8B37ED1516F1FF7F06E46103A941E1EC53DE4620FF85265BFAD942D32` |

## Separate shared-file work

History disable, clear-all, expanded privacy messaging, retention preset UX, and settings
migration remain a separate slice because they cross shared Settings/App ownership. This
performance slice deliberately does not edit those files.

The installed WinShot folder, installed process, main checkout, remote repository, and release
state were not modified.
