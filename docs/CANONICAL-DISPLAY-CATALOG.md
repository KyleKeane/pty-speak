# Canonical Display Catalog

**Status:** Cycle 33 working catalog
**Drafted:** 2026-05-09
**Snapshot:** 2026-05-09
**Companion to:** [`docs/rfc/0001-linear-text-substrate.md`](archive/pre-cycle-45/0001-linear-text-substrate.md)
**Authority:** [`docs/CORE-ABSTRACTION-BOUNDARY.md`](CORE-ABSTRACTION-BOUNDARY.md) §5

## Front matter

This catalog specifies the **three exemplar canonical displays** that pty-speak commits to as a working seed taxonomy: raw text, interactive list, form with text input. It also names a set of extension points (severity alert, indeterminate progress, command-output-tuple wrapper, plus Tier-2 and Tier-3 deferred primitives) without locking their specs.

The three-exemplar scope is deliberate. Per the maintainer's 2026-05-09 directive — *"we just need two or three simple canonical examples to get us through the pipeline"* — the catalog avoids premature taxonomy lock-in. Future cycles promote extension points to exemplars when concrete workloads demand them.

### Sources surveyed

- [`docs/research/Output-paradigms.md`](archive/pre-cycle-45/research/Output-paradigms.md) — primary source (849 LOC). Six Tier-1 primitives surveyed in depth; this catalog lifts three with attribution.
- [`docs/research/emission-paradigms.md`](archive/pre-cycle-45/research/emission-paradigms.md) — secondary source (174 LOC). Cited for live-region detection + cadence parameters that affect each exemplar's update cadence.
- [`docs/CORE-ABSTRACTION-BOUNDARY.md`](CORE-ABSTRACTION-BOUNDARY.md) §5 — authoritative for the three-exemplar framing; this catalog provides the full per-primitive treatment.
- [`docs/PANE-MODEL.md`](PANE-MODEL.md) — pane catalog; the CommandOutputTuple wrapper for raw-text in the history sub-pane (per [PR #236](https://github.com/KyleKeane/pty-speak/pull/236) doc tweak) is referenced from §1.6 here.

### Structure

Each exemplar gets a §X with the following subsections:

- **Content profile** — what concrete workloads land in this primitive.
- **UIA control type** — Windows accessibility surface (NVDA + JAWS + Narrator's primary integration).
- **Required UIA pattern providers** — what `IXxxxProvider` interfaces the host must implement.
- **ARIA role analog** — for parity with the WebView-based docs / help surface.
- **NVDA reading pattern** — concrete browse-mode / focus-mode behavior + quick-nav letters.
- **JAWS virtual cursor** — JAWS-specific behavior (forms-mode triggers, keystroke equivalents).
- **Narrator behavior** — Narrator-specific Scan Mode + landmarks behavior.
- **Interaction contract** — what keystrokes the user issues; what the primitive does in response.
- **Substrate consumption** — how the primitive is materialised from the linear-text substrate via detector annotations.
- **Update cadence** — live polite vs. assertive vs. snapshot; debounce / coalesce contracts.
- **Output channel routing** — which channels are native fits vs. require translation.
- **Example terminal scenarios** — three concrete real-world workloads that trigger this primitive.

### Literal-language convention

Throughout this catalog: *select / mark / announce / present / read / focused / current*. Sight metaphors *highlight / view / show* are eliminated, per the Output-paradigms.md Front Matter discipline (and CORE-ABSTRACTION-BOUNDARY.md's adoption of literal-language vocabulary).

-----

## §1 Exemplar 1 — Raw Text

The append-mostly canonical display for assistant prose, command output, and long-running tool stdout. Single highest-traffic primitive in the Claude Code workload.

### 1.1 Content profile

Token-streamed assistant prose with interleaved semantic blocks (thinking, tool-use markers, code fences); shell stdout / stderr bursts; `npm install` log lines; `git push` progress chatter; `cargo build` warnings as they emit. Concrete example: "I'll read the file and then…" arrives as a stream of UTF-8 grapheme clusters, with embedded `<thinking>` and `<tool_use>` regions detected by upstream pipeline stages and re-emitted as named TextPattern annotations.

### 1.2 UIA control type

`ControlType.Document` (control & content view, focusable=true). `LocalizedControlType = "terminal log"`. `LiveSetting = AutomationLiveSetting.Polite`. `ItemType = "stream"`.

### 1.3 Required UIA pattern providers

- **`ITextProvider`** + **`ITextProvider2`** exposing `DocumentRange` and `RangeFromPoint`.
- **`ITextRangeProvider`** supporting `Move` / `MoveEndpointByUnit` for `TextUnit.Character / Word / Line / Paragraph / Document`.
- `GetAttributeValue` returns `UIA_FontWeightAttributeId` / `UIA_BackgroundColorAttributeId` / `UIA_ForegroundColorAttributeId` / `UIA_IsHiddenAttributeId` / a custom `pty.SemanticBlock` attribute (`assistant` / `thinking` / `tool-use` / `code` / `diff`). Follow the `microsoft/terminal` `UiaTextRangeBase.GetAttributeValue` / `FindAttribute` precedent (microsoft/terminal PR #10336).
- **Recommended:** `IValueProvider` (read-only) for legacy MSAA bridging.

### 1.4 ARIA role analog

`role="log"` on the wrapping container with `aria-live="polite"`, `aria-atomic="false"`, `aria-relevant="additions text"`, `aria-busy="true"` while the model is still streaming.

Each interleaved semantic block is wrapped as `role="article"` (assistant turn) or `role="group"` with `aria-roledescription` of `"thinking block"` / `"tool use"` / `"code block"`.

Reference: WAI-ARIA 1.2 `role=log`, MDN log-role doc.

### 1.5 NVDA reading pattern

**Browse mode:** entered automatically on the Document by NVDA's UIA terminal handler; arrow keys read by line; `NVDA+UpArrow` / `NVDA+DownArrow` for say-all from caret. Quick-nav letters (exposed via embedded TextPattern annotations):

- `h` / `Shift+h` — next/previous semantic-block boundary (level-2 headings map to Claude Code turn boundaries; level-3 to thinking / tool-use sub-blocks).
- `o` / `Shift+o` — next/previous embedded object (tool-use blocks surface as embedded objects).
- `k` / `Shift+k` — next/previous link (file-paths and URLs detected upstream).
- `g` / `Shift+g` — next/previous graphic (ASCII art / diff hunk markers when graphic-detector profile is active).

**Focus mode:** entered on `Tab` into the prompt input; while in focus mode all keystrokes pass to the PTY.

`Insert+F7` (Elements List) reveals: Headings (turn boundaries), Links (file paths, URLs), Landmarks (Tier-1 channel regions). NVDA's element-list dialog supports type-ahead filtering.

**Review cursor:** object review treats the Document as one navigator object; document review is scoped to the entire scrollback. `BrailleExtender`'s "automatic review cursor tethering in terminal role" must be honored — emit `terminal` role hint via a custom UIA property so the add-on engages.

### 1.6 CommandOutputTuple wrapper (history sub-pane)

Per the post-Cycle-31a doc tweak ([PR #236](https://github.com/KyleKeane/pty-speak/pull/236)) and [`docs/PANE-MODEL.md`](PANE-MODEL.md) "Shell pane internal structure" §3, the history sub-pane consumes raw-text exemplars wrapped as **CommandOutputTuple** primitives. Each tuple wraps the submitted command, the output stream, and the exit code as a single semantically-navigable region.

- **UIA wrapper:** `ControlType.Group` with `LocalizedControlType = "command/output pair"`, `Name = "<truncated command>"`, `HelpText = "exit code <N>, <duration>"`, `LiveSetting = AutomationLiveSetting.Off` (the inner raw-text exemplar handles live update).
- **Children:** `ControlType.Edit` (command, read-only after submission via `IValueProvider`), `ControlType.Document` (output; full raw-text exemplar), `ControlType.Text` (exit-code element with `Name = "Exit code 0"` / `"Exit code 127"`, `ItemStatus = "success" | "failure"`).
- **Quick-nav contract:** `h` / `Shift+h` (command boundaries via level-2 headings), `o` / `Shift+o` (output blocks via embedded-object navigation), `Alt+Up` / `Alt+Down` (tuple boundaries, parallel to VS Code's `editor.action.marker.next`).
- **Substrate:** the linear-text producer's high-water-mark commits at prompt-boundary fires (per RFC 0001 §7); the wrapper is constructed from the resulting SessionTuple's `CommandText` + `OutputText` + `ExitCode` fields.

### 1.7 JAWS virtual cursor

The `ControlType.Document` with `IsContentElement=true` activates JAWS's virtual buffer (PC Cursor in document-mode) automatically. `INSERT+F6` lists turn-boundary headings; `INSERT+F7` lists detected file-path links; `INSERT+F5` is empty unless a form-input primitive is also present; `INSERT+CTRL+R` lists pty-speak's exposed Landmark regions (input prompt, scrollback, status). Smart Navigation announces the role and content of each element as the virtual cursor crosses it. JAWS Auto Forms Mode triggers when focus enters an embedded `Edit` (the prompt). `NumPad +` / `NumPad -` (Forms Mode toggle) returns control to virtual buffer.

### 1.8 Narrator behavior

Document control type is fully supported. Scan Mode (`Caps+Spacebar` or `Insert+Spacebar`) enables `H/L/K/T/I/B/F` style navigation and arrow-key reading. `Narrator+F6` lists headings, `Narrator+F7` lists links, `Narrator+F5` lists landmarks. Narrator's reading depends heavily on TextPattern (per Chromium docs) — the host's `ITextProvider2.DocumentRange` must be complete and traversable end-to-end without per-character cross-process round-trips (use `GetText(-1)` precedent).

### 1.9 Interaction contract

- **Read keys:** `Up` / `Down` (line), `Left` / `Right` (character; word with `Ctrl`), `Home` / `End` (line bounds), `Ctrl+Home` / `Ctrl+End` (scrollback bounds), `PageUp` / `PageDown` (viewport).
- **Search:** `Ctrl+F` opens an in-region find-bar (separate primitive — `Find` uses Exemplar 3's form contract).
- **Type-ahead is NOT consumed in browse mode;** it reaches the PTY only in focus mode.
- **Activation:** `Enter` on a detected file-path link emits an `Invoke` event consumable by upstream tooling (e.g., open-in-editor pathway).

### 1.10 Substrate consumption

**Linear-text substrate (post-RFC 0001).** The detectors stage tags substrate runs as `assistant-prose | thinking | tool-use | code | diff | command-echo | stdout | stderr` and emits append events; the raw-text exemplar's UIA Document reads from this annotated event store, never from the raw screen-cell substrate. **Substrate inversion:** the screen-cell grid is *secondary* and reconstructed only for the WPF visual channel.

### 1.11 Update cadence

Live, polite. Per-token announcement is **forbidden**. Coalesce appends with the producer's `live_region_debounce_ms` (250 ms default per RFC 0001 §5.2) keyed on word-boundary detection. Emit `UIA_Text_TextChangedEventId` after each coalesced append. Raise an explicit `LiveRegionChanged` event with `AutomationLiveSetting.Polite` only when (a) a complete sentence has been emitted *and* (b) no further bytes have arrived for ≥ 200 ms (lifted from research §1.1; close to the producer's `idle_quantum_ms` of 150 ms). Boundary notifications (block start/end) fire via `UiaRaiseNotificationEvent(NotificationKind.ItemAdded, NotificationProcessing.CurrentThenMostRecent)`.

### 1.12 Output channel routing

- **NVDA UIA:** native fit (Document + TextPattern is the canonical NVDA terminal contract).
- **Self-voicing TTS:** primary fit; pty-speak's TTS pathway can subscribe to the same coalesced append events with no translation.
- **Earcon:** semantic-block-boundary earcons (one per kind: assistant, thinking, tool-use, code, diff). Brewster timbre/rhythm guidelines apply, BUT per [`CONTRIBUTING.md`](../CONTRIBUTING.md) line 200's tighter empirical bounds (frequencies must be either below 180 Hz or above 1.5 kHz to stay out of the speech band).
- **Refreshable braille:** native fit via TextPattern (NVDA's braille subsystem follows the review cursor; `BrailleExtender` adds terminal-mode tethering). LibLouis tables apply.
- **Spatial audio:** optional — render different `SemanticBlock` kinds at distinct azimuths via Windows `ISpatialAudioClient` with HRTF (e.g., assistant at 0°, thinking at +30° elevation, tool-use at -30° azimuth left). Defer behind a feature flag.
- **FileLogger:** native fit (annotated events serialize to NDJSON).
- **WPF visual:** native fit (the screen-cell grid drives the WPF surface; this is the one consumer that legitimately reads from the grid post-substrate-inversion).

### 1.13 Example terminal scenarios

1. Claude Code streaming an assistant turn with embedded `<thinking>` block, then a `tool_use` for `Edit`, then resumed prose.
2. `npm install` emitting hundreds of `added X packages` lines.
3. `cargo build --verbose` long-running compiler output with interleaved warnings (stderr) and progress (stdout).

-----

## §2 Exemplar 2 — Interactive List

Vertical or horizontal selection menu, no embedded prose. Distinguished from raw text by **selection semantics** — the user's keystroke commits a choice rather than appending to a stream.

### 2.1 Content profile

`fzf` selection UI; Claude Code `/model` slash-command list; `gh pr list` interactive picker; `git checkout` branch picker; PowerShell `Out-GridView -PassThru`; Ink `SelectInput` components. Also: cmd `choice /C YN` and `apt`'s `Continue? [Y/n]` (these escalate to the ConfirmationPrompt hybrid, see §2.14).

### 2.2 UIA control type

- **Container:** `ControlType.List` implementing `ISelectionProvider`. `IsKeyboardFocusable=true`. `AutomationProperties.Name` set to a derived label (e.g., "Branch picker"). For multi-select pickers (`fzf -m`), `CanSelectMultiple=true`.
- **Each item:** `ControlType.ListItem` implementing `ISelectionItemProvider`; `IInvokeProvider` is **not** implemented (selection ≠ activation in this exemplar; activation goes through a separate Enter-key dispatch routed by the prompt-substrate detector).
- `AutomationElementIdentifiers.PositionInSetProperty` and `SizeOfSetProperty` populated from the list cardinality.

### 2.3 Required UIA pattern providers

- **`ISelectionProvider`** on the container (`CanSelectMultiple`, `IsSelectionRequired`, `GetSelection`).
- **`ISelectionItemProvider`** on each item (`Select`, `AddToSelection`, `RemoveFromSelection`, `IsSelected`, `SelectionContainer`).
- For ConfirmationPrompt hybrid (§2.14): the items also implement `IInvokeProvider` (single-key activation).

### 2.4 ARIA role analog

`role="listbox"` on the container with `aria-orientation="vertical"` (or `horizontal` per layout), `aria-required="true"`, single-select via `aria-selected`. For multi-select: `aria-multiselectable="true"`. Each `role="option"` carries `aria-keyshortcuts` if a keyboard shortcut exists.

References: WAI-ARIA 1.2 listbox pattern; APG Listbox example.

### 2.5 NVDA reading pattern

Focus mode auto-entered (NVDA's "form field" detection). `Down` / `Up` (or `Right` / `Left` for horizontal) move selection. First-letter type-ahead jumps by `Name`. `Insert+F7` Elements List shows options under "Form fields" / "Lists." Quick-nav `i` (list item) and `l` (list) jump out and back. Quick-nav `b` does **not** apply (these are listitems, not buttons).

### 2.6 JAWS virtual cursor

JAWS auto-forms-mode triggers on focus into the listbox. PC Cursor announces the list role and current item, then "1 of N." `INSERT+F5` (Form Fields List) includes the options.

### 2.7 Narrator behavior

Scan Mode auto-disables when focus enters the listbox (Narrator's "form field" detection). Narrator announces the list role and the focused item, then "1 of N."

### 2.8 Interaction contract

- **Arrow keys** move selection within the listbox.
- **First-letter type-ahead** (`Y`, `A`, `N`) selects-and-activates immediately when option labels are unambiguous (the Claude Code pattern).
- **`Enter`** activates the selected option.
- **`Escape`** dismisses the list (cancels selection); the substrate detector emits `SelectionDismissed`.
- **`Tab`** is **trapped** within the prompt while it is active for the ConfirmationPrompt hybrid; pure SelectableList lets Tab through to the next element.
- For the ConfirmationPrompt hybrid: focus is moved into the prompt on appearance and restored on dismissal.

### 2.9 Substrate consumption

**Derived semantic-event store.** The `SelectionDetector` (Cycle 29a substrate; `src/Terminal.Core/SelectionDetector.fs`) consumes the linear-text producer's bytes (post-Cycle 35 inversion) and emits `SelectionShown` / `SelectionItem` / `SelectionDismissed` semantic events. The `SelectionProfile` (Cycle 29b consumer; `src/Terminal.Core/SelectionProfile.fs`) translates these events into NVDA announcements and UIA structure-changed events.

For Claude Code's tool-use confirmations: a `ConfirmationDetector` (future cycle) detects the Ink Select component output pattern. For cmd `choice` / PowerShell `PromptForChoice`: pattern-matched as the prompt seam fires. The exemplar is constructed from the structured detection event, never re-parsed from cells.

### 2.10 Update cadence

**Snapshot-on-render** with selection-follows-focus events on each arrow press.

- One `NotificationKind.Other` + `NotificationProcessing.ImportantAll` event on appearance (see §2.14 for ConfirmationPrompt's stronger `ImportantAll` semantics).
- One `NotificationKind.ActionCompleted` event on dismissal.
- `SelectionItemPatternIdentifiers.ElementSelectedEvent` per arrow press.
- `AutomationElementIdentifiers.StructureChangedEvent` when the underlying list mutates (e.g., fzf type-ahead filtering).

### 2.11 Output channel routing

- **NVDA UIA:** native fit.
- **Self-voicing TTS:** native fit (the appearance announcement is the trigger).
- **Earcon:** boundary tick on each selection-shift; **mandatory** "decision required" earcon on appearance for ConfirmationPrompt hybrid + confirming earcon on dismissal. Brewster timbre/rhythm guidelines, but constrained by CONTRIBUTING.md's <180 Hz / >1.5 kHz speech-band rule.
- **Refreshable braille:** native via UIA TextPattern propagation (the focused option name and selection state).
- **Spatial audio:** optional — render the prompt earcon at center-front (0°) for spatial salience.
- **FileLogger:** native — record question, options, default, chosen.
- **WPF visual:** native.

### 2.12 Example terminal scenarios

1. `fzf` invocation: type-ahead filtering as the user types; arrow keys move within the filtered set; `Enter` commits.
2. Claude Code `/model` slash-command picker: 4-5 model names; first-letter type-ahead jumps; `Enter` switches model.
3. `gh pr checkout` interactive PR picker: dynamic list from API; selection commits the checkout.

### 2.13 Selection thresholds (Cycle 32a TOML configurability)

The detector's four tunables are now overridable via `[profile.selection]` in `config.toml` per Cycle 32a (PR #238): `highlight_detection_threshold_ms` (default 100), `dismissal_grace_ms` (default 150), `keystroke_correlation_window_ms` (default 250), `min_confidence` (`heuristic_sgr` | `heuristic_sgr_with_keystroke`, default `heuristic_sgr`). Documented in [`docs/USER-SETTINGS.md`](USER-SETTINGS.md) "Selection prompt thresholds and confidence modes".

### 2.14 ConfirmationPrompt hybrid (related primitive — future)

When the interactive list carries assertive-notification semantics — Claude Code tool-use confirmation, `apt`'s `Continue? [Y/n]`, cmd `choice /C YN` — the catalog notes a hybrid alert+selection pattern that combines this exemplar's selection contract with `role="alertdialog"` modality.

- **UIA:** outer `ControlType.Pane` with `LocalizedControlType = "confirmation"`, `LiveSetting = AutomationLiveSetting.Assertive`. Embedded list per §2.2-§2.3.
- **ARIA:** outer `role="alertdialog"` with `aria-modal="true"`; inner `role="listbox"` per §2.4.
- **Notification on appearance:** `UiaRaiseNotificationEvent(NotificationKind.Other, NotificationProcessing.ImportantAll, displayString = "Confirmation required: <prompt>", activityId = "pty-speak.confirmation.<id>")`. **`ImportantAll` (not `ImportantMostRecent`)** because confirmations must not be dropped under burst.
- **Default option** exposed via `AutomationElementIdentifiers.IsDefaultProperty` (or `aria-current="true"` for ARIA).

Implementation timing: future cycle once both halves of the hybrid (interactive list ✓ shipped via Cycle 29a/29b/32a; assertive-notification UIA peer pending) are stable. Currently Stage 8e-A ships the substrate (Cycle 29a + 29b) + config (Cycle 32a); Stage 8e-B (UIA listbox peer) is the next plan-mode pass.

-----

## §3 Exemplar 3 — Form with Text Input

Field-with-label primitive for shell prompts that require typed input.

### 3.1 Content profile

PowerShell `Read-Host` chains; cmd `set /p var=Prompt: `; password prompts (`sudo`, `ssh`, `git push` over HTTPS); future Claude tool-use approve-with-comment forms (e.g., "approve and tell Claude what to do" option in the §2.14 hybrid). Multi-field forms compose multiple Edit children inside a single Group.

### 3.2 UIA control type

- **Container:** `ControlType.Group` with `LocalizedControlType = "form"`, `Name` set to a derived label (e.g., "Login form"), `LiveSetting = AutomationLiveSetting.Off` (the inner Edit handles live update).
- **Each field:** `ControlType.Edit` with `IValueProvider` (read-only after submission via `IsReadOnly=true`). For password fields: `ControlType.Edit` with `IsPassword=true` (UIA exposes the field but not its content).

### 3.3 Required UIA pattern providers

- **`IValueProvider`** on each Edit (`Value`, `IsReadOnly`, `SetValue`).
- Optional: **`ITextProvider`** for multi-line edits with caret navigation (defer to a future cycle if the workload demands).

### 3.4 ARIA role analog

`role="form"` on the container with `aria-label` set to the form description. Each field uses `role="textbox"` with `aria-readonly="true"` once submitted; `aria-required="true"` if mandatory; `aria-describedby` referencing any hint text.

For password fields: `role="textbox" aria-autocomplete="none" aria-roledescription="password"` (the actual input type=password attribute lives in the form-rendering layer; this is for the UIA peer to declare).

### 3.5 NVDA reading pattern

Focus mode auto-entered (NVDA's "edit field" detection). `Tab` between fields; `Shift+Tab` reverse. `Enter` submits. Within an Edit: arrow keys navigate caret; `Home` / `End` for line bounds; `Ctrl+Home` / `Ctrl+End` for field bounds.

`Insert+F5` Form Fields List enumerates the form's edits.

### 3.6 JAWS virtual cursor

JAWS auto-forms-mode on focus into the Edit. PC Cursor announces field role + current value. `INSERT+F5` Form Fields List enumerates fields. `NumPad +` / `NumPad -` toggles forms-mode.

### 3.7 Narrator behavior

Scan Mode auto-disables when focus enters an Edit. Narrator announces field role + value + position (e.g., "Username, edit, 1 of 3").

### 3.8 Interaction contract

- **`Tab` / `Shift+Tab`** between fields.
- **`Enter`** submits the form.
- **`Escape`** dismisses (where the underlying shell supports cancellation).
- **Within an Edit:** standard caret-navigation keys.
- **For password fields:** content is announced as "password edit, value protected" (literal-language convention; do NOT announce the typed characters).

### 3.9 Substrate consumption

**Linear-text substrate + future `InputPathway`** (Cycle 38). The `FormProfile` detector (Cycle 38a) consumes linear-text bytes and emits `FormPrompt` semantic events when it recognises a `Read-Host` chain or password prompt; the UIA peer (`TerminalFormAutomationPeer`, Cycle 38b) translates the semantic events into the UIA Edit + Group structure.

Until Cycle 38, plain-cmd typed-input echo is handled by Stage 6's typed-echo coalescer (already shipped) — the form exemplar is named in the catalog but its detector + UIA peer arrive in a future cycle.

### 3.10 Update cadence

- **Live polite for typed-echo:** the user's typed bytes arrive via Stage 6's typed-echo coalescer; each character (or coalesced word) raises a `UIA_Text_TextChangedEventId` on the focused Edit.
- **Snapshot at submit:** on `Enter`, the form's `IsReadOnly` flips to `true`; the submission is announced via `NotificationKind.ActionCompleted`.
- **Password redaction is mandatory** in all channels except the WPF visual surface (which renders bullets / asterisks per the host's password-field convention).

### 3.11 Output channel routing

- **NVDA UIA:** native fit.
- **Self-voicing TTS:** native fit (with redaction for password fields).
- **Earcon:** field tick on `Tab` between fields; submit confirmation earcon on `Enter`.
- **Refreshable braille:** native via UIA TextPattern (with redaction for password fields — braille displays "•" per cell or similar).
- **Spatial audio:** not applicable for forms (focus-driven; spatial cues add noise).
- **FileLogger:** native (with redaction for password fields — log "[password redacted]" not the bytes).
- **WPF visual:** native (password fields render as bullets per host convention).

### 3.12 Example terminal scenarios

1. `git push origin main` over HTTPS → password prompt → typed bytes redacted in all channels except WPF visual.
2. PowerShell `Read-Host -Prompt 'Enter your name'` → single-field form → typed-echo polite live region.
3. Future Claude tool-use approve-with-comment: `[Yes] [No] [Approve and comment...]` → selecting the third option opens a single-field form for the comment text.

### 3.13 Implementation timing

Cycle 38 (input framework cycle). Spec lifted from [`docs/CORE-ABSTRACTION-BOUNDARY.md`](CORE-ABSTRACTION-BOUNDARY.md) §5 Exemplar 3 + extended here. The exemplar is named in the catalog for completeness; the detector + UIA peer arrive when Cycle 38 ships.

-----

## §4 Extension points (named, not specified)

The catalog names these as future canonical displays without locking the spec. Each is a 1-paragraph description; full per-primitive specs land in future cycles when concrete workloads demand them.

### 4.1 SeverityAlert

Single-shot interruptive announcement of error / warning / fatal. `role="alert"` (implicit `aria-live="assertive"` + `aria-atomic="true"`). Distinguish severity tiers via `aria-roledescription="error"` / `"warning"` / `"fatal"`. Detector consumes ANSI-red + lexical patterns (`error:` / `warning:` / `panic` / `fatal`) + stderr stream origin. Mandatory severity-tier earcons (distinct timbres per tier; rhythmic motif per Brewster, constrained to <180 Hz / >1.5 kHz). Coalescing: collapse identical severity messages within 500 ms into one notification with displayString suffix " (×N)" to avoid flooding (compiler error storms).

Concrete workloads: `tsc` diagnostic with severity, Rust panic, `git push` rejection, `npm ERR!` lines, `cargo build` warnings.

### 4.2 IndeterminateProgress

Spinner-class display: ongoing activity with no completion estimate. Designed deliberately **not** as `role="progressbar"` (which requires `aria-valuenow` and screen-reader percent reporting; per-frame value-change events are catastrophic for screen-reader users).

- **UIA:** `ControlType.Custom` with `LocalizedControlType = "activity indicator"`, `LiveSetting = AutomationLiveSetting.Off` (critical).
- **ARIA:** `role="status"` with `aria-busy="true"` + `aria-atomic="true"` + descriptive `aria-label`.
- **No `IRangeValueProvider`. No per-frame UIA events.** The frame animation is purely a WPF-visual concern and must not propagate through the UIA tree.
- **Lifecycle notifications:** `UiaRaiseNotificationEvent(NotificationKind.ActionCompleted, NotificationProcessing.CurrentThenMostRecent, "Claude is thinking", "<activityId>")` on **start**, with the same `activityId` reused on **end** with `displayString = "Done."`. `CurrentThenMostRecent` so a new "thinking" event supersedes a stale one.
- **Earcon:** "activity start" + "activity end" earcons; optional looped low-volume background drone while busy (volume below speech threshold; user-disable-able).

Concrete workloads: Claude Code thinking (`⠋ ⠙ ⠹` braille spinner), `npm install` rotating spinner, `git clone` `Receiving objects:` chatter, `cargo build` "Compiling crate-name" status.

The Cycle 29b regression (~80 spinner announcements per Claude turn) motivates getting this right out of the gate when the future cycle ships.

### 4.3 CommandOutputTuple (wrapper for raw-text in history sub-pane)

Already documented in §1.6 as a wrapper primitive. Named here as a discrete extension point because its UIA contract (`ControlType.Group` wrapping `Edit` + `Document` + `Text` for exit code) is distinct from the raw-text exemplar's contract. Its substrate is the linear-text producer's high-water-mark commits; its detector is `HeuristicPromptDetector` (already shipping). The full implementation is the [`PANE-MODEL.md`](PANE-MODEL.md) "Shell pane internal structure" §3 history sub-pane navigation contract.

### 4.4 Tier-2 deferred

These are named in the research doc as Tier-2 primitives (ship within 12 months, post-Cycle 38). Each gets a future canonical-display catalog entry when concrete workloads demand:

- **DiffView** — unified-diff and side-by-side patch displays. Concrete workloads: `git diff`, Claude Code Edit-tool previews, `delta` / `diff-so-fancy` colorised output. UIA: `ControlType.Group` wrapping line-by-line `ControlType.Text` children with `ItemStatus = "added" | "removed" | "context"`; earcon distinct timbres for added vs. removed runs.
- **CodeBlockWithSyntaxStructure** — syntax-aware code-fence rendering. Concrete workloads: Claude Code's code blocks, `bat`'s syntax-highlighted output, `man` page code samples. UIA: `ControlType.Document` + custom `pty.SyntaxToken` attribute on TextRange; computer-braille tables for braille channel.
- **DeterminateProgress** — known-completion-percentage activity. Concrete workloads: `wget` / `curl --progress-bar`, `apt install` percentage, `dd` block-count progress. UIA: `ControlType.ProgressBar` + `IRangeValueProvider` (now appropriate because completion is known); rising-sweep earcon at milestones (25%, 50%, 75%, 100%).
- **FormInputGroup** — specific form variants beyond the generic Exemplar 3. Concrete workloads: multi-field login forms, structured questionnaires, Claude Code's future tool-use approve-with-comment dialog. Builds on Exemplar 3's substrate; adds field-grouping semantics.
- **TabularDataDisplay** — rows × columns with header semantics. Concrete workloads: PowerShell `Get-Process` formatted output, `kubectl get pods`, SQL query results. UIA: `ControlType.Table` + `ControlType.DataItem` per cell + `ITableProvider`; row-vs-column-tick earcons for navigation.

### 4.5 Tier-3 deferred (research / defer)

Named for future research. No commitment to ship:

- **HierarchicalTreeDisclosure** — multi-level expand / collapse trees. Concrete workloads: `npm ls`, `git log --graph`, `tree`. UIA: `ControlType.Tree` + `IExpandCollapseProvider`; depth-as-pitch earcons.
- **SpatialAudioStatusField** — continuous low-level status conveyed through HRTF-spatialized ambient tones (concurrent build status, battery, network, model token-rate). UIA: non-focusable `ControlType.Custom` with `LiveSetting=Off`; spatial audio is the primary channel (research-grade; defer to v2).
- **MultiRegionFocusManager** — composite primitive coordinating multiple concurrent canonical displays in a single terminal viewport (split panes, Claude Code's prompt + assistant + status). Defines the focus-arbitration contract between regions. UIA: each region exposes its own provider tree with `LandmarkType` set; `Ctrl+F6` cycles regions (mirroring the Windows pattern).

-----

## §5 Output Channel Routing Matrix

Legend: ● native fit · ◐ requires translation · ○ not applicable

| Primitive                           | NVDA via UIA | Self-voicing TTS   | Earcon (WASAPI)     | Braille via UIA TextPattern | Spatial audio (HRTF) | FileLogger | WPF visual |
|-------------------------------------|--------------|--------------------|---------------------|------------------------------|----------------------|------------|------------|
| **Exemplar 1 — Raw Text**           | ●            | ●                  | ● (block-boundary)  | ●                            | ◐ (block-azimuth)    | ●          | ●          |
| **Exemplar 1 — CommandOutputTuple** | ●            | ●                  | ● (start / exit-code)| ●                          | ◐ (time-axis)        | ●          | ●          |
| **Exemplar 2 — Interactive List**   | ●            | ●                  | ● (boundary tick)   | ●                            | ○                    | ●          | ●          |
| **Exemplar 2 — ConfirmationPrompt** | ●            | ●                  | ● (decision earcon) | ●                            | ◐ (front-center)     | ●          | ●          |
| **Exemplar 3 — Form with Text Input** | ●          | ● (with redaction) | ● (field tick)      | ●                            | ○                    | ● (redacted) | ●        |
| **Extension: SeverityAlert**        | ●            | ●                  | ● (severity-tier)   | ●                            | ◐ (severity azimuth) | ●          | ●          |
| **Extension: IndeterminateProgress**| ●            | ● (start / end only)| ● (start / end + drone) | ◐ (description only)   | ◐ (drone azimuth)    | ●          | ● (frames) |

Self-voicing, refreshable braille, and spatial audio are **first-class peers** — every primitive defines a path through each channel even when a translation step is required. Earcons are mandatory for primitives where a non-speech indication materially reduces dual-task interference (per the Auditory Perception & Cognition 2023 meta-analysis; CONTRIBUTING.md's <180 Hz / >1.5 kHz speech-band exclusion is the production constraint).

-----

## §6 Versioning + maintenance

This catalog uses the snapshot model. Top-of-doc front matter carries `Snapshot: YYYY-MM-DD`. Trigger conditions for re-snapshot:

- An extension point promotes to an exemplar (would happen if a future cycle's NVDA validation surfaces a workload that doesn't fit the three exemplars + named extension points).
- A new channel is added to the routing matrix (e.g., a future Bluetooth-vibration channel for tactile feedback).
- The literal-language convention's vocabulary expands (a future cycle adds new approved terms or retires a sight metaphor).
- A maintainer-authored amendment (per spec-immutability discipline) updates a load-bearing contract — a dated "Amended in Cycle N" note is appended to the affected exemplar.

The three-exemplar scope is **deliberately non-extensible without maintainer authorization**. Future cycles that want to add an exemplar require ADR-style review per the spec-immutability discipline in [`CLAUDE.md`](../CLAUDE.md).

-----

## Cross-references

- [`docs/rfc/0001-linear-text-substrate.md`](archive/pre-cycle-45/0001-linear-text-substrate.md) — companion RFC; the substrate that feeds this catalog's primitives.
- [`docs/CORE-ABSTRACTION-BOUNDARY.md`](CORE-ABSTRACTION-BOUNDARY.md) §5 — three-exemplar framing authority.
- [`docs/PANE-MODEL.md`](PANE-MODEL.md) — pane catalog; CommandOutputTuple wrapper for raw-text in history sub-pane.
- [`docs/research/Output-paradigms.md`](archive/pre-cycle-45/research/Output-paradigms.md) — primary source for exemplar UIA / ARIA / NVDA contracts.
- [`docs/research/emission-paradigms.md`](archive/pre-cycle-45/research/emission-paradigms.md) — secondary source for cadence parameters affecting update cadence.
- [`docs/USER-SETTINGS.md`](USER-SETTINGS.md) — Cycle 32a `[profile.selection]` thresholds for Exemplar 2's detector.
- [`docs/STAGE-7-ISSUES.md`](archive/pre-cycle-45/STAGE-7-ISSUES.md) — Stage 7 + 8 issue tracker; closed `[output-selection]` row (Cycle 32a) and open Stage 8e-B (UIA listbox peer for Exemplar 2).
- [`CONTRIBUTING.md`](../CONTRIBUTING.md) — earcon frequency constraint (<180 Hz or >1.5 kHz) authoritative over research's wider Brewster guidance.
