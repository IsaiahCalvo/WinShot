# Settings truthfulness and accessibility slice

## Scope and protection

- Base: selector commit `6e36c4d495133b559ecd5438fdcd7b7d8c87152e`
- Branch: `feature/cleanshot-settings-truth`
- Worktree: `C:\Users\icalvo\.codex\worktrees\cleanshot-settings-truth-20260731\winshot`
- Installed WinShot, `main`, other worktrees, GitHub, and release state were not changed.
- Existing WPF/unpackaged architecture and dependencies were preserved.

## Visible controls verified against runtime behavior

| Settings surface | Visible control | Runtime consumer |
| --- | --- | --- |
| General | Start at login | `StartupRegistration.Apply/Reconcile` |
| General | Check for updates at startup | `App.StartupUpdateCheckAsync` |
| General | Export location | capture, overlay, editor, background, and recording save paths |
| General | Hide desktop icons while capturing | desktop capture guard and recording controller |
| General | After screenshot action | `App.HandleCapture` (`overlay`, `copy`, `save`, `edit`, `pin`, `background`) |
| General | Also copy after other actions | `App.HandleCapture` / deferred overlay copy |
| Shortcuts | 8 global shortcuts | `App.RegisterHotkeys` and matching command handlers |
| Recording | Cursor, click highlights, keystrokes, countdown, webcam position/size | recording options dialog and controller |
| Recording > Video | FPS, microphone, system audio | recording options dialog and MP4 controller |
| Recording > GIF | GIF FPS | recording options dialog and GIF controller |
| Screenshots | Format, HiDPI scale, self-timer | save pipeline, capture pipeline, self-timer flow |
| Screenshots | Freeze, crosshair mode, magnifier | `SelectorOptions` and both selector dialogs |
| Advanced | File-name template | `FileNamer` |
| Advanced | History age retention | `HistoryService` and `HistoryWindow` |
| Advanced | Remember All-In-One selection | `SelectorOptions.ForAllInOne` |
| Advanced | Join recognized text lines | `App.RunOcrToClipboard` / `OcrService` |
| About | update check, repository, logs | `UpdateService` and local/external open actions |

The 93 persisted model properties are exhaustively classified in `SettingsTruthCatalog`: visible and runtime-backed, stored but unavailable, or non-interactive runtime state. Tests fail if a model property is unclassified or appears in conflicting groups.

## Placebo controls removed from the working UI

Values remain serialized and are not overwritten by Settings saves.

- Sounds, shutter selection, tray visibility.
- The independent screenshot/recording action matrix. It implied multiple simultaneous actions, while runtime uses one screenshot action plus optional copy. It was replaced by those truthful controls.
- Quick Access configuration on this branch base. The separate Quick Access implementation slice can re-enable these controls when branches are integrated.
- Recording controls/timer/HiDPI/DND/remember area/dim screen/countdown toggle/max resolution/mono/video-editor/GIF quality/optimization/size.
- Screenshot sRGB conversion, border, automatic background, and screenshot cursor.
- Annotate settings page, pin styling, ask-for-name, Retina suffix, clipboard format, OCR language/link detection, and the no-op warning reset.
- 52 stored shortcut placeholders. Only the 8 actions actually registered by WinShot are bindable. Unknown older bindings remain in `ShortcutBindings` unchanged.

## Accessibility and migration

- Explicit automation names and help for visible interactive controls and all shortcut fields.
- Visible keyboard focus, logical tab order, resizable/scrolled layout, and system-color High Contrast fallback local to Settings.
- `Esc` and Cancel close without applying; Done is the default apply action.
- Crosshair migration maps legacy `ShowCrosshair=false` to the visible `Never` mode and keeps both persisted fields consistent on save.
- Self-timer now saves the visible combo selection directly; the old hidden field can no longer make the visible control a placebo.
- “Never” history wording changed to “No age limit” because the separate local item-count limit still applies.

## Evidence

- `docs/evidence/settings-truthfulness/after/normal-general.png` — SHA-256 `646B8F7F8E8AE1C761A95A9BAA89E2709719F16B4ECAFAF87987DA094C5A7615`
- `docs/evidence/settings-truthfulness/after/focus-general.png` — SHA-256 `BB533B698E0907248D95EC33410625D187E5A006425676A152A84E777C40BE51`
- `docs/evidence/settings-truthfulness/after/high-dpi-general.png` (150%) — SHA-256 `AF728589C81D76AD344F3B3CB896B936F519E3F2DBD99720C052443098497C3A`

All evidence uses the sanitized path `C:\Pictures\WinShot`.

## Verification

- Baseline full Release tests: 336/336 passed.
- Focused Settings/accessibility/migration/hotkey tests: 20/20 passed.
- Final full Release tests: 339/339 passed.
- Release build/publish: passed; existing unrelated warnings only (`FastQuickActionsWindow.Margin`, test nullable warning).
- Publish size: 342,416,226 -> 342,441,094 bytes; +24,868 bytes (+0.0073%).
- `git diff --check`: passed.
- Settings window was rendered and exercised in-process by the WPF render/window harness. The installed app was not launched, stopped, or replaced.

## Deferred integration notes

- When the Quick Access slice is integrated, re-audit and re-enable its now-runtime-backed configuration controls rather than blindly taking this branch's hidden state.
- Annotate, pin, recording polish, and OCR language controls should become visible only in the slice that consumes each stored property.
