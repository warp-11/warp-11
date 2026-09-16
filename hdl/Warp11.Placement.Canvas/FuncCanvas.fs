/// The canvas, in FuncUI: a `Graph` drawn as boxes, pins and wires, and
/// edited in place. Everything renders from the state — the graph, where the
/// boxes are, the pan and zoom, what is selected, what is being dragged —
/// which is the property the debugger needs: a snapshot of the design's
/// signals will one day be one more input to the same render.
///
/// Geometry lives in world coordinates; one transform on the inner canvas
/// puts it on screen, and pointer positions come back through its inverse.
/// Hit-testing is our own arithmetic over the same geometry, pins before
/// boxes, so nothing depends on which child control caught the event.
module Warp11.Placement.Canvas.FuncCanvas

open System.Globalization
open Warp11
open Avalonia
open Avalonia.Controls
open Avalonia.Controls.Shapes
open Avalonia.FuncUI
open Avalonia.FuncUI.DSL
open Avalonia.Input
open Avalonia.Media
open Avalonia.VisualTree
open Warp11.Placement.Fu
open Warp11.Placement.Graph

// ---------------------------------------------------------------------------
// Geometry, in world units.

let boxWidth = 170.0
let headerHeight = 34.0
let rowHeight = 26.0
let pinRadius = 6.0
let pinHitRadius = 10.0

/// A box's height, from its taller pin column.
let boxHeight (g: Graph) (box: string) =
    let inputs, outputs = pinsOf g box
    headerHeight + float (max inputs.Length outputs.Length) * rowHeight + 10.0

/// Where a pin sits, relative to its box's top-left: inputs down the left
/// edge, outputs down the right.
let private pinOffset (g: Graph) (p: PinRef) (side: Side) : (float * float) option =
    let inputs, outputs = pinsOf g p.box

    let row (pins: (string * NumberFormat) list) =
        pins |> List.tryFindIndex (fun (n, _) -> n = p.pin)

    match side with
    | In -> row inputs |> Option.map (fun r -> 0.0, headerHeight + float r * rowHeight + rowHeight / 2.0)
    | Out -> row outputs |> Option.map (fun r -> boxWidth, headerHeight + float r * rowHeight + rowHeight / 2.0)

/// A pin's centre in world coordinates.
let pinCentre (g: Graph) (positions: Map<string, float * float>) (p: PinRef) (side: Side) : (float * float) option =
    match Map.tryFind p.box positions, pinOffset g p side with
    | Some(bx, by), Some(dx, dy) -> Some(bx + dx, by + dy)
    | _ -> None

/// Boxes in the order a chain reads: input, the design's boxes, output.
let boxOrder (g: Graph) = "input" :: (g.boxes |> List.map (fun b -> b.name)) @ [ "output" ]

/// The first layout: one column per box, left to right. Positions are canvas
/// state for now (Q: they belong in the `Graph` once a GUI saves one).
let initialPositions (g: Graph) : Map<string, float * float> =
    boxOrder g |> List.mapi (fun i name -> name, (60.0 + float i * 260.0, 120.0)) |> Map.ofList

// ---------------------------------------------------------------------------
// What the pointer can be doing.

type Drag =
    | MovingBox of box: string * grabOffset: (float * float)
    | Panning of lastScreen: (float * float)
    | Wiring of from: (PinRef * Side) * atWorld: (float * float)

/// A pin under the pointer, or a box, or nothing — pins first, because a pin
/// sits on its box's edge and a wire is the rarer, more deliberate gesture.
type Hit =
    | HitPin of PinRef * Side
    | HitBox of string
    | HitNothing

let hitTest (g: Graph) (positions: Map<string, float * float>) (wx: float, wy: float) : Hit =
    let pins =
        [ for box in boxOrder g do
              let inputs, outputs = pinsOf g box

              for pins, side in [ inputs, In; outputs, Out ] do
                  for n, _ in pins do
                      let p = pin box n

                      match pinCentre g positions p side with
                      | Some(px, py) when (px - wx) ** 2.0 + (py - wy) ** 2.0 <= pinHitRadius ** 2.0 -> yield p, side
                      | _ -> () ]

    match pins with
    | (p, side) :: _ -> HitPin(p, side)
    | [] ->
        // Later boxes draw on top, so they win a hit.
        boxOrder g
        |> List.rev
        |> List.tryFind (fun box ->
            match Map.tryFind box positions with
            | Some(bx, by) -> wx >= bx && wx <= bx + boxWidth && wy >= by && wy <= by + boxHeight g box
            | None -> false)
        |> Option.map HitBox
        |> Option.defaultValue HitNothing

