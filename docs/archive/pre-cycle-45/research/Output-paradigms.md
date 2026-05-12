# Canonical Display Primitives for pty-speak

## Front Matter

### Project context

**pty-speak** is an F# / .NET 9 / WPF terminal emulator built screen-reader-native for blind and low-vision developers, MIT-licensed, pre-alpha, maintained by Dr. Kyle Keane (University of Bristol). The deterministic parser pipeline is **substrate → detectors → pathway routing → profiles → channels**, where *channels* are canonical display interfaces with reliable interaction contracts. Tier-1 workloads are Anthropic’s Claude Code (custom-fork Ink/React TUI atop a Yoga-layout reconciler emitting OSC/CSI/SGR streams across full-frame redraws), other Ink/React TUIs, and general `cmd.exe` / PowerShell usage.

### Sources surveyed (cited inline)

- Microsoft Learn — UI Automation control types overview (List, ListItem, Button, Document, Edit, Group, ProgressBar, DataItem) and System.Windows.Automation namespace docs.
- Microsoft Learn — `ITextProvider` / `ITextProvider2` / `ITextRangeProvider`, TextPattern guidelines, `UIA_Text_TextChangedEventId`.
- Microsoft Learn — `UiaRaiseNotificationEvent` (uiautomationcoreapi.h), `AutomationNotificationKind` (`ItemAdded`/`ItemRemoved`/`ActionCompleted`/`ActionAborted`/`Other`), `AutomationNotificationProcessing` (`ImportantAll`/`ImportantMostRecent`/`All`/`MostRecent`/`CurrentThenMostRecent`).
- Microsoft Learn — `AutomationLiveSetting` (`Off`/`Polite`/`Assertive`), `LiveSettingProperty`. 
- Microsoft Learn — pattern provider interfaces: `IInvokeProvider`, `IToggleProvider`, `ISelectionProvider`, `ISelectionItemProvider`, `IRangeValueProvider`, `IExpandCollapseProvider`, `IValueProvider`.
- W3C — WAI-ARIA 1.2 / 1.3 (live region roles `log`/`status`/`alert`/`progressbar`/`timer`/`marquee`; widget roles `listbox`/`combobox`/`menu`/`alertdialog`); ARIA Authoring Practices Guide (Listbox, Combobox, Alert, Alertdialog patterns); Core-AAM 1.2 (ARIA→UIA mappings).
- NV Access — NVDA 2025.3 Commands Quick Reference (single-letter quick-nav: `h l i t k n f u v e b x c r q s m g d o p a w` plus 1–9; Elements List `NVDA+F7`).
- NV Access — terminal accessibility blog (in-process), PR #13261 (“UIA Notification events from Sun Valley 2 console blocked to avoid double-reporting”).
- Freedom Scientific — JAWS keystrokes: `INSERT+F6` headings list, `INSERT+F7` links list, `INSERT+F5` form-fields list, `INSERT+F3` virtual-buffer elements, `INSERT+CTRL+R` regions; quick keys `H L I T R Q F B`.
- Microsoft Support — Narrator Complete Guide; Scan Mode (`Narrator+Spacebar`), `Narrator+F5/F6/F7` for landmark/heading/link lists.
- microsoft/terminal — PR #10336 (`UiaTextRangeBase` `GetAttributeValue`/`FindAttribute`), Windows Terminal `TermControl` UIA architecture; conhost UIA class-name change.
- VS Code — Accessible View / Accessible Buffer; Monaco-editor terminal output mirror (Programming-L collaboration, ACM ASSETS 2023).
- ACM Digital Library — Pradhan et al., *Accessibility of Command Line Interfaces*, CHI ’21 (DOI 10.1145/3411764.3445544). 
- DEV Community / DeepWiki — Claude Code Ink fork, custom React reconciler, CSI/OSC/DEC parser, full-screen redraw model. 
- vt100.net — Paul Williams, *A parser for DEC’s ANSI-compatible video terminals* (state machine).
- ICAD / Brewster, Wright & Edwards — earcon design guidelines; Blattner et al. on earcons; meta-analysis of auditory icons/earcons/spearcons (Auditory Perception & Cognition 2023).
- Microsoft Learn — `ISpatialAudioClient`, Windows HRTF support. 
- NV Access — NVDA braille subsystem (LibLouis),  `BrailleExtender` review-cursor tethering for terminal role. 
- DIAGRAM Center — Keane, *Interactive Scientific Graphics: Recommended Practices for Verbal Description* (Benetech, June 2014).

### Scope statement

This catalog defines **canonical display primitives** — the abstract interface contracts emitted by the pipeline’s `profiles` stage and consumed by `channels`. Each primitive is a substrate-and-cadence-aware *type*, not a widget. Channels (NVDA-via-UIA, self-voicing TTS, earcon, refreshable braille, spatial audio, FileLogger, WPF visual) are **first-class peers**: self-voicing, braille, and spatial audio are not afterthoughts. Literal-language convention is enforced throughout: *select*, *mark*, *announce*, *present*, *read*, *focused*, *current*. Sight metaphors (“highlight”, “view”, “show”) are eliminated.

-----

## Section 1: Tier 1 — Must-Support Primitives (ship within 6 months)

### 1.1 `StreamingTextLog`

The append-mostly canonical display for assistant prose, command output, and long-running tool stdout. This is the single highest-traffic primitive in the Claude Code workload.

#### Name

**StreamingTextLog** (a hybrid of ARIA `role=log` and UIA `Document` — see §5).

#### Content profile

Token-streamed assistant prose with interleaved semantic blocks (thinking, tool-use markers, code fences); shell stdout/stderr bursts; `npm install` log lines; `git push` progress chatter; `cargo build` warnings as they emit. Concrete example: “I’ll read the file and then…” arrives as a stream of UTF-8 grapheme clusters, with embedded `<thinking>` and `<tool_use>` regions detected by upstream pipeline stages and re-emitted as named TextPattern annotations.

#### UIA control-type mapping

