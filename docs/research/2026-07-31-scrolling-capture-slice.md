# Scrolling capture parity and hardening slice - 2026-07-31

## Protected scope

- Worktree: `C:\Users\icalvo\.codex\worktrees\cleanshot-scrolling-20260731\winshot`
- Branch: `feature/cleanshot-scrolling`
- Base: `origin/main` at `87bc2b73aee68cf45702d4fd9ed4043b57734bd1`
- Only scrolling-owned source, tests, evidence, and this report changed.
- The installed app, main checkout, settings, shared selectors/theme, and recording code were not changed or stopped.
- No cloud service, dependency, proprietary asset, install, push, PR, merge, tag, or release was added.

## Outcome

This is a hardening pass, not a rewrite. Existing manual and automatic capture, vertical and horizontal modes, region selection, live preview, Done/Cancel/Escape, sticky-footer recovery, local-only output, and the 32,000px cap remain.

Changes:

1. The non-activating controls now have scoped keyboard commands: Alt+S Start, Alt+A Auto/Pause, Alt+D Done, and Alt+X Cancel. Escape still discards. The captured app keeps focus.
2. Start, Auto, Done, Cancel, status, and preview controls now expose accessible names/help. Status and recovery guidance raise a best-effort UI Automation announcement.
3. The controls and preview use DPI autoscaling; rounded chrome, monitor padding, and minimum button hit targets scale with DPI.
4. Horizontal auto-scroll now correctly recognizes that its first frame is ready. Its input ladder can fall back from horizontal wheel input to direct horizontal wheel messages before declaring the end.
5. Horizontal stitching now re-locks against the complete captured canvas after a miss. Reverse movement is treated as review, so columns are not duplicated; a fast no-overlap flick can recover after scrolling back.
6. Horizontal bitmap and preview growth are geometric and incremental. The old path allocated and copied the complete growing screenshot for every appended frame.
7. A 512 MiB final-canvas budget now guards both axes. Normal regions retain the 32,000px cap; unusually large cross-axis selections stop earlier with an explicit safe-memory message.

## Verification

| Gate | Baseline | Candidate |
|---|---:|---:|
| Scrolling-focused Release tests | 52 passed | 67 passed |
| Full Release suite | 328 passed | 343 passed |
| Release build | Passed | Passed |
| Release publish | Passed | Passed |
| Unpacked publish | 342,407,758 bytes | 342,430,866 bytes |
| Package delta | - | +23,108 bytes (+0.0067%) |

- Candidate `WinShot.dll` SHA-256: `B9BFD0409E6D5C2A9D2D30B5ECB9B135831E9F6BE7716541D416D7ECC1DD6992`
- Candidate `WinShot.exe` SHA-256: `3F770FE547228D065F2F6525A01F28B034E8969B7A76B728452E401F0F2A8680`
- Publish output is intentionally untracked under `artifacts\scrolling-candidate\publish\WinShot`.
- Publish emitted the existing `FastQuickActionsWindow.Margin` warning; this slice introduced no new warning.
- `git diff --check` passed.

New synthetic coverage includes slow horizontal stitching, fast no-overlap miss plus re-lock, reverse then forward without duplication, geometric buffer growth, memory limits, non-activating keyboard actions, accessible names/help, shortcut parsing, and 96/144/192 DPI metric scaling.

## Sanitized render evidence

The controls' visible hierarchy was intentionally preserved; this slice improves keyboard, accessibility, DPI behavior, and engine correctness. Therefore the 96-DPI before/after renders are pixel-identical, which is the expected visual regression result.

- Before: `docs/evidence/scrolling-capture/before/controls-ready.png`, `controls-capturing.png`, `controls-recovery.png`
- After: `docs/evidence/scrolling-capture/after/controls-ready.png`, `controls-capturing.png`, `controls-recovery.png`
- Render harness: `tests/WinShot.Tests/ScrollingChromeRenderHarness.cs`

The renders are created in-process from the controls only. They contain no desktop or user content.

## Explicit follow-up - not claimed by this slice

- Real Chrome/Edge, Electron, Excel/Office, RDP, elevated-window, inertial/lazy-loaded-page, and nested-scroller compatibility.
- Live 100/125/150/200% mixed-DPI and negative-coordinate multi-monitor behavior.
- Cross-monitor scrolling selection/chrome ownership; no behavior was invented here.
- Live Narrator/keyboard hardware verification and whether another application reserves one of the scoped Alt shortcuts.
- Real horizontal wheel injection/fallback verification. Synthetic tests cover stitching/recovery, not operating-system input routing.
- Sticky-header-specific removal; existing sticky-footer behavior is preserved.

The candidate executable was not launched because the protected installed WinShot instance owns the single-instance/command channel. Proving a candidate launch would require stopping or displacing that installed instance, which this slice explicitly forbids.