// ---------------------------------------------------------------------------
// The wire check: what the elaborator would refuse, said before the wire is
// drawn. Direction, format, and one wire per input.

let private formatOf (g: Graph) (p: PinRef) (side: Side) : NumberFormat option =
    let inputs, outputs = pinsOf g p.box
    (match side with In -> inputs | Out -> outputs) |> List.tryFind (fun (n, _) -> n = p.pin) |> Option.map snd

let private describeFormat (f: NumberFormat) =
    let sign = if f.signed then "signed" else "unsigned"
    $"%d{f.totalWidth}w/%d{f.fracBits}f/{sign}"

/// Either the edge to add, or why not. An edge always runs output → input,
/// whichever end the gesture started from.
let connect (g: Graph) (a: PinRef, sa: Side) (b: PinRef, sb: Side) : Result<Edge, string> =
    match formatOf g a sa, formatOf g b sb with
    | None, _ -> Error $"{a.box}.{a.pin}: no such pin"
    | _, None -> Error $"{b.box}.{b.pin}: no such pin"
    | Some fa, Some fb ->
        if sa = sb then
            Error(if sa = In then "both pins are inputs" else "both pins are outputs")
        elif a.box = b.box then
            Error $"{a.box}: a box cannot feed itself"
        else
            let src, dst = if sa = In then b, a else a, b

            if fa <> fb then
                Error $"{src.box}.{src.pin} is {describeFormat fa}, {dst.box}.{dst.pin} is {describeFormat fb}"
            elif g.edges |> List.exists (fun e -> e.``to`` = dst) then
                Error $"{dst.box}.{dst.pin} already has a wire"
            else
                Ok { from = src; ``to`` = dst }

// ---------------------------------------------------------------------------
// Drawing.

let private inv (x: float) = x.ToString("0.##", CultureInfo.InvariantCulture)

/// A wire from an output pin to an input pin: a cubic that leaves and arrives
/// horizontally, with a bend that grows with the distance.
let wirePath (x1: float, y1: float) (x2: float, y2: float) =
    let dx = max 40.0 (abs (x2 - x1) / 2.0)
    $"M {inv x1},{inv y1} C {inv (x1 + dx)},{inv y1} {inv (x2 - dx)},{inv y2} {inv x2},{inv y2}"

let private wireBrush = SolidColorBrush(Color.FromRgb(200uy, 40uy, 40uy))
let private pendingBrush = SolidColorBrush(Color.FromRgb(120uy, 120uy, 140uy))
let private faintBrush = SolidColorBrush(Color.FromRgb(225uy, 190uy, 190uy))
let private boxFill = SolidColorBrush(Color.FromRgb(250uy, 250uy, 252uy))
let private boxStroke = SolidColorBrush(Color.FromRgb(120uy, 130uy, 160uy))
let private selectedStroke = SolidColorBrush(Color.FromRgb(30uy, 120uy, 220uy))
let private pinFill = Brushes.White
let private pinStroke = Brushes.Black

let private wireView (_key: string) (brush: IBrush) (a: float * float) (b: float * float) : Types.IView =
    Path.create
        [ Path.data (Geometry.Parse(wirePath a b))
          Path.stroke brush
          Path.strokeThickness 2.0
          Path.isHitTestVisible false ]
    :> Types.IView

