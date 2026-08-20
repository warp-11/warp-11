/// The debugger window's body. It knows an `IDebugSession` and nothing else —
/// which world is behind that seam is the composition root's business, exactly
/// as `IGolBus` is for the GoL view.
///
/// The view never touches the `Sim`. It posts commands and renders the
/// snapshots the session publishes, which is what lets another window drive the
/// same design at the same time.
module Warp11.SimView.View

open System.Numerics
open Avalonia
open Avalonia.Controls
open Avalonia.FuncUI
open Avalonia.FuncUI.DSL
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Styling
open Avalonia.Threading
open Warp11.Catalog
open Warp11.Debug
open Warp11.Inventory

let private mono = FontFamily "monospace"

let private blank =
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

/// How much of a memory one page shows. A mem is 2^addrWidth words — the whole
/// reason it is a window and not a table.
let private memoryRows = 16
let private memoryColumns = 8
let private pageWords = memoryRows * memoryColumns

type private Radix =
    | Dec
    | Hex
    | Bin

/// Hex and bin show the bits, which is what they mean. Dec shows the *value*,
/// so a signal the design declared signed reads as the negative number it holds
/// rather than as its two's-complement bit pattern — the debugger has no other
/// way to know, since the bits are the same either way.
let private format radix width signed (v: BigInteger) =
    match radix with
    | Dec when signed && (v >>> (width - 1)) &&& BigInteger.One = BigInteger.One ->
        (v - (BigInteger.One <<< width)).ToString()
    | Dec -> v.ToString()
    | Hex ->
        let digits = (width + 3) / 4
        let body = v.ToString("x").TrimStart '0'
        "0x" + (if body = "" then "0" else body).PadLeft(digits, '0')
    | Bin ->
        [ for i in width - 1 .. -1 .. 0 -> if (v >>> i) &&& BigInteger.One = BigInteger.One then '1' else '0' ]
        |> System.String.Concat
        |> sprintf "0b%s"

/// What a value *means*, where elaboration knows: a state machine's register
/// reads as `assemble`, not as 1. The number stays alongside — it is what the
/// emitted Verilog holds and what another waveform viewer will show — and a
/// code no state was given reads as `?`, which is the whole point of asking.
let private formatValue (machines: Map<string, Map<uint64, string>>) (signedSignals: Set<string>) radix name width (v: BigInteger) =
    let plain = format radix width (signedSignals.Contains name) v

    match machines.TryFind name with
    | None -> plain
    | Some states ->
        let stateName =
            if v >= BigInteger.Zero && v <= BigInteger System.UInt64.MaxValue then
                states.TryFind(uint64 v)
            else
                None

        let meaning = Option.defaultValue "?" stateName
        $"{meaning} ({plain})"

let private kindTag kind =
    match kind with
    | SignalKind.Input -> "in"
    | SignalKind.Output -> "out"
    | SignalKind.Reg -> "reg"
    | SignalKind.Wire -> "wire"

let private kindColor (palette: Theme.Palette) kind =
    match kind with
    | SignalKind.Input -> palette.input
    | SignalKind.Output -> palette.output
    | SignalKind.Reg -> palette.reg
    | SignalKind.Wire -> palette.wire

/// A group's own name is its instance path without the trailing underscore;
/// the top module's signals have no path at all.
let private groupLabel topName group =
    if group = "" then topName else String.length group - 1 |> (fun n -> group.Substring(0, n))

let private count n one many =
    if n = 1 then $"1 {one}" else $"%d{n} {many}"

/// What a poke field accepts: the same three radixes it displays. An
/// unparseable value pokes nothing rather than poking zero.
/// Text back into bits. A leading minus is accepted at the signal's width, so
/// a signed input round-trips: the box shows −56 and −56 is what you may type.
let private parseValue width (text: string) =
    let t = text.Trim()

    let wrap (v: BigInteger) =
        let span = BigInteger.One <<< width
        ((v % span) + span) % span

    let digits (prefix: string) (radix: int) =
        let body = t.Substring prefix.Length

        if body = "" then
            None
        else
            body
            |> Seq.fold
                (fun acc c ->
                    match acc with
                    | None -> None
                    | Some v ->
                        let d = System.Convert.ToInt32(string c, 16)
                        if d >= radix then None else Some(v * BigInteger radix + BigInteger d))
                (Some BigInteger.Zero)

    if t = "" || t = "-" then None
    elif t.StartsWith "0x" || t.StartsWith "0X" then digits "0x" 16
    elif t.StartsWith "0b" || t.StartsWith "0B" then digits "0b" 2
    elif t.StartsWith "-" && t.Substring 1 |> Seq.forall System.Char.IsAsciiDigit then
        Some(wrap -(BigInteger.Parse(t.Substring 1)))
    elif t |> Seq.forall System.Char.IsAsciiDigit then Some(BigInteger.Parse t)
    else None

let private digitsOnly (s: string) =
    s |> Seq.filter System.Char.IsAsciiDigit |> Seq.truncate 9 |> Seq.toArray |> System.String

/// Rendering every row of a design that has thousands is work nobody reads;
/// the count below the list says what was left out rather than pretending the
/// filter matched only this much.
let private listCap = 300

let private allGroups = "all groups"
let private allKinds = "all"

/// What the text buttons move, and how far they may go. The bounds are where
/// the window stops being usable rather than where it stops being legible:
/// below 0.7 the watch table's values run together, and above 1.8 the three
/// columns have no room left to be three columns.
///
/// Not called zoom: `Waveform.zoomFor` already owns that word here, and it
/// means pixels per column rather than a scale on everything.
let private minScale = 0.7
let private maxScale = 1.8
let private scaleStep = 0.1

/// Rounded at each step, because a tenth is not a binary fraction and eight
/// clicks of it would otherwise land on 1.0000000000000002 — which reads as
/// 100% and compares as not 1.0.
let private stepScale (delta: float) (current: float) =
    System.Math.Round(current + delta, 2) |> max minScale |> min maxScale

/// The whole window at the reader's chosen scale.
///
/// A *layout* transform, not a render one, and the difference is the whole
/// reason this works: the child is measured in its own coordinates and the
/// finished layout is scaled, rather than finished pixels being stretched. So
/// every width and column share in this file still means exactly what it says.
/// What scaling up actually does is hand the child *less* room to lay out in —
/// which is the same thing as making the window narrower, and is why the
/// filter row had to stop being a fixed-width stack before this was safe.
let private scaled factor (view: Avalonia.FuncUI.Types.IView) : Avalonia.FuncUI.Types.IView =
    LayoutTransformControl.create
        [ LayoutTransformControl.layoutTransform (ScaleTransform(factor, factor))
          Decorator.child view ]

/// How many of a design's ports to put in the watch list on open, per kind.
/// Every design in the registry has a handful; the cap is there so that a design
/// with a port per row of a grid — Game of Life has sixty-four rows out — cannot
/// fill the table by itself.
let private autoWatchPorts = 16

