namespace PtySpeak.App

open System
open System.Threading
open System.Threading.Tasks
open System.Windows
open System.Windows.Threading
open Velopack
open Terminal.Core
open Terminal.Parser
open Terminal.Pty
open PtySpeak.Views

module Program =

    let private ScreenRows = 30
    let private ScreenCols = 120

    /// Wire a freshly-spawned ConPtyHost into the screen + view. Spawns
    /// a single background task that pulls byte chunks off the host's
    /// stdout channel, feeds them through the parser, and applies the
    /// resulting VtEvents to the screen (on the WPF dispatcher thread
    /// for Stage 3b — Stage 5 will move parser mutation onto a
    /// dedicated thread with snapshot-on-render semantics).
    let private startReaderLoop
            (dispatcher: Dispatcher)
            (host: ConPtyHost)
            (parser: Parser)
            (screen: Screen)
            (view: TerminalView)
            (ct: CancellationToken) : Task =
        Task.Run(fun () ->
            task {
                try
                    while not ct.IsCancellationRequested do
                        let! chunk = host.Stdout.ReadAsync(ct).AsTask()
                        if chunk.Length > 0 then
                            let events = Parser.feedArray parser chunk
                            if events.Length > 0 then
                                let action () =
                                    for e in events do screen.Apply(e)
                                    view.InvalidateScreen()
                                let! _ =
                                    dispatcher
                                        .InvokeAsync(Action(action))
                                        .Task
                                ()
                with
                | :? OperationCanceledException -> ()
                | _ -> ()
            } :> Task)

    /// Composition seam — Stage 4+ plugs Elmish.WPF and the UIA peer
    /// in here. For Stage 3b we just hold references to the long-lived
    /// pieces and ensure they're disposed on Application.Exit.
    let compose (app: Application) (window: MainWindow) : unit =
        let cts = new CancellationTokenSource()
        let screen = Screen(rows = ScreenRows, cols = ScreenCols)
        let parser = Parser.create ()
        window.TerminalSurface.SetScreen(screen)

        let cfg : PtyConfig =
            { Cols = int16 ScreenCols
              Rows = int16 ScreenRows
              CommandLine = "cmd.exe" }

        let mutable hostHandle : ConPtyHost option = None

        // Defer ConPTY spawn until the window has loaded so any startup
        // failure can surface in the UI rather than crashing during
        // Application.OnStartup.
        window.Loaded.Add(fun _ ->
            match ConPtyHost.start cfg with
            | Error _ ->
                // Stage 3b: silently ignore the failure for now —
                // the empty terminal still renders. Stage 5 will add
                // a UIA notification announcing the failure.
                ()
            | Ok host ->
                hostHandle <- Some host
                let _ =
                    startReaderLoop
                        window.Dispatcher
                        host
                        parser
                        screen
                        window.TerminalSurface
                        cts.Token
                ())

        app.Exit.Add(fun _ ->
            try cts.Cancel() with _ -> ()
            match hostHandle with
            | Some h -> (h :> IDisposable).Dispose()
            | None -> ()
            cts.Dispose())

    [<EntryPoint>]
    [<STAThread>]
    let main _argv =
        // VelopackApp.Build().Run() must execute before any WPF type loads
        // (Velopack issue #195). It returns immediately for normal launches
        // and short-circuits the process during install/update events.
        VelopackApp.Build().Run()

        let app = App()
        let window = MainWindow()
        compose app window
        app.Run(window)