let private boxView (g: Graph) (selected: string option) (name: string) (bx: float, by: float) : Types.IView list =
    let inputs, outputs = pinsOf g name

    let title, subtitle =
        match name with
        | "input" -> "input", $"%d{g.streams} stream(s)"
        | "output" -> "output", ""
        | _ ->
            let b = g.boxes |> List.find (fun b -> b.name = name)
            let u = palette[b.unit]
            let sequential =
                match u.law with
                | Sequential _ -> " (sequential)"
                | Combinational _ -> ""

            name, $"{b.unit} × %d{b.copies}" + sequential

    let body =
        Border.create
            [ Canvas.left bx
              Canvas.top by
              Border.width boxWidth
              Border.height (boxHeight g name)
              Border.background boxFill
              Border.borderBrush (if selected = Some name then selectedStroke :> IBrush else boxStroke :> IBrush)
              Border.borderThickness (Thickness(if selected = Some name then 2.0 else 1.0))
              Border.cornerRadius (CornerRadius 6.0)
              Border.isHitTestVisible false
              Border.child (
                  StackPanel.create
                      [ StackPanel.margin (Thickness(10.0, 6.0))
                        StackPanel.children
                            [ TextBlock.create [ TextBlock.text title; TextBlock.fontWeight FontWeight.Bold ]
                              TextBlock.create
                                  [ TextBlock.text subtitle
                                    TextBlock.foreground Brushes.Gray
                                    TextBlock.fontSize 11.0 ] ] ]
              ) ]
        :> Types.IView

    let pinViews =
        [ for pins, isInput in [ inputs, true; outputs, false ] do
              for row, (pinName, _) in List.indexed pins do
                  let px = bx + (if isInput then 0.0 else boxWidth)
                  let py = by + headerHeight + float row * rowHeight + rowHeight / 2.0

                  yield
                      Ellipse.create
                          [ Canvas.left (px - pinRadius)
                            Canvas.top (py - pinRadius)
                            Ellipse.width (2.0 * pinRadius)
                            Ellipse.height (2.0 * pinRadius)
                            Ellipse.fill pinFill
                            Ellipse.stroke pinStroke
                            Ellipse.strokeThickness 1.5
                            Ellipse.isHitTestVisible false ]
                      :> Types.IView

                  // The pin's name, inside the box beside it.
                  yield
                      TextBlock.create
                          [ Canvas.left (if isInput then px + pinRadius + 4.0 else px - pinRadius - 4.0 - 7.0 * float pinName.Length)
                            Canvas.top (py - 7.0)
                            TextBlock.text pinName
                            TextBlock.fontSize 11.0
                            TextBlock.foreground Brushes.DimGray
                            TextBlock.isHitTestVisible false ]
                      :> Types.IView ]

    body :: pinViews

// ---------------------------------------------------------------------------
// A running patch behind the canvas.

/// The recording on the boundary, as the canvas reads it: progress, and
/// what has been heard so far.
type Recording =
    { framesOffered: unit -> int
      remaining: unit -> int
      heard: unit -> WavData option }

/// What the canvas needs to paint a design that is running: the session to
/// drive and read, the recording playing into it (if one is), a speaker (if
/// there is one), the design's controls, and the nets each pin rides on.
type Live =
    { session: Warp11.Debug.IDebugSession
      recording: Recording option
      /// A speaker behind the output box. With one, Play is a free run the
      /// speaker paces; without, the timer paces it.
      audio: AudioSink.AudioSink option
      controls: (string * NumberFormat) list
      /// The net a pin's value is on, for the watch list.
      probeOf: PinRef -> Side -> string option
      /// The net that says a box's pins on one side carry a beat this cycle.
      validOf: string -> Side -> string
      /// Every signal of the design that belongs to a box, by the box's name —
      /// its stage's registers and the module instance inside it.
      signalsOf: string -> string list
      /// Where "save what was heard" writes.
      savePath: string option
      /// Frames a second at real time — one frame is one beat is one cycle here.
      framesPerSecond: int
      /// The same patch from the start, with these control values: a fresh
      /// session with the recording rewound. `Reset` is this, since a device
      /// has no rewind of its own — and the knobs stay where they were.
      reopen: (string * uint64) list -> Live }

let private valueOf (snapshot: Warp11.Debug.Snapshot) (name: string) : System.Numerics.BigInteger option =
    snapshot.values |> List.tryFind (fun v -> v.name = name) |> Option.map (fun v -> v.value)

/// A value as the pin's format reads it: signed two's complement, or plain.
let private showValue (f: NumberFormat) (v: System.Numerics.BigInteger) =
    let signed =
        if f.signed && (v >>> (f.totalWidth - 1)) &&& System.Numerics.BigInteger.One = System.Numerics.BigInteger.One then
            v - (System.Numerics.BigInteger.One <<< f.totalWidth)
        else
            v

    if f.fracBits = 0 then
        signed.ToString()
    else
        (float signed / float (1L <<< f.fracBits)).ToString("0.####", CultureInfo.InvariantCulture)

