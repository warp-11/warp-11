/// The canvas, in FuncUI: a `Graph` drawn as boxes, pins and wires, and
/// edited in place. Everything renders from the state — the graph and its
/// history, the pan and zoom, what is selected, what is being dragged —
/// which is the property the debugger needs: a snapshot of the design's
/// signals is one more input to the same render.
///
/// Every change goes through `Edit`: the canvas asks for a change, shows
/// what came back, and shows the reason when it was refused. It decides
/// nothing about the design itself.
///
/// Geometry lives in world coordinates; one transform on the inner canvas
/// puts it on screen, and pointer positions come back through its inverse.
/// Hit-testing is our own arithmetic over the same geometry, pins before
/// wires before boxes, so nothing depends on which child control caught
/// the event.
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
open Warp11.Fu
open Warp11.Factories
open Warp11.Graph
open Warp11.Edit
open Warp11.Elaborate
open Warp11.Devices

// ---------------------------------------------------------------------------
// Geometry, in world units.

let boxWidth = 170.0
let headerHeight = 34.0
let rowHeight = 26.0
let pinRadius = 6.0
let pinHitRadius = 10.0
let wireHitDistance = 6.0

/// One row of pins on a box's side: signal pins first, then the controls —
/// control inlets on a box's left, the design's controls as outlets on the
/// input box's right. Pure Data's two kinds of inlet, one under the other.
type PinRow =
    { pin: string
      format: NumberFormat
      control: bool }

let pinRows (g: Graph) (box: string) (side: Side) : PinRow list =
    let inputs, outputs = pinsOf g box
    let controls = controlsOf g box

    let signals, controls =
        match side, box, controlBoxOf g box with
        | Out, _, Some c -> [], [ controlOutlet, c.format ]
        | In, _, Some _ -> [], []
        | In, "input", _ -> [], []
        | In, _, _ -> inputs, controls
        | Out, "input", _ -> outputs, controls
        | Out, _, _ -> outputs, []

    [ for n, f in signals -> { pin = n; format = f; control = false } ]
    @ [ for n, f in controls -> { pin = n; format = f; control = true } ]

let controlBoxWidth = 120.0

/// A box's width: a control box is narrow.
let boxWidthOf (g: Graph) (box: string) =
    if (controlBoxOf g box).IsSome then controlBoxWidth else boxWidth

/// A box's height, from its taller pin column; a control box is one row.
let boxHeight (g: Graph) (box: string) =
    if (controlBoxOf g box).IsSome then
        headerHeight + rowHeight
    else
        headerHeight + float (max (pinRows g box In).Length (pinRows g box Out).Length) * rowHeight + 10.0

/// Where a pin sits, relative to its box's top-left: inputs down the left
/// edge, outputs down the right.
let private pinOffset (g: Graph) (p: PinRef) (side: Side) : (float * float) option =
    let row = pinRows g p.box side |> List.tryFindIndex (fun r -> r.pin = p.pin)
    let x = match side with In -> 0.0 | Out -> boxWidthOf g p.box
    row |> Option.map (fun r -> x, headerHeight + float r * rowHeight + rowHeight / 2.0)

/// A pin's centre in world coordinates.
let pinCentre (g: Graph) (p: PinRef) (side: Side) : (float * float) option =
    match Map.tryFind p.box g.positions, pinOffset g p side with
    | Some(bx, by), Some(dx, dy) -> Some(bx + dx, by + dy)
    | _ -> None

/// Boxes in the order a chain reads: input, the design's boxes, output, and
/// the control boxes beside them.
let boxOrder (g: Graph) =
    "input" :: (g.boxes |> List.map (fun b -> b.name)) @ [ "output" ] @ (g.controlBoxes |> List.map (fun c -> c.name))

/// A position for every box that has none: one column per box, left to
/// right. A graph written in F# arrives with none; a saved one with all.
let withLayout (g: Graph) : Graph =
    let positions =
        (g.positions, List.indexed (boxOrder g))
        ||> List.fold (fun ps (i, name) ->
            if ps.ContainsKey name then
                ps
            else
                ps |> Map.add name (60.0 + float i * 260.0, 120.0))

    { g with positions = positions }

// ---------------------------------------------------------------------------
// What the pointer can be doing, and what is selected.

type Drag =
    /// The graph as it was when the box was picked up, so the move is one
    /// history entry on release rather than one per pixel.
    | MovingBox of box: string * grabOffset: (float * float) * before: Graph
    | Panning of lastScreen: (float * float)
    | Wiring of from: (PinRef * Side) * atWorld: (float * float)

type Selection =
    | SelectedBox of string
    | SelectedWire of Edge

/// A pin under the pointer, or a wire, or a box, or nothing — pins first,
/// because a pin sits on its box's edge and a wire is the rarer, more
/// deliberate gesture; wires before boxes because a wire is thin.
type Hit =
    | HitPin of PinRef * Side
    | HitWire of Edge
    | HitBox of string
    | HitNothing

/// A point on the wire's cubic, `t` from 0 to 1.
let private cubicAt (x1: float, y1: float) (x2: float, y2: float) (t: float) =
    let dx = max 40.0 (abs (x2 - x1) / 2.0)
    let cx1, cy1, cx2, cy2 = x1 + dx, y1, x2 - dx, y2
    let u = 1.0 - t

    u * u * u * x1 + 3.0 * u * u * t * cx1 + 3.0 * u * t * t * cx2 + t * t * t * x2,
    u * u * u * y1 + 3.0 * u * u * t * cy1 + 3.0 * u * t * t * cy2 + t * t * t * y2

let private nearWire (a: float * float) (b: float * float) (wx: float, wy: float) =
    [ 0..24 ]
    |> List.exists (fun i ->
        let x, y = cubicAt a b (float i / 24.0)
        (x - wx) ** 2.0 + (y - wy) ** 2.0 <= wireHitDistance ** 2.0)

let hitTest (g: Graph) (wx: float, wy: float) : Hit =
    let pins =
        [ for box in boxOrder g do
              for side in [ In; Out ] do
                  for r in pinRows g box side do
                      let p = pin box r.pin

                      match pinCentre g p side with
                      | Some(px, py) when (px - wx) ** 2.0 + (py - wy) ** 2.0 <= pinHitRadius ** 2.0 -> yield p, side
                      | _ -> () ]

    let wire =
        g.edges
        |> List.tryFind (fun e ->
            match pinCentre g e.from Out, pinCentre g e.``to`` In with
            | Some a, Some b -> nearWire a b (wx, wy)
            | _ -> false)

    match pins, wire with
    | (p, side) :: _, _ -> HitPin(p, side)
    | [], Some e -> HitWire e
    | [], None ->
        // Later boxes draw on top, so they win a hit.
        boxOrder g
        |> List.rev
        |> List.tryFind (fun box ->
            match Map.tryFind box g.positions with
            | Some(bx, by) -> wx >= bx && wx <= bx + boxWidthOf g box && wy >= by && wy <= by + boxHeight g box
            | None -> false)
        |> Option.map HitBox
        |> Option.defaultValue HitNothing

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
let private selectedWireBrush = SolidColorBrush(Color.FromRgb(30uy, 120uy, 220uy))
let private boxFill = SolidColorBrush(Color.FromRgb(250uy, 250uy, 252uy))
let private boundaryFill = SolidColorBrush(Color.FromRgb(240uy, 244uy, 250uy))
let private boxStroke = SolidColorBrush(Color.FromRgb(120uy, 130uy, 160uy))
let private selectedStroke = SolidColorBrush(Color.FromRgb(30uy, 120uy, 220uy))
let private pinFill = Brushes.White
let private pinStroke = Brushes.Black

let private wireView (brush: IBrush) (thickness: float) (a: float * float) (b: float * float) : Types.IView =
    Path.create
        [ Path.data (Geometry.Parse(wirePath a b))
          Path.stroke brush
          Path.strokeThickness thickness
          Path.isHitTestVisible false ]
    :> Types.IView

