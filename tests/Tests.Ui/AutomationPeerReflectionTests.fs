module PtySpeak.Tests.Ui.AutomationPeerReflectionTests

open System.Reflection
open System.Windows.Automation.Peers
open Xunit
open Xunit.Abstractions

/// Spike #2 from the Stage 4 follow-up plan: probe whether
/// `FrameworkElementAutomationPeer.GetPatternCore` is reachable
/// via reflection at runtime, even though it's not reachable at
/// compile time from a subclass override (CS0117 / FS0855 from
/// PRs #47, #48, #50).
///
/// Two possible outcomes, each with clear architectural
/// implications for Issue #49's Text-pattern exposure:
///
///   * Method found → the runtime metadata for
///     `FrameworkElementAutomationPeer` has `GetPatternCore`;
///     only the public reference assembly we compile against
///     strips it. Reflection-based binding (option 3 from
///     Issue #49) becomes a real-if-brittle path: load the
///     method via reflection, install our pattern map, dispatch
///     `GetPattern` calls through it. Brittle because runtime
///     metadata could change in a future .NET update; viable
///     because the same is true for any reflection-based
///     interop and Microsoft's own peers use the method at
///     runtime.
///
///   * Method NOT found → the runtime hides the method too,
///     not just the ref assembly. Reflection-based binding is
///     not viable; pattern exposure HAS to go through a
///     non-AutomationPeer mechanism (raw
///     `IRawElementProviderSimple` via `WM_GETOBJECT` hook).
///
/// The test always prints its findings via ITestOutputHelper
/// regardless of pass/fail so the merged PR captures the
/// diagnostic in CI logs that future readers can consult.

type AutomationPeerReflectionTests(output: ITestOutputHelper) =

    /// Sanity check: a method we KNOW is reachable via
    /// reflection (the spike PR #47 successfully overrode it)
    /// should also be findable. If THIS fails, the test
    /// infrastructure itself is wrong, not our specific probe.
    [<Fact>]
    member _.``GetClassNameCore is findable via reflection (sanity baseline)`` () =
        let t = typeof<FrameworkElementAutomationPeer>
        let flags =
            BindingFlags.Instance
            ||| BindingFlags.NonPublic
            ||| BindingFlags.Public
        let m = t.GetMethod("GetClassNameCore", flags)
        match m with
        | null ->
            output.WriteLine(
                "Sanity baseline FAILED: GetClassNameCore not found via reflection.")
            Assert.Fail(
                "Reflection sanity baseline failed — GetClassNameCore should always be findable.")
        | found ->
            output.WriteLine(
                sprintf
                    "Sanity baseline PASSED: GetClassNameCore is %s%s%s, declared on %s, returns %s."
                    (if found.IsPublic then "public " else "")
                    (if found.IsFamily then "protected " else "")
                    (if found.IsAssembly then "internal " else "")
                    found.DeclaringType.FullName
                    found.ReturnType.FullName)

    /// The actual question. Probes for `GetPatternCore` on
    /// `FrameworkElementAutomationPeer` and its base types using
    /// `BindingFlags.FlattenHierarchy` so an inherited method
    /// counts.
    [<Fact>]
    member _.``GetPatternCore reflection probe (the architectural question)`` () =
        let t = typeof<FrameworkElementAutomationPeer>
        let flags =
            BindingFlags.Instance
            ||| BindingFlags.NonPublic
            ||| BindingFlags.Public
            ||| BindingFlags.FlattenHierarchy

        // Use GetMethods (plural) to find ALL methods named
        // GetPatternCore including ones inherited from
        // UIElementAutomationPeer / AutomationPeer.
        let candidates =
            t.GetMethods(flags)
            |> Array.filter (fun m -> m.Name = "GetPatternCore")

        if candidates.Length = 0 then
            output.WriteLine(
                "GetPatternCore is NOT findable via reflection on FrameworkElementAutomationPeer (or its bases). The runtime metadata strips it the same way the public reference assembly does. Reflection-based binding is not viable; option 1 (raw IRawElementProviderSimple via WM_GETOBJECT) is the only remaining Text-pattern exposure path.")
            Assert.Fail(
                "GetPatternCore not findable via reflection — see test output for architectural implication.")
        else
            for m in candidates do
                let access =
                    if m.IsPublic then "public"
                    elif m.IsFamily then "protected"
                    elif m.IsFamilyOrAssembly then "protected internal"
                    elif m.IsFamilyAndAssembly then "private protected"
                    elif m.IsAssembly then "internal"
                    elif m.IsPrivate then "private"
                    else "(unknown access)"
                let modifiers =
                    [ if m.IsAbstract then yield "abstract"
                      if m.IsVirtual then yield "virtual"
                      if m.IsFinal then yield "sealed" ]
                    |> String.concat " "
                let parameters =
                    m.GetParameters()
                    |> Array.map (fun p ->
                        sprintf "%s %s" p.ParameterType.Name p.Name)
                    |> String.concat ", "
                output.WriteLine(
                    sprintf
                        "Found: %s %s %s.%s(%s) -> %s [DeclaringType assembly: %s]"
                        access
                        modifiers
                        m.DeclaringType.FullName
                        m.Name
                        parameters
                        m.ReturnType.FullName
                        m.DeclaringType.Assembly.GetName().Name)
            output.WriteLine(
                sprintf
                    "GetPatternCore IS findable via reflection (%d match%s). The runtime metadata has the method even though the public reference assembly strips it. Reflection-based binding is viable as a Text-pattern exposure path; option 1 (raw IRawElementProviderSimple) and option 3 (reflection hook) are both architecturally available."
                    candidates.Length
                    (if candidates.Length = 1 then "" else "es"))
