namespace PtySpeak.App

open System
open System.Windows
open Velopack

module Program =

    /// Composition seam. Stage 4 will plug Elmish.WPF in here.
    let compose (app: Application) (window: Window) : unit =
        ignore app
        ignore window

    [<EntryPoint>]
    [<STAThread>]
    let main _argv =
        // VelopackApp.Build().Run() must execute before any WPF type loads
        // (Velopack issue #195). It returns immediately for normal launches
        // and short-circuits the process during install/update events.
        VelopackApp.Build().Run()

        let app = PtySpeak.Views.App()
        let window = PtySpeak.Views.MainWindow()
        compose app window
        app.Run(window)