let private boxView (g: Graph) (selected: Selection option) (name: string) (bx: float, by: float) : Types.IView list =
    let isBoundary = name = "input" || name = "output"
    let isSelected = selected = Some(SelectedBox name)

    let width = boxWidthOf g name

    let title, subtitle =
        match name, controlBoxOf g name with
        | "input", _ -> "input", $"%d{g.streams} stream(s), %d{g.controls.Length} control(s)"
        | "output", _ -> "output", ""
        | _, Some c ->
            let kind =
                match c.kind, c.format.totalWidth with
                | NumberBox, 1 -> "toggle"
                | NumberBox, _ -> "number"
                | ConstantBox, _ -> "constant"

            name, $"{kind} {c.value}"
        | _ ->
            let b = g.boxes |> List.find (fun b -> b.name = name)

            match designOf g b with
            | Some sub -> name, $"design {sub.name} × %d{b.copies} — double-click to open"
            | None ->

            let u = unitOf g b

            let sequential =
                match u.law with
                | Sequential _ -> " (sequential)"
                | Combinational _ -> ""

            // The arguments as typed, as Pure Data writes them in the box.
            let arguments =
                palette[b.unit].parameters
                |> List.map (fun p -> b.arguments |> Map.tryFind p.name |> Option.defaultValue p.``default``)
                |> String.concat " "

            name, $"{b.unit} {arguments} × %d{b.copies}" + sequential

    let body =
        Border.create
            [ Canvas.left bx
              Canvas.top by
              Border.width width
              Border.height (boxHeight g name)
              Border.background (if isBoundary then boundaryFill :> IBrush else boxFill :> IBrush)
              Border.borderBrush (if isSelected then selectedStroke :> IBrush else boxStroke :> IBrush)
              Border.borderThickness (Thickness(if isSelected then 2.0 else 1.0))
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
        [ for side in [ In; Out ] do
              for row, r in List.indexed (pinRows g name side) do
                  let isInput = side = In
                  let px = bx + (if isInput then 0.0 else width)
                  let py = by + headerHeight + float row * rowHeight + rowHeight / 2.0

                  // A signal pin is a circle; a control pin a square, as a
                  // control wire is the thinner one.
                  if r.control then
                      yield
                          Rectangle.create
                              [ Canvas.left (px - pinRadius + 1.0)
                                Canvas.top (py - pinRadius + 1.0)
                                Rectangle.width (2.0 * pinRadius - 2.0)
                                Rectangle.height (2.0 * pinRadius - 2.0)
                                Rectangle.fill pinFill
                                Rectangle.stroke pinStroke
                                Rectangle.strokeThickness 1.5
                                Rectangle.isHitTestVisible false ]
                          :> Types.IView
                  else
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

                  // The pin's name, inside the box beside it — and for a
                  // control inlet nobody wired, the value it holds.
                  let text =
                      match g.boxes |> List.tryFind (fun b -> b.name = name) with
                      | Some b when r.control && isInput && not (g.edges |> List.exists (fun e -> e.``to`` = pin name r.pin)) ->
                          let held = b.settings |> Map.tryFind r.pin |> Option.defaultValue "0"
                          $"{r.pin} = {held}"
                      | _ -> r.pin

                  yield
                      TextBlock.create
                          [ Canvas.left (if isInput then px + pinRadius + 4.0 else px - pinRadius - 4.0 - 7.0 * float text.Length)
                            Canvas.top (py - 7.0)
                            TextBlock.text text
                            TextBlock.fontSize 11.0
                            TextBlock.fontStyle (if r.control then FontStyle.Italic else FontStyle.Normal)
                            TextBlock.foreground Brushes.DimGray
                            TextBlock.isHitTestVisible false ]
                      :> Types.IView ]

    body :: pinViews

// ---------------------------------------------------------------------------
// A running design behind the canvas.

/// The source on the boundary, as the canvas reads it: progress, and how
/// to save what has come out so far.
type Recording =
    { framesOffered: unit -> int
      remaining: unit -> int
      save: string -> string }

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
      /// Every signal of the design that belongs to a box, by the box's
      /// flattened name — its stage's registers and the module instance
      /// inside it. A box inside a design inside the design is named with
      /// its instance prefix.
      signalsOf: string -> string list
      /// Where "save what was heard" writes.
      savePath: string option
      /// Frames a second at real time — one frame is one beat is one cycle here.
      framesPerSecond: int
      /// A design from the start, with these control values: a fresh session
      /// with the recording rewound. `Reset` is this on the same graph, since
      /// a device has no rewind of its own; an edited design is this on the
      /// new graph, since a change to the design is a new design.
      reopen: Graph -> (string * uint64) list -> Live }

/// What the canvas opens on: a graph, the design running behind it if one
/// is, how to run one when there is not (a mapping without a session yet),
/// and the file the design is saved to.
type Opening =
    { graph: Graph
      live: Live option
      opener: (Graph -> (string * uint64) list -> Live) option
      file: string option }

let private valueOf (snapshot: Warp11.Debug.Snapshot) (name: string) : System.Numerics.BigInteger option =
    snapshot.values |> List.tryFind (fun v -> v.name = name) |> Option.map (fun v -> v.value)

/// A value as the pin's format reads it: signed two's complement, or plain.
/// A field wider than a word — a packed row, a table's word — is not a
/// number to read, and says its width instead.
let private showValue (f: NumberFormat) (v: System.Numerics.BigInteger) =
    if f.totalWidth > 64 then $"%d{f.totalWidth} bits" else

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
    match lookupPin g side p with
    | Ok(_, f) -> Some f
    | Error _ -> None

let private isControlWire (g: Graph) (e: Edge) =
    match lookupPin g In e.``to`` with
    | Ok(ControlIn, _) -> true
    | _ -> false

// ---------------------------------------------------------------------------
// The component.

let private mono = FontFamily "monospace"

let private button (label: string) (act: unit -> unit) =
    Button.create [ Button.content label; Button.margin (Thickness(2.0, 0.0)); Button.onClick ((fun _ -> act ()), SubPatchOptions.Always) ]
    :> Types.IView

let private label (text: string) =
    TextBlock.create [ TextBlock.text text; TextBlock.verticalAlignment Layout.VerticalAlignment.Center; TextBlock.margin (Thickness(4.0, 0.0)) ]
    :> Types.IView

/// A one-line entry: its text is state; Enter commits it, and so does
/// clicking away — a value typed and left is a value meant. The handler
/// gets the box's text as it is at that moment rather than the state's,
/// since a re-render is scheduled and a fast Enter can land before it; a
/// commit of what is already held is a change of nothing.
let private entry (width: float) (text: string) (onChanged: string -> unit) (onEnter: string -> unit) =
    let commit (source: obj) =
        match source with
        | :? TextBox as t -> onEnter (if isNull t.Text then "" else t.Text)
        | _ -> ()

    TextBox.create
        [ TextBox.width width
          TextBox.text text
          TextBox.onTextChanged (onChanged, SubPatchOptions.Always)
          TextBox.onLostFocus ((fun e -> commit e.Source), SubPatchOptions.Always)
          TextBox.onKeyDown (
              (fun e ->
                  if e.Key = Key.Enter then
                      e.Handled <- true
                      commit e.Source),
              SubPatchOptions.Always
          ) ]
    :> Types.IView