let private formatOfPin (g: Graph) (p: PinRef) (side: Side) : NumberFormat option =
    let ins, outs = pinsOf g p.box
    (match side with In -> ins | Out -> outs) |> List.tryFind (fun (n, _) -> n = p.pin) |> Option.map snd

// ---------------------------------------------------------------------------
// The component.

/// The canvas over a graph. `initial` is what it opens on; the graph the user
/// edits is the component's own state from then on. With `live`, the design
/// is running behind the canvas: values are painted on the wires, a box's
/// signals are listed when it is selected, and the toolbar drives the run.
let view (initial: Graph) (live: Live option) : Control =
    Component(fun ctx ->
        let graph = ctx.useState initial
        let positions = ctx.useState (initialPositions initial)
        let pan = ctx.useState ((0.0, 0.0))
        let zoom = ctx.useState 1.0
        let selected = ctx.useState<string option> None
        let drag = ctx.useState<Drag option> None
        let message = ctx.useState ""

        let blankSnapshot: Warp11.Debug.Snapshot =
            { cycle = 0
              running = false
              rate = 0.0
              values = []
              sampled = Map.empty
              breakpoints = []
              memory = None
              recording = false
              recorded = 0
              capacity = 0
              hit = None }

        // The running patch is state, because `Reset` replaces it.
        let liveState = ctx.useState live
        let snapshot = ctx.useState (live |> Option.map (fun l -> l.session.Latest) |> Option.defaultValue blankSnapshot)
        let controlText = ctx.useState Map.empty<string, string>
        // Playing: the timer steps the session as many frames as real time
        // has passed — Pure Data's "DSP on" — so a knob turned mid-file is
        // heard mid-file. `Run` is the free-running alternative.
        let playing = ctx.useState false
        let tickMs = 33.0

        // Polling the latest snapshot at frame rate, as the debugger does: the
        // session decides how often a snapshot is worth taking, and nothing is
        // marshalled across threads. The same timer pumps a session that has
        // no thread of its own (the browser), and paces a playing one.
        ctx.useEffect (
            handler =
                (fun () ->
                    match live with
                    | None -> ()
                    | Some _ ->
                        Avalonia.Threading.DispatcherTimer.Run(
                            (fun () ->
                                match liveState.Current with
                                | Some l ->
                                    // Timer pacing only without a speaker; with one, the
                                    // session free-runs and the speaker holds it back.
                                    if playing.Current && l.audio.IsNone && not l.session.Latest.running then
                                        l.session.Step(int (float l.framesPerSecond * tickMs / 1000.0))

                                    // The recording's end is the end of Play.
                                    let finished =
                                        l.recording |> Option.map (fun r -> r.remaining () = 0) |> Option.defaultValue false

                                    if playing.Current && finished then
                                        l.session.Pause()
                                        l.audio |> Option.iter (fun a -> a.Stop())
                                        playing.Set false
                                        message.Set "end of the recording"

                                    l.session.Pump() |> ignore
                                    snapshot.Set l.session.Latest
                                | None -> ()

                                true),
                            System.TimeSpan.FromMilliseconds tickMs
                        )
                        |> ignore),
            triggers = [ EffectTrigger.AfterInit ]
        )

        // Every read of the patch below goes through the state, so a reset
        // is seen by the next render and the next tick alike.
        let live = liveState.Current

        // Pointer positions are screen coordinates of the OUTER canvas; the
        // inner one is transformed, so the inverse takes them to the world.
        let outerCanvasOf (e: PointerEventArgs) =
            (e.Source :?> Visual).FindAncestorOfType<Canvas>(true)
            |> Option.ofObj
            |> Option.map (fun c ->
                // The outer canvas is the one whose parent is not a canvas.
                let rec outermost (c: Canvas) =
                    match c.GetVisualParent() with
                    | :? Canvas as p -> outermost p
                    | _ -> c

                outermost c)

        let toWorld (e: PointerEventArgs) =
            match outerCanvasOf e with
            | Some canvas ->
                let p = e.GetPosition canvas
                let px, py = pan.Current
                let z = zoom.Current
                Some(canvas, (p.X, p.Y), ((p.X - px) / z, (p.Y - py) / z))
            | None -> None

        let onPressed (e: PointerPressedEventArgs) =
            match toWorld e with
            | None -> ()
            | Some(canvas, screen, world) ->
                e.Pointer.Capture canvas

                match hitTest graph.Current positions.Current world with
                | HitPin(p, side) ->
                    drag.Set(Some(Wiring((p, side), world)))
                    message.Set $"wiring from {p.box}.{p.pin}"
                | HitBox name ->
                    let bx, by = positions.Current[name]
                    selected.Set(Some name)
                    live |> Option.iter (fun l -> l.signalsOf name |> List.iter l.session.Watch)
                    drag.Set(Some(MovingBox(name, (fst world - bx, snd world - by))))
                | HitNothing ->
                    selected.Set None
                    drag.Set(Some(Panning screen))

        let onMoved (e: PointerEventArgs) =
            match drag.Current, toWorld e with
            | Some(MovingBox(name, (ox, oy))), Some(_, _, (wx, wy)) ->
                positions.Set(positions.Current |> Map.add name (wx - ox, wy - oy))
            | Some(Panning(lx, ly)), Some(_, (sx, sy), _) ->
                let px, py = pan.Current
                pan.Set((px + sx - lx, py + sy - ly))
                drag.Set(Some(Panning(sx, sy)))
            | Some(Wiring(from, _)), Some(_, _, world) -> drag.Set(Some(Wiring(from, world)))
            | _ -> ()

        let onReleased (e: PointerReleasedEventArgs) =
            match drag.Current, toWorld e with
            | Some(Wiring(from, _)), Some(_, _, world) ->
                match hitTest graph.Current positions.Current world with
                | HitPin(target, side) ->
                    match connect graph.Current from (target, side) with
                    | Ok edge ->
                        graph.Set { graph.Current with edges = graph.Current.edges @ [ edge ] }
                        message.Set $"wired {edge.from.box}.{edge.from.pin} → {edge.``to``.box}.{edge.``to``.pin}"
                    | Error why -> message.Set $"refused: {why}"
                | _ -> message.Set "wire dropped"
            | _ -> ()

            e.Pointer.Capture null
            drag.Set None

        let onWheel (e: PointerWheelEventArgs) =
            match toWorld e with
            | None -> ()
            | Some(_, (sx, sy), (wx, wy)) ->
                // Zoom about the cursor: the world point under it stays put.
                let z = zoom.Current * (if e.Delta.Y > 0.0 then 1.1 else 1.0 / 1.1) |> max 0.2 |> min 5.0
                zoom.Set z
                pan.Set((sx - wx * z, sy - wy * z))
                e.Handled <- true

        let g = graph.Current
        let pos = positions.Current
        let px, py = pan.Current
        let z = zoom.Current

        let transform =
            let t = TransformGroup()
            t.Children.Add(ScaleTransform(z, z))
            t.Children.Add(TranslateTransform(px, py))
            t

        let snap = snapshot.Current

        // A wire carries a value this cycle if its source box's outputs are
        // valid; painted strong then, faint otherwise, with the value beside it.
        let wireState (e: Edge) =
            match live with
            | None -> wireBrush :> IBrush, None
            | Some l ->
                let valid =
                    valueOf snap (l.validOf e.from.box Out) |> Option.map (fun v -> not v.IsZero) |> Option.defaultValue false

                let label =
                    match l.probeOf e.from Out, formatOfPin g e.from Out with
                    | Some net, Some f -> valueOf snap net |> Option.map (showValue f)
                    | _ -> None

                (if valid then wireBrush :> IBrush else faintBrush :> IBrush), label

        let wires =
            [ for i, e in List.indexed g.edges do
                  match pinCentre g pos e.from Out, pinCentre g pos e.``to`` In with
                  | Some a, Some b ->
                      let brush, label = wireState e
                      yield wireView $"wire:%d{i}" brush a b

                      match label with
                      | Some text ->
                          let (ax, ay), (bx, by) = a, b

                          yield
                              TextBlock.create
                                  [ Canvas.left ((ax + bx) / 2.0 - 12.0)
                                    Canvas.top ((ay + by) / 2.0 - 16.0)
                                    TextBlock.text text
                                    TextBlock.fontSize 11.0
                                    TextBlock.fontFamily (FontFamily "monospace")
                                    TextBlock.foreground Brushes.DarkRed
                                    TextBlock.isHitTestVisible false ]
                              :> Types.IView
                      | None -> ()
                  | _ -> () ]

        let pending =
            match drag.Current with
            | Some(Wiring((from, side), at)) ->
                match pinCentre g pos from side with
                | Some a -> [ wireView "wire:pending" pendingBrush a at ]
                | None -> []
            | _ -> []

        let boxes = [ for name in boxOrder g do yield! boxView g selected.Current name pos[name] ]

        // Play: with a speaker, start it and free-run — the speaker paces the
        // session. Without one, the timer paces `Step`s. Stop undoes either.
        let togglePlay () =
            match live with
            | None -> ()
            | Some l ->
                match l.audio, playing.Current with
                | Some a, false ->
                    (match a.Start() with
                     | Ok player ->
                         playing.Set true
                         l.session.Run()
                         message.Set $"listening through {player}"
                     | Error why -> message.Set why)
                | Some a, true ->
                    l.session.Pause()
                    a.Stop()
                    playing.Set false
                | None, _ -> playing.Set(not playing.Current)

        // ---- the toolbar: run controls, the recording's progress, a number
        // box per control — Pure Data's number box wired to a control inlet.
        let toolbar =
            match live with
            | None -> []
            | Some l ->
                let button (label: string) (act: unit -> unit) =
                    Button.create [ Button.content label; Button.margin (Thickness(2.0, 0.0)); Button.onClick ((fun _ -> act ()), SubPatchOptions.Always) ]
                    :> Types.IView

                let progress =
                    (match l.recording with
                     | Some r -> $"  frames %d{r.framesOffered ()} offered, %d{r.remaining ()} to go"
                     | None -> "")
                    + (match l.audio with
                       | Some a when a.Listening -> $"  heard %d{a.Sent}"
                       | _ -> "")

                let numberBox (name: string, f: NumberFormat) =
                    let text = controlText.Current |> Map.tryFind name |> Option.defaultValue (valueOf snap name |> Option.map string |> Option.defaultValue "")

                    StackPanel.create
                        [ StackPanel.orientation Avalonia.Layout.Orientation.Horizontal
                          StackPanel.margin (Thickness(0.0, 0.0, 8.0, 0.0))
                          StackPanel.children
                              [ TextBlock.create [ TextBlock.text $"{name} "; TextBlock.verticalAlignment Avalonia.Layout.VerticalAlignment.Center ]
                                TextBox.create
                                    [ TextBox.width 70.0
                                      TextBox.text text
                                      TextBox.onTextChanged (
                                          (fun t ->
                                              controlText.Set(controlText.Current |> Map.add name t)

                                              match System.UInt64.TryParse t with
                                              | true, v when v < (1UL <<< f.totalWidth) ->
                                                  l.session.Poke(name, System.Numerics.BigInteger v)
                                                  message.Set $"{name} = %d{v}"
                                              | _ -> ()),
                                          SubPatchOptions.Always
                                      ) ] ] ]
                    :> Types.IView

                [ StackPanel.create
                      [ DockPanel.dock Dock.Top
                        StackPanel.orientation Avalonia.Layout.Orientation.Horizontal
                        StackPanel.margin (Thickness(10.0, 6.0))
                        // Knobs first, so they never move as the counters grow.
                        StackPanel.children
                            ((l.controls |> List.map numberBox)
                             @ [
                               button (if playing.Current then "Stop" else "Play") togglePlay
                               button (if snap.running then "Pause" else "Run") (fun () -> if snap.running then l.session.Pause() else l.session.Run())
                               button "Step" (fun () -> l.session.Step 1)
                               button "Step 100" (fun () -> l.session.Step 100)
                               button "Reset" (fun () ->
                                   playing.Set false
                                   l.audio |> Option.iter (fun a -> a.Stop())

                                   // What each box says now — typed text if it parses, else
                                   // the value the design last had — goes into the new session.
                                   let knobs =
                                       [ for name, _ in l.controls ->
                                             let typed =
                                                 controlText.Current
                                                 |> Map.tryFind name
                                                 |> Option.bind (fun t ->
                                                     match System.UInt64.TryParse t with
                                                     | true, v -> Some v
                                                     | _ -> None)

                                             let last = valueOf snap name |> Option.map uint64

                                             name, (typed |> Option.orElse last |> Option.defaultValue 0UL) ]

                                   let fresh = l.reopen knobs
                                   liveState.Set(Some fresh)
                                   snapshot.Set fresh.session.Latest
                                   message.Set "reset: the recording plays again from the start")
                               (match l.recording, l.savePath with
                                | Some tape, Some path ->
                                    button "Save heard" (fun () ->
                                        match tape.heard () with
                                        | Some heard ->
                                            writeWavFile path heard
                                            message.Set $"wrote {path}: %d{heard.FrameCount} frames"
                                        | None -> message.Set "nothing heard yet")
                                | _ -> TextBlock.create [] :> Types.IView)
                               TextBlock.create
                                   [ TextBlock.margin (Thickness(10.0, 0.0))
                                     TextBlock.verticalAlignment Avalonia.Layout.VerticalAlignment.Center
                                     TextBlock.fontFamily (FontFamily "monospace")
                                     TextBlock.text $"cycle %d{snap.cycle}{progress}" ] ]) ]
                  :> Types.IView ]

        // ---- the side panel: the selected box's pins and its own signals,
        // with this cycle's values.
        let sidePanel =
            match live, selected.Current with
            | Some l, Some box ->
                let ins, outs = pinsOf g box
                let controls = controlsOf g box

                let pinRows =
                    [ for pins, side, tag in [ ins, In, "in"; outs, Out, "out" ] do
                          match box with
                          | "input" | "output" -> ()
                          | _ -> yield $"{tag} valid", (valueOf snap (l.validOf box side) |> Option.map string |> Option.defaultValue "—")

                          for n, f in pins do
                              let shown =
                                  match l.probeOf (pin box n) side with
                                  | Some net -> valueOf snap net |> Option.map (showValue f) |> Option.defaultValue "—"
                                  | None -> "—"

                              yield $"{tag} {n}", shown
                      for n, f in controls do
                          if box <> "input" then
                              let shown =
                                  match l.probeOf (pin box n) In with
                                  | Some net -> valueOf snap net |> Option.map (showValue f) |> Option.defaultValue "—"
                                  | None -> "—"

                              yield $"ctl {n}", shown ]

                let ownRows =
                    [ for name in l.signalsOf box ->
                          name, (valueOf snap name |> Option.map string |> Option.defaultValue "—") ]

                let row (name: string, value: string) =
                    TextBlock.create
                        [ TextBlock.fontFamily (FontFamily "monospace")
                          TextBlock.fontSize 12.0
                          TextBlock.text $"%-28s{name} {value}" ]
                    :> Types.IView

                [ ScrollViewer.create
                      [ DockPanel.dock Dock.Right
                        ScrollViewer.width 320.0
                        ScrollViewer.content (
                            StackPanel.create
                                [ StackPanel.margin (Thickness 10.0)
                                  StackPanel.children (
                                      [ TextBlock.create [ TextBlock.text box; TextBlock.fontWeight FontWeight.Bold ] :> Types.IView ]
                                      @ (pinRows |> List.map row)
                                      @ (if ownRows.IsEmpty then [] else [ TextBlock.create [ TextBlock.text " "; TextBlock.fontSize 6.0 ] :> Types.IView ])
                                      @ (ownRows |> List.map row)
                                  ) ]
                        ) ]
                  :> Types.IView ]
            | _ -> []

        DockPanel.create
            [ DockPanel.children (
                  [ TextBlock.create
                        [ DockPanel.dock Dock.Top
                          TextBlock.margin (Thickness(10.0, 6.0))
                          TextBlock.fontFamily (FontFamily "monospace")
                          TextBlock.text
                              $"{g.name} — {g.boxes.Length} boxes, {g.edges.Length} wires — zoom {inv z}  {message.Current}" ]
                    :> Types.IView ]
                  @ toolbar
                  @ sidePanel
                  @ [ Canvas.create
                          [ Canvas.background (SolidColorBrush(Color.FromRgb(255uy, 255uy, 255uy)))
                            Canvas.clipToBounds true
                            Canvas.onPointerPressed (onPressed, SubPatchOptions.Always)
                            Canvas.onPointerMoved (onMoved, SubPatchOptions.Always)
                            Canvas.onPointerReleased (onReleased, SubPatchOptions.Always)
                            Canvas.onPointerWheelChanged (onWheel, SubPatchOptions.Always)
                            Canvas.children
                                [ Canvas.create
                                      [ Canvas.renderTransformOrigin (RelativePoint(0.0, 0.0, RelativeUnit.Absolute))
                                        Canvas.renderTransform transform
                                        // Wires over boxes, as a patch is read.
                                        Canvas.children (boxes @ wires @ pending) ] ] ]
                      :> Types.IView ]
              ) ])