/// Where the debugger gets a design from.
type Source =
    /// The standalone debugger: it picks from a catalog and owns every session
    /// it opens. Which catalog is the caller's business — there is more than
    /// one, and a view that named a particular one could never show the others.
    | FromCatalog of catalog: Catalog * initial: string option
    /// Opened by another app on a session that app already owns and is
    /// driving. The picker becomes a label, and nothing here disposes it —
    /// closing the debugger window must not stop the design its owner is
    /// still running.
    | Attached of session: IDebugSession * title: string

/// The catalog an attached debugger reads for prose and source: none, since
/// its design came from somewhere the picker cannot see.
let private noCatalog =
    { entries = []; doc = (fun _ -> None); source = fun _ -> None }

/// What a host-supplied panel is handed. Everything the debugger knows that a
/// panel might reasonably want, and nothing about how the debugger draws.
type PanelContext =
    { /// The session driving what is on screen. `None` while a design loads.
      /// A panel may read it *and* drive it — a game-of-life view pokes the
      /// same design the watch table is showing.
      session: IDebugSession option
      /// Which catalog entry is open, for a panel that wants the design's
      /// prose, its source, or just its name.
      entry: Entry option
      /// The most recent snapshot, so a panel renders from the same frame the
      /// rest of the window does rather than sampling its own.
      snapshot: Snapshot
      /// The colours this window is using, so a panel matches rather than
      /// picking its own and being invisible in one of the two variants.
      palette: Theme.Palette }

/// A tab the host adds beside `watch`, `memory` and `waveform`.
///
/// The debugger owns the three panels that are about *any* design — its
/// signals, its memories, its trace. Everything else belongs to whoever knows
/// what the design means: the tutorial's prose, a Game of Life grid, a
/// register-map view for an AXI slave. Those arrive here rather than being
/// built in, which is why this list is a parameter and not a feature.
/// Where a host panel lives.
type Placement =
    /// A tab beside `watch`, `memory` and `waveform` — one instrument among the
    /// others, and only one of them on screen at a time. A Game of Life grid
    /// wants this: it is a thing you look *at*, in the space the instruments use.
    | WithInstruments
    /// Its own column, on screen the whole time you work the instruments.
    /// Reference — what the design is, what it is written as, what to try next —
    /// which is no use in a tab you have to leave the watch list to read.
    | Alongside

type Panel =
    { label: string
      view: PanelContext -> Avalonia.FuncUI.Types.IView
      placement: Placement }

