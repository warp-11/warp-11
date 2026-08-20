/// The live view: a 64×64 frame drawn into a WriteableBitmap (one pixel per
/// cell, scaled up nearest-neighbor — 4096 controls would be soup), a column
/// of controls down one side, and the counters the fabric keeps. The view
/// knows only IGolBus; which world is behind it is Program.fs's business.
module Warp11.GolView.View

open Avalonia
open Avalonia.Controls
open Avalonia.FuncUI
open Avalonia.FuncUI.DSL
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Media.Imaging
open Avalonia.Platform
open Avalonia.Threading
open Warp11.GolView.Bus

let private alive = 0xFF3FB618u // ARGB — the living green
let private dead = 0xFF101418u

/// A fresh bitmap per frame: mutating a live WriteableBitmap does not
/// invalidate the Image showing it, and at 64×64×4 bytes a new one per frame
/// is nothing — the state swap is what makes the control repaint.
let private renderFrame (rows: uint64[]) : WriteableBitmap =
    let bitmap =
        new WriteableBitmap(PixelSize(64, 64), Vector(96.0, 96.0), PixelFormat.Bgra8888, AlphaFormat.Opaque)

    use fb = bitmap.Lock()

    for y in 0..63 do
        let line =
            [| for x in 0..63 -> int (if (rows[y] >>> x) &&& 1UL = 1UL then alive else dead) |]

        System.Runtime.InteropServices.Marshal.Copy(
            line,
            0,
            fb.Address + nativeint (y * fb.RowBytes),
            line.Length
        )

    bitmap

/// Generations/second, measured where every engine looks identical: the
/// generation counter's advance between published frames, wall-clocked.
/// uint32 subtraction survives the counter wrapping (25 s at fabric speed).
let private formatRate (r: float) =
    if r >= 1e6 then sprintf "%.1fM" (r / 1e6)
    elif r >= 1e3 then sprintf "%.1fk" (r / 1e3)
    else sprintf "%.0f" r

/// The target rate the slider carries, as a power of ten.
///
/// Nine decades on one track, so the slider holds log10 of the rate rather
/// than the rate: linear, everything below a million would live in the bottom
/// tenth of a percent of the travel, and the settings anyone actually watches
/// — one a second, ten a second, a thousand — would be unreachable.
let private minDecade = 0.0
let private maxDecade = 9.0

/// The top of the track is *unpaced*, not a billion a second. The fabric does
/// 500M generations/s and the software twin nothing like it, so asking for a
/// billion is asking for as fast as it goes — which the bus already spells
/// `Run 0`.
let private isFlatOut decade = decade >= maxDecade

let private targetRate decade = 10.0 ** decade

let private describeTarget decade =
    if isFlatOut decade then
        "flat out"
    else
        $"%s{formatRate (targetRate decade)}/s"

let private randomSoup () =
    let rand = System.Random()
    [| for _ in 0..63 -> uint64 (rand.NextInt64()) |]

let private glider () =
    let rows = Array.zeroCreate<uint64> 64
    rows[1] <- 0b010UL <<< 30
    rows[2] <- 0b100UL <<< 30
    rows[3] <- 0b111UL <<< 30
    rows

