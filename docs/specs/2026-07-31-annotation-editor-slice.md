# Annotation editor parity slice - 2026-07-31

## Scope and result

- Worktree: `C:\Users\icalvo\.codex\worktrees\cleanshot-annotation-20260731\winshot`
- Branch: `feature/cleanshot-annotation-editor`
- Base: `origin/main` at `87bc2b73aee68cf45702d4fd9ed4043b57734bd1`
- Source of truth: written CleanShot X 4.8.7 evidence contract `CS-ANN-001..010`.
- Result: the local-only WinShot editor now has the verified shell hierarchy and fixes the two identified correctness defects without removing any of its 17 tools.

## Acceptance delivered

1. `Ctrl+C` resolves to the existing source-resolution clipboard action. `Ctrl+Shift+S`, `Ctrl+Z`, `Ctrl+Y`, and `Ctrl+0` remain Windows-native editor commands.
2. Straight-arrow style (`Straight`, `Double`, or `Thin`) and counter mode (`Number` or `Letter`) survive `.winshot` save and reopen.
3. Top leading actions are Crop and resize, Add screenshot or image, and Add background. Primary tools follow Cursor, Rectangle, Filled rectangle, Ellipse, Line, Arrow, Text, Pixelate, Spotlight, Counter, Draw, Draw highlight.
4. Pan, Curved arrow, Blur, Eyedropper, transforms, resize, undo, and redo remain available in More. Emoji stays directly discoverable. No working tool or image action was removed.
5. Local Save as and Done remain at the top. Zoom, Drag me, Pin, Copy, and Fit and center remain at the bottom. No Share, Cloud, upload, or new online path was added.
6. Tool selection immediately reveals only its applicable controls while retaining WinShot's stronger fill, opacity, text-style, arrow-style, crop-ratio, and effect-strength choices. Default text size is 27 points.
7. Icon-only controls have explicit automation names and help text. Shared icon, pill, color, and round-action templates expose a visible keyboard focus ring.
8. The toolbar fits one row at the 980-DIP minimum and is covered at 100%, 125%, 150%, and 200% Windows scaling. The editor remains PerMonitorV2 and source-resolution rendering is unchanged.

The Mac contract left selection, pan, crop commit details, undo/redo behavior, exact tab order, output results, and light theme unverified. Existing WinShot behavior was preserved for those areas rather than inventing parity.

## Evidence

- Baseline inventory and acceptance skeleton: `artifacts/annotation-baseline/`
- Six visually checked before renders: `artifacts/annotation-baseline/renders/`
- Ten visually checked after renders: `artifacts/annotation-slice/renders/`
- 200% render: `artifacts/annotation-slice/renders/editor-after-shell-200pct.png` (`2304x1180`)
- Publish output: `artifacts/annotation-slice/publish/WinShot/`
- Local package: `artifacts/annotation-slice/publish/WinShot-win-x64.zip`

All renders were produced in-process from synthetic content. `WinShot.exe` and the installed app were not launched.

## Verification

All commands used the Dell SDK override:

`-p:CsWinRTWindowsMetadata=C:\Users\icalvo\.nuget\packages\microsoft.windows.sdk.net.ref\10.0.19041.56\winmd`

- Baseline full Release suite: 328 passed.
- Baseline focused editor set: 39 passed.
- Final focused editor set: 54 passed, 0 failed, 0 skipped.
- Final full Release suite: 343 passed, 0 failed, 0 skipped.
- Gated sanitized after-render run: 1 passed and produced ten PNGs.
- Final Release build: succeeded with 0 warnings and 0 errors.
- Self-contained ReadyToRun publish: succeeded. The publish warning is the pre-existing `FastQuickActionsWindow.Margin` warning.
- Package diff against a fresh `origin/main` publish: 480 files in both; no added or removed files; only `WinShot.exe`, `WinShot.dll`, and `WinShot.pdb` changed. Uncompressed output grew 71,980 bytes; ZIP grew 60,088 bytes (118,051,865 to 118,111,953 bytes).

## File overlap map

| Area | Files | Purpose |
| --- | --- | --- |
| Correctness | `EditorShortcut.cs`, `EditorWindow.Edit.cs`, `ProjectSerializer.cs` | Copy shortcut and non-destructive style reload |
| Shell and contexts | `EditorWindow.xaml`, `EditorWindow.xaml.cs`, `EditorShellContract.cs`, `AnnotationFactory.cs` | Verified order, More grouping, immediate controls, 27-point text default |
| Accessibility | `EditorWindow.xaml`, `EditorWindow.Styling.cs`, `Theme.xaml` | Automation names/help and visible focus |
| Behavior preservation | `EditorWindow.Tools.cs`, `EditorWindow.Edit.cs` | Existing tool semantics and local background handoff |
| Tests and renders | `EditorShortcutTests.cs`, `EditorShellContractTests.cs`, `ProjectSerializerTests.cs`, `ThemedWindowTests.cs`, `AnnotationEditorRenderHarness.cs` | Shortcut, fidelity, ordering, context, accessibility, DPI, and sanitized render coverage |

No dependency, cloud feature, background poller, or installed-app change was introduced.
