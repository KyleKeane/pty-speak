# CMD interaction test corpus

> **Snapshot**: Cycle 47 (2026-05-13).
> Eight tiny `.cmd` scripts, each exercising one cmd interaction
> primitive in a maintainer-walkable form.

## Why these exist

Cycle 46 wrapped up the substrate (`ContentHistory`) + channel
(`Edit` control + caret + capped `Announce`) for basic terminal
output. Now we need to know how those interact with the *other*
things cmd can do beyond plain `echo`: prompted input, choice
prompts, pauses, progress loops, stderr.

Rather than walking the matrix entirely from memory or
ad-hocing each test inside a debug session, each interaction
primitive gets its own minimal script. The maintainer
(KyleKeane) walks them through the `Diagnostics → CMD
Interaction Tests` menu; the menu inserts the script
invocation into the PTY input cursor so the script invocation
can be reviewed (or edited) before pressing Enter.

Each script is wrapped in start / end markers visible in
`Ctrl+Shift+D` snapshots and the `FileLogger` log:

```
=== PTYSPEAK-TEST-START: <id> ===
...
=== PTYSPEAK-TEST-END: <id> ===
```

The corresponding F# handler emits an INFO log line
(`CmdTest invoked. TestId=<id>`) when the menu item is
clicked, so timing correlation between the menu click and
the resulting `ContentHistory` entries is straightforward.

## The scripts

| File | What it exercises | What to listen for |
|---|---|---|
| `test-01-echo.cmd` | Multi-line `echo` output | All eight numbered lines audible; ready-prompt click at end |
| `test-02-text-input.cmd` | `set /p name=...` text input | Prompt announces; typed name echoes back in greeting |
| `test-03-numeric-input.cmd` | `set /p num=...` + `set /a` arithmetic | Number-plus-one calculation narrates after Enter |
| `test-04-yes-no.cmd` | `choice /c YN` single-keystroke prompt | "Continue? Y, N?" prompt audible; branch result narrates |
| `test-05-multi-choice.cmd` | `choice /c 1234 /n` multi-option | Four options announced; user picks 1-4; chosen branch narrates |
| `test-06-pause.cmd` | `pause` between output sections | First section narrates; pause prompt audible; second section narrates after keypress |
| `test-07-progress.cmd` | Loop with `timeout` between steps | ~5s silent (under `TupleFinalOnly`); all five steps narrate together at tuple finalise |
| `test-08-stderr.cmd` | `>&2` redirected error / warning lines | All lines audible; no audible distinction between stdout and stderr (pty-speak doesn't separate them today) |

## How the menu wiring works

1. Each `.cmd` file is copied flat into `<install-dir>/scripts/cmd-tests/`
   at build time via the `<Content Include="..\..\scripts\cmd-tests\*.cmd">`
   block in
   [`src/Terminal.App/Terminal.App.fsproj`](../../src/Terminal.App/Terminal.App.fsproj).
2. `HotkeyRegistry.AppCommand` has one case per test
   (`CmdTestEcho`, `CmdTestTextInput`, etc., menu-only — no
   keyboard accelerator). See
   [`src/Terminal.Core/HotkeyRegistry.fs`](../../src/Terminal.Core/HotkeyRegistry.fs).
3. `Program.fs runCmdTest <id>` resolves the script path via
   `AppContext.BaseDirectory + scripts/cmd-tests/<id>.cmd`,
   writes a quoted invocation to the PTY via
   `TerminalView.WritePtyBytes`, and announces "Test command
   inserted: `<id>`. Press Enter to run." through
   `ActivityIds.diagnostic`.
4. The corresponding `MainWindow.xaml` `MenuItem` is named
   `MenuItem_CmdTestEcho` etc.; the reflective binding loop
   in `compose()` finds it via
   `window.GetType().GetField(...)` and wires the routed
   command.

The invocation never includes a trailing `\r` — the user has
to press Enter explicitly. This avoids accidental execution
and lets the maintainer add redirects / pipes if useful
(e.g. tack `> result.txt` onto the inserted command).

## Adding a new test

1. Create `scripts/cmd-tests/test-NN-<slug>.cmd` with the
   start / end markers + the interaction body.
2. Add `CmdTest<Slug>` to the `AppCommand` DU,
   `HotkeyRegistry.nameOf`, `HotkeyRegistry.builtIns`,
   and `HotkeyRegistry.allCommands` (all four, per the
   `HotkeyRegistryTests` documented-commands assertion).
3. Add `runCmdTest<Slug> () = runCmdTest "test-NN-<slug>"`
   in `Program.fs` near the other test wrappers.
4. Add `bind HotkeyRegistry.CmdTest<Slug> runCmdTest<Slug>`
   in the same `bind` block.
5. Add `MenuItem_CmdTest<Slug>` to the CMD Interaction
   Tests submenu in `MainWindow.xaml`.
6. Add `HotkeyRegistry.CmdTest<Slug>` to the
   `HotkeyRegistryTests` documented-commands set.
7. Update this README's table.

Each step is small and the discipline forces a reviewer to
acknowledge each addition.