- `ControlType.Document` (control & content view, focusable=true).
- **Required pattern:** `ITextProvider` + `ITextProvider2` exposing `DocumentRange` and `RangeFromPoint`; `ITextRangeProvider` supporting `Move`/`MoveEndpointByUnit` for `TextUnit.Character/Word/Line/Paragraph/Document`.  `GetAttributeValue` returns `UIA_FontWeightAttributeId` / `UIA_BackgroundColorAttributeId` / `UIA_ForegroundColorAttributeId` / `UIA_IsHiddenAttributeId`  / a custom `pty.SemanticBlock` attribute (assistant/thinking/tool-use/code/diff). Follow the `microsoft/terminal` `UiaTextRangeBase.GetAttributeValue`/`FindAttribute` precedent (PR #10336). 
- **Recommended pattern:** `IValueProvider` (read-only) for legacy MSAA bridging.
- `AutomationProperties.Name = "Terminal output"`, `LocalizedControlType = "terminal log"`, `LiveSetting = AutomationLiveSetting.Polite`, `ItemType = "stream"`.
- **Events:** `UIA_Text_TextChangedEventId` after each coalesced append  (debounced — see Update cadence). `UiaRaiseNotificationEvent` is **not** used per-append (it produces double-reporting; Sun Valley 2 console proved this — NVDA PR #13261).  Per-line announcement is delegated to the LiveRegionChanged event raised on a child `Text` element only when the `assertive` profile is active.
- `NotificationKind = ItemAdded`, `NotificationProcessing = CurrentThenMostRecent` is reserved for *boundary* events (block-start/block-end of a thinking or tool-use region), not per-token.

#### ARIA role analog

`role="log"` on the wrapping container with `aria-live="polite"`,  `aria-atomic="false"`,  `aria-relevant="additions text"`,  `aria-busy="true"` while the model is still streaming. Each interleaved semantic block is wrapped as `role="article"` (assistant turn) or `role="group"` with `aria-roledescription` of `"thinking block"` / `"tool use"` / `"code block"`. Reference: WAI-ARIA 1.2 `role=log`, MDN log-role doc.

#### NVDA reading pattern

- **Browse mode:** entered automatically on the Document by NVDA’s UIA terminal handler; arrow keys read by line, `NVDA+UpArrow` / `NVDA+DownArrow` by say-all from caret. Quick-nav keys exposed via the embedded text annotations:
  - `h` / `Shift+h` — next/previous semantic-block boundary  (we map level-2 headings to Claude Code turn boundaries, level-3 to thinking/tool-use sub-blocks).
  - `o` / `Shift+o` — next/previous embedded object   (tool-use block surfaces as embedded object).
  - `k` / `Shift+k` — next/previous link  (file-paths and URLs detected upstream).
  - `g` / `Shift+g` — next/previous graphic  (ASCII art / diff hunk markers when graphic-detector profile is active).
- **Focus mode:** entered on `Tab` into the prompt input; while in focus mode all keystrokes pass to the PTY. 
- `Insert+F7` (Elements List)  reveals: Headings (turn boundaries), Links (file paths, URLs), Landmarks (Tier-1 channel regions). NVDA’s element-list dialog supports type-ahead filtering.
- **Review cursor:** object review treats the Document as one navigator object; document review is scoped to the entire scrollback. `BrailleExtender`’s “automatic review cursor tethering in terminal role”   must be honored — emit `terminal` role hint via a custom UIA property so the add-on engages.

#### JAWS virtual cursor

The `ControlType.Document` with `IsContentElement=true` activates JAWS’s virtual buffer (PC Cursor in document-mode) automatically. `INSERT+F6` lists turn-boundary headings; `INSERT+F7` lists detected file-path links;  `INSERT+F5` is empty unless a form-input primitive is also visible;  `INSERT+CTRL+R` lists pty-speak’s exposed Landmark regions (input prompt, scrollback, status). Smart Navigation announces the role and content of each element as the virtual cursor crosses it. JAWS Auto Forms Mode triggers when focus enters an embedded `Edit` (the prompt). `NumPad +` / `NumPad −` (Forms Mode toggle) returns control to virtual buffer. 

#### Narrator behavior

Document control type is fully supported. Scan Mode (`Caps+Spacebar` or `Insert+Spacebar`)  enables `H/L/K/T/I/B/F` style navigation and arrow-key reading. `Narrator+F6` lists  headings, `Narrator+F7` lists links, `Narrator+F5` lists  landmarks.  Narrator’s reading depends heavily on TextPattern (per Chromium docs)  — our `ITextProvider2.DocumentRange` must be complete and traversable end-to-end without per-character cross-process round-trips (use `GetText(-1)`  precedent).

#### Interaction contract

- Read keys: `Up`/`Down` (line), `Left`/`Right` (character; word with `Ctrl`), `Home`/`End` (line bounds), `Ctrl+Home`/`Ctrl+End` (scrollback bounds), `PageUp`/`PageDown` (viewport).
- Search: `Ctrl+F` opens an in-region find-bar (separate primitive — `Find` uses the Edit primitive).
- Type-ahead is **not** consumed in browse mode; it reaches the PTY only in focus mode.
- Activation (`Enter` on a detected file-path link) emits an `Invoke` event consumable by upstream tooling (e.g., open-in-editor pathway).

#### Substrate consumption

**Derived semantic event store.** The detectors stage tags substrate runs as `assistant-prose | thinking | tool-use | code | diff | command-echo | stdout | stderr` and emits append events; the StreamingTextLog channel reads from this annotated event store, never from the raw screen-cell substrate (substrate inversion: the screen-cell grid is *secondary* and reconstructed only for the WPF visual channel).

#### Update cadence

Live, polite. Per-token announcement is **forbidden**. Coalesce appends with a 75–150 ms debouncer keyed on word-boundary detection. Emit `UIA_Text_TextChangedEventId` after each coalesced append. Raise an explicit `LiveRegionChanged` event with `AutomationLiveSetting.Polite` only when (a) a complete sentence has been emitted *and* (b) no further bytes have arrived for ≥ 200 ms. Boundary notifications (block start/end) fire via `UiaRaiseNotificationEvent(NotificationKind.ItemAdded, NotificationProcessing.CurrentThenMostRecent)`.

#### Output channel routing

- **NVDA UIA:** native fit (Document + TextPattern is the canonical NVDA terminal contract).
- **Self-voicing TTS:** primary fit; pty-speak’s TTS pathway can subscribe to the same coalesced append events with no translation.
- **Earcon:** semantic-block-boundary earcons (one per kind: assistant, thinking, tool-use, code, diff). Brewster timbre/rhythm guidelines (≥ 125 Hz, ≤ 5 kHz, multi-harmonic timbre, rhythmic motif rather than pitch alone). 
- **Refreshable braille:** native fit via TextPattern (NVDA’s braille subsystem follows the review cursor; `BrailleExtender` adds terminal-mode tethering).   LibLouis tables apply. 
- **Spatial audio:** optional — render different SemanticBlock kinds at distinct azimuths via `ISpatialAudioClient` with HRTF  (e.g., assistant at 0°, thinking at +30° elevation, tool-use at −30° azimuth left).
- **FileLogger:** native fit (annotated events serialize to NDJSON).
- **WPF visual:** native fit (the substrate cell-grid drives the WPF surface).

#### Example terminal scenarios

1. Claude Code streaming an assistant turn with embedded `<thinking>` block, then a `tool_use` for `Edit`, then resumed prose.
1. `npm install` emitting hundreds of `added X packages` lines.
1. `cargo build --verbose` long-running compiler output with interleaved warnings (stderr) and progress (stdout).

-----

### 1.2 `ConfirmationPrompt`

Hybrid alert + selection primitive. The single most accuracy-critical Claude Code surface.

#### Name

**ConfirmationPrompt** (designed primitive — see §5; neither pure `role="alertdialog"` nor pure `role="listbox"` fits cleanly).

#### Content profile

Claude Code’s tool-use confirmation: “Edit `src/foo.fs`?” with options `[Yes] [Yes, and don't ask again] [No, and tell Claude what to do]`. cmd.exe `choice /C YN /M "Continue?"`. PowerShell `$Host.UI.PromptForChoice(...)`. `git rebase -i` confirmation. `apt` “Do you want to continue? [Y/n]”. `npm` `--yes` interactive fallback.

#### UIA control-type mapping

- Outer container: `ControlType.Pane` with `LocalizedControlType = "confirmation"`. Set `AutomationProperties.LiveSetting = AutomationLiveSetting.Assertive`.  `Name` = the prompt question. `HelpText` = additional context (e.g., the diff being confirmed). `ItemStatus = "awaiting confirmation"`.
- Embedded options container: `ControlType.List`  implementing `ISelectionProvider` (single-selection: `CanSelectMultiple=false`, `IsSelectionRequired=true`). 
- Each option: `ControlType.ListItem` implementing `ISelectionItemProvider`  *and* `IInvokeProvider`. `Name` is the option label. `AccessKey` is the shortcut letter. The currently-selected option fires `SelectionItemPatternIdentifiers.ElementSelectedEvent`  on focus change (selection-follows-focus).
- The default option is exposed via `AutomationElementIdentifiers.IsDefaultProperty` (custom property; expose via `AutomationProperties.HelpText` if no native equivalent).
- **Notification on appearance:** `UiaRaiseNotificationEvent(NotificationKind.ActionAborted | ActionRequired, NotificationProcessing.ImportantAll, displayString = "Confirmation required: <prompt>", activityId = "<promptId>")`. We use `ImportantAll` (not `ImportantMostRecent`) because confirmations must not be dropped. Per Microsoft’s `AutomationNotificationKind` enum the closest existing kind is `Other` with a clear `displayString` — pty-speak ships its own `ActionRequired`-flavored kind by using `Other` plus an `activityId` of `"pty-speak.confirmation.<id>"`.

#### ARIA role analog

A *bespoke* hybrid: outer `role="alertdialog"` with `aria-modal="true"`, `aria-labelledby` referencing the prompt text, `aria-describedby` referencing the context block. Inner option group: `role="listbox"` with `aria-orientation="horizontal"` (or `vertical` per Claude Code’s layout), `aria-required="true"`, single-select via `aria-selected`.  Each `role="option"` carries `aria-keyshortcuts`. The default option is marked with `aria-current="true"`. References: APG Listbox Pattern; APG Alert and Alertdialog patterns.

#### NVDA reading pattern

Appearance fires the assertive notification — NVDA interrupts current speech   and announces “Confirmation required: Edit src/foo.fs? Yes, default.” NVDA enters focus mode automatically (focus moves into the listbox). `Down`/`Up` (or `Right`/`Left` for horizontal) move selection; first-letter type-ahead jumps by `Name`. `Insert+F7` Elements List shows the option set under “Form fields” / “Lists.” Quick-nav `b` does **not** apply (these are listitems, not buttons) — instead `i` (list item) and `l` (list)   jump out and back.

#### JAWS virtual cursor

JAWS auto-forms-mode triggers on focus into the listbox. PC Cursor announces the prompt assertively (alertdialog semantics), then reads the focused listitem. `INSERT+F5` (Form Fields List) includes the options.  The `Default` flag is announced as “default” suffix.

#### Narrator behavior

Scan Mode auto-disables when focus enters the listbox (Narrator’s “form field” detection). Narrator announces the alert content followed by the option, then “1 of 4.”

#### Interaction contract

- Arrow keys move selection within the listbox.
- First-letter type-ahead (`Y`, `A`, `N`) selects-and-activates immediately when option labels are unambiguous (Claude Code pattern).
- `Enter` activates the selected option; `Escape` activates the cancel option (semantic mapping; not necessarily the last option).
- `Tab` is **trapped** within the prompt while it is active.
- Focus is moved into the prompt on appearance and restored on dismissal.

#### Substrate consumption

**Derived semantic event store.** A `ConfirmationDetector` detects Claude Code’s option-rendering pattern (Ink Select component output) and `choice.exe`-style prompts in the substrate. The primitive is constructed from the structured detection event, never re-parsed from cells.

#### Update cadence

**Snapshot-on-render**, with one `NotificationKind.Other` + `NotificationProcessing.ImportantAll` event on appearance and one `NotificationKind.ActionCompleted` event on dismissal. Selection-follows-focus fires `ElementSelectedEvent` per arrow press.

#### Output channel routing

- **NVDA UIA:** native fit.
- **Self-voicing TTS:** native fit; the assertive announcement is the trigger.
- **Earcon:** **mandatory** “decision required” earcon on appearance (musical motif, not a beep) and a confirming earcon on dismissal. Brewster guidelines.
- **Refreshable braille:** native via UIA TextPattern propagation (the focused option name and selection state).
- **Spatial audio:** optional — render the prompt earcon at center-front (0°) for spatial salience.
- **FileLogger:** native — record question, options, default, chosen.
- **WPF visual:** native.

#### Example terminal scenarios

1. Claude Code Edit confirmation: `Edit src/lib/parser.fs? (Y)es / (A)ll / (N)o / (T)ell Claude…`
1. Claude Code tool-use confirmation: `Bash command: rm -rf node_modules — Approve? Yes / No`
1. cmd.exe `choice /C YN /N /M "Proceed?"`
1. `apt install foo`’s “Do you want to continue? [Y/n]” line.

-----

### 1.3 `SelectableList`

Vertical or horizontal selection menu, no embedded prose. Distinguished from ConfirmationPrompt by *non-modal* semantics and *deferred* activation.

#### Content profile

`fzf` selection UI; Claude Code `/model` slash-command list; `gh pr list` interactive picker; `git checkout` branch picker; PowerShell `Out-GridView -PassThru`; Ink `SelectInput` components.

#### UIA control-type mapping

- Container: `ControlType.List` implementing `ISelectionProvider`. `IsKeyboardFocusable=true`. Set `AutomationProperties.Name` to a derived label (e.g., “Branch picker”). For multi-select pickers (`fzf -m`), `CanSelectMultiple=true`. 
- Each item: `ControlType.ListItem` implementing `ISelectionItemProvider`; `IInvokeProvider` is **not** implemented (selection ≠ activation). Activation goes through a separate Enter-key dispatch routed by the prompt-substrate detector.
- `AutomationElementIdentifiers.PositionInSetProperty` and `SizeOfSetProperty` are populated from the list cardinality.
- **Events:** `SelectionItemPatternIdentifiers.ElementSelectedEvent` on each move; `AutomationElementIdentifiers.StructureChangedEvent` when the underlying list mutates (e.g., fzf type-ahead filtering).
- **Notifications:** when the list is filtered down, raise `UiaRaiseNotificationEvent(NotificationKind.ItemRemoved, NotificationProcessing.MostRecent, "<n> matches")` with a coalesced display string, not per-item events.

#### ARIA role analog

`role="listbox"` (single-select default); `role="listbox" aria-multiselectable="true"` for `fzf -m`. Items are `role="option"` with `aria-selected`. The container has `aria-label` or `aria-labelledby`. For a fuzzy-matching picker with an input, the parent is `role="combobox"` with `aria-haspopup="listbox"` and `aria-expanded`  (APG Combobox Pattern).

#### NVDA reading pattern

Focus mode entered automatically. Items are read by `Up`/`Down`. `Insert+F7` Elements List exposes the options under “Lists” → “List items.” Quick-nav `i` / `Shift+i` jumps by list-item; `l` / `Shift+l` by list.  NVDA’s review cursor (`NVDA+Numpad7..9`) reads by-character within the focused option without disturbing selection.

#### JAWS virtual cursor

`L` (next list), `I` (next list item), `Insert+F3` (virtual buffer elements).   For an `fzf`-style live filter, JAWS Auto Forms Mode engages because the parent is exposed as combobox.

#### Narrator behavior

Scan Mode disables. Narrator announces the option, position-in-set, selection state. `Narrator+F5` lists landmarks (the picker is exposed under `role="region"`).

#### Interaction contract

- `Up`/`Down` (or `Left`/`Right` if horizontal) move focus and selection (selection-follows-focus).
- Type-ahead first-letter jump on `Name` (case-insensitive, prefix-match, with debounce).
- `Enter` activates the focused item; `Escape` cancels.
- `Tab` exits the picker and returns to the host shell prompt.
- For multi-select: `Space` toggles inclusion in the selection set.

#### Substrate consumption

**Derived semantic event store** — fzf and Ink pickers emit recognizable layouts the detector stage extracts as a structured list event; reading from raw cells would lose ANSI selection-marker semantics.

#### Update cadence

Live, **polite**. For `fzf` typing-driven filters, debounce result-list mutation announcements at 250 ms with `NotificationKind.Other`/`NotificationProcessing.MostRecent` and a count-only displayString.

#### Output channel routing

- **NVDA UIA:** native fit.
- **Self-voicing TTS:** native fit.
- **Earcon:** boundary earcons at top/bottom of list (low-frequency tick), distinct earcon on multi-select toggle.
- **Refreshable braille:** native (UIA TextPattern propagates focused-item Name + selection state).
- **Spatial audio:** optional; not recommended for plain pickers.
- **FileLogger:** native.
- **WPF visual:** native.

#### Example terminal scenarios

1. `fzf` over git branches.
1. Claude Code `/model` slash-command picker (Ink `SelectInput`).
1. `gh pr checkout` interactive PR list.

-----

### 1.4 `SeverityAlert`

Single-shot interruptive announcement of error, warning, or fatal condition.

#### Content profile

`error: cannot find module 'foo'`; `npm ERR! code ELIFECYCLE`; `panicked at 'unwrap on None', src/main.rs:42`; `fatal: not a git repository`; PowerShell red-channel exception output; `tsc` diagnostic with severity.

#### UIA control-type mapping

- `ControlType.Text` (or `Group` if multi-line and non-trivially structured) with `AutomationProperties.LiveSetting = AutomationLiveSetting.Assertive`, `Name = "<severity>: <message>"`, `ItemStatus = "error"|"warning"|"fatal"`, `HelpText` = optional location/source line.
- **Notification:** `UiaRaiseNotificationEvent(NotificationKind.Other, NotificationProcessing.ImportantAll, displayString, activityId = "pty-speak.severity.error")`. `ImportantAll` ensures no error is dropped even under burst.
- `IInvokeProvider` is implemented if the alert is actionable (e.g., a clickable file:line link).

#### ARIA role analog

`role="alert"` (implicit `aria-live="assertive"`, `aria-atomic="true"`).  Distinguish severity tiers via `aria-roledescription="error"` / `"warning"` / `"fatal"`. Reference: ARIA APG Alert pattern; W3C ARIA22.

#### NVDA reading pattern

Assertive announcement interrupts ongoing speech. Browse mode quick-nav: `e` / `Shift+e` is **not** the default (`e` is “edit field” in NVDA)  — pty-speak documents `w` / `Shift+w` (spelling error)   as a fallback and exposes a custom add-on gesture for “next severity alert” if installed. Otherwise, alerts appear in `Insert+F7` Elements List under “Landmarks” when wrapped in `ControlType.Custom` with a “complementary”-style role.

#### JAWS virtual cursor

Speak the alert immediately (assertive live-region behavior). Quick-nav `R` jumps to next region (alerts are also region landmarks).

#### Narrator behavior

Assertive live region — Narrator interrupts Scan Mode reading and speaks the alert. `Narrator+F5` lists alerts as landmarks.

#### Interaction contract

- No focus change by default (alerts must not steal focus per ARIA APG).
- `Enter` on a focused actionable alert activates it (Invoke).
- Alerts are ephemeral in announcement but **persistent in scrollback** (reviewable later via the StreamingTextLog Document).

#### Substrate consumption

**Derived semantic event store** — a `SeverityDetector` recognizes ANSI red/bold patterns, `error:` / `warning:` / `panic` / `fatal` lexemes, exit-code semantics, stderr stream origin.

#### Update cadence

Snapshot, assertive. Coalescing: collapse identical severity messages within 500 ms into one notification with displayString suffix `" (×N)"` to avoid flooding (compiler error storms).

#### Output channel routing

- **NVDA UIA:** native fit.
- **Self-voicing TTS:** native fit; assertive interrupt.
- **Earcon:** **mandatory** severity-tier earcons (distinct timbres per tier, increasing pitch register from warning → error → fatal; rhythmic motif per Brewster).
- **Refreshable braille:** native via UIA TextPattern; severity prefix flashed.
- **Spatial audio:** optional — render error at front-center, warning at +30° elevation, fatal at front-center with low-frequency support tone.
- **FileLogger:** native; severity goes to a separate error log.
- **WPF visual:** native (red/yellow markers).

#### Example terminal scenarios

1. `tsc src/foo.ts:42:7 — error TS2322: Type 'string' is not assignable to type 'number'`.
1. Rust `panicked at 'index out of bounds'`.
1. `git push` rejection: `! [rejected]   main -> main (fetch first)`.

-----

### 1.5 `IndeterminateProgress`

Spinner-class display: ongoing activity with no completion estimate. Designed deliberately *not* as `role="progressbar"` (see §5).

#### Content profile

Claude Code’s `⠋ ⠙ ⠹ ⠸ ⠼ ⠴ ⠦ ⠧ ⠇ ⠏` braille spinner  during model thinking; `npm install`’s rotating spinner; `git clone`’s `Receiving objects: …`; long-running shell command with no progress signal.

#### UIA control-type mapping

- `ControlType.Custom` with `LocalizedControlType = "activity indicator"`, `AutomationProperties.Name = "<activity description>"` (e.g., “Claude is thinking”), `ItemStatus = "in progress"`, `LiveSetting = AutomationLiveSetting.Off`  (critical — see Update cadence).
- **No `IRangeValueProvider`.** Indeterminate progress with `RangeValuePattern` causes NVDA to attempt percent-based reporting that is meaningless (NVDA issue #12724 contextualizes the value/range conflict). 
- **Activity lifecycle notifications:** `UiaRaiseNotificationEvent(NotificationKind.ActionCompleted, NotificationProcessing.CurrentThenMostRecent, "Claude is thinking", "<activityId>")` on **start**, with the same `activityId` reused on **end** with `displayString = "Done."`. Use `CurrentThenMostRecent` so a new “thinking” event supersedes a stale one.
- **No per-frame UIA events.** The animation frame is purely a WPF-visual concern and must not propagate through the UIA tree.

#### ARIA role analog

`role="status"`  with `aria-live="polite"`, `aria-busy="true"`, `aria-atomic="true"`. Set `aria-label` to the activity description. Do **not** use `role="progressbar"` for indeterminate work — `progressbar` requires a numeric `aria-valuenow` and screen readers report it as a value change, which is wrong for ephemeral spinners (MDN, A11Y Collective guidance).

#### NVDA reading pattern

On start, NVDA announces “Claude is thinking, busy.” During the activity, NVDA is silent (the spinner glyph is not in the UIA text tree — it lives only in the WPF visual). On completion, NVDA announces “Done.” Quick-nav: not a target.

#### JAWS virtual cursor

Status live region announced once at start. Not enumerated in `Insert+F5`/`F6`/`F7`.

#### Narrator behavior

Status live region announces start and end. Scan Mode does not stop on it.

#### Interaction contract

- Not focusable.
- Cancel goes through the host shell (Ctrl+C) — not a primitive responsibility.

#### Substrate consumption

**Derived semantic event store.** A `SpinnerDetector` recognizes braille-spinner glyph cycling, `*-/-\\` ASCII spinners, and CSI cursor-back-and-overwrite patterns; the channel sees only `Started` and `Ended` events, never frames.

#### Update cadence

Frame-coalescing is **mandatory**. The detector emits one `Started` event and one `Ended` event per spinner episode; any UIA `LiveRegionChanged` or notification raise per frame is forbidden. If activity description text changes mid-spin (e.g., “Reading file… Calling tool…”), debounce at 1 s and emit one notification with `NotificationProcessing.MostRecent`.

#### Output channel routing

- **NVDA UIA:** native fit (status live region).
- **Self-voicing TTS:** native fit.
- **Earcon:** “activity start” and “activity end” earcons, plus an optional **looped low-volume background drone** while busy (volume below speech threshold, distinct from speech band; user-disable-able). This is the empirically validated alternative to per-frame announcements.
- **Refreshable braille:** the displayString is propagated via TextPattern; the spinner glyph itself is not.
- **Spatial audio:** optional — render the activity drone at the user’s chosen ambient azimuth (e.g., +90° / right side).
- **FileLogger:** native (start/end timestamps).
- **WPF visual:** the **only** channel that renders the spinner glyph.

#### Example terminal scenarios

1. Claude Code thinking: `⠋ Thinking… (esc to interrupt)`.
1. `npm install`’s spinner during dependency resolution.
1. `cargo build`’s “Compiling crate-name” status line.

-----

### 1.6 `CommandOutputTuple`

First-class primitive pairing a submitted command with its output (stdout/stderr/exit-code). Without this, scrollback navigation by command — the single most-requested feature in the CHI ’21 CLI accessibility study (Pradhan et al.)  — is impossible.

#### Content profile

`$ git status` followed by N lines of output and an exit code. PowerShell `PS C:\>` prompt-command-output triplet. Claude Code’s user-prompt → assistant-response pair is a *parallel* primitive (StreamingTextLog) but the underlying shell pairs are CommandOutputTuples.

#### UIA control-type mapping

- Outer: `ControlType.Group` with `LocalizedControlType = "command/output pair"`, `Name = "<truncated command>"`, `HelpText = "exit code <N>, <duration>"`, `LiveSetting = AutomationLiveSetting.Off` (the inner StreamingTextLog handles live update).
- Children: a `ControlType.Edit` for the command (read-only after submission, `IsReadOnly=true` via `IValueProvider`), and a `ControlType.Document` for the output (TextPattern-backed, same as §1.1).
- Exit-code element: `ControlType.Text` with `Name = "Exit code 0"` / `"Exit code 127"`, `ItemStatus = "success"|"failure"`, exposed under the Group.
- Fire `AutomationElementIdentifiers.StructureChangedEventId` when each new tuple is appended.

#### ARIA role analog

`role="article"` for the tuple wrapper, with `aria-labelledby` pointing to a heading (level-2) containing the command line. The command itself uses `role="textbox" aria-readonly="true"`. The output uses `role="region" aria-label="output"`. Exit code uses `role="status"`. `aria-roledescription="command and output"`.

#### NVDA reading pattern

Treat each tuple as a heading-2 region in the Document. `h` / `Shift+h` jumps between commands (this is the pty-speak-specific behavior the user research demands). Pty-speak additionally documents `c` / `Shift+c` as **next/previous command in this design** when shipped as an NVDA add-on; in baseline NVDA without the add-on, `c` is “next combo box”   — fall back to `h` over the level-2 heading produced for the command line. `o` / `Shift+o` jumps to the output block (mapped to the embedded-object navigation that NVDA exposes for non-text descendants of a Document).

#### JAWS virtual cursor

`H` jumps to next command heading; `INSERT+F6` lists all commands.  `INSERT+CTRL+R` lists tuples as regions.

#### Narrator behavior

`Narrator+F6` lists commands as headings. Tab navigation between tuple regions uses `Narrator+F5`.

#### Interaction contract

- `Enter` on a focused command re-runs it (route through host shell history); `Ctrl+Enter` copies the command to the prompt without executing.
- `Ctrl+C` while focus is in a tuple copies the tuple’s text to clipboard.
- `Alt+Up` / `Alt+Down` jump to previous/next tuple boundary (binding parallel to VS Code’s `editor.action.marker.next`).

#### Substrate consumption

**Derived semantic event store.** A `PromptDetector` consumes shell-integration sequences (OSC 133 prompt marks per FinalTerm, used by VS Code, iTerm2, and Windows Terminal), or detects prompt regex patterns when shell-integration is unavailable. The tuple is constructed from the structured event, never re-derived from cells at read time.

#### Update cadence

Snapshot at `command-submitted` (creates the tuple), live (polite, via the embedded StreamingTextLog) during execution, snapshot at `command-completed` with a single `NotificationKind.ActionCompleted`/`NotificationProcessing.MostRecent` event carrying the exit-code summary.

#### Output channel routing

- **NVDA UIA:** native fit.
- **Self-voicing TTS:** native fit.
- **Earcon:** distinctive earcons at command-start, command-success (exit 0), command-failure (non-zero exit). This satisfies ICAD literature on dual-task interference: success/failure is conveyed without speech.
- **Refreshable braille:** native via UIA TextPattern.
- **Spatial audio:** optional — recent commands at front-center, older commands at receding azimuths (auditory time-axis). Defer behind a feature flag.
- **FileLogger:** native (one record per tuple).
- **WPF visual:** native.

#### Example terminal scenarios

1. `$ git status` → 8 lines → exit 0.
1. `$ npm test` → mixed stdout/stderr → exit 1, with a SeverityAlert raised on failure.
1. PowerShell `Get-Process | Where-Object { $_.CPU -gt 100 }` → tabular output → exit 0.

-----

## Section 2: Tier 2 — Should-Support Primitives (ship within 12 months)

### 2.1 `DiffView`

First-class primitive for unified-diff and side-by-side patch displays — Claude Code emits diffs constantly during edits.

#### Content profile

Unified diffs from `git diff`, `git show`, Claude Code Edit-tool previews; `delta` and `diff-so-fancy` colorized output.

#### UIA control-type mapping

- Outer: `ControlType.Group`, `LocalizedControlType = "diff"`, `Name = "<filename>"`, `HelpText = "+<additions> −<deletions>"`.
- Each hunk: `ControlType.Group` exposed as a level-3 heading, with `Name = "Hunk @@ -A,B +C,D @@"`.
- Each line: `ControlType.Text` with a custom `pty.DiffLineKind` attribute (`context | added | removed | hunk-header | file-header`) consumable through `ITextRangeProvider.GetAttributeValue`.
- File header: level-2 heading.

#### ARIA role analog

`role="article" aria-roledescription="diff"`. Hunks: `role="region" aria-roledescription="hunk"`. Added/removed lines use `aria-roledescription="added line"` / `"removed line"` (screen readers announce the role description in addition to content).

#### NVDA reading pattern

`h` jumps file-headers (level-2), `3` jumps hunks (level-3 in Elements List). A pty-speak NVDA add-on registers `+` / `Shift++` and `-` / `Shift+-` to navigate added and removed lines respectively.

#### JAWS virtual cursor

`H` for file headers, `2`/`3` for heading levels. `INSERT+F6` lists all hunks.

#### Narrator behavior

Heading-list navigation via `Narrator+F6`. Custom roledescription is announced per Narrator’s UIA roledescription support.

#### Interaction contract

- `j`/`k` (vim-style), or `Down`/`Up`, walks lines.
- `n`/`p` walks hunks.
- `]c`/`[c` walks change-blocks (added-or-removed runs).
- `Enter` on a file-header (when actionable) opens at line — emits Invoke.

#### Substrate consumption

**Derived semantic event store.** A `DiffDetector` parses unified-diff syntax in the substrate and emits a structured diff tree.

#### Update cadence

Snapshot-on-render. Diffs are not live by nature.

#### Output channel routing

- **NVDA UIA:** native.
- **Self-voicing TTS:** native; pty-speak’s TTS prefixes added lines with “plus”, removed with “minus” (configurable; literal-language preferred over color names).
- **Earcon:** distinct ticks on entering an added vs. removed run (per Brewster, rhythm-distinct rather than pitch-distinct).
- **Refreshable braille:** native; emit dot-7/dot-8 markers per `BrailleExtender` precedent  for added/removed.
- **Spatial audio:** optional; added at +30° azimuth, removed at −30° azimuth.
- **FileLogger:** native.
- **WPF visual:** native (color-blind safe defaults: blue/orange, not red/green).

#### Example scenarios

1. Claude Code Edit preview before applying.
1. `git diff HEAD~1`.
1. `git show <sha>`.

-----

### 2.2 `CodeBlockWithSyntaxStructure`

Code blocks with parseable structure (function names, classes, variables) preserved through the pipeline.

#### Content profile

Triple-backtick fenced code from Claude Code; `bat`/`pygmentize` syntax-highlighted output; `cat src/lib.rs` with shell-integration syntax markers.

#### UIA control-type mapping

- `ControlType.Document` (or `Edit` if writable), with `LocalizedControlType = "code"`, `AutomationProperties.HelpText = "<language> <line-count> lines"`.
- TextPattern attributes expose syntax kinds via custom attribute IDs (`pty.SyntaxRole = "keyword"|"identifier"|"string"|"comment"|"function-name"`) on the line and token granularity.
- `ITextProvider2.RangeFromAnnotation` returns ranges for symbol annotations (function definitions detected by upstream language-aware parsers).

#### ARIA role analog

`role="region" aria-roledescription="code block" aria-label="<language>"`. Inner pre `role="code"` (ARIA 1.3 reserved name `code`).

#### NVDA reading pattern

Symbol-list dialog via a pty-speak custom gesture (parallel to Claude Code Chat’s “Symbol View”). Line-by-line read by default; word-by-word for code; character-by-character for ambiguous tokens.

#### JAWS virtual cursor

Standard text-buffer navigation. JAWS’ “Reading Mode” (formerly “Say All”) with punctuation-mode “all” reads code reliably.

#### Narrator behavior

Standard Document reading. `Narrator+F6` lists function-definition headings if pty-speak emits them as level-3 headings.

#### Interaction contract

- Standard text navigation.
- `Ctrl+Shift+O` (mirrors VS Code) opens the symbol list.
- `Ctrl+G` jumps to line number.

#### Substrate consumption

**Derived semantic event store.** A `CodeBlockDetector` recognizes triple-backtick fences from Claude Code’s output stream and `bat`-style syntax-highlight sequences; an optional `LanguageParser` stage produces symbol annotations using tree-sitter.

#### Update cadence

Snapshot-on-render. Code blocks are atomic by nature.

#### Output channel routing

- **NVDA UIA:** native (Document + custom attributes).
- **Self-voicing TTS:** native; symbol-aware reading.
- **Earcon:** subtle in/out boundary earcons.
- **Refreshable braille:** native; computer-braille table for code (Liblouis `en-us-comp8.ctb`).
- **Spatial audio:** not applicable.
- **FileLogger:** native.
- **WPF visual:** native.

-----

### 2.3 `DeterminateProgress`

True progress with known total — distinct from §1.5.

#### Content profile

`curl -L file`’s percent indicator with byte counts; `wget`’s progress; `dd status=progress`; `apt-get`’s “Get:1 / 25” counter; `pip install`’s package-count progress.

#### UIA control-type mapping

- `ControlType.ProgressBar` with `IRangeValueProvider`  (`Minimum=0`, `Maximum=100` normalized;  expose actual byte counts via `ItemStatus`). `IsReadOnly=true`.
- `LiveSetting = AutomationLiveSetting.Off` for the value (announcement is throttled), `LiveSetting = AutomationLiveSetting.Polite` only on milestone boundaries (10 %, 25 %, 50 %, 75 %, 90 %, 100 %).
- Per-update `AutomationPropertyChanged(RangeValuePatternIdentifiers.ValueProperty)`; **milestone notifications via** `UiaRaiseNotificationEvent(NotificationKind.ActionCompleted, NotificationProcessing.MostRecent, "50 percent", activityId)`.

#### ARIA role analog

`role="progressbar"` with `aria-valuemin`, `aria-valuemax`, `aria-valuenow`, `aria-valuetext` (human-friendly text including units and ETA), `aria-label`.

#### NVDA reading pattern

NVDA reports value changes per its progress-bar verbosity setting (Off / Speak / Beep / Both). Pty-speak documents that users should set “Beep” for progress bars to get tonal updates without speech interruption — and pty-speak’s earcon channel mirrors this behavior natively when NVDA setting is Off.

#### JAWS virtual cursor

JAWS announces progress at user-configurable intervals (default 10 %).

#### Narrator behavior

Narrator announces value changes on focus and milestones.

#### Interaction contract

Non-focusable by default.

#### Substrate consumption

**Derived semantic event store.** A `ProgressDetector` recognizes percent-encoding patterns and `\r`-overwrite progress lines.

#### Update cadence

Throttled. Per-tick `AutomationPropertyChanged` allowed at ≤ 4 Hz; per-milestone `NotificationKind.ActionCompleted` at boundary crossings.

#### Output channel routing

- **NVDA UIA:** native.
- **Self-voicing TTS:** milestone announcements only.
- **Earcon:** **rising-pitch tonal sweep** at each milestone (Brewster pitch-contour-as-progress finding); the de facto progress sonification.
- **Refreshable braille:** numeric value flashed.
- **Spatial audio:** optional; pan from left (0 %) to right (100 %).
- **FileLogger:** native.
- **WPF visual:** native.

-----

### 2.4 `FormInputGroup`

Multi-field input prompt block (PowerShell `Read-Host`, multi-question setup wizards).

#### Content profile

`npm init` interactive form; `gh repo create` wizard; `ssh-keygen` Q&A; PowerShell `Read-Host -AsSecureString`.

#### UIA control-type mapping

- Outer: `ControlType.Group`, `LocalizedControlType = "form"`, `Name = "<wizard title>"`.
- Each field: `ControlType.Edit` with `IValueProvider`. Secure fields set `AutomationProperties.IsPassword=true` and `IsContentElement=true` but suppress text-content propagation through TextPattern (TextPattern security note: do **not** expose password content). 
- Field labels: `ControlType.Text` linked via `LabeledByProperty`.
- Field-level validation errors: child `ControlType.Text` with `LiveSetting=Assertive` and `role=alert` analog.

#### ARIA role analog

`role="form"` (or `role="group"`) wrapping `role="textbox"` fields with `aria-label`, `aria-required`, `aria-invalid`, `aria-describedby` for error text.

#### NVDA reading pattern

Forms-mode auto-engage on field focus. `f` quick-nav jumps fields.  `Insert+F7` Forms list.

#### JAWS virtual cursor

Auto Forms Mode. `INSERT+F5` lists fields. `F` quick-nav between forms.

#### Narrator behavior

Scan Mode disables on field focus; field label, value, and required state announced.

#### Interaction contract

- `Tab` / `Shift+Tab` move between fields.
- `Enter` submits when on the last/submit field; the host PTY sees the same keystroke.
- Field-level validation errors fire assertive notifications without moving focus.

#### Substrate consumption

**Derived semantic event store.** A `WizardDetector` recognizes prompt-then-readline patterns.

#### Update cadence

Live, per-field. Validation errors assertive.

#### Output channel routing

- **NVDA UIA:** native.
- **Self-voicing TTS:** native; password fields read as “<n> characters” never plaintext.
- **Earcon:** field-boundary tick.
- **Refreshable braille:** native.
- **Spatial audio:** not applicable.
- **FileLogger:** native (with password redaction).
- **WPF visual:** native.

-----

### 2.5 `TabularDataDisplay`

Grid-structured shell output with row/column semantics — without this, `ls -la`, `docker ps`, `kubectl get pods`, `Get-Process` are all unstructured walls.

#### Content profile

`docker ps`, `kubectl get pods`, `ls -la`, `Get-Process | Format-Table`, `npm ls` tree-as-table, GitHub CLI tables.

#### UIA control-type mapping

- `ControlType.DataGrid` (preferred) or `ControlType.Table`, implementing `IGridProvider` (`RowCount`, `ColumnCount`, `GetItem`) and `ITableProvider` (`GetRowHeaders`, `GetColumnHeaders`, `RowOrColumnMajor`).
- Each cell: `ControlType.DataItem` with `IGridItemProvider` (`Row`, `Column`, `RowSpan`, `ColumnSpan`) and `ITableItemProvider` (linking to row/column headers).
- Headers: `ControlType.HeaderItem`.

#### ARIA role analog

`role="table"` (non-interactive) or `role="grid"` (with cell focus). `role="row"`, `role="columnheader"`, `role="rowheader"`, `role="cell"` / `role="gridcell"`. `aria-rowcount`, `aria-colcount`, `aria-rowindex`, `aria-colindex` for virtualized tables.

#### NVDA reading pattern

`t` / `Shift+t` next/previous table;  in-table `Ctrl+Alt+Arrow` walks cells  with header context.   NVDA reads column header on column change, row header on row change.

#### JAWS virtual cursor

`T` for tables; `Ctrl+Alt+Arrow` for cells.  Smart Navigation announces headers.

#### Narrator behavior

Narrator’s table-reading mode (`Caps+F9`/`Caps+F10` row/column read). Headers spoken on transition.

#### Interaction contract

- Arrow keys walk cells.
- `Ctrl+Home`/`Ctrl+End` to first/last cell.
- `Enter` activates cell if Invoke-able.
- `Space` toggles selection if SelectionItem-supported.

#### Substrate consumption

**Derived semantic event store.** A `TableDetector` parses ASCII table layouts (column-aligned whitespace, box-drawing characters, ANSI markers from `--format`); a `JsonTableDetector` upgrades to schema-aware parsing for JSON-emitting tools (`docker ps --format json`).

#### Update cadence

Snapshot-on-render for static tables; for live-updating tables (`watch -n 1 kubectl get pods`), debounce at 1 s and use `StructureChangedEvent` rather than per-cell change events.

#### Output channel routing

- **NVDA UIA:** native (Grid + Table patterns).
- **Self-voicing TTS:** native; header context per move.
- **Earcon:** row-change vs. column-change ticks distinguished.
- **Refreshable braille:** native; tab-aligned representation.
- **Spatial audio:** not applicable.
- **FileLogger:** native (CSV/JSON serialization).
- **WPF visual:** native.

-----

## Section 3: Tier 3 — Defer / Future Primitives

### 3.1 `HierarchicalTreeDisclosure`

Multi-level expand/collapse trees.

- **UIA:** `ControlType.Tree` + `ControlType.TreeItem` with `IExpandCollapseProvider` (`ExpandCollapseState.Expanded/Collapsed/PartiallyExpanded/LeafNode`)  and `ISelectionItemProvider`. `Level` and `Position` properties populated.
- **ARIA:** `role="tree"` / `role="treeitem"` with `aria-expanded`, `aria-level`, `aria-setsize`, `aria-posinset`.
- **NVDA / JAWS / Narrator:** standard tree navigation; `Right`/`Left` expand/collapse; Quick-nav `i`/`l` for items/lists where supported.
- **Substrate:** derived event store from indented-list detectors; deferred until `npm ls`, `git log --graph`, and `tree` outputs become a usability bottleneck.
- **Channels:** earcons for expand/collapse, depth-encoded pitch (deeper = lower); spatial audio optional with depth-as-elevation mapping.

### 3.2 `SpatialAudioStatusField`

Continuous low-level status conveyed through HRTF-spatialized ambient tones (e.g., concurrent build status, battery, network, model token-rate). Implementation via Windows `ISpatialAudioClient` with HRTF support;  user-pluggable HRTF profile (SONICOM dataset, photogrammetry-derived per Pirard et al.  2026 — *the literature here is exploratory*; do not promise individualized HRTF in v1).

- **UIA:** modeled as a non-focusable `ControlType.Custom` with `LiveSetting=Off`; status reachable on demand via a hotkey that re-announces speech-form via `UiaRaiseNotificationEvent(NotificationKind.Other, NotificationProcessing.MostRecent, displayString)`.
- **ARIA:** no native analog; expose hidden `role="status"` mirror.
- **Channels:** spatial audio is the *primary* channel. Earcon and TTS fall-backs mandatory. Defer to v2; this is research-grade.

### 3.3 `MultiRegionFocusManager`

Composite primitive coordinating multiple concurrent canonical displays in a single terminal viewport (split panes, Claude Code’s prompt + assistant + status). Defines the *focus arbitration contract* between regions.

- **UIA:** each region exposes its own provider tree; pty-speak emits a `ControlType.Pane` per region with `LandmarkType` set to `UIA_MainLandmarkTypeId` (output region) / `UIA_NavigationLandmarkTypeId` (prompt region) / `UIA_CustomLandmarkTypeId + LocalizedLandmarkType` (status region).
- **ARIA:** `role="main"`, `role="complementary"`, `role="region"` with labels.
- **Interaction:** `Ctrl+F6` cycles regions (mirroring Windows pattern); per-region quick-nav remains scoped.

-----

## Section 4: Out of Scope / Explicit Non-Goals

|Excluded primitive                                 |Why excluded                                                                                                                                                                                                                |
|---------------------------------------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
|`role="banner"` / site-banner landmark             |Terminals have no document-banner concept. The closest analog (window title) is the WPF window’s accessible title.                                                                                                          |
|Sliders / range value pickers (`role="slider"`)    |Terminal input is character-stream; sliders require continuous value input that no shell emits. Determinate progress is read-only and uses `RangeValuePattern` differently.                                                 |
|Complex tab panels (`role="tablist"`/`tabpanel`)   |Multiple WPF tabs are a window-chrome concern, not a canonical *terminal* display. Each tab is its own terminal session and has its own primitive set.                                                                      |
|WPF dialog modals (`role="dialog"` in window sense)|Terminal “modals” (confirmation prompts) are inline, scrollback-preserving, and do not steal window focus. ConfirmationPrompt explicitly avoids `aria-modal="true"` at the window level — modality is logical, not platform.|
|Drawing canvases (`role="img"` for ASCII art)      |ASCII art is excluded by literal-language convention; CHI ’21 (Pradhan et al.) confirms it is a top irritant.  Detect-and-suppress via an `AsciiArtDetector` rather than try to render it.                                  |
|`role="marquee"`                                   |Maps poorly to terminal output and triggers misuse. Spinners use the IndeterminateProgress primitive instead.                                                                                                               |
|`role="timer"`                                     |Self-defeating — `aria-live="off"` by spec;  pty-speak’s clock displays are static and do not need a primitive.                                                                                                             |

-----

## Section 5: Gap Analysis

### 5.1 Scrollback vs. `role="log"`

ARIA `log` is append-only and live; the user’s review action is implicit (browse-mode caret). **Terminal scrollback is finite, reviewable, navigable, indexed, and bounded.** Recommend the **StreamingTextLog** hybrid (§1.1): UIA `Document` with `TextPattern` (full-content, navigable, attribute-bearing) plus ARIA `log`-style live announcements gated by debounced sentence boundaries — *not* per-line. The Document is the substrate-of-truth; live announcements are derived advisories. NVDA’s existing UIA terminal handler integrates with this contract directly (PR #13261 confirms NVDA’s expectation that UIA notifications are reserved for non-text events; the text stream itself flows through TextPattern). 

### 5.2 Spinners vs. `role="progressbar"`

Spinners are indeterminate, ephemeral, and high-frequency. `progressbar` requires `aria-valuenow` and screen-reader percent-reporting. Per-frame update is catastrophic (would emit dozens of value-change events per second). Recommend the **IndeterminateProgress** primitive (§1.5) using `role="status"` + `aria-busy="true"` + a *single* start event and *single* end event, with WPF-only frame rendering. **No `IRangeValueProvider`. No `LiveRegionChanged` per frame.** An optional looped sub-speech-band drone earcon provides continuous “still working” signal without speech.

### 5.3 `CommandOutputTuple` (concrete design — see §1.6)

**Name:** CommandOutputTuple. **UIA:** `ControlType.Group` wrapping a read-only `ControlType.Edit` (the command), a `ControlType.Document` (the output, TextPattern-backed), and a `ControlType.Text` exit-code child with `ItemStatus`. **Substrate:** OSC 133 prompt marks (FinalTerm protocol) where supported, regex prompt detection otherwise. **Quick-nav:** `h`/`Shift+h` over level-2 headings (the command lines), `o` over outputs, `Alt+Up`/`Alt+Down` over tuples. **Reading:** “command, *git status*; output 8 lines; exit 0, success.” This primitive is **the** answer to the CHI ’21 finding that CLI users need command-as-anchor navigation, and it is non-negotiable for Tier-1 ship.

### 5.4 Streaming assistant prose with embedded tool-use

**Hybrid log + document with interleaved semantic blocks.** This is the StreamingTextLog (§1.1) with mandatory `pty.SemanticBlock` text-attribute support for `assistant | thinking | tool-use | code | diff`. Block boundaries are level-3 headings exposed through TextPattern with the boundary attribute; the screen-reader user navigates blocks via `3` quick-nav and reads block content with arrow keys. Tool-use blocks expose `IInvokeProvider` for “approve” actions when the prompt is active (escalating to a ConfirmationPrompt primitive if the approval is interactive).

### 5.5 Tool-use confirmation as hybrid alert + selection

**Designed primitive: ConfirmationPrompt (§1.2).** Pure `role="alertdialog"` lacks the option-set semantics; pure `role="listbox"` lacks the assertive announcement. The hybrid uses `ControlType.Pane` (alertdialog-equivalent) wrapping a `ControlType.List` (selection-bearing) with both `LiveSetting=Assertive` and the listbox’s `ISelectionProvider`. Notification on appearance is `NotificationKind.Other` + `NotificationProcessing.ImportantAll`. Default option is exposed via custom property and `aria-current="true"`. This is the design — implement it as written.

-----

## Section 6: Conflict Analysis (UIA vs. ARIA — pty-speak’s positions)

|Concern           |UIA path                                                                                          |ARIA path                                                                                |**pty-speak position**                                                                                                                                                                                                                                                                                                                                                                                                       |
|------------------|--------------------------------------------------------------------------------------------------|-----------------------------------------------------------------------------------------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
|List semantics    |`ControlType.List` + `ISelectionProvider`                                                         |`role="listbox"` (no implicit selection) vs. `role="menu"` (no value, no `aria-selected`)|**Use UIA `List` + `ISelectionProvider`** as the source of truth (NVDA on Windows is the optimization target). Mirror as ARIA `listbox` for the WebView-based docs/help surface only. Avoid `role="menu"` — terminal selection menus carry value semantics.                                                                                                                                                                  |
|Live regions      |`UiaRaiseNotificationEvent` (`NotificationKind`/`NotificationProcessing`) + `LiveSetting` property|`aria-live` + `role=alert/status/log`                                                    |**Use UIA notifications for discrete events** (confirmation appeared, severity raised, milestone crossed). **Use UIA `LiveSetting` + `LiveRegionChanged`** for region-mutation announcements. ARIA mappings exist *for parity* but the canonical event surface is UIA — NVDA’s terminal pipeline reads UIA directly (per NV Access PR #13261 discussion) and Narrator’s responsiveness on UIA notifications is best-in-class.|
|Document structure|`ControlType.Document` + `TextPattern` (with attributes)                                          |`role="document"` / `role="article"`                                                     |**Use UIA `Document` + full `ITextProvider2`/`ITextRangeProvider`** with attribute support (mirrors `microsoft/terminal` PR #10336). This is what NVDA and Narrator’s reading pipelines exploit;  ARIA `article`/`document` is a thinner contract.                                                                                                                                                                           |
|Confirmation      |No exact UIA control type for “alert dialog with options”                                         |`role="alertdialog"`                                                                     |**Synthesize via `ControlType.Pane` + `LiveSetting=Assertive` + nested `List`.** Surface ARIA-1.3 `aria-roledescription="confirmation"` for browsers in the help surface only.                                                                                                                                                                                                                                               |

**Optimization stack:** NVDA (primary) ⇒ Narrator (secondary, must-work) ⇒ JAWS (tertiary, must-work) ⇒ braille via NVDA’s UIA-aware subsystem ⇒ pty-speak self-voicing (always-on fallback). All conflicts above are resolved in favor of UIA surfaces because every consumer in this stack reads UIA on Windows.

-----

## Section 7: Output Channel Extensibility Matrix

Legend: ● native fit · ◐ requires translation · ○ not applicable

|Primitive                   |NVDA via UIA|Self-voicing TTS  |Earcon (WASAPI)      |Braille via UIA TextPattern|Spatial audio (HRTF)  |FileLogger  |WPF visual|
|----------------------------|------------|------------------|---------------------|---------------------------|----------------------|------------|----------|
|StreamingTextLog            |●           |●                 |● (block-boundary)   |●                          |◐ (block-azimuth)     |●           |●         |
|ConfirmationPrompt          |●           |●                 |● (decision earcon)  |●                          |◐ (front-center)      |●           |●         |
|SelectableList              |●           |●                 |● (boundary tick)    |●                          |○                     |●           |●         |
|SeverityAlert               |●           |●                 |● (severity-tier)    |●                          |◐ (severity azimuth)  |●           |●         |
|IndeterminateProgress       |●           |● (start/end only)|● (start/end + drone)|◐ (description only)       |◐ (drone azimuth)     |●           |● (frames)|
|CommandOutputTuple          |●           |●                 |● (start/exit-code)  |●                          |◐ (time-axis)         |●           |●         |
|DiffView                    |●           |●                 |● (added/removed run)|● (dot-7/8)                |◐ (add/remove azimuth)|●           |●         |
|CodeBlockWithSyntaxStructure|●           |●                 |◐ (boundary only)    |● (computer-braille)       |○                     |●           |●         |
|DeterminateProgress         |●           |● (milestones)    |● (rising sweep)     |● (numeric flash)          |◐ (L→R pan)           |●           |●         |
|FormInputGroup              |●           |● (with redaction)|● (field tick)       |●                          |○                     |● (redacted)|●         |
|TabularDataDisplay          |●           |● (header context)|● (row vs. col tick) |●                          |○                     |● (CSV/JSON)|●         |
|HierarchicalTreeDisclosure  |●           |●                 |● (expand/collapse)  |●                          |◐ (depth-as-elevation)|●           |●         |
|SpatialAudioStatusField     |◐ (mirror)  |● (on demand)     |●                    |◐                          |● (primary)           |●           |◐         |
|MultiRegionFocusManager     |●           |●                 |● (region transition)|●                          |◐                     |●           |●         |

Self-voicing, braille, and spatial audio are *first-class* — every primitive defines a path through each channel even when a translation step is required. Earcons are mandatory for primitives where a non-speech indication materially reduces dual-task interference (per the Auditory Perception & Cognition 2023 meta-analysis).

-----

## Section 8: Closing Recommendation

Design and implement these **six primitives first**, in this order, mapping directly to Claude Code’s actual interaction surface:

1. **StreamingTextLog** — without this nothing else matters; every Claude Code response is a streaming log with embedded semantic blocks, and every shell command produces output that lands here.
1. **CommandOutputTuple** — the single most-requested capability from CLI accessibility research (CHI ’21); without command-as-anchor navigation, scrollback is unusable.
1. **ConfirmationPrompt** — Claude Code asks for confirmation on every `Edit` and every `Bash` tool-use; correctness here is a safety property, not a usability nicety.
1. **SelectableList** — Claude Code’s `/model`, `/help`, and Ink-based selection components flow through this primitive; `fzf` and `gh` round out the surface.
1. **SeverityAlert** — compiler errors, `npm ERR!`, `git push` rejections, panics. Until this exists, error storms drown speech.
1. **IndeterminateProgress** — the Claude Code thinking spinner runs constantly and is the single greatest source of accidental-announcement disasters. Designing this *correctly out of the gate* (no per-frame events, earcon-driven status) prevents NVDA-overload reports from ever filing.

Tier 2 (DiffView, CodeBlockWithSyntaxStructure, DeterminateProgress, FormInputGroup, TabularDataDisplay) follows naturally as the ANSI-detector substrate matures. Tier 3 is research surface — implement when Tier 1+2 are stable and validated with blind developer end-users.

The architecture’s value proposition is the channel-extensibility column: by routing each primitive through self-voicing TTS, earcons, refreshable braille, and (eventually) spatial audio at the *channel* layer rather than retrofitting these into the screen-reader path, pty-speak becomes the first Windows terminal where these output modalities are first-class peers — precisely the gap that VS Code’s xterm.js work, Windows Terminal’s UIA implementation, and macOS Terminal/iTerm2 leave open.