/// The canvas over a design. The graph the user edits is the component's
/// own state from the opening on; with a design running behind it, values
/// are painted on the wires, a box's signals are listed when it is
/// selected, and the toolbar drives the run.
let view (opening: Opening) : Control =
    Component(fun ctx ->
        let history = ctx.useState (history (withLayout opening.graph))
        /// The boxes drilled into, from the top: the view is over the design
        /// the last of them is. Empty is the design itself.
        let path = ctx.useState<string list> []
        let pan = ctx.useState ((0.0, 0.0))
        let zoom = ctx.useState 1.0
        let selection = ctx.useState<Selection option> None
        let drag = ctx.useState<Drag option> None
        let message = ctx.useState ""
        /// Double-click on the canvas: where, and what has been typed so far.
        let typing = ctx.useState<((float * float) * string) option> None
        let filePath = ctx.useState (opening.file |> Option.defaultValue "")
        let importText = ctx.useState ""
        /// A unit being written: its source, compiled on request.
        let unitSource = ctx.useState Compiler.template
        let unitPanelOpen = ctx.useState false
        // The property panel's entries.
        let nameText = ctx.useState ""
        let designName = ctx.useState opening.graph.name
        let rateText = ctx.useState (opening.graph.sampleRate.ToString(CultureInfo.InvariantCulture))
        let argumentText = ctx.useState Map.empty<string, string>
        let copiesText = ctx.useState ""
        let pinName = ctx.useState ""
        let pinWidth = ctx.useState "24"
        let pinFraction = ctx.useState "0"
        let pinSigned = ctx.useState true

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

        // The running design is state, because `Reset` replaces it and an
        // edit to the design drops it.
        let liveState = ctx.useState opening.live
        let snapshot = ctx.useState (opening.live |> Option.map (fun l -> l.session.Latest) |> Option.defaultValue blankSnapshot)
        let controlText = ctx.useState Map.empty<string, string>
        /// The recent trace: the last `scopeSamples` cycles of the watched
        /// signals, refreshed each tick while a design runs.
        let slice = ctx.useState<Warp11.Debug.Trace> { firstCycle = 0; signals = [] }
        let breakText = ctx.useState ""
        let streamsText = ctx.useState (string opening.graph.streams)
        // Playing: the timer steps the session as many frames as real time
        // has passed — Pure Data's "DSP on" — so a knob turned mid-file is
        // heard mid-file. `Run` is the free-running alternative.
        let playing = ctx.useState false
        let tickMs = 33.0
        let scopeSamples = 900

        // Polling the latest snapshot at frame rate, as the debugger does: the
        // session decides how often a snapshot is worth taking, and nothing is
        // marshalled across threads. The same timer pumps a session that has
        // no thread of its own (the browser), and paces a playing one.
        ctx.useEffect (
            handler =
                (fun () ->
                    match opening.live, opening.opener with
                    | None, None -> ()
                    | _ ->
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
                                    let latest = l.session.Latest
                                    snapshot.Set latest

                                    // The trace ring fills with the watched signals, so a
                                    // box's waveform and a wire's level are there as soon as
                                    // beats are. Started here rather than at the opening:
                                    // the watches reach the session's thread as commands,
                                    // and a recording asked for before they land has nothing
                                    // to record.
                                    if not latest.recording && not latest.values.IsEmpty then
                                        l.session.StartRecording false |> ignore

                                    if latest.recorded > 0 then
                                        slice.Set(l.session.TraceSlice(max 0 (latest.recorded - scopeSamples), scopeSamples))
                                | None -> ()

                                true),
                            System.TimeSpan.FromMilliseconds tickMs
                        )
                        |> ignore),
            triggers = [ EffectTrigger.AfterInit ]
        )

        // Every read of the running design below goes through the state, so
        // a reset is seen by the next render and the next tick alike.
        let live = liveState.Current
        let root = history.Current.present

        // The design under the view: the one the path leads to, laid out if
        // it never was. A path an undo has invalidated falls back to the top.
        let g =
            match graphAt path.Current root with
            | Some g -> withLayout g
            | None -> root

        /// A change to the design under the view is a change to the design
        /// at the path — the parent is a new design for it.
        let here (what: Graph -> Result<Graph, string>) : Graph -> Result<Graph, string> = atPath path.Current what

        /// The prefix the simulator puts on every net inside the boxes the
        /// path drills through.
        let rec prefixFor (g: Graph) (path: string list) =
            match path with
            | [] -> ""
            | box :: rest ->
                match g.boxes |> List.tryFind (fun b -> b.name = box) with
                | Some b -> instancePrefix b + (designOf g b |> Option.map (fun sub -> prefixFor sub rest) |> Option.defaultValue "")
                | None -> ""

        let prefix = prefixFor root path.Current
        let probeOf (p: PinRef) (side: Side) = probeName g p side |> Option.map (fun n -> prefix + n)
        let validOf (box: string) (side: Side) = prefix + validName g box side

        let stopLive () =
            live
            |> Option.iter (fun l ->
                l.session.Pause()
                l.audio |> Option.iter (fun a -> a.Stop()))

            playing.Set false
            liveState.Set None
            snapshot.Set blankSnapshot

        // ---- changes: through `Edit`, with the refusal shown. A change to
        // the design is a new design, so the session running the old one
        // stops; the toolbar opens the new one on request.
        let change (what: Graph -> Result<Graph, string>) : bool =
            match apply (here what) history.Current with
            | h, None when obj.ReferenceEquals(h, history.Current) -> true
            | h, None ->
                history.Set h

                if live.IsSome then
                    stopLive ()
                    message.Set "the design changed — Open in sim runs it again"
                else
                    message.Set ""

                true
            | _, Some why ->
                message.Set $"refused: {why}"
                false

        // A value — a setting, a number box — is not a new design: the port
        // exists either way, so the session keeps running and is poked.
        let changeValue (what: Graph -> Result<Graph, string>) (poke: (string * uint64) option) : bool =
            match apply (here what) history.Current with
            | h, None ->
                history.Set h

                match live, poke with
                | Some l, Some(name, v) ->
                    l.session.Poke(prefix + name, System.Numerics.BigInteger v)
                    message.Set $"{prefix}{name} = %d{v}"
                | _ -> message.Set ""

                true
            | _, Some why ->
                message.Set $"refused: {why}"
                false

        let select (s: Selection option) =
            selection.Set s

            match s with
            | Some(SelectedBox name) ->
                let current = graphAt path.Current history.Current.present |> Option.defaultValue history.Current.present
                nameText.Set name
                copiesText.Set(current.boxes |> List.tryFind (fun b -> b.name = name) |> Option.map (fun b -> string b.copies) |> Option.defaultValue "")
                argumentText.Set(current.boxes |> List.tryFind (fun b -> b.name = name) |> Option.map (fun b -> b.arguments) |> Option.defaultValue Map.empty)
                live |> Option.iter (fun l -> l.signalsOf (prefix + name) |> List.iter l.session.Watch)
            | _ -> ()

        let deleteSelection () =
            match selection.Current with
            | Some(SelectedBox name) when name <> "input" && name <> "output" ->
                if change (removeBox name) then
                    select None
                    message.Set $"removed {name}"
            | Some(SelectedBox name) -> message.Set $"the {name} box is the design's own boundary"
            | Some(SelectedWire e) ->
                if change (removeWire e >> Ok) then
                    select None
                    message.Set $"removed the wire {showPin e.from} → {showPin e.``to``}"
            | None -> ()

        let stepHistory (what: string) (step: History -> History) =
            let h = step history.Current

            if not (obj.ReferenceEquals(h, history.Current)) then
                history.Set h

                if (graphAt path.Current h.present).IsNone then
                    path.Set []

                designName.Set(graphAt path.Current h.present |> Option.map (fun g -> g.name) |> Option.defaultValue h.present.name)
                rateText.Set(h.present.sampleRate.ToString(CultureInfo.InvariantCulture))
                select None
                message.Set what

                if live.IsSome then
                    stopLive ()

        let undoLast () = stepHistory "undone" undo
        let redoLast () = stepHistory "redone" redo

        /// A new box of `unit` at `at`, selected.
        let placeBox (unit: string) (at: float * float) =
            let mutable placed = None

            if change (addBox unit at >> Result.map (fun (g, name) -> placed <- Some name; g)) then
                placed |> Option.iter (fun name -> select (Some(SelectedBox name)); message.Set $"added {name}")

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

        /// The world point at the middle of what is on screen, or near it:
        /// where a palette click puts a box, stepped so boxes do not stack.
        let somewhereVisible () =
            let px, py = pan.Current
            let z = zoom.Current
            let k = float g.boxes.Length
            (-px / z + 120.0 + 30.0 * k, -py / z + 260.0 + 30.0 * k)

        let onPressed (e: PointerPressedEventArgs) =
            match toWorld e with
            | None -> ()
            | Some(canvas, screen, world) ->
                canvas.Focus() |> ignore
                e.Pointer.Capture canvas
                typing.Set None

                match hitTest g world with
                | HitPin(p, side) ->
                    drag.Set(Some(Wiring((p, side), world)))
                    message.Set $"wiring from {p.box}.{p.pin}"
                | HitWire edge ->
                    select (Some(SelectedWire edge))
                    message.Set $"wire {showPin edge.from} → {showPin edge.``to``}"
                | HitBox name ->
                    let isDesign = g.boxes |> List.tryFind (fun b -> b.name = name) |> Option.bind (designOf g) |> Option.isSome

                    if e.ClickCount = 2 && isDesign then
                        // Into the design the box is: the same view over it.
                        let deeper = path.Current @ [ name ]

                        match graphAt deeper root with
                        | Some sub ->
                            path.Set deeper
                            designName.Set sub.name
                            select None
                            message.Set $"in {name}"
                        | None -> ()
                    else
                        let bx, by = g.positions[name]
                        select (Some(SelectedBox name))
                        drag.Set(Some(MovingBox(name, (fst world - bx, snd world - by), root)))
                | HitNothing ->
                    select None

                    if e.ClickCount = 2 then
                        typing.Set(Some(world, ""))
                    else
                        drag.Set(Some(Panning screen))

        let onMoved (e: PointerEventArgs) =
            match drag.Current, toWorld e with
            | Some(MovingBox(name, (ox, oy), _)), Some(_, _, (wx, wy)) ->
                // The move is not history until the box is put down.
                match here (moveBox name (wx - ox, wy - oy) >> Ok) history.Current.present with
                | Ok moved -> history.Set { history.Current with present = moved }
                | Error _ -> ()
            | Some(Panning(lx, ly)), Some(_, (sx, sy), _) ->
                let px, py = pan.Current
                pan.Set((px + sx - lx, py + sy - ly))
                drag.Set(Some(Panning(sx, sy)))
            | Some(Wiring(from, _)), Some(_, _, world) -> drag.Set(Some(Wiring(from, world)))
            | _ -> ()

        let onReleased (e: PointerReleasedEventArgs) =
            match drag.Current, toWorld e with
            | Some(Wiring(from, _)), Some(_, _, world) ->
                match hitTest g world with
                | HitPin(target, side) ->
                    if change (addWire from (target, side)) then
                        message.Set $"wired {showPin (fst from)} — {showPin target}"
                | _ -> message.Set "wire dropped"
            | Some(MovingBox(name, _, before)), _ ->
                let h = history.Current
                let at (r: Graph) = graphAt path.Current r |> Option.bind (fun g -> g.positions |> Map.tryFind name)

                if at h.present <> at before then
                    history.Set { h with past = before :: h.past; future = [] }
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

        let onKey (e: KeyEventArgs) =
            let ctrl = e.KeyModifiers.HasFlag KeyModifiers.Control
            let shift = e.KeyModifiers.HasFlag KeyModifiers.Shift

            match e.Key with
            | Key.Delete
            | Key.Back ->
                deleteSelection ()
                e.Handled <- true
            | Key.Z when ctrl && shift ->
                redoLast ()
                e.Handled <- true
            | Key.Z when ctrl ->
                undoLast ()
                e.Handled <- true
            | Key.Y when ctrl ->
                redoLast ()
                e.Handled <- true
            | Key.D when ctrl ->
                match selection.Current with
                | Some(SelectedBox name) when name <> "input" && name <> "output" ->
                    let bx, by = g.positions[name]
                    let mutable placed = None

                    if change (duplicateBox name (bx + 40.0, by + 40.0) >> Result.map (fun (g, n) -> placed <- Some n; g)) then
                        placed |> Option.iter (fun n -> select (Some(SelectedBox n)); message.Set $"duplicated {name} as {n}")
                | _ -> ()

                e.Handled <- true
            | Key.Escape ->
                typing.Set None
                select None
                e.Handled <- true
            | _ -> ()

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
            | Some _ ->
                let valid =
                    valueOf snap (validOf e.from.box Out) |> Option.map (fun v -> not v.IsZero) |> Option.defaultValue false

                let label =
                    match probeOf e.from Out, formatOfPin g e.from Out with
                    | Some net, Some f -> valueOf snap net |> Option.map (showValue f)
                    | _ -> None

                (if valid then wireBrush :> IBrush else faintBrush :> IBrush), label

        /// How loud a signal wire has been lately: the peak of its beats over
        /// the recent trace, as a fraction of full scale. Nothing for a control
        /// wire, or before anything ran.
        let levelOf (e: Edge) =
            match live, probeOf e.from Out, formatOfPin g e.from Out with
            | Some _, Some net, Some f when not (isControlWire g e) ->
                let values = Scope.beats slice.Current (validOf e.from.box Out) net
                if values.Length = 0 then None else Some(Scope.level f.totalWidth values)
            | _ -> None

        let wires =
            [ for e in g.edges do
                  match pinCentre g e.from Out, pinCentre g e.``to`` In with
                  | Some a, Some b ->
                      let brush, label = wireState e
                      let isSelected = selection.Current = Some(SelectedWire e)
                      let thickness = if isControlWire g e then 1.2 else 2.0

                      // One strand per stream: the same wire carries a beat on
                      // each, and the elaborator places a stage on each.
                      for k in 1 .. g.streams - 1 do
                          let (ax, ay), (bx, by) = a, b
                          yield wireView faintBrush 1.0 (ax, ay + 3.0 * float k) (bx, by + 3.0 * float k)

                      yield wireView (if isSelected then selectedWireBrush :> IBrush else brush) (if isSelected then thickness + 1.5 else thickness) a b

                      // The level, as a bar under the wire's middle: green to
                      // red as it nears full scale.
                      match levelOf e with
                      | Some level ->
                          let (ax, ay), (bx, by) = a, b

                          yield
                              Rectangle.create
                                  [ Canvas.left ((ax + bx) / 2.0 - 30.0)
                                    Canvas.top ((ay + by) / 2.0 + 4.0)
                                    Rectangle.width (max 1.0 (60.0 * level))
                                    Rectangle.height 3.0
                                    Rectangle.fill (
                                        if level > 0.9 then Brushes.Red :> IBrush
                                        elif level > 0.6 then Brushes.Orange :> IBrush
                                        else Brushes.Green :> IBrush
                                    )
                                    Rectangle.isHitTestVisible false ]
                              :> Types.IView
                      | None -> ()

                      match label with
                      | Some text ->
                          let (ax, ay), (bx, by) = a, b

                          yield
                              TextBlock.create
                                  [ Canvas.left ((ax + bx) / 2.0 - 12.0)
                                    Canvas.top ((ay + by) / 2.0 - 16.0)
                                    TextBlock.text text
                                    TextBlock.fontSize 11.0
                                    TextBlock.fontFamily mono
                                    TextBlock.foreground Brushes.DarkRed
                                    TextBlock.isHitTestVisible false ]
                              :> Types.IView
                      | None -> ()
                  | _ -> () ]

        let pending =
            match drag.Current with
            | Some(Wiring((from, side), at)) ->
                match pinCentre g from side with
                | Some a -> [ wireView pendingBrush 2.0 a at ]
                | None -> []
            | _ -> []

        let boxes = [ for name in boxOrder g do yield! boxView g selection.Current name g.positions[name] ]

        // ---- type to create: a box at the double-click, named from the
        // palette as it is typed. Enter takes the first match.
        let typingView =
            match typing.Current with
            | None -> []
            | Some((wx, wy), text) ->
                let matches =
                    Seq.append palette.Keys g.designs.Keys
                    |> Seq.filter (fun n -> text = "" || n.StartsWith(text, System.StringComparison.OrdinalIgnoreCase))
                    |> Seq.sort
                    |> List.ofSeq

                let create () =
                    match matches with
                    | first :: _ ->
                        let exact = matches |> List.tryFind (fun n -> n = text) |> Option.defaultValue first
                        typing.Set None
                        placeBox exact (wx, wy)
                    | [] -> message.Set $"no unit called '{text}'"

                [ StackPanel.create
                      [ Canvas.left (wx * z + px)
                        Canvas.top (wy * z + py)
                        StackPanel.children (
                            [ TextBox.create
                                  [ TextBox.width 160.0
                                    TextBox.text text
                                    TextBox.placeHolderText "unit name"
                                    TextBox.onLoaded ((fun e -> (e.Source :?> TextBox).Focus() |> ignore), SubPatchOptions.Always)
                                    TextBox.onTextChanged ((fun t -> typing.Set(Some((wx, wy), t))), SubPatchOptions.Always)
                                    TextBox.onKeyDown (
                                        (fun e ->
                                            match e.Key with
                                            | Key.Enter ->
                                                e.Handled <- true
                                                create ()
                                            | Key.Escape ->
                                                e.Handled <- true
                                                typing.Set None
                                            | _ -> ()),
                                        SubPatchOptions.Always
                                    ) ]
                              :> Types.IView ]
                            @ [ for n in List.truncate 6 matches ->
                                    TextBlock.create
                                        [ TextBlock.text n
                                          TextBlock.fontSize 12.0
                                          TextBlock.margin (Thickness(6.0, 1.0))
                                          TextBlock.foreground Brushes.DimGray ]
                                    :> Types.IView ]
                        ) ]
                  :> Types.IView ]

        // ---- the palette: every unit, a click adds a box of it.
        let paletteView =
            StackPanel.create
                [ DockPanel.dock Dock.Left
                  StackPanel.width 130.0
                  StackPanel.margin (Thickness 6.0)
                  StackPanel.children (
                      [ TextBlock.create [ TextBlock.text "palette"; TextBlock.fontWeight FontWeight.Bold; TextBlock.margin (Thickness(4.0, 2.0)) ] :> Types.IView ]
                      @ [ for name in palette.Keys |> Seq.sort ->
                              Button.create
                                  [ Button.content name
                                    Button.horizontalAlignment Layout.HorizontalAlignment.Stretch
                                    Button.margin (Thickness(2.0, 1.0))
                                    Button.onClick ((fun _ -> placeBox name (somewhereVisible ())), SubPatchOptions.Always) ]
                              :> Types.IView ]
                      @ [ TextBlock.create [ TextBlock.text "designs"; TextBlock.fontWeight FontWeight.Bold; TextBlock.margin (Thickness(4.0, 8.0, 4.0, 2.0)) ] :> Types.IView ]
                      @ [ for name in g.designs.Keys |> Seq.sort ->
                              Button.create
                                  [ Button.content name
                                    Button.horizontalAlignment Layout.HorizontalAlignment.Stretch
                                    Button.margin (Thickness(2.0, 1.0))
                                    Button.onClick ((fun _ -> placeBox name (somewhereVisible ())), SubPatchOptions.Always) ]
                              :> Types.IView ]
                      @ [ entry 118.0 importText.Current importText.Set (fun typed ->
                              match Warp11.DesignFile.load typed with
                              | Ok sub -> if change (importDesign sub) then message.Set $"imported {sub.name}: place it from the palette"
                              | Error why -> message.Set $"refused: {why}")
                          TextBlock.create
                              [ TextBlock.text "a design file's path, Enter imports it"
                                TextBlock.fontSize 10.0
                                TextBlock.foreground Brushes.Gray
                                TextBlock.textWrapping TextWrapping.Wrap
                                TextBlock.margin (Thickness(4.0, 0.0)) ]
                          :> Types.IView ]
                      @ [ TextBlock.create [ TextBlock.text "write a unit"; TextBlock.fontWeight FontWeight.Bold; TextBlock.margin (Thickness(4.0, 8.0, 4.0, 2.0)) ] :> Types.IView
                          Button.create
                              [ Button.content (if unitPanelOpen.Current then "close" else "new unit…")
                                Button.horizontalAlignment Layout.HorizontalAlignment.Stretch
                                Button.margin (Thickness(2.0, 1.0))
                                Button.onClick ((fun _ -> unitPanelOpen.Set(not unitPanelOpen.Current)), SubPatchOptions.Always) ]
                          :> Types.IView ]
                      @ [ TextBlock.create [ TextBlock.text "controls"; TextBlock.fontWeight FontWeight.Bold; TextBlock.margin (Thickness(4.0, 8.0, 4.0, 2.0)) ] :> Types.IView ]
                      @ [ for label, kind, format, value in [ "number", NumberBox, unsignedInt 16, "0"; "toggle", NumberBox, unsignedInt 1, "0"; "constant", ConstantBox, unsignedInt 16, "0" ] ->
                              Button.create
                                  [ Button.content label
                                    Button.horizontalAlignment Layout.HorizontalAlignment.Stretch
                                    Button.margin (Thickness(2.0, 1.0))
                                    Button.onClick (
                                        (fun _ ->
                                            let mutable placed = None

                                            if change (addControlBox kind format value (somewhereVisible ()) >> Result.map (fun (g, n) -> placed <- Some n; g)) then
                                                placed |> Option.iter (fun n -> select (Some(SelectedBox n)); message.Set $"added {n}")),
                                        SubPatchOptions.Always
                                    ) ]
                              :> Types.IView ]
                  ) ]
            :> Types.IView

        // ---- the toolbar: the file and the history on the first row; the
        // run controls, with a number box per control, on the second.
        let openInSim () =
            match opening.opener with
            | None -> ()
            | Some opener ->
                let knobs =
                    [ for name, _ in g.controls ->
                          let typed =
                              controlText.Current
                              |> Map.tryFind name
                              |> Option.bind (fun t ->
                                  match System.UInt64.TryParse t with
                                  | true, v -> Some v
                                  | _ -> None)

                          let last = valueOf snap name |> Option.map uint64
                          name, (typed |> Option.orElse last |> Option.defaultValue 0UL) ]

                try
                    let fresh = opener g knobs
                    liveState.Set(Some fresh)
                    snapshot.Set fresh.session.Latest
                    message.Set "opened in the simulator: the recording plays from the start"
                with e ->
                    message.Set $"refused: {e.Message}"

        let fileRow =
            StackPanel.create
                [ DockPanel.dock Dock.Top
                  StackPanel.orientation Layout.Orientation.Horizontal
                  StackPanel.margin (Thickness(10.0, 6.0, 10.0, 0.0))
                  StackPanel.children
                      [ button "New" (fun () ->
                            stopLive ()
                            history.Set(Warp11.Edit.history (withLayout (emptyGraph "Untitled" g.sampleRate)))
                            designName.Set "Untitled"
                            select None
                            message.Set "a new design")
                        button "Open" (fun () ->
                            match Warp11.DesignFile.load filePath.Current with
                            | Ok g ->
                                stopLive ()
                                history.Set(Warp11.Edit.history (withLayout g))
                                designName.Set g.name
                                rateText.Set(g.sampleRate.ToString(CultureInfo.InvariantCulture))
                                select None
                                message.Set $"opened {filePath.Current}: {g.name}, %d{g.boxes.Length} boxes, %d{g.edges.Length} wires"
                            | Error why -> message.Set $"refused: {why}")
                        button "Save" (fun () ->
                            if filePath.Current = "" then
                                message.Set "a path to save to, first"
                            else
                                try
                                    Warp11.DesignFile.save filePath.Current g
                                    message.Set $"saved {filePath.Current}"
                                with e ->
                                    message.Set $"could not save: {e.Message}")
                        entry 260.0 filePath.Current filePath.Set (fun _ -> ())
                        button "Board" (fun () ->
                            if filePath.Current = "" then
                                message.Set "a path to write beside, first"
                            else
                                try
                                    let dir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath filePath.Current), "board")
                                    let written = Warp11.BoardTop.write dir (Warp11.BoardTop.boardTop kv260 g) |> String.concat ", "
                                    message.Set $"wrote {written}"
                                with e ->
                                    message.Set $"refused: {e.Message}")
                        button "Export F#" (fun () ->
                            if filePath.Current = "" then
                                message.Set "a path to export beside, first"
                            else
                                match Warp11.Export.export g with
                                | Ok source ->
                                    let path = System.IO.Path.ChangeExtension(filePath.Current, ".fs")
                                    System.IO.File.WriteAllText(path, source)
                                    message.Set $"exported {path}"
                                | Error why -> message.Set $"refused: {why}")
                        button "Undo" undoLast
                        button "Redo" redoLast
                        button "Delete" deleteSelection
                        (match live, opening.opener with
                         | None, Some _ -> button "Open in sim" openInSim
                         | _ -> TextBlock.create [] :> Types.IView)
                        // Where the view is: the design, then each box drilled into,
                        // each a step back out.
                        StackPanel.create
                            [ StackPanel.orientation Layout.Orientation.Horizontal
                              StackPanel.margin (Thickness(10.0, 0.0, 0.0, 0.0))
                              StackPanel.children
                                  [ for i, name in List.indexed (root.name :: path.Current) ->
                                        Button.create
                                            [ Button.content (if i = 0 then name else $"› {name}")
                                              Button.margin (Thickness(0.0, 0.0, 2.0, 0.0))
                                              Button.background (if i = path.Current.Length then Brushes.LightSteelBlue :> IBrush else Brushes.Transparent :> IBrush)
                                              Button.onClick (
                                                  (fun _ ->
                                                      let shorter = List.truncate i path.Current
                                                      path.Set shorter
                                                      graphAt shorter root |> Option.iter (fun g -> designName.Set g.name)
                                                      select None),
                                                  SubPatchOptions.Always
                                              ) ]
                                        :> Types.IView ] ]
                        TextBlock.create
                            [ TextBlock.margin (Thickness(10.0, 0.0))
                              TextBlock.verticalAlignment Layout.VerticalAlignment.Center
                              TextBlock.fontFamily mono
                              TextBlock.text
                                  $"{g.boxes.Length} boxes, {g.edges.Length} wires — %d{history.Current.past.Length} to undo — zoom {inv z}" ] ] ]
            :> Types.IView

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

        let runRow =
            match live with
            | None -> []
            | Some l ->
                let progress =
                    (match l.recording with
                     | Some r -> $"  beats %d{r.framesOffered ()} offered, %d{r.remaining ()} to go"
                     | None -> "")
                    + (match l.audio with
                       | Some a when a.Listening -> $"  heard %d{a.Sent}"
                       | _ -> "")

                let numberBox (name: string, f: NumberFormat) =
                    let text = controlText.Current |> Map.tryFind name |> Option.defaultValue (valueOf snap name |> Option.map string |> Option.defaultValue "")

                    StackPanel.create
                        [ StackPanel.orientation Layout.Orientation.Horizontal
                          StackPanel.margin (Thickness(0.0, 0.0, 8.0, 0.0))
                          StackPanel.children
                              [ label name
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
                        StackPanel.orientation Layout.Orientation.Horizontal
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

                                   let fresh = l.reopen g knobs
                                   liveState.Set(Some fresh)
                                   snapshot.Set fresh.session.Latest
                                   message.Set "reset: the recording plays again from the start")
                               (match l.recording, l.savePath with
                                | Some tape, Some path -> button "Save heard" (fun () -> message.Set(tape.save path))
                                | _ -> TextBlock.create [] :> Types.IView)
                               TextBlock.create
                                   [ TextBlock.margin (Thickness(10.0, 0.0))
                                     TextBlock.verticalAlignment Layout.VerticalAlignment.Center
                                     TextBlock.fontFamily mono
                                     TextBlock.text (
                                         $"cycle %d{snap.cycle}{progress}"
                                         + (match snap.hit with
                                            | Some hit -> $"  — stopped: {hit}"
                                            | None -> "")
                                     ) ] ]) ]
                  :> Types.IView
                  // Breakpoints: a condition over the design's nets — the names the
                  // panel shows — and Run stops the cycle it holds.
                  StackPanel.create
                      [ DockPanel.dock Dock.Top
                        StackPanel.orientation Layout.Orientation.Horizontal
                        StackPanel.margin (Thickness(10.0, 0.0, 10.0, 6.0))
                        StackPanel.children (
                            [ label "break when"
                              entry 260.0 breakText.Current breakText.Set (fun typed ->
                                  if typed <> "" then
                                      match l.session.AddBreakpoint typed with
                                      | Ok() ->
                                          breakText.Set ""
                                          message.Set $"breaks when {typed}"
                                      | Error why -> message.Set $"refused: {why}") ]
                            @ [ for b in snap.breakpoints ->
                                    // A TextBlock as the content: a button's bare text reads
                                    // an underscore as a mnemonic and drops it.
                                    Button.create
                                        [ Button.content (TextBlock.create [ TextBlock.text $"× {b.text}  (%d{b.hits})"; TextBlock.fontFamily mono ])
                                          Button.margin (Thickness(2.0, 0.0))
                                          Button.onClick ((fun _ -> l.session.RemoveBreakpoint b.text), SubPatchOptions.Always) ]
                                    :> Types.IView ]
                        ) ]
                  :> Types.IView ]

        // ---- the property panel: what is selected. A box's name and copies;
        // the boundary's pins, added and removed here; a wire's ends. And
        // when the design is running, the pins' values and the box's own
        // signals.
        let row (name: string, value: string) =
            TextBlock.create [ TextBlock.fontFamily mono; TextBlock.fontSize 12.0; TextBlock.text $"%-28s{name} {value}" ] :> Types.IView

        let heading (text: string) =
            TextBlock.create [ TextBlock.text text; TextBlock.fontWeight FontWeight.Bold; TextBlock.margin (Thickness(0.0, 8.0, 0.0, 2.0)) ]
            :> Types.IView

        let liveRows (box: string) =
            match live with
            | None -> []
            | Some l ->
                let ins, outs = pinsOf g box
                let controls = controlsOf g box

                let pinRows =
                    [ for pins, side, tag in [ ins, In, "in"; outs, Out, "out" ] do
                          match box with
                          | "input"
                          | "output" -> ()
                          | _ -> yield $"{tag} valid", (valueOf snap (validOf box side) |> Option.map string |> Option.defaultValue "—")

                          for n, f in pins do
                              let shown =
                                  match probeOf (pin box n) side with
                                  | Some net -> valueOf snap net |> Option.map (showValue f) |> Option.defaultValue "—"
                                  | None -> "—"

                              yield $"{tag} {n}", shown
                      for n, f in controls do
                          if box <> "input" then
                              let shown =
                                  match probeOf (pin box n) In with
                                  | Some net -> valueOf snap net |> Option.map (showValue f) |> Option.defaultValue "—"
                                  | None -> "—"

                              yield $"ctl {n}", shown ]

                let ownRows =
                    [ for name in l.signalsOf (prefix + box) -> name, (valueOf snap name |> Option.map string |> Option.defaultValue "—") ]

                // The box's signal outlets over the recent trace, one colour
                // each: the waveform, full scale the lane's height.
                let lane =
                    let traces =
                        [ for i, (n, f) in List.indexed outs do
                              match probeOf (pin box n) Out with
                              | Some net when i < Scope.colours.Length ->
                                  let values = Scope.beats slice.Current (validOf box Out) net
                                  if values.Length > 0 then yield values, Scope.colours[i], f.totalWidth
                              | _ -> () ]

                    match traces with
                    | [] -> []
                    | (_, _, width) :: _ ->
                        [ heading "the last beats out"
                          Image.create
                              [ Image.source (Scope.render [ for v, c, _ in traces -> v, c ] 360 90 width)
                                Image.width 360.0
                                Image.height 90.0
                                Image.stretch Stretch.None
                                Image.horizontalAlignment Layout.HorizontalAlignment.Left ]
                          :> Types.IView
                          TextBlock.create
                              [ TextBlock.fontSize 10.0
                                TextBlock.foreground Brushes.Gray
                                TextBlock.text (String.concat ", " [ for i, (n, _) in List.indexed outs do if i < Scope.colours.Length then yield n ]) ]
                          :> Types.IView ]

                lane @ [ heading "this cycle" ] @ (pinRows |> List.map row) @ (ownRows |> List.map row)

        let pinFormat () =
            match System.Int32.TryParse pinWidth.Current, System.Int32.TryParse pinFraction.Current with
            | (true, w), (true, f) when w > 0 && f >= 0 && f <= w -> Ok({ totalWidth = w; fracBits = f; signed = pinSigned.Current }: NumberFormat)
            | _ -> Error $"a pin's format is a width and a fraction, not '{pinWidth.Current}' and '{pinFraction.Current}'"

        let addPin (add: string * NumberFormat -> Graph -> Result<Graph, string>) =
            match pinFormat () with
            | Ok f ->
                if change (add (pinName.Current, f)) then
                    message.Set $"added {pinName.Current}"
                    pinName.Set ""
            | Error why -> message.Set $"refused: {why}"

        let boundaryRows (box: string) =
            let pinRow (kind: string) (n: string, f: NumberFormat) =
                StackPanel.create
                    [ StackPanel.orientation Layout.Orientation.Horizontal
                      StackPanel.children
                          [ button "×" (fun () -> if change (removeBoundaryPin (pin box n)) then message.Set $"removed {box}.{n}")
                            TextBlock.create
                                [ TextBlock.fontFamily mono
                                  TextBlock.fontSize 12.0
                                  TextBlock.verticalAlignment Layout.VerticalAlignment.Center
                                  TextBlock.text $"{kind} {n}: {describeFormat f}" ] ] ]
                :> Types.IView

            let signals, controls =
                match box with
                | "input" -> g.inputs, g.controls
                | _ -> g.outputs, []

            // The form before the list, so adding a pin does not move the form
            // under the pointer — or under the view diff, which patches by
            // position and would hand one entry's text to another.
            ([
                heading "add a pin"
                StackPanel.create
                    [ StackPanel.orientation Layout.Orientation.Horizontal
                      StackPanel.children
                          [ label "name"
                            entry 110.0 pinName.Current pinName.Set (fun _ -> ())
                            label "width"
                            entry 44.0 pinWidth.Current pinWidth.Set (fun _ -> ()) ] ]
                :> Types.IView
                StackPanel.create
                    [ StackPanel.orientation Layout.Orientation.Horizontal
                      StackPanel.margin (Thickness(0.0, 4.0))
                      StackPanel.children
                          [ label "fraction"
                            entry 44.0 pinFraction.Current pinFraction.Set (fun _ -> ())
                            CheckBox.create
                                [ CheckBox.content "signed"
                                  CheckBox.isChecked pinSigned.Current
                                  CheckBox.onIsCheckedChanged (
                                      (fun e ->
                                          match e.Source with
                                          | :? Avalonia.Controls.Primitives.ToggleButton as t -> pinSigned.Set(t.IsChecked.GetValueOrDefault())
                                          | _ -> ()),
                                      SubPatchOptions.Always
                                  ) ]
                            (if box = "input" then
                                 button "add signal" (fun () -> addPin addInputPin)
                             else
                                 button "add signal" (fun () -> addPin addOutputPin))
                            (if box = "input" then
                                 button "add control" (fun () -> addPin addControl)
                             else
                                 TextBlock.create [] :> Types.IView) ] ]
                :> Types.IView
                heading "pins" ])
            @ (signals |> List.map (pinRow "signal"))
            @ (controls |> List.map (pinRow "control"))

        /// What the placement decided for a box: how its copies sit against
        /// the design's streams, and what the stage puts around the unit.
        let placementOf (b: Box) (u: ErasedFu) =
            let m, n = g.streams, b.copies

            match u.law with
            | Combinational _ when n = m -> "in place: the law inside the beat, no net declared"
            | Combinational _ when n = 1 -> $"shared: %d{m} streams on one copy behind an arbiter, a client stage each"
            | Combinational _ -> $"%d{n} copies for %d{m} streams: the elaborator will refuse this"
            | Sequential _ when n = m -> "one copy per stream, a context FIFO holding the rest of the beat beside it"
            | Sequential _ when n > m && n % m = 0 -> $"a farm of %d{n / m} copies per stream, in order, a context FIFO each"
            | Sequential _ -> $"%d{n} copies over %d{m} streams: the elaborator will refuse this"

        let boxRows (name: string) =
            let b = g.boxes |> List.find (fun b -> b.name = name)
            let u = unitOf g b

            match designOf g b with
            | Some sub ->
                [ StackPanel.create
                      [ StackPanel.orientation Layout.Orientation.Horizontal
                        StackPanel.children
                            [ label "name"
                              entry 120.0 nameText.Current nameText.Set (fun typed ->
                                  if change (renameBox name typed) then
                                      select (Some(SelectedBox typed))
                                      message.Set $"renamed {name} to {typed}") ] ]
                  :> Types.IView
                  row ("design", $"{sub.name}: %d{sub.boxes.Length} boxes, %d{sub.edges.Length} wires")
                  row ("placement", placementOf b u)
                  button "Open" (fun () ->
                      path.Set(path.Current @ [ name ])
                      designName.Set sub.name
                      select None
                      message.Set $"in {name}")
                  heading "signal inlets" ]
                @ (sub.inputs |> List.map (fun (n, f) -> row (n, describeFormat f)))
                @ [ heading "signal outlets" ]
                @ (sub.outputs |> List.map (fun (n, f) -> row (n, describeFormat f)))
                @ [ heading "control inlets — the design's own ports" ]
                @ (controlPorts sub |> List.map (fun (n, f) -> row (n, describeFormat f)))
            | None ->

            let factory = palette[b.unit]

            // A creation argument: typed, committed on Enter, refused by the
            // factory naming the parameter. A change is a new design.
            let argumentRow (p: Parameter) =
                let text = argumentText.Current |> Map.tryFind p.name |> Option.defaultValue p.``default``

                let kind =
                    match p.kind with
                    | IntParameter -> "whole number"
                    | FloatParameter -> "number"
                    | FloatsParameter -> "numbers, comma-separated"
                    | ChoiceParameter choices -> String.concat " | " choices

                StackPanel.create
                    [ StackPanel.orientation Layout.Orientation.Horizontal
                      StackPanel.margin (Thickness(0.0, 2.0))
                      StackPanel.children
                          [ TextBlock.create
                                [ TextBlock.text p.name
                                  TextBlock.width 80.0
                                  TextBlock.fontFamily mono
                                  TextBlock.fontSize 12.0
                                  TextBlock.verticalAlignment Layout.VerticalAlignment.Center ]
                            entry 150.0 text (fun t -> argumentText.Set(argumentText.Current |> Map.add p.name t)) (fun typed ->
                                if change (setArgument name p.name typed) then
                                    message.Set $"{name}: {p.name} = {typed}")
                            TextBlock.create
                                [ TextBlock.text $"{p.about} ({kind})"
                                  TextBlock.fontSize 10.0
                                  TextBlock.foreground Brushes.Gray
                                  TextBlock.textWrapping TextWrapping.Wrap
                                  TextBlock.width 120.0
                                  TextBlock.margin (Thickness(6.0, 0.0, 0.0, 0.0))
                                  TextBlock.verticalAlignment Layout.VerticalAlignment.Center ] ] ]
                :> Types.IView

            let lawText =
                match u.law with
                | Sequential _ -> "sequential"
                | Combinational _ -> "combinational"

            // A wired inlet says where from; an unwired one holds a value,
            // typed here — poked live when the design is running.
            let controlRow (n: string, f: NumberFormat) =
                match g.edges |> List.tryFind (fun e -> e.``to`` = pin name n) with
                | Some e -> row (n, $"{describeFormat f}  from {showPin e.from}")
                | None ->
                    let key = $"setting:{n}"
                    let text = argumentText.Current |> Map.tryFind key |> Option.defaultValue (b.settings |> Map.tryFind n |> Option.defaultValue "0")

                    StackPanel.create
                        [ StackPanel.orientation Layout.Orientation.Horizontal
                          StackPanel.margin (Thickness(0.0, 2.0))
                          StackPanel.children
                              [ TextBlock.create
                                    [ TextBlock.text n
                                      TextBlock.width 80.0
                                      TextBlock.fontFamily mono
                                      TextBlock.fontSize 12.0
                                      TextBlock.verticalAlignment Layout.VerticalAlignment.Center ]
                                entry 100.0 text (fun t -> argumentText.Set(argumentText.Current |> Map.add key t)) (fun typed ->
                                    let bits =
                                        match System.UInt64.TryParse typed with
                                        | true, v -> Some(implicitPortName name n, v)
                                        | _ -> None

                                    changeValue (setSetting name n typed) bits |> ignore)
                                TextBlock.create
                                    [ TextBlock.text $"{describeFormat f}, unwired"
                                      TextBlock.fontSize 10.0
                                      TextBlock.foreground Brushes.Gray
                                      TextBlock.margin (Thickness(6.0, 0.0, 0.0, 0.0))
                                      TextBlock.verticalAlignment Layout.VerticalAlignment.Center ] ] ]
                    :> Types.IView

            [ StackPanel.create
                  [ StackPanel.orientation Layout.Orientation.Horizontal
                    StackPanel.children
                        [ label "name"
                          entry 120.0 nameText.Current nameText.Set (fun typed ->
                              if change (renameBox name typed) then
                                  select (Some(SelectedBox typed))
                                  message.Set $"renamed {name} to {typed}") ] ]
              :> Types.IView
              StackPanel.create
                  [ StackPanel.orientation Layout.Orientation.Horizontal
                    StackPanel.margin (Thickness(0.0, 4.0))
                    StackPanel.children
                        [ label "copies"
                          entry 50.0 copiesText.Current copiesText.Set (fun typed ->
                              match System.Int32.TryParse typed with
                              | true, n -> if change (setCopies name n) then message.Set $"{name}: %d{n} copies"
                              | _ -> message.Set $"refused: copies is a number, not '{typed}'") ] ]
              :> Types.IView
              row ("unit", $"{b.unit}, {lawText}")
              row ("placement", placementOf b u) ]
            @ (if factory.parameters.IsEmpty then [] else heading "creation arguments" :: (factory.parameters |> List.map argumentRow))
            @ [ heading "signal inlets" ]
            @ (u.operands.fields |> List.map (fun (n, f) -> row (n, describeFormat f)))
            @ [ heading "signal outlets" ]
            @ (u.results.fields |> List.map (fun (n, f) -> row (n, describeFormat f)))
            @ [ heading "control inlets" ]
            @ (u.controls |> List.map controlRow)

        let controlBoxRows (c: ControlBox) =
            let kindText =
                match c.kind, c.format.totalWidth with
                | NumberBox, 1 -> "toggle: a one-bit number box, a port of the design"
                | NumberBox, _ -> "number box: a port of the design, poked live"
                | ConstantBox, _ -> "constant: a literal in the design"

            let text = argumentText.Current |> Map.tryFind "value" |> Option.defaultValue c.value

            let commit (typed: string) =
                match c.kind, System.UInt64.TryParse typed with
                | NumberBox, (true, v) -> changeValue (setControlValue c.name typed) (Some(c.name, v)) |> ignore
                | NumberBox, _ -> changeValue (setControlValue c.name typed) None |> ignore
                | ConstantBox, _ -> if change (setControlValue c.name typed) then message.Set $"{c.name} = {typed}"

            [ StackPanel.create
                  [ StackPanel.orientation Layout.Orientation.Horizontal
                    StackPanel.children
                        [ label "name"
                          entry 120.0 nameText.Current nameText.Set (fun typed ->
                              if change (renameBox c.name typed) then
                                  select (Some(SelectedBox typed))
                                  message.Set $"renamed {c.name} to {typed}") ] ]
              :> Types.IView
              row ("kind", kindText)
              row ("format", describeFormat c.format)
              StackPanel.create
                  [ StackPanel.orientation Layout.Orientation.Horizontal
                    StackPanel.margin (Thickness(0.0, 4.0))
                    StackPanel.children
                        (if c.kind = NumberBox && c.format.totalWidth = 1 then
                             [ label "on"
                               CheckBox.create
                                   [ CheckBox.isChecked (c.value = "1")
                                     CheckBox.onIsCheckedChanged (
                                         (fun e ->
                                             match e.Source with
                                             | :? Avalonia.Controls.Primitives.ToggleButton as t ->
                                                 let v = if t.IsChecked.GetValueOrDefault() then 1UL else 0UL

                                                 if string v <> c.value then
                                                     changeValue (setControlValue c.name (string v)) (Some(c.name, v)) |> ignore
                                             | _ -> ()),
                                         SubPatchOptions.Always
                                     ) ] ]
                         else
                             [ label "value"
                               entry 100.0 text (fun t -> argumentText.Set(argumentText.Current |> Map.add "value" t)) commit ]) ]
              :> Types.IView
              row ("outlet", $"{controlOutlet}: {describeFormat c.format}") ]

        // The panel is keyed by what it shows: the view diff patches by
        // position, and a panel of another shape would otherwise inherit the
        // last one's entries, text and all.
        // A unit written here: F# in a box, compiled by the compiler service
        // against the library this canvas runs on, added to the palette for
        // the session and its source kept with the design.
        let unitRows =
            // Compiled on a worker: the compiler service waits on its own
            // work inside, and on the UI thread that wait meets the
            // dispatcher and neither moves. The verdict comes back posted.
            let compile () =
                match Compiler.definedName unitSource.Current with
                | None -> message.Set "refused: the source needs a `let` to name the unit by"
                | Some name ->
                    let source = unitSource.Current
                    let rate = g.sampleRate
                    message.Set $"compiling {name}…"

                    System.Threading.Tasks.Task.Run(fun () -> Compiler.compileUnit name source)
                    |> fun task ->
                        task.ContinueWith(fun (t: System.Threading.Tasks.Task<Result<Factory, string>>) ->
                            Avalonia.Threading.Dispatcher.UIThread.Post(fun () ->
                                match t.Result with
                                | Error why -> message.Set $"refused: {why}"
                                | Ok factory ->
                                    match addSessionUnit factory with
                                    | Error why -> message.Set $"refused: {why}"
                                    | Ok() ->
                                        match factory.make rate Map.empty with
                                        | Ok u ->
                                            if change (defineUnit name source) then
                                                message.Set $"unit {name}: in [{describePins u.operands.fields}] out [{describePins u.results.fields}] — in the palette"
                                        | Error why -> message.Set $"refused: {why}"))
                        |> ignore

            [ TextBlock.create
                  [ TextBlock.text "F# defining one value, a unit over typed pins (`fu`, `fuSequential`, `moduleUnit`). The library is open. Compile adds it to the palette and keeps the source with the design."
                    TextBlock.fontSize 11.0
                    TextBlock.foreground Brushes.Gray
                    TextBlock.textWrapping TextWrapping.Wrap ]
              :> Types.IView
              TextBox.create
                  [ TextBox.text unitSource.Current
                    TextBox.acceptsReturn true
                    TextBox.acceptsTab true
                    TextBox.fontFamily mono
                    TextBox.fontSize 12.0
                    TextBox.height 220.0
                    TextBox.textWrapping TextWrapping.NoWrap
                    TextBox.onTextChanged (unitSource.Set, SubPatchOptions.Always) ]
              :> Types.IView
              button "Compile" compile ]

        let propertyPanel =
            let key, title, rows =
                match selection.Current, unitPanelOpen.Current with
                | None, true -> "unit", "a unit", unitRows
                | _ ->

                match selection.Current with
                | Some(SelectedBox("input" | "output" as box)) -> $"boundary:{box}", box, boundaryRows box @ liveRows box
                | Some(SelectedBox box) when (controlBoxOf g box).IsSome -> $"control:{box}", box, controlBoxRows (controlBoxOf g box).Value
                | Some(SelectedBox box) -> $"box:{box}", box, boxRows box @ liveRows box
                | Some(SelectedWire e) ->
                    "wire",
                    "wire",
                    [ row ("from", showPin e.from)
                      row ("to", showPin e.``to``)
                      button "Delete" deleteSelection ]
                | None ->
                    // Nothing selected: the design itself.
                    let where = String.concat "/" path.Current
                    $"design:{where}",
                    "design",
                    [ StackPanel.create
                          [ StackPanel.orientation Layout.Orientation.Horizontal
                            StackPanel.children
                                [ label "name"
                                  entry 160.0 designName.Current designName.Set (fun typed ->
                                      if change (rename typed) then
                                          message.Set $"the design is {typed}") ] ]
                      :> Types.IView
                      StackPanel.create
                          [ StackPanel.orientation Layout.Orientation.Horizontal
                            StackPanel.margin (Thickness(0.0, 4.0))
                            StackPanel.children
                                [ label "sample rate"
                                  entry 100.0 rateText.Current rateText.Set (fun typed ->
                                      match System.Double.TryParse(typed, System.Globalization.NumberStyles.Float, CultureInfo.InvariantCulture) with
                                      | true, rate ->
                                          if change (setSampleRate rate) then
                                              message.Set $"the design is made for %g{rate} Hz"
                                      | _ -> message.Set $"refused: '{typed}' is not a rate")
                                  label "Hz" ] ]
                      :> Types.IView
                      StackPanel.create
                          [ StackPanel.orientation Layout.Orientation.Horizontal
                            StackPanel.margin (Thickness(0.0, 4.0))
                            StackPanel.children
                                [ label "streams"
                                  entry 50.0 streamsText.Current streamsText.Set (fun typed ->
                                      match System.Int32.TryParse typed with
                                      | true, n -> if change (setStreams n) then message.Set $"%d{n} streams — a sequential box needs a copy per stream"
                                      | _ -> message.Set $"refused: '{typed}' is not a count") ] ]
                      :> Types.IView
                      TextBlock.create
                          [ TextBlock.margin (Thickness(0.0, 12.0, 0.0, 0.0))
                            TextBlock.text "select a box or a wire; double-click the canvas to add a box"
                            TextBlock.foreground Brushes.Gray
                            TextBlock.textWrapping TextWrapping.Wrap ]
                      :> Types.IView ]

            ScrollViewer.create
                [ DockPanel.dock Dock.Right
                  ScrollViewer.width 390.0
                  ScrollViewer.content (
                      View.withKey
                          key
                          (StackPanel.create
                              [ StackPanel.margin (Thickness 10.0)
                                StackPanel.children (
                                    [ TextBlock.create [ TextBlock.text title; TextBlock.fontWeight FontWeight.Bold; TextBlock.fontSize 14.0 ] :> Types.IView ]
                                    @ rows
                                ) ])
                  ) ]
            :> Types.IView

        DockPanel.create
            [ DockPanel.children (
                  [ TextBlock.create
                        [ DockPanel.dock Dock.Top
                          TextBlock.margin (Thickness(10.0, 6.0, 10.0, 0.0))
                          TextBlock.fontFamily mono
                          TextBlock.foreground (if message.Current.StartsWith "refused" then Brushes.DarkRed else Brushes.Black)
                          TextBlock.text (if message.Current = "" then " " else message.Current) ]
                    :> Types.IView
                    fileRow ]
                  @ runRow
                  @ [ paletteView; propertyPanel ]
                  @ [ Canvas.create
                          [ Canvas.background (SolidColorBrush(Color.FromRgb(255uy, 255uy, 255uy)))
                            Canvas.clipToBounds true
                            Canvas.focusable true
                            Canvas.onPointerPressed (onPressed, SubPatchOptions.Always)
                            Canvas.onPointerMoved (onMoved, SubPatchOptions.Always)
                            Canvas.onPointerReleased (onReleased, SubPatchOptions.Always)
                            Canvas.onPointerWheelChanged (onWheel, SubPatchOptions.Always)
                            Canvas.onKeyDown (onKey, SubPatchOptions.Always)
                            Canvas.children (
                                [ Canvas.create
                                      [ Canvas.renderTransformOrigin (RelativePoint(0.0, 0.0, RelativeUnit.Absolute))
                                        Canvas.renderTransform transform
                                        // Wires over boxes, as a design is read.
                                        Canvas.children (boxes @ wires @ pending) ]
                                  :> Types.IView ]
                                @ typingView
                            ) ]
                      :> Types.IView ]
              ) ])
