/// The NodeEditor spike, kept a day or two beside the FuncUI canvas.
/// A `Graph` as a NodeEditor drawing. Boxes become nodes, pins become pins
/// (inputs on the left, outputs on the right), wires become connectors. The
/// design's own boundary is two more boxes, `input` and `output`.
module Warp11.Placement.Canvas.NodeEditorSpike

open System.Collections.ObjectModel
open Avalonia.Controls
open Avalonia.FuncUI
open Avalonia.FuncUI.Builder
open Avalonia.FuncUI.DSL
open Avalonia.FuncUI.Types
open Avalonia.Media
open NodeEditor.Controls
open NodeEditor.Model
open NodeEditor.Mvvm
open Warp11.Placement.Graph

let private nodeWidth = 170.0
let private rowHeight = 26.0
let private headerHeight = 34.0
let private pinSize = 12.0

/// What a node shows: its name, its unit and its copies.
let private content (title: string) (subtitle: string) : Control =
    let stack = StackPanel(Margin = Avalonia.Thickness(10.0, 6.0))
    stack.Children.Add(TextBlock(Text = title, FontWeight = FontWeight.Bold))
    stack.Children.Add(TextBlock(Text = subtitle, Foreground = Brushes.Gray, FontSize = 11.0))

    Border(
        Child = stack,
        Background = SolidColorBrush(Color.FromRgb(250uy, 250uy, 252uy)),
        BorderBrush = SolidColorBrush(Color.FromRgb(120uy, 130uy, 160uy)),
        BorderThickness = Avalonia.Thickness 1.0,
        CornerRadius = Avalonia.CornerRadius 6.0
    )

let private addPin (node: NodeViewModel) (name: string) (format: Warp11.Placement.Fu.NumberFormat) (row: int) (direction: PinDirection) =
    let onLeft = direction = PinDirection.Input

    let pin =
        PinViewModel(
            Name = name,
            Parent = node,
            X = (if onLeft then 0.0 else nodeWidth),
            Y = headerHeight + float row * rowHeight + rowHeight / 2.0,
            Width = pinSize,
            Height = pinSize,
            Alignment = (if onLeft then PinAlignment.Left else PinAlignment.Right),
            Direction = direction,
            BusWidth = format.totalWidth
        )

    node.Pins.Add pin
    pin

/// The drawing for a graph, boxes laid out left to right in the order given.
let drawingOf (g: Graph) : DrawingNodeViewModel =
    let settings =
        DrawingNodeSettingsViewModel(
            EnableConnections = true,
            RequireDirectionalConnections = true,
            RequireMatchingBusWidth = true,
            EnableGrid = true,
            GridCellWidth = 20.0,
            GridCellHeight = 20.0,
            EnableSnap = true,
            SnapX = 10.0,
            SnapY = 10.0
        )

    let drawing =
        DrawingNodeViewModel(
            Name = g.name,
            Settings = settings,
            X = 0.0,
            Y = 0.0,
            Width = 1200.0,
            Height = 700.0,
            Nodes = ObservableCollection<INode>(),
            Connectors = ObservableCollection<IConnector>()
        )

    let columns = "input" :: (g.boxes |> List.map (fun b -> b.name)) @ [ "output" ]
    let pinsByRef = System.Collections.Generic.Dictionary<PinRef, IPin>()

    for column, boxName in List.indexed columns do
        let inputs, outputs = pinsOf g boxName

        let title, subtitle =
            match boxName with
            | "input" -> "input", $"%d{g.streams} stream(s)"
            | "output" -> "output", ""
            | name ->
                let b = g.boxes |> List.find (fun b -> b.name = name)
                let u = palette[b.unit]
                let sequential =
                    match u.law with
                    | Warp11.Placement.Fu.Sequential _ -> " (sequential)"
                    | Warp11.Placement.Fu.Combinational _ -> ""

                name, $"{b.unit} × %d{b.copies}" + sequential

        let rows = max inputs.Length outputs.Length

        let node =
            NodeViewModel(
                Name = boxName,
                X = 60.0 + float column * 260.0,
                Y = 120.0,
                Width = nodeWidth,
                Height = headerHeight + float rows * rowHeight + 10.0,
                Pins = ObservableCollection<IPin>(),
                Content = content title subtitle
            )

        for row, (name, format) in List.indexed inputs do
            pinsByRef[pin boxName name] <- addPin node name format row PinDirection.Input

        for row, (name, format) in List.indexed outputs do
            pinsByRef[pin boxName name] <- addPin node name format row PinDirection.Output

        drawing.Nodes.Add node

    for e in g.edges do
        drawing.Connectors.Add(ConnectorViewModel(Parent = drawing, Start = pinsByRef[e.from], End = pinsByRef[e.``to``]))

    drawing

/// The third-party control, wrapped for the FuncUI DSL: one attribute, the
/// drawing it shows.
let editor (drawing: IDrawingNode) : IView<Editor> =
    ViewBuilder.Create<Editor>(
        [ AttrBuilder<Editor>.CreateProperty<IDrawingNode>(Editor.DrawingSourceProperty, drawing, ValueNone) ]
    )

/// The spike's whole view: a title line and the editor. Platform-neutral, so
/// the desktop head and the browser head both show exactly this.
let view (g: Graph) (where: string) : Control =
    Component(fun _ ->
        let drawing = drawingOf g

        DockPanel.create
            [ DockPanel.children
                  [ TextBlock.create
                        [ DockPanel.dock Dock.Top
                          TextBlock.margin 10.0
                          TextBlock.text $"{g.name} — {g.boxes.Length} boxes, {g.edges.Length} wires — NodeEditor spike, {where}" ]
                    editor drawing ] ])

/// The theme the editor's controls need, added to an application's styles.
let nodeEditorTheme (hostAssembly: string) =
    Avalonia.Markup.Xaml.Styling.StyleInclude(
        System.Uri $"avares://{hostAssembly}/",
        Source = System.Uri "avares://NodeEditorAvalonia/Themes/NodeEditorTheme.axaml"
    )