let debugger (source: Source) (panels: Panel list) =
    Component(fun ctx ->
        let catalog =
            match source with
            | FromCatalog (c, _) -> c
            | Attached _ -> noCatalog

        let labels = catalog.entries |> List.map (fun e -> e.label)

        let opening =
            match source with
            | Attached (_, title) -> title
            | FromCatalog (_, initial) ->
                initial
                |> Option.filter (fun d -> List.contains d labels)
                |> Option.defaultValue (List.head labels)

        let selected = ctx.useState opening
        let snapshot = ctx.useState blank
        let summary = ctx.useState ""
        let stepText = ctx.useState "10"
        let filter = ctx.useState ""
        let kindFilter = ctx.useState allKinds
        let groupFilter = ctx.useState allGroups
        let radix = ctx.useState Hex
        // Which theme is showing. Held here rather than read from the
        // application on every render so that flipping it re-renders: the
        // hand-picked colours below are not Fluent's to swap.
        let variant = ctx.useState (Theme.currentVariant ())
        let palette = Theme.ofVariant variant.Current
        // How large everything is drawn. A scale rather than a font size: it is
        // applied to the whole window as a layout transform, so the type, the
        // controls and the space between them all move together and every
        // width in here keeps meaning what it meant.
        let textScale = ctx.useState 1.0
        let breakText = ctx.useState ""
        let breakError = ctx.useState ""
        // The host's panels, split by where they asked to live.
        let instruments = panels |> List.filter (fun p -> p.placement = WithInstruments)
        let alongside = panels |> List.filter (fun p -> p.placement = Alongside)

        // Which upper panel is showing, and where the memory one is looking.
        let panel = ctx.useState "watch"
        // Which tab of the reference column, when there is one.
        let reference =
            ctx.useState (alongside |> List.tryHead |> Option.map (fun p -> p.label) |> Option.defaultValue "")
        let memName = ctx.useState ""
        let memStart = ctx.useState 0
        let recordAll = ctx.useState false
        let traceNote = ctx.useState ""
        // None means "follow the end of the trace"; Some is a paged-back view.
        let wavePage = ctx.useState<int option> None
        let waveCursor = ctx.useState -1
        // What is typed into an input's field, kept apart from what the design
        // holds: the snapshot arrives 30 times a second and would otherwise
        // overwrite a half-typed number.
        let pokeText = ctx.useState Map.empty<string, string>
        // Bumped when a design finishes loading: the inventory itself is held
        // rather than rendered, because comparing a design's worth of signals
        // on every state change would cost more than the render it guards.
        let loaded = ctx.useState 0
        let held = ctx.useState<(IDebugSession * ModuleInventory) option> (None, renderOnChange = false)
        // Which entry is open, for the panels the host added. The catalog
        // memoizes prose and source, so a panel may ask on every render.
        let openEntry = ctx.useState<Entry option> None

        let session () = held.Current |> Option.map fst
        let post action = session () |> Option.iter action

        let describe (inventory: ModuleInventory) =
            let parts =
                [ count (List.length inventory.signals) "signal" "signals"
                  count (List.length inventory.mems) "memory" "memories"
                  count (List.length inventory.groups) "group" "groups" ]

            inventory.topName + " · " + String.concat " · " parts

        let load label =
            held.Current |> Option.iter (fun (s, _) -> s.Dispose())
            held.Set None
            filter.Set ""
            kindFilter.Set allKinds
            groupFilter.Set allGroups
            pokeText.Set Map.empty
            breakText.Set ""
            breakError.Set ""
            panel.Set "watch"
            memName.Set ""
            memStart.Set 0
            recordAll.Set false
            traceNote.Set ""
            wavePage.Set None
            waveCursor.Set -1
            snapshot.Set blank

            match
                catalog.entries |> List.tryFind (fun e -> e.label = label)
            with
            | None -> summary.Set $"no design named '{label}'"
            | Some chosen ->
                openEntry.Set(Some chosen)
                let build = chosen.build

                let live =
                    new DebugSession(build (), ownThread = not (System.OperatingSystem.IsBrowser()))
                    :> IDebugSession
                held.Set(Some(live, live.Inventory))
                snapshot.Set live.Latest
                summary.Set(describe live.Inventory)

                // The ports arrive watched, inputs first: between them they are
                // the design's interface, which is what someone opening it came
                // to see.
                //
                // Inputs because in this mode nothing else is driving them, and
                // a design whose enable is low looks broken rather than idle:
                // you step, the output holds at zero, and there is nothing on
                // screen to say why — least of all that the way to drive an
                // input is to watch it first. Outputs because a result you
                // cannot see is one you have to go hunting through the signal
                // list to judge, and the answer is the point.
                let watchPorts kind =
                    live.Inventory.signals
                    |> List.filter (fun s -> s.kind = kind)
                    |> List.truncate autoWatchPorts
                    |> List.iter (fun s -> live.Watch s.name)

                watchPorts SignalKind.Input
                watchPorts SignalKind.Output

                // And whatever else this entry's page talks about — a register
                // the prose reasons over is not something the debugger could
                // have guessed at, and it arrives last so the interface still
                // reads first. Already-watched names are dropped by the
                // session rather than doubled.
                chosen.watch |> List.iter live.Watch

                // The page's assumed inputs, so its first instruction works on
                // a fresh session. Poked after the watches purely for
                // tidiness; order is invisible either way.
                chosen.pokes
                |> List.iter (fun (name, value) -> live.Poke(name, System.Numerics.BigInteger value))

            loaded.Set(loaded.Current + 1)

        ctx.useEffect (
            handler =
                (fun () ->
                    match source with
                    | FromCatalog _ -> load opening
                    | Attached (session, _) ->
                        held.Set(Some(session, session.Inventory))
                        snapshot.Set session.Latest
                        summary.Set(describe session.Inventory)
                        loaded.Set(loaded.Current + 1)

                    // Polling the latest snapshot at frame rate rather than
                    // subscribing: the session already decides how often a
                    // snapshot is worth taking, and this way nothing has to be
                    // marshalled across threads.
                    //
                    // The same timer drives the session where nothing else can.
                    // `Pump` is a no-op for a session that runs its own thread,
                    // so this asks no question about which host it is in.
                    DispatcherTimer.Run(
                        (fun () ->
                            post (fun s ->
                                s.Pump() |> ignore
                                snapshot.Set s.Latest)

                            true),
                        System.TimeSpan.FromMilliseconds 33.0
                    )),
            triggers = [ EffectTrigger.AfterInit ]
        )

        let current = snapshot.Current
        let inventory = held.Current |> Option.map snd

        let watchedNames =
            current.values |> List.map (fun v -> v.name) |> Set.ofList

        let kindOf =
            inventory
            |> Option.map (fun i -> i.signals |> List.map (fun s -> s.name, s.kind) |> Map.ofList)
            |> Option.defaultValue Map.empty

        // ---- the header and the transport -------------------------------

        // An attached debugger has nothing to pick: the design is whatever its
        // owner is running.
        let designChooser: Avalonia.FuncUI.Types.IView =
            match source with
            | Attached _ ->
                TextBlock.create
                    [ TextBlock.width 260.0
                      TextBlock.verticalAlignment VerticalAlignment.Center
                      TextBlock.fontSize 15.0
                      TextBlock.text selected.Current ]
            | FromCatalog _ ->
                ComboBox.create
                    [ ComboBox.width 260.0
                      ComboBox.dataItems labels
                      ComboBox.selectedItem (box selected.Current)
                      ComboBox.onSelectedItemChanged (fun item ->
                          match item with
                          | :? string as label when label <> selected.Current ->
                              selected.Set label
                              load label
                          | _ -> ()) ]

        let transport =
            // Wraps rather than stacks. This row's width is the sum of its
            // parts, and a stack asks for that sum whether or not the window
            // has it — which is how the filter row and the trace status line
            // both ended up drawn over the panel beside them. A toolbar that
            // has run out of room should fold onto a second line.
            WrapPanel.create
                [ WrapPanel.orientation Orientation.Horizontal
                  WrapPanel.itemSpacing 6.0
                  WrapPanel.lineSpacing 6.0
                  WrapPanel.children
                      [ Button.create
                            [ Button.content "Reset"
                              Button.onClick (fun _ ->
                                  pokeText.Set Map.empty
                                  post (fun s -> s.Reset())) ]
                        Button.create [ Button.content "Step"; Button.onClick (fun _ -> post (fun s -> s.Step 1)) ]
                        Button.create
                            [ Button.content "Step N"
                              Button.onClick (fun _ ->
                                  match System.Int32.TryParse stepText.Current with
                                  | true, n when n > 0 -> post (fun s -> s.Step n)
                                  | _ -> ()) ]
                        TextBox.create
                            [ TextBox.width 80.0
                              TextBox.text stepText.Current
                              TextBox.onTextChanged (fun t -> stepText.Set(digitsOnly t)) ]
                        Button.create
                            [ Button.content "Run"
                              Button.isEnabled (not current.running)
                              Button.onClick (fun _ -> post (fun s -> s.Run())) ]
                        Button.create
                            [ Button.content "Pause"
                              Button.isEnabled current.running
                              Button.onClick (fun _ -> post (fun s -> s.Pause())) ]
                        Border.create [ Border.width 12.0 ]

                        Controls.fieldset
                            palette
                            "radix"
                            (StackPanel.create
                                [ StackPanel.orientation Orientation.Horizontal
                                  StackPanel.spacing 6.0
                                  StackPanel.children
                                      [ for r, name in [ Dec, "dec"; Hex, "hex"; Bin, "bin" ] ->
                                            Button.create
                                                [ Button.content name
                                                  Button.background (
                                                      if radix.Current = r then
                                                          palette.accent
                                                      else
                                                          Brushes.Transparent
                                                  )
                                                  Button.foreground (
                                                      if radix.Current = r then
                                                          palette.onAccent
                                                      else
                                                          palette.text
                                                  )
                                                  Button.onClick (fun _ -> radix.Set r) ] ] ])

                        Controls.fieldset
                            palette
                            "theme"
                            (StackPanel.create
                                [ StackPanel.orientation Orientation.Horizontal
                                  StackPanel.spacing 6.0
                                  StackPanel.children
                                      [ for v, name in
                                            [ ThemeVariant.Light, "light"; ThemeVariant.Dark, "dark" ] ->
                                            Button.create
                                                [ Button.content name
                                                  Button.background (
                                                      if variant.Current = v then
                                                          palette.accent
                                                      else
                                                          Brushes.Transparent
                                                  )
                                                  Button.foreground (
                                                      if variant.Current = v then
                                                          palette.onAccent
                                                      else
                                                          palette.text
                                                  )
                                                  Button.onClick (fun _ ->
                                                      // The application owns the chrome —
                                                      // Fluent repaints panels and buttons —
                                                      // and the state owns everything this
                                                      // window coloured itself.
                                                      Theme.apply v
                                                      variant.Set v) ] ] ])

                        Controls.fieldset
                            palette
                            "text"
                            (StackPanel.create
                                [ StackPanel.orientation Orientation.Horizontal
                                  StackPanel.spacing 6.0
                                  StackPanel.children
                                      [ Button.create
                                            [ Button.content "A−"
                                              Button.isEnabled (textScale.Current > minScale)
                                              Button.background Brushes.Transparent
                                              Button.foreground palette.text
                                              Button.onClick (fun _ ->
                                                  textScale.Set(stepScale (-scaleStep) textScale.Current)) ]

                                        // Both the readout and the way back to 1.0,
                                        // because a window left at 140% is otherwise a
                                        // size you have to click your way out of by
                                        // counting.
                                        Button.create
                                            [ Button.content
                                                  $"%d{int (System.Math.Round(textScale.Current * 100.0))}%%"
                                              Button.background Brushes.Transparent
                                              Button.foreground (
                                                  if textScale.Current = 1.0 then
                                                      palette.text
                                                  else
                                                      palette.accent
                                              )
                                              Button.onClick (fun _ -> textScale.Set 1.0) ]

                                        Button.create
                                            [ Button.content "A+"
                                              Button.isEnabled (textScale.Current < maxScale)
                                              Button.background Brushes.Transparent
                                              Button.foreground palette.text
                                              Button.onClick (fun _ ->
                                                  textScale.Set(stepScale scaleStep textScale.Current)) ] ] ]) ] ]

        let header =
            StackPanel.create
                [ StackPanel.spacing 6.0
                  StackPanel.children
                      [ StackPanel.create
                            [ StackPanel.orientation Orientation.Horizontal
                              StackPanel.spacing 10.0
                              StackPanel.children
                                  [ designChooser
                                    TextBlock.create
                                        [ TextBlock.verticalAlignment VerticalAlignment.Center
                                          TextBlock.fontFamily mono
                                          TextBlock.opacity 0.75
                                          TextBlock.text summary.Current ] ] ]
                        StackPanel.create
                            [ StackPanel.orientation Orientation.Horizontal
                              StackPanel.spacing 16.0
                              StackPanel.children
                                  [ TextBlock.create
                                        [ TextBlock.fontFamily mono
                                          TextBlock.fontSize 20.0
                                          TextBlock.text $"cycle %d{current.cycle}" ]
                                    TextBlock.create
                                        [ TextBlock.verticalAlignment VerticalAlignment.Center
                                          TextBlock.fontFamily mono
                                          TextBlock.opacity 0.75
                                          TextBlock.text (
                                              if current.rate >= 1e6 then sprintf "%.2fM cycles/s" (current.rate / 1e6)
                                              elif current.rate >= 1e3 then sprintf "%.1fk cycles/s" (current.rate / 1e3)
                                              elif current.rate > 0.0 then sprintf "%.0f cycles/s" current.rate
                                              else "idle"
                                          ) ]
                                    TextBlock.create
                                        [ TextBlock.verticalAlignment VerticalAlignment.Center
                                          TextBlock.fontFamily mono
                                          TextBlock.foreground (
                                              if current.hit.IsSome then palette.alert else palette.muted
                                          )
                                          TextBlock.text (
                                              match current.hit with
                                              | Some text -> $"stopped at  {text}"
                                              | None -> if current.running then "running" else "paused"
                                          ) ] ] ]
                        transport ] ]

        // ---- the signal picker ------------------------------------------

        let signals = inventory |> Option.map (fun i -> i.signals) |> Option.defaultValue []
        let topName = inventory |> Option.map (fun i -> i.topName) |> Option.defaultValue ""

        let stateMachines =
            inventory |> Option.map (fun i -> i.stateMachines) |> Option.defaultValue Map.empty

        // Which signals the design declared two's complement. Reaches the
        // formatter the same way the state-machine decode does.
        let signedSignals =
            inventory
            |> Option.map (fun i -> set [ for s in i.signals do if s.signed then yield s.name ])
            |> Option.defaultValue Set.empty

        let matching =
            let needle = filter.Current.Trim().ToLowerInvariant()

            signals
            |> List.filter (fun s ->
                (needle = "" || s.name.ToLowerInvariant().Contains needle)
                && (kindFilter.Current = allKinds || kindTag s.kind = kindFilter.Current)
                && (groupFilter.Current = allGroups || groupLabel topName s.group = groupFilter.Current))

        let shown = matching |> List.truncate listCap

        let groupChoices =
            allGroups
            :: (inventory
                |> Option.map (fun i -> i.groups |> List.map (groupLabel i.topName))
                |> Option.defaultValue [])

        let picker =
            DockPanel.create
                [ DockPanel.children
                      [ StackPanel.create
                            [ StackPanel.dock Dock.Top
                              StackPanel.spacing 6.0
                              StackPanel.margin (Thickness(0.0, 0.0, 0.0, 8.0))
                              StackPanel.children
                                  [ TextBox.create
                                        [ TextBox.placeHolderText "filter signals"
                                          TextBox.text filter.Current
                                          TextBox.onTextChanged filter.Set ]
                                    // A grid rather than a horizontal stack,
                                    // because a stack measures its children
                                    // with the width they ask for and then
                                    // overhangs the panel beside this one when
                                    // the column is narrower than their sum.
                                    // The kind list is a fixed set of short
                                    // words and is sized to them; the group
                                    // list takes whatever is left, which is
                                    // where the long names are.
                                    Grid.create
                                        [ Grid.columnDefinitions "Auto,6,*"
                                          Grid.children
                                              [ ComboBox.create
                                                    [ Grid.column 0
                                                      ComboBox.dataItems [ allKinds; "in"; "out"; "reg"; "wire" ]
                                                      ComboBox.selectedItem (box kindFilter.Current)
                                                      ComboBox.onSelectedItemChanged (fun item ->
                                                          match item with
                                                          | :? string as k -> kindFilter.Set k
                                                          | _ -> ()) ]
                                                ComboBox.create
                                                    [ Grid.column 2
                                                      // Fills the cell rather
                                                      // than the selected name,
                                                      // which would otherwise
                                                      // resize the control every
                                                      // time the group changed.
                                                      ComboBox.horizontalAlignment HorizontalAlignment.Stretch
                                                      ComboBox.dataItems groupChoices
                                                      ComboBox.selectedItem (box groupFilter.Current)
                                                      ComboBox.onSelectedItemChanged (fun item ->
                                                          match item with
                                                          | :? string as g -> groupFilter.Set g
                                                          | _ -> ()) ] ] ]
                                    TextBlock.create
                                        [ TextBlock.fontFamily mono
                                          TextBlock.fontSize 11.0
                                          TextBlock.opacity 0.6
                                          // The long form of this line is wider
                                          // than a narrow column, and it is the
                                          // only other thing here that is not
                                          // already inside a scroll viewer.
                                          TextBlock.textWrapping TextWrapping.Wrap
                                          TextBlock.text (
                                              if List.length matching > listCap then
                                                  $"showing %d{listCap} of %d{List.length matching} — narrow the filter"
                                              else
                                                  $"%d{List.length matching} of %d{List.length signals} signals"
                                          ) ] ] ]
                        ListBox.create
                            [ ListBox.dataItems shown
                              ListBox.selectedItem null
                              ListBox.onSelectedItemChanged (fun item ->
                                  match item with
                                  | :? SignalEntry as entry -> post (fun s -> s.Watch entry.name)
                                  | _ -> ())
                              ListBox.itemTemplate (
                                  DataTemplateView<SignalEntry>.create (fun entry ->
                                      StackPanel.create
                                          [ StackPanel.orientation Orientation.Horizontal
                                            StackPanel.spacing 8.0
                                            StackPanel.children
                                                [ TextBlock.create
                                                      [ TextBlock.width 34.0
                                                        TextBlock.fontFamily mono
                                                        TextBlock.fontSize 11.0
                                                        TextBlock.foreground (kindColor palette entry.kind)
                                                        TextBlock.text (kindTag entry.kind) ]
                                                  TextBlock.create
                                                      [ TextBlock.fontFamily mono
                                                        TextBlock.fontSize 12.0
                                                        TextBlock.opacity (
                                                            if watchedNames.Contains entry.name then 0.45 else 1.0
                                                        )
                                                        TextBlock.text entry.name ]
                                                  TextBlock.create
                                                      [ TextBlock.fontFamily mono
                                                        TextBlock.fontSize 11.0
                                                        TextBlock.opacity 0.5
                                                        TextBlock.text $"%d{entry.width}b" ] ] ])
                              ) ] ] ]

        // ---- the watch table --------------------------------------------

        /// One row of the watch table, as four cells of a shared grid rather
        /// than a row of its own. The name column is `Auto`, so it is as wide as
        /// the longest name being watched and no wider — measured by the layout
        /// rather than guessed at, which a per-row stack could never do: each
        /// row would size to its own name and the widths would stop lining up.
        let watchCells (row: int) (v: WatchValue) : Avalonia.FuncUI.Types.IView list =
            [ Button.create
                  [ Grid.row row
                    Grid.column 0
                    Button.content "×"
                    Button.padding (Thickness(6.0, 0.0, 6.0, 0.0))
                    Button.fontSize 11.0
                    Button.verticalAlignment VerticalAlignment.Center
                    // Keyed on the name: a handler is attached once
                    // per control, so without this the button at row
                    // 1 keeps removing whatever was at row 1 when it
                    // was first drawn.
                    Button.onClick ((fun _ -> post (fun s -> s.Unwatch v.name)), SubPatchOptions.OnChangeOf v.name) ]
              TextBlock.create
                  [ Grid.row row
                    Grid.column 1
                    TextBlock.margin (Thickness(8.0, 0.0, 0.0, 0.0))
                    TextBlock.verticalAlignment VerticalAlignment.Center
                    TextBlock.fontFamily mono
                    TextBlock.text v.name ]
              TextBlock.create
                  [ Grid.row row
                    Grid.column 2
                    TextBlock.margin (Thickness(8.0, 0.0, 0.0, 0.0))
                    TextBlock.verticalAlignment VerticalAlignment.Center
                    TextBlock.fontFamily mono
                    TextBlock.fontSize 11.0
                    TextBlock.opacity 0.5
                    TextBlock.text $"%d{v.width}b" ]
              (if kindOf.TryFind v.name = Some SignalKind.Input then
                            // An input is the one thing the design does not
                            // drive, so it is the one thing worth typing into.
                            TextBox.create
                                [ Grid.row row
                                  Grid.column 3
                                  TextBox.width 200.0
                                  TextBox.margin (Thickness(10.0, 1.0, 0.0, 1.0))
                                  TextBox.horizontalAlignment HorizontalAlignment.Left
                                  TextBox.fontFamily mono
                                  TextBox.padding (Thickness(6.0, 2.0, 6.0, 2.0))
                                  TextBox.text (
                                      pokeText.Current
                                      |> Map.tryFind v.name
                                      |> Option.defaultValue (
                                          format radix.Current v.width (signedSignals.Contains v.name) v.value
                                      )
                                  )
                                  TextBox.onTextChanged (
                                      (fun t ->
                                          pokeText.Set(Map.add v.name t pokeText.Current)

                                          match parseValue v.width t with
                                          | Some value -> post (fun s -> s.Poke(v.name, value))
                                          | None -> ()),
                                      SubPatchOptions.OnChangeOf v.name
                                  ) ]
                            :> Avalonia.FuncUI.Types.IView
                        else
                            TextBlock.create
                                [ Grid.row row
                                  Grid.column 3
                                  TextBlock.margin (Thickness(10.0, 0.0, 0.0, 0.0))
                                  TextBlock.verticalAlignment VerticalAlignment.Center
                                  TextBlock.fontFamily mono
                                  TextBlock.text (formatValue stateMachines signedSignals radix.Current v.name v.width v.value) ]) ]

        let watch =
            DockPanel.create
                [ DockPanel.children
                      [ TextBlock.create
                            [ TextBlock.dock Dock.Top
                              TextBlock.margin (Thickness(0.0, 0.0, 0.0, 6.0))
                              TextBlock.opacity 0.6
                              TextBlock.textWrapping TextWrapping.Wrap
                              TextBlock.text (
                                  if List.isEmpty current.values then
                                      "pick signals on the left to watch them — inputs get a field to drive them"
                                  else
                                      count (List.length current.values) "watched signal" "watched signals"
                              ) ]
                        ScrollViewer.create
                            [ ScrollViewer.horizontalScrollBarVisibility Primitives.ScrollBarVisibility.Auto
                              ScrollViewer.content (
                                  Grid.create
                                      [ // Remove: the name, the width, then the
                                        // value — the first three sized to what
                                        // is in them, so the columns sit against
                                        // each other whatever is being watched.
                                        Grid.columnDefinitions "Auto,Auto,Auto,*"
                                        Grid.rowDefinitions (
                                            current.values |> List.map (fun _ -> "Auto") |> String.concat ","
                                        )
                                        Grid.children [ for i, v in List.indexed current.values do yield! watchCells i v ] ]
                              ) ] ] ]

        // ---- the memory window -------------------------------------------

        let mems = inventory |> Option.map (fun i -> i.mems) |> Option.defaultValue []

        let lookAt name start =
            memName.Set name
            memStart.Set start
            post (fun s -> s.ViewMemory(name, start, pageWords))

        let memoryPanel =
            let shape = mems |> List.tryFind (fun m -> m.name = memName.Current)

            // The session clamps a request to the memory it knows, so the page
            // that came back is the truth about where we are — not the address
            // that was asked for.
            let landed =
                match current.memory with
                | Some view when Some view.name = (shape |> Option.map (fun m -> m.name)) ->
                    Some(view.start, view.words.Length)
                | _ -> None

            let here = landed |> Option.map fst |> Option.defaultValue memStart.Current
            let depth = shape |> Option.map (fun m -> 1 <<< m.addrWidth) |> Option.defaultValue 0
            let atEnd = landed |> Option.map (fun (s, n) -> s + n >= depth) |> Option.defaultValue true

            let rows: Avalonia.FuncUI.Types.IView list =
                match current.memory, shape with
                | Some view, Some m when view.name = m.name ->
                    // Addresses are the memory's own, not the window's, so a
                    // paged view still reads as the memory it came from.
                    [ for row in 0 .. (view.words.Length + memoryColumns - 1) / memoryColumns - 1 ->
                          let first = row * memoryColumns

                          let cells =
                              [ for i in first .. min (first + memoryColumns - 1) (view.words.Length - 1) ->
                                    // A mem word is addressed, not declared —
                                    // it has no reading of its own.
                                    format radix.Current m.wordWidth false view.words[i] ]

                          StackPanel.create
                              [ StackPanel.orientation Orientation.Horizontal
                                StackPanel.spacing 10.0
                                StackPanel.children
                                    [ yield
                                          TextBlock.create
                                              [ TextBlock.fontFamily mono
                                                TextBlock.fontSize 12.0
                                                TextBlock.opacity 0.5
                                                TextBlock.text (sprintf "%04x" (view.start + first)) ]
                                      for cell in cells do
                                          yield
                                              TextBlock.create
                                                  [ TextBlock.fontFamily mono
                                                    TextBlock.fontSize 12.0
                                                    TextBlock.text cell ] ] ] ]
                | _ -> []

            let content: Avalonia.FuncUI.Types.IView list =
                if List.isEmpty mems then
                    [ TextBlock.create
                          [ TextBlock.opacity 0.5
                            TextBlock.text "this design has no memories" ] ]
                elif List.isEmpty rows then
                    [ TextBlock.create [ TextBlock.opacity 0.5; TextBlock.text "pick a memory to look at" ] ]
                else
                    rows

            DockPanel.create
                [ DockPanel.children
                      [ StackPanel.create
                            [ StackPanel.dock Dock.Top
                              StackPanel.orientation Orientation.Horizontal
                              StackPanel.spacing 6.0
                              StackPanel.margin (Thickness(0.0, 0.0, 0.0, 8.0))
                              StackPanel.children
                                  [ ComboBox.create
                                        [ ComboBox.width 240.0
                                          ComboBox.isEnabled (not (List.isEmpty mems))
                                          ComboBox.dataItems (mems |> List.map (fun m -> m.name))
                                          ComboBox.selectedItem (box memName.Current)
                                          ComboBox.onSelectedItemChanged (fun item ->
                                              match item with
                                              | :? string as name when name <> memName.Current -> lookAt name 0
                                              | _ -> ()) ]
                                    Button.create
                                        [ Button.content "◀"
                                          Button.isEnabled (here > 0)
                                          Button.onClick (
                                              (fun _ -> lookAt memName.Current (max 0 (here - pageWords))),
                                              SubPatchOptions.OnChangeOf(box (memName.Current, here))
                                          ) ]
                                    Button.create
                                        [ Button.content "▶"
                                          Button.isEnabled (not atEnd)
                                          Button.onClick (
                                              (fun _ -> lookAt memName.Current (here + pageWords)),
                                              SubPatchOptions.OnChangeOf(box (memName.Current, here))
                                          ) ]
                                    TextBlock.create
                                        [ TextBlock.verticalAlignment VerticalAlignment.Center
                                          TextBlock.fontFamily mono
                                          TextBlock.fontSize 11.0
                                          TextBlock.opacity 0.6
                                          TextBlock.text (
                                              match shape with
                                              | Some m -> $"%d{depth} words × %d{m.wordWidth}b — from 0x%04x{here}"
                                              | None -> ""
                                          ) ] ] ]
                        ScrollViewer.create
                            [ ScrollViewer.horizontalScrollBarVisibility Primitives.ScrollBarVisibility.Auto
                              ScrollViewer.content (
                                  StackPanel.create [ StackPanel.spacing 2.0; StackPanel.children content ]
                              ) ] ] ]

        // ---- the waveform ------------------------------------------------

        let recordButtons =
            StackPanel.create
                [ StackPanel.dock Dock.Left
                  StackPanel.orientation Orientation.Horizontal
                  StackPanel.spacing 6.0
                  StackPanel.children
                      [ Button.create
                            [ Button.content (if current.recording then "Stop" else "Record")
                              Button.onClick (fun _ ->
                                  match session () with
                                  | None -> ()
                                  | Some s ->
                                      if s.Latest.recording then
                                          s.StopRecording()
                                          traceNote.Set ""
                                      else
                                          match s.StartRecording recordAll.Current with
                                          | Ok () -> traceNote.Set ""
                                          | Error message -> traceNote.Set message) ]
                        Button.create
                            [ Button.content (if recordAll.Current then "every signal" else "watch list")
                              Button.isEnabled (not current.recording)
                              Button.onClick (fun _ -> recordAll.Set(not recordAll.Current)) ]
                        Button.create
                            [ Button.content "Save VCD"
                              Button.isEnabled (current.recorded > 0)
                              Button.onClick (fun _ ->
                                  match session () with
                                  | None -> ()
                                  | Some s ->
                                      let name =
                                          held.Current
                                          |> Option.map (fun (_, i) -> i.topName)
                                          |> Option.defaultValue "design"

                                      let path =
                                          System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"warp11-{name}.vcd")

                                      System.IO.File.WriteAllText(path, Warp11.Vcd.render name (s.Trace()))
                                      traceNote.Set $"wrote {path}") ] ] ]

        // A dock rather than one long horizontal stack: the buttons keep the
        // width they need and the status line takes what is left, wrapping
        // inside it. As a stack it measured at its own full width and ran over
        // the panel beside this one — which the text scale made easy to see,
        // and a narrow window could always do.
        let recordControls =
            DockPanel.create
                [ DockPanel.dock Dock.Top
                  DockPanel.margin (Thickness(0.0, 0.0, 0.0, 8.0))
                  DockPanel.children
                      [ recordButtons
                        StackPanel.create
                            [ StackPanel.verticalAlignment VerticalAlignment.Center
                              StackPanel.margin (Thickness(6.0, 0.0, 0.0, 0.0))
                              StackPanel.children
                                  [ TextBlock.create
                                        [ TextBlock.fontFamily mono
                                          TextBlock.fontSize 11.0
                                          TextBlock.opacity 0.6
                                          TextBlock.textWrapping TextWrapping.Wrap
                                          TextBlock.text (
                                              if current.recording then
                                                  $"recording · %d{current.recorded} of %d{current.capacity} cycles"
                                              elif current.recorded > 0 then
                                                  $"%d{current.recorded} cycles held"
                                              else
                                                  "not recording"
                                          ) ]
                                    TextBlock.create
                                        [ TextBlock.isVisible (traceNote.Current <> "")
                                          TextBlock.fontFamily mono
                                          TextBlock.fontSize 11.0
                                          TextBlock.textWrapping TextWrapping.Wrap
                                          TextBlock.foreground (
                                              if traceNote.Current.StartsWith "wrote" then
                                                  palette.muted
                                              else
                                                  palette.alert
                                          )
                                          TextBlock.text traceNote.Current ] ] ] ] ]

        // The visible page. Held at the end of the trace unless paged back, so
        // a run that stops on a breakpoint shows the cycles leading into it.
        let pageStart =
            let held = current.recorded

            match wavePage.Current with
            | Some start -> max 0 (min start (max 0 (held - 1)))
            | None -> max 0 (held - Waveform.pageSamples)

        let slice =
            match session () with
            | Some s when current.recorded > 0 -> s.TraceSlice(pageStart, Waveform.pageSamples)
            | _ -> { firstCycle = 0; signals = [] }

        let columnCount = slice.Length
        let zoom = Waveform.zoomFor columnCount

        // -1 follows the end of the trace, which is what the watch table shows,
        // so the two panels agree until you deliberately move the cursor.
        let cursorColumn =
            if waveCursor.Current < 0 then
                columnCount - 1
            else
                max 0 (min waveCursor.Current (columnCount - 1))

        // The lane labels: a name and the value under the cursor, per row, sized
        // to what is in them rather than to a fixed 260. Rows are the trace's
        // own row height, which is what keeps a label beside the lane it names.
        //
        // A grid rather than a stack per row, for the same reason the watch
        // table is one: sizing each row to its own name would leave the values
        // ragged down the column.
        let laneLabels =
            Grid.create
                [ Grid.columnDefinitions "Auto,Auto"
                  Grid.rowDefinitions (
                      slice.signals |> Seq.map (fun _ -> string Waveform.rowHeight) |> String.concat ","
                  )
                  Grid.children
                      [ for i, s in Seq.indexed slice.signals do
                            TextBlock.create
                                [ Grid.row i
                                  Grid.column 0
                                  TextBlock.fontFamily mono
                                  TextBlock.fontSize 11.0
                                  TextBlock.text s.name ]
                            TextBlock.create
                                [ Grid.row i
                                  Grid.column 1
                                  TextBlock.margin (Thickness(8.0, 0.0, 0.0, 0.0))
                                  TextBlock.fontFamily mono
                                  TextBlock.fontSize 11.0
                                  TextBlock.opacity 0.8
                                  TextBlock.text (
                                      if columnCount = 0 then
                                          ""
                                      else
                                          formatValue
                                              stateMachines
                                              signedSignals
                                              radix.Current
                                              s.name
                                              s.width
                                              (Waveform.valueAt s cursorColumn)
                                  ) ] ] ]

        let waveform =
            DockPanel.create
                [ DockPanel.children
                      [ recordControls
                        StackPanel.create
                            [ StackPanel.dock Dock.Bottom
                              StackPanel.orientation Orientation.Horizontal
                              StackPanel.spacing 6.0
                              StackPanel.margin (Thickness(0.0, 6.0, 0.0, 0.0))
                              StackPanel.children
                                  [ Button.create
                                        [ Button.content "◀"
                                          Button.isEnabled (pageStart > 0)
                                          Button.onClick (fun _ ->
                                              wavePage.Set(Some(max 0 (pageStart - Waveform.pageSamples)))) ]
                                    Button.create
                                        [ Button.content "▶"
                                          Button.isEnabled (pageStart + columnCount < current.recorded)
                                          Button.onClick (fun _ -> wavePage.Set(Some(pageStart + Waveform.pageSamples))) ]
                                    Button.create
                                        [ Button.content "end"
                                          Button.isEnabled wavePage.Current.IsSome
                                          Button.onClick (fun _ -> wavePage.Set None) ]
                                    TextBlock.create
                                        [ TextBlock.verticalAlignment VerticalAlignment.Center
                                          TextBlock.fontFamily mono
                                          TextBlock.fontSize 11.0
                                          TextBlock.opacity 0.6
                                          TextBlock.text (
                                              if columnCount = 0 then
                                                  ""
                                              else
                                                  $"cycles %d{slice.firstCycle}–%d{slice.firstCycle + columnCount - 1}   cursor at %d{slice.firstCycle + cursorColumn}"
                                          ) ] ] ]
                        (if columnCount = 0 then
                             TextBlock.create
                                 [ TextBlock.opacity 0.5
                                   TextBlock.textWrapping TextWrapping.Wrap
                                   TextBlock.text
                                       "press Record, then step or run — one column per cycle, and Save VCD for a real viewer" ]
                             :> Avalonia.FuncUI.Types.IView
                         else
                             ScrollViewer.create
                                 [ ScrollViewer.horizontalScrollBarVisibility Primitives.ScrollBarVisibility.Auto
                                   ScrollViewer.content (
                                       StackPanel.create
                                           [ StackPanel.orientation Orientation.Horizontal
                                             StackPanel.spacing 8.0
                                             StackPanel.children
                                                 [ laneLabels
                                                   Image.create
                                                       [ Image.source (Waveform.render slice cursorColumn zoom)
                                                         Image.stretch Media.Stretch.None
                                                         Image.horizontalAlignment HorizontalAlignment.Left
                                                         Image.verticalAlignment VerticalAlignment.Top
                                                         // A cycle is a pixel, so the click *is* the cycle.
                                                         Image.onPointerPressed (
                                                             (fun e ->
                                                                 let source = e.Source :?> Controls.Control
                                                                 let x = (e.GetPosition source).X
                                                                 waveCursor.Set(max 0 (int x / zoom))),
                                                             SubPatchOptions.OnChangeOf(box columnCount)
                                                         ) ] ] ]
                                   ) ]
                             :> Avalonia.FuncUI.Types.IView) ] ]

        // ---- whatever the host added --------------------------------------

        let panelContext =
            { session = session ()
              entry = openEntry.Current
              snapshot = current
              palette = palette }

        /// One tab. Both groups draw the same, because they are the same thing:
        /// a choice of what fills the box under it.
        let tabButton (selected: string) (label: string) (onPick: unit -> unit) =
            Button.create
                [ Button.content label
                  Button.background (if selected = label then palette.accent else Brushes.Transparent)
                  Button.foreground (if selected = label then palette.onAccent else palette.text)
                  Button.onClick (fun _ -> onPick ()) ]

        let tabStrip (children: Avalonia.FuncUI.Types.IView list) =
            StackPanel.create
                [ StackPanel.dock Dock.Top
                  StackPanel.orientation Orientation.Horizontal
                  StackPanel.spacing 6.0
                  StackPanel.margin (Thickness(0.0, 0.0, 0.0, 8.0))
                  StackPanel.children children ]

        let tabs =
            StackPanel.create
                [ StackPanel.dock Dock.Top
                  StackPanel.orientation Orientation.Horizontal
                  StackPanel.spacing 6.0
                  StackPanel.margin (Thickness(0.0, 0.0, 0.0, 8.0))
                  StackPanel.children
                      [ for label in [ "watch"; "memory"; "waveform" ] @ [ for p in instruments -> p.label ] ->
                            Button.create
                                [ Button.content label
                                  Button.background (
                                      if panel.Current = label then palette.accent else Brushes.Transparent
                                  )
                                  Button.foreground (
                                      if panel.Current = label then palette.onAccent else palette.text
                                  )
                                  Button.onClick (fun _ ->
                                      panel.Set label

                                      // Read through the state rather than the
                                      // render's own `mems`: FuncUI attaches a
                                      // handler once and never re-attaches it,
                                      // so anything captured here is whatever
                                      // the FIRST render saw — which, for a
                                      // design still loading, is nothing.
                                      let live =
                                          held.Current
                                          |> Option.map (fun (_, i) -> i.mems)
                                          |> Option.defaultValue []

                                      // Nothing is sampled for a panel nobody is
                                      // looking at.
                                      if label = "memory" then
                                          match memName.Current, live with
                                          | "", first :: _ -> lookAt first.name 0
                                          | "", [] -> ()
                                          | name, _ -> lookAt name memStart.Current
                                      else
                                          post (fun s -> s.ClearMemoryView())) ] ] ]

        let upper: Avalonia.FuncUI.Types.IView =
            DockPanel.create
                [ DockPanel.children
                      [ tabs
                        (match panel.Current with
                         | "memory" -> memoryPanel :> Avalonia.FuncUI.Types.IView
                         | "waveform" -> waveform
                         | label ->
                             match instruments |> List.tryFind (fun p -> p.label = label) with
                             | Some p -> p.view panelContext
                             | None -> watch) ] ]

        /// The column that stays put: the design's page and its source, beside
        /// the instruments rather than behind them, so a page can say "poke
        /// `enable` and press Step" and you can do it without leaving the words.
        let referenceColumn: Avalonia.FuncUI.Types.IView =
            DockPanel.create
                [ DockPanel.children
                      [ tabStrip [ for p in alongside -> tabButton reference.Current p.label (fun () -> reference.Set p.label) ]
                        (match alongside |> List.tryFind (fun p -> p.label = reference.Current) with
                         | Some p -> p.view panelContext
                         | None ->
                             match alongside with
                             | p :: _ -> p.view panelContext
                             | [] -> TextBlock.create [ TextBlock.text "" ]) ] ]

        // ---- the breakpoints ---------------------------------------------

        let addBreakpoint () =
            match session () with
            | None -> ()
            | Some s ->
                match s.AddBreakpoint breakText.Current with
                | Ok () ->
                    breakText.Set ""
                    breakError.Set ""
                | Error message -> breakError.Set message

        let breakRow (b: BreakpointView) =
            let fired = current.hit = Some b.text

            StackPanel.create
                [ StackPanel.orientation Orientation.Horizontal
                  StackPanel.spacing 8.0
                  StackPanel.margin (Thickness(0.0, 1.0, 0.0, 1.0))
                  StackPanel.children
                      [ Button.create
                            [ Button.content "×"
                              Button.padding (Thickness(6.0, 0.0, 6.0, 0.0))
                              Button.fontSize 11.0
                              Button.onClick (
                                  (fun _ -> post (fun s -> s.RemoveBreakpoint b.text)),
                                  SubPatchOptions.OnChangeOf b.text
                              ) ]
                        Button.create
                            [ Button.content (if b.enabled then "on" else "off")
                              Button.width 44.0
                              Button.fontSize 11.0
                              Button.padding (Thickness(4.0, 0.0, 4.0, 0.0))
                              Button.opacity (if b.enabled then 1.0 else 0.5)
                              Button.onClick (
                                  (fun _ -> post (fun s -> s.EnableBreakpoint(b.text, not b.enabled))),
                                  SubPatchOptions.OnChangeOf(box (b.text, b.enabled))
                              ) ]
                        // The hit count keeps its place at the right and the
                        // expression takes what is left: a fixed-width
                        // expression pushes the count off the edge of a column
                        // narrower than the one this was written in.
                        DockPanel.create
                            [ DockPanel.lastChildFill true
                              DockPanel.children
                                  [ TextBlock.create
                                        [ DockPanel.dock Dock.Right
                                          TextBlock.verticalAlignment VerticalAlignment.Center
                                          TextBlock.margin (Thickness(8.0, 0.0, 0.0, 0.0))
                                          TextBlock.fontFamily mono
                                          TextBlock.fontSize 11.0
                                          TextBlock.opacity 0.6
                                          TextBlock.text (if b.hits = 0 then "" else count b.hits "hit" "hits") ]
                                    TextBlock.create
                                        [ TextBlock.verticalAlignment VerticalAlignment.Center
                                          TextBlock.fontFamily mono
                                          TextBlock.textTrimming TextTrimming.CharacterEllipsis
                                          TextBlock.opacity (if b.enabled then 1.0 else 0.5)
                                          TextBlock.foreground (if fired then palette.alert else palette.text)
                                          TextBlock.text b.text ] ] ] ] ]

        let breakpoints =
            DockPanel.create
                [ DockPanel.children
                      [ StackPanel.create
                            [ StackPanel.dock Dock.Top
                              StackPanel.spacing 4.0
                              StackPanel.margin (Thickness(0.0, 0.0, 0.0, 6.0))
                              StackPanel.children
                                  [ // A DockPanel rather than a horizontal stack:
                                    // stacking hands each child unbounded width
                                    // along its axis, so a fixed-width box does
                                    // not shrink with the column — it overflows
                                    // into whatever is beside it.
                                    DockPanel.create
                                        [ DockPanel.lastChildFill true
                                          DockPanel.children
                                              [ Button.create
                                                    [ DockPanel.dock Dock.Right
                                                      Button.content "Add"
                                                      Button.margin (Thickness(6.0, 0.0, 0.0, 0.0))
                                                      Button.onClick (fun _ -> addBreakpoint ()) ]
                                                TextBox.create
                                                    [ TextBox.fontFamily mono
                                                      TextBox.placeHolderText "break when …  e.g. count == 0x40 && !valid"
                                                      TextBox.text breakText.Current
                                                      TextBox.onTextChanged breakText.Set
                                                      TextBox.onKeyDown (fun e ->
                                                          if e.Key = Avalonia.Input.Key.Enter then
                                                              e.Handled <- true
                                                              addBreakpoint ()) ] ] ]
                                    TextBlock.create
                                        [ TextBlock.isVisible (breakError.Current <> "")
                                          TextBlock.fontFamily mono
                                          TextBlock.fontSize 11.0
                                          TextBlock.foreground palette.alert
                                          TextBlock.textWrapping TextWrapping.Wrap
                                          TextBlock.text breakError.Current ] ] ]
                        ScrollViewer.create
                            [ ScrollViewer.content (
                                  StackPanel.create
                                      [ StackPanel.children (
                                            if List.isEmpty current.breakpoints then
                                                [ TextBlock.create
                                                      [ TextBlock.opacity 0.5
                                                        TextBlock.textWrapping TextWrapping.Wrap
                                                        TextBlock.text
                                                            "no breakpoints — Run goes until you press Pause" ] ]
                                            else
                                                [ for b in current.breakpoints -> breakRow b ]
                                        ) ]
                              ) ] ] ]

        DockPanel.create
            [ DockPanel.margin 12.0
              DockPanel.children
                  [ Border.create
                        [ Border.dock Dock.Top
                          Border.padding (Thickness(0.0, 0.0, 0.0, 10.0))
                          Border.child header ]
                    Grid.create
                        [ // Three columns when a host has reference panels
                          // to show, and the shape the debugger has always had
                          // when it does not — an empty third of the window is
                          // worse than no column at all.
                          Grid.columnDefinitions (if List.isEmpty alongside then "380,12,*" else "20*,12,30*,12,45*")
                          Grid.children
                              [ Border.create
                                    [ Border.column 0
                                      Border.borderThickness 1.0
                                      Border.borderBrush palette.rule
                                      Border.cornerRadius 4.0
                                      Border.padding 8.0
                                      Border.child picker ]
                                Grid.create
                                    [ Grid.column 2
                                      Grid.rowDefinitions "*,10,230"
                                      Grid.children
                                          [ Border.create
                                                [ Border.row 0
                                                  Border.borderThickness 1.0
                                                  Border.borderBrush palette.rule
                                                  Border.cornerRadius 4.0
                                                  Border.padding 8.0
                                                  Border.child upper ]
                                            Border.create
                                                [ Border.row 2
                                                  Border.borderThickness 1.0
                                                  Border.borderBrush palette.rule
                                                  Border.cornerRadius 4.0
                                                  Border.padding 8.0
                                                  Border.child breakpoints ] ] ]
                                if not (List.isEmpty alongside) then
                                    Border.create
                                        [ Border.column 4
                                          Border.borderThickness 1.0
                                          Border.borderBrush palette.rule
                                          Border.cornerRadius 4.0
                                          Border.padding 8.0
                                          Border.child referenceColumn ] ] ] ] ]
        |> scaled textScale.Current)
