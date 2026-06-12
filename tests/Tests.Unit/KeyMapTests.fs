module PtySpeak.Tests.Unit.KeyMapTests

open Xunit
open Engine.Core.KeyMap

// ---------------------------------------------------------------------
// ADR 0014 C2 — keymap contract: conflict-free defaults (CI is
// the guard), table-driven dispatch, generated help, override
// rules (apply / unknown-verb / conflict-keeps-default).
// ---------------------------------------------------------------------

[<Fact>]
let ``the default bindings are conflict-free`` () =
    Assert.Empty(validate defaults)

[<Fact>]
let ``every verb name is unique`` () =
    let names = defaults |> List.map (fun b -> verbName b.Verb)
    Assert.Equal(List.length names, names |> List.distinct |> List.length)

[<Fact>]
let ``dispatch resolves per mode`` () =
    Assert.Equal(Some Compose, tryFind Transcript 'c' defaults)
    // 'c' is transcript-only.
    Assert.Equal(None, tryFind NotebookMode 'c' defaults)
    // 'x' removes only in notebook mode.
    Assert.Equal(Some NotebookRemove, tryFind NotebookMode 'x' defaults)
    Assert.Equal(None, tryFind Transcript 'x' defaults)
    // Shared keys resolve in both.
    Assert.Equal(Some Quit, tryFind Transcript 'q' defaults)
    Assert.Equal(Some Quit, tryFind NotebookMode 'q' defaults)

[<Fact>]
let ``unbound keys resolve to nothing`` () =
    Assert.Equal(None, tryFind Transcript 'Z' defaults)

[<Fact>]
let ``help is generated from the table and covers every binding`` () =
    for mode in [ Transcript; NotebookMode ] do
        let help = helpFor mode defaults
        for binding in defaults do
            if binding.Modes |> List.contains mode then
                Assert.Contains(binding.Description, help)
                Assert.Contains(string binding.Key, help)

[<Fact>]
let ``an override moves a verb to a new key`` () =
    // 'Z' (uppercase) is free in both modes; lowercase 'z' is
    // structure_summary since the exploration-verbs PR — the
    // conflict path for it is covered below.
    let bindings, warnings =
        withOverrides (Map.ofList [ "pin", 'Z' ]) defaults
    Assert.Empty(warnings)
    Assert.Equal(Some Pin, tryFind Transcript 'Z' bindings)
    Assert.Equal(None, tryFind Transcript 'p' bindings)

[<Fact>]
let ``an unknown verb name warns and is ignored`` () =
    let bindings, warnings =
        withOverrides (Map.ofList [ "frobnicate", 'z' ]) defaults
    Assert.Equal<Binding list>(defaults, bindings)
    match warnings with
    | [ w ] -> Assert.Contains("does not name a verb", w)
    | other -> failwithf "expected one warning, got %A" other

[<Fact>]
let ``a conflicting override warns and keeps the default`` () =
    // 'q' is quit in both modes; rebinding pin onto it must be
    // refused.
    let bindings, warnings =
        withOverrides (Map.ofList [ "pin", 'q' ]) defaults
    Assert.Equal(Some Pin, tryFind Transcript 'p' bindings)
    Assert.True(
        warnings |> List.exists (fun w -> w.Contains "conflicts"))

[<Fact>]
let ``cross-mode-disjoint keys may legitimately repeat`` () =
    // 'e' (last sibling, both modes) vs a hypothetical
    // notebook-only rebinding: moving notebook_remove onto a
    // transcript-only key is fine because the modes never
    // overlap. 'y' (rerun) is transcript-only.
    let bindings, warnings =
        withOverrides (Map.ofList [ "notebook_remove", 'y' ]) defaults
    Assert.Empty(warnings)
    Assert.Equal(Some NotebookRemove, tryFind NotebookMode 'y' bindings)
    Assert.Equal(Some Rerun, tryFind Transcript 'y' bindings)