/// `openDebugger` is `Some` only when the world behind the bus is one that can
/// be stepped — which is the composition root's knowledge, not the view's, and
/// certainly not `IGolBus`'s. `autoRun` starts the engine on arrival — for a
/// host that is a demonstration rather than an instrument, where a grid
/// holding still until someone finds the Run button reads as broken.
let view (bus: IGolBus) (openDebugger: (unit -> unit) option) (autoRun: bool) =
    Component(fun ctx ->
        let generation = ctx.useState 0u
        let population = ctx.useState 0u
        // Where the slider is, in decades. Ten a second to start: fast enough
        // to be alive, slow enough to watch a glider move.
        let decade = ctx.useState 1.0
        let running = ctx.useState autoRun
        let rate = ctx.useState 0.0
        let lastSample = ctx.useState ((0u, 0L))
        let bitmap = ctx.useState (renderFrame (Array.zeroCreate 64))
        let palette = Warp11.SimView.Theme.ofVariant (Warp11.SimView.Theme.currentVariant ())

        /// Forget the rate measurement, for a command that puts the generation
        /// counter back to zero.
        ///
        /// The counter is uint32 and the measurement subtracts in uint32 so
        /// that a wrap — 25 seconds at fabric speed — reads as a small step
        /// forward rather than a huge one back. A *reset* is indistinguishable
        /// from a wrap in that arithmetic, and reads as four billion
        /// generations since the last frame. The view is the one that knows the
        /// difference, because the view is what asked.
        let rebaseline () =
            rate.Set 0.0
            lastSample.Set(0u, 0L)

        /// Start, or re-pace something already running. The bus takes
        /// generations per second and reads zero as unpaced.
        let drive decade =
            if isFlatOut decade then
                bus.Run 0u
            else
                bus.Run(uint32 (round (targetRate decade)))

        ctx.useEffect (
            handler =
                (fun () ->
                    if autoRun then drive decade.Current

                    bus.FrameReceived.Subscribe(fun frame ->
                        Dispatcher.UIThread.Post(fun () ->
                            bitmap.Set(renderFrame frame.rows)
                            generation.Set frame.generation
                            population.Set frame.population

                            let lastGen, lastTicks = lastSample.Current
                            let nowTicks = System.Diagnostics.Stopwatch.GetTimestamp()
                            let dt = float (nowTicks - lastTicks) / float System.Diagnostics.Stopwatch.Frequency

                            if lastTicks <> 0L && dt > 0.0 then
                                let sample = float (frame.generation - lastGen) / dt
                                rate.Set(0.7 * rate.Current + 0.3 * sample)

                            lastSample.Set(frame.generation, nowTicks)))),
            triggers = [ EffectTrigger.AfterInit ]
        )

        let patternButton label load =
            Button.create
                [ Button.content (label: string)
                  Button.horizontalAlignment HorizontalAlignment.Stretch
                  Button.horizontalContentAlignment HorizontalAlignment.Center
                  Button.onClick (fun _ -> load ()) ]

        /// One counter: what it is, and what it reads. The number is monospaced
        /// so that a population climbing through four digits does not shuffle
        /// the label beside it.
        let counter name value =
            DockPanel.create
                [ DockPanel.children
                      [ TextBlock.create
                            [ TextBlock.dock Dock.Left
                              TextBlock.width 42.0
                              TextBlock.fontSize 11.0
                              TextBlock.foreground palette.muted
                              TextBlock.text (name: string) ]
                        TextBlock.create
                            [ TextBlock.fontFamily (FontFamily "monospace")
                              TextBlock.horizontalAlignment HorizontalAlignment.Right
                              TextBlock.text (value: string) ] ] ]

        DockPanel.create
            [ DockPanel.children
                  [ StackPanel.create
                        [ StackPanel.dock Dock.Right
                          StackPanel.width 168.0
                          StackPanel.margin (Thickness(0.0, 8.0, 8.0, 8.0))
                          StackPanel.spacing 10.0
                          StackPanel.children
                              [ Warp11.SimView.Controls.fieldset
                                    palette
                                    "run"
                                    (DockPanel.create
                                        [ DockPanel.children
                                              [ Button.create
                                                    [ Button.dock Dock.Top
                                                      Button.content (if running.Current then "Stop" else "Run")
                                                      Button.horizontalAlignment HorizontalAlignment.Stretch
                                                      Button.horizontalContentAlignment HorizontalAlignment.Center
                                                      Button.onClick (fun _ ->
                                                          if running.Current then
                                                              bus.Stop()
                                                              running.Set false
                                                          else
                                                              drive decade.Current
                                                              running.Set true) ]
                                                TextBlock.create
                                                    [ TextBlock.dock Dock.Bottom
                                                      TextBlock.horizontalAlignment HorizontalAlignment.Center
                                                      TextBlock.margin (Thickness(0.0, 6.0, 0.0, 0.0))
                                                      TextBlock.fontFamily (FontFamily "monospace")
                                                      TextBlock.text (describeTarget decade.Current) ]
                                                Slider.create
                                                    [ Slider.orientation Orientation.Vertical
                                                      Slider.minimum minDecade
                                                      Slider.maximum maxDecade
                                                      // One tick per decade, so
                                                      // the track reads as the
                                                      // nine orders of magnitude
                                                      // it is.
                                                      Slider.tickFrequency 1.0
                                                      Slider.tickPlacement TickPlacement.Outside
                                                      Slider.isSnapToTickEnabled false
                                                      Slider.height 200.0
                                                      Slider.horizontalAlignment HorizontalAlignment.Center
                                                      Slider.margin (Thickness(0.0, 8.0, 0.0, 0.0))
                                                      Slider.value decade.Current
                                                      Slider.onValueChanged (fun v ->
                                                          decade.Set v
                                                          // Only re-paces what is
                                                          // already going: dragging
                                                          // the target should not
                                                          // start a stopped design.
                                                          if running.Current then drive v) ] ] ])

                                Warp11.SimView.Controls.fieldset
                                    palette
                                    "pattern"
                                    (StackPanel.create
                                        [ StackPanel.spacing 6.0
                                          StackPanel.children
                                              [ patternButton "Soup" (fun () ->
                                                    bus.Load(randomSoup ())
                                                    rebaseline ())
                                                patternButton "Glider" (fun () ->
                                                    bus.Load(glider ())
                                                    rebaseline ())
                                                patternButton "Clear" (fun () ->
                                                    // Reset stops the engine as
                                                    // well as clearing it, on
                                                    // every bus.
                                                    bus.Reset()
                                                    running.Set false
                                                    rebaseline ()) ] ])

                                Warp11.SimView.Controls.fieldset
                                    palette
                                    "counters"
                                    (StackPanel.create
                                        [ StackPanel.spacing 4.0
                                          StackPanel.children
                                              [ counter "gen" $"%d{generation.Current}"
                                                counter "pop" $"%d{population.Current}"
                                                counter "gens/s" (formatRate rate.Current) ] ])

                                if openDebugger.IsSome then
                                    Button.create
                                        [ Button.content "Debugger"
                                          Button.horizontalAlignment HorizontalAlignment.Stretch
                                          Button.horizontalContentAlignment HorizontalAlignment.Center
                                          Button.onClick (fun _ -> openDebugger.Value()) ] ] ]
                    Border.create
                        [ Border.margin 8.0
                          Border.child (
                              Image.create
                                  [ Image.source bitmap.Current
                                    Image.stretch Stretch.Uniform
                                    // Cells are pixels: never smooth them.
                                    Image.init (fun img ->
                                        RenderOptions.SetBitmapInterpolationMode(
                                            img,
                                            BitmapInterpolationMode.None
                                        )) ]
                          ) ] ] ])
