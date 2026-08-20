/// The live view: one accelerator frame in a WriteableBitmap, the render
/// button that asks for another, and the two numbers that are the point —
/// fabric time and round trip. The view knows only IMandelBus; which world
/// is behind it is Program.fs's business.
///
/// Deliberately a fixed view, matching what the Kotlin app shipped. Pan and
/// zoom were built and measured here first (2026-08-13) and then removed:
/// they worked, but `MAX_ITER` is fixed at 256 in the elaborated pod, so a
/// few wheel notches toward the boundary is all it takes for every pixel to
/// hit the cap and the frame to go black. That is a bitstream limit, not a
/// UI one, and a control that degrades the picture is worse than no control.
/// Making `maxIter` a register is what would earn them back.
module Warp11.MandelView.View

open Avalonia
open Avalonia.Controls
open Avalonia.FuncUI
open Avalonia.FuncUI.DSL
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Media.Imaging
open Avalonia.Platform
open Avalonia.Threading
open Warp11.MandelView.Bus

/// The whole set: the rectangle `mandel_frame_first_light` and the daemon
/// both default to. Its 1.75 aspect matches the fabric's 1400x800, so a
/// pixel is square.
type Rect =
    { cx: float
      cy: float
      spanX: float
      spanY: float }

let home =
    { cx = -2.5
      cy = -1.0
      spanX = 3.5
      spanY = 2.0 }

let private toView (rect: Rect) (width: int) (height: int) =
    { cxOrigin = toQ rect.cx
      cyOrigin = toQ rect.cy
      dx = toQ (rect.spanX / float width)
      dy = toQ (rect.spanY / float height) }

// ---- colour ----

let private hsvToArgb (hue: float) (s: float) (v: float) =
    let h = (hue % 360.0) / 60.0
    let c = v * s
    let x = c * (1.0 - abs (h % 2.0 - 1.0))
    let m = v - c

    let r, g, b =
        match int h with
        | 0 -> c, x, 0.0
        | 1 -> x, c, 0.0
        | 2 -> 0.0, c, x
        | 3 -> 0.0, x, c
        | 4 -> x, 0.0, c
        | _ -> c, 0.0, x

    let channel value = int ((value + m) * 255.0 + 0.5)
    0xFF000000 ||| (channel r <<< 16) ||| (channel g <<< 8) ||| channel b

/// 256 entries, the Kotlin app's palette: hue cycling five times across the
/// escape range so deep bands stay distinguishable, and the interior — the
/// pixels that never escaped — black.
let private palette =
    [| for i in 0..255 ->
           if i >= 255 then
               0xFF000000
           else
               hsvToArgb (float i / 256.0 * 5.0 * 360.0) 0.85 1.0 |]

/// A fresh bitmap per frame: mutating a live WriteableBitmap does not
/// invalidate the Image showing it — the state swap is what makes the
/// control repaint. (The lesson GoL's view already paid for.)
let private renderFrame (frame: MandelFrame) : WriteableBitmap =
    let bitmap =
        new WriteableBitmap(
            PixelSize(frame.width, frame.height),
            Vector(96.0, 96.0),
            PixelFormat.Bgra8888,
            AlphaFormat.Opaque
        )

    use buffer = bitmap.Lock()
    let line = Array.zeroCreate<int> frame.width

    for y in 0 .. frame.height - 1 do
        let rowBase = y * frame.width

        for x in 0 .. frame.width - 1 do
            line[x] <- palette[int frame.pixels[rowBase + x]]

        System.Runtime.InteropServices.Marshal.Copy(
            line,
            0,
            buffer.Address + nativeint (y * buffer.RowBytes),
            line.Length
        )

    bitmap

let private blankFrame width height =
    { view = toView home width height
      cycles = 0u
      width = width
      height = height
      maxIter = 256
      pixels = Array.zeroCreate (width * height) }

// ---- the view ----

let view (bus: IMandelBus) (frameWidth: int) (frameHeight: int) =
    Component(fun ctx ->
        let frame = ctx.useState (blankFrame frameWidth frameHeight)
        let bitmap = ctx.useState (renderFrame (blankFrame frameWidth frameHeight))
        let roundTripMs = ctx.useState 0.0
        let awaiting = ctx.useState false
        let requestedAt = ctx.useState 0L
        // The view most recently asked for. The daemon coalesces requests, so
        // a frame answering an earlier one is worth drawing but not worth
        // timing — measuring it against a request it never answered inflates
        // the number by however long the user kept clicking.
        let requestedView = ctx.useState (toView home frameWidth frameHeight)

        let request () =
            let wanted = toView home frameWidth frameHeight
            requestedView.Set wanted
            requestedAt.Set(System.Diagnostics.Stopwatch.GetTimestamp())
            awaiting.Set true
            bus.Render wanted

        ctx.useEffect (
            handler =
                (fun () ->
                    let subscription =
                        bus.FrameReceived.Subscribe(fun received ->
                            Dispatcher.UIThread.Post(fun () ->
                                bitmap.Set(renderFrame received)
                                frame.Set received

                                if received.view = requestedView.Current then
                                    awaiting.Set false

                                    let elapsed =
                                        float (System.Diagnostics.Stopwatch.GetTimestamp() - requestedAt.Current)
                                        / float System.Diagnostics.Stopwatch.Frequency

                                    roundTripMs.Set(elapsed * 1e3)))
                    // Ask for the opening frame; without it the window sits
                    // blank until the user touches something.
                    request ()
                    subscription),
            triggers = [ EffectTrigger.AfterInit ]
        )

        let statusText =
            let px = float (frame.Current.width * frame.Current.height)

            let fabric =
                if bus.FabricHz > 0.0 && frame.Current.cycles > 0u then
                    let ms = float frame.Current.cycles / bus.FabricHz * 1e3
                    let mpps = px / (ms / 1e3) / 1e6
                    $"%d{frame.Current.cycles} cycles = %.2f{ms} ms fabric · %.0f{mpps} Mpx/s · "
                else
                    ""

            $"{fabric}%.0f{roundTripMs.Current} ms round trip"

        DockPanel.create
            [ DockPanel.children
                  [ StackPanel.create
                        [ StackPanel.dock Dock.Bottom
                          StackPanel.orientation Orientation.Horizontal
                          StackPanel.spacing 8.0
                          StackPanel.margin 8.0
                          StackPanel.children
                              // Stays enabled while rendering: a request that
                              // is never answered would otherwise lock the one
                              // control that could recover from it.
                              [ Button.create
                                    [ Button.content (if awaiting.Current then "Rendering…" else "Render")
                                      Button.onClick (fun _ -> request ()) ]
                                TextBlock.create
                                    [ TextBlock.verticalAlignment VerticalAlignment.Center
                                      TextBlock.fontFamily "monospace"
                                      TextBlock.text statusText ]
                                TextBlock.create
                                    [ TextBlock.verticalAlignment VerticalAlignment.Center
                                      TextBlock.opacity 0.6
                                      TextBlock.text $"   {bus.Describe}" ] ] ]
                    Border.create
                        [ Border.margin 8.0
                          Border.background (SolidColorBrush(Color.FromRgb(0x11uy, 0x11uy, 0x11uy)))
                          Border.child (
                              Image.create
                                  [ Image.source bitmap.Current
                                    Image.stretch Stretch.Uniform ]
                          ) ] ] ])
