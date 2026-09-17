/// A mapping: which board a design goes to, as the board record with every
/// axis, and which way its boundary gets there. A file beside the design
/// (`gain.kv260.json`), so one design carries as many as it has boards; the
/// design names its default. A board alone is a file too (`*.board.json`),
/// which is how a custom target saved from the dialog becomes a preset
/// other designs pick.
///
/// The JSON is the record, field for field, so a person can read what a
/// build was made with and a diff shows exactly which axis moved.
module Warp11.Mapping

open System.Text.Json
open System.Text.Json.Nodes

type Mapping =
    { board: Board
      path: DataPath }

/// The preset a board equals, if any: the provenance a dialog shows.
let presetOf (board: Board) : string option =
    presets |> List.tryFind (fun (_, b) -> b = board) |> Option.map fst

// ---------------------------------------------------------------------------
// The board as JSON.

let private str (s: string) : JsonNode = JsonValue.Create s
let private num (n: int) : JsonNode = JsonValue.Create n

let private boardNode (b: Board) : JsonObject =
    let o = JsonObject()
    o["name"] <- str b.name

    let part = JsonObject()
    part["family"] <- str (match b.part.family with UltraScalePlus -> "UltraScalePlus" | Ice40UltraPlus -> "Ice40UltraPlus")
    part["device"] <- str b.part.device
    part["package"] <- str b.part.package
    b.part.boardPart |> Option.iter (fun bp -> part["boardPart"] <- str bp)
    o["part"] <- part

    o["tool"] <- str (match b.tool with Vivado -> "Vivado" | OpenFlow -> "OpenFlow")

    let clock = JsonObject()

    match b.clock with
    | PsClock i ->
        clock["source"] <- str "PsClock"
        clock["index"] <- num i
    | Crystal hz ->
        clock["source"] <- str "Crystal"
        clock["hz"] <- num hz
    | Oscillator hz ->
        clock["source"] <- str "Oscillator"
        clock["hz"] <- num hz

    o["clock"] <- clock
    o["fabricHz"] <- num b.fabricHz

    b.hostMemory
    |> Option.iter (fun m ->
        let hm = JsonObject()
        hm["port"] <- str m.port
        hm["width"] <- num m.width
        hm["arenaBytes"] <- num m.arenaBytes
        o["hostMemory"] <- hm)

    let host = JsonObject()

    match b.host with
    | NoHost -> host["driver"] <- str "NoHost"
    | AxiLiteAt baseAddr ->
        host["driver"] <- str "AxiLite"
        host["baseAddr"] <- str $"0x%X{baseAddr}"
    | UartAt baud ->
        host["driver"] <- str "Uart"
        host["baud"] <- num baud

    o["host"] <- host

    let loading = JsonObject()

    match b.loading with
    | OsApp dir ->
        loading["kind"] <- str "OsApp"
        loading["firmwareDir"] <- str dir
    | Sram -> loading["kind"] <- str "Sram"
    | Flash -> loading["kind"] <- str "Flash"

    o["loading"] <- loading

    o["connectors"] <-
        JsonArray(
            [| for c in b.connectors ->
                   let co = JsonObject()

                   co["role"] <-
                       str (
                           match c.role with
                           | I2sSeparateCodecs -> "I2sSeparateCodecs"
                           | I2sSharedBus -> "I2sSharedBus"
                           | HostUart -> "HostUart"
                           | ClockIn -> "ClockIn"
                           | Leds -> "Leds"
                       )

                   let pins = JsonObject()

                   for port, pin in c.pins do
                       let po = JsonObject()
                       po["pin"] <- str pin.pin
                       pin.standard |> Option.iter (fun st -> po["standard"] <- str st)
                       pins[port] <- po

                   co["pins"] <- pins
                   co :> JsonNode |]
        )

    o

let private field (o: JsonNode) (name: string) : Result<JsonNode, string> =
    match o with
    | :? JsonObject as obj ->
        match obj.ContainsKey name with
        | true when not (isNull obj[name]) -> Ok obj[name]
        | _ -> Error $"'{name}' is missing"
    | _ -> Error $"'{name}': expected an object"

let private optional (o: JsonNode) (name: string) : JsonNode option =
    match field o name with
    | Ok v -> Some v
    | Error _ -> None

let private asString (what: string) (n: JsonNode) : Result<string, string> =
    try
        Ok(n.GetValue<string>())
    with _ ->
        Error $"{what}: expected a string"

let private asInt (what: string) (n: JsonNode) : Result<int, string> =
    try
        Ok(n.GetValue<int>())
    with _ ->
        Error $"{what}: expected a number"

let private getString (o: JsonNode) (name: string) = field o name |> Result.bind (asString $"'{name}'")
let private getInt (o: JsonNode) (name: string) = field o name |> Result.bind (asInt $"'{name}'")

let private readBoard (o: JsonNode) : Result<Board, string> =
    let (>>=) r f = Result.bind f r

    getString o "name"
    >>= fun name ->
        field o "part"
        >>= fun part ->
            getString part "family"
            >>= fun family ->
                (match family with
                 | "UltraScalePlus" -> Ok UltraScalePlus
                 | "Ice40UltraPlus" -> Ok Ice40UltraPlus
                 | other -> Error $"'family': no family called '{other}'")
                >>= fun family ->
                    getString part "device"
                    >>= fun device ->
                        getString part "package"
                        >>= fun package ->
                            (match optional part "boardPart" with
                             | Some bp -> asString "'boardPart'" bp |> Result.map Some
                             | None -> Ok None)
                            >>= fun boardPart ->
                                getString o "tool"
                                >>= fun tool ->
                                    (match tool with
                                     | "Vivado" -> Ok Vivado
                                     | "OpenFlow" -> Ok OpenFlow
                                     | other -> Error $"'tool': no build tool called '{other}'")
                                    >>= fun tool ->
                                        field o "clock"
                                        >>= fun clock ->
                                            getString clock "source"
                                            >>= fun source ->
                                                (match source with
                                                 | "PsClock" -> getInt clock "index" |> Result.map PsClock
                                                 | "Crystal" -> getInt clock "hz" |> Result.map Crystal
                                                 | "Oscillator" -> getInt clock "hz" |> Result.map Oscillator
                                                 | other -> Error $"'clock': no source called '{other}'")
                                                >>= fun clock ->
                                                    getInt o "fabricHz"
                                                    >>= fun fabricHz ->
                                                        (match optional o "hostMemory" with
                                                         | None -> Ok None
                                                         | Some hm ->
                                                             getString hm "port"
                                                             >>= fun port ->
                                                                 getInt hm "width"
                                                                 >>= fun width ->
                                                                     getInt hm "arenaBytes"
                                                                     |> Result.map (fun arena -> Some { port = port; width = width; arenaBytes = arena }))
                                                        >>= fun hostMemory ->
                                                            field o "host"
                                                            >>= fun host ->
                                                                getString host "driver"
                                                                >>= fun driver ->
                                                                    (match driver with
                                                                     | "NoHost" -> Ok NoHost
                                                                     | "AxiLite" ->
                                                                         getString host "baseAddr"
                                                                         >>= fun b ->
                                                                             try
                                                                                 Ok(AxiLiteAt(System.Convert.ToUInt64(b.Replace("0x", "").Replace("0X", ""), 16)))
                                                                             with _ ->
                                                                                 Error $"'baseAddr': '{b}' is not a hex address"
                                                                     | "Uart" -> getInt host "baud" |> Result.map UartAt
                                                                     | other -> Error $"'host': no driver called '{other}'")
                                                                    >>= fun host ->
                                                                        field o "loading"
                                                                        >>= fun loading ->
                                                                            getString loading "kind"
                                                                            >>= fun kind ->
                                                                                (match kind with
                                                                                 | "OsApp" -> getString loading "firmwareDir" |> Result.map OsApp
                                                                                 | "Sram" -> Ok Sram
                                                                                 | "Flash" -> Ok Flash
                                                                                 | other -> Error $"'loading': no kind called '{other}'")
                                                                                >>= fun loading ->
                                                                                    (match field o "connectors" with
                                                                                     | Ok(:? JsonArray as arr) ->
                                                                                         arr
                                                                                         |> List.ofSeq
                                                                                         |> List.fold
                                                                                             (fun acc c ->
                                                                                                 acc
                                                                                                 >>= fun cs ->
                                                                                                     getString c "role"
                                                                                                     >>= fun role ->
                                                                                                         (match role with
                                                                                                          | "I2sSeparateCodecs" -> Ok I2sSeparateCodecs
                                                                                                          | "I2sSharedBus" -> Ok I2sSharedBus
                                                                                                          | "HostUart" -> Ok HostUart
                                                                                                          | "ClockIn" -> Ok ClockIn
                                                                                                          | "Leds" -> Ok Leds
                                                                                                          | other -> Error $"'role': no device role called '{other}'")
                                                                                                         >>= fun role ->
                                                                                                             (match field c "pins" with
                                                                                                              | Ok(:? JsonObject as pins) ->
                                                                                                                  pins
                                                                                                                  |> List.ofSeq
                                                                                                                  |> List.fold
                                                                                                                      (fun acc (KeyValue(port, po)) ->
                                                                                                                          acc
                                                                                                                          >>= fun ps ->
                                                                                                                              getString po "pin"
                                                                                                                              >>= fun pin ->
                                                                                                                                  (match optional po "standard" with
                                                                                                                                   | Some st -> asString "'standard'" st |> Result.map Some
                                                                                                                                   | None -> Ok None)
                                                                                                                                  |> Result.map (fun st -> ps @ [ port, { pin = pin; standard = st } ]))
                                                                                                                      (Ok [])
                                                                                                              | _ -> Error "'pins': expected an object")
                                                                                                             |> Result.map (fun pins -> cs @ [ { role = role; pins = pins } ]))
                                                                                             (Ok [])
                                                                                     | _ -> Error "'connectors': expected an array")
                                                                                    |> Result.map (fun connectors ->
                                                                                        { name = name
                                                                                          part =
                                                                                            { family = family
                                                                                              device = device
                                                                                              package = package
                                                                                              boardPart = boardPart }
                                                                                          tool = tool
                                                                                          clock = clock
                                                                                          fabricHz = fabricHz
                                                                                          hostMemory = hostMemory
                                                                                          host = host
                                                                                          loading = loading
                                                                                          connectors = connectors })

let private options = JsonSerializerOptions(WriteIndented = true)

/// A board as JSON text.
let writeBoard (b: Board) : string = boardNode(b).ToJsonString options

/// JSON text as a board, or the first thing wrong with it.
let parseBoard (text: string) : Result<Board, string> =
    try
        readBoard (JsonNode.Parse text) |> Result.bind (fun b -> checkBoard b; Ok b)
    with e ->
        Error e.Message

let saveBoard (path: string) (b: Board) = System.IO.File.WriteAllText(path, writeBoard b)

let loadBoard (path: string) : Result<Board, string> =
    if System.IO.File.Exists path then
        parseBoard (System.IO.File.ReadAllText path)
    else
        Error $"{path}: no such file"

// ---------------------------------------------------------------------------
// The mapping as JSON.

let private pathText (p: DataPath) =
    match p with
    | Pins -> "pins"
    | HostMemory -> "memory"
    | Counted -> "count"

let private readPath (text: string) : Result<DataPath, string> =
    match text with
    | "pins" -> Ok Pins
    | "memory" -> Ok HostMemory
    | "count" -> Ok Counted
    | other -> Error $"'path': a data path is pins, memory or count, not '{other}'"

let write (m: Mapping) : string =
    let o = JsonObject()
    o["board"] <- boardNode m.board
    o["path"] <- str (pathText m.path)
    o.ToJsonString options

let parse (text: string) : Result<Mapping, string> =
    try
        let root = JsonNode.Parse text

        field root "board"
        |> Result.bind readBoard
        |> Result.bind (fun board ->
            getString root "path"
            |> Result.bind readPath
            |> Result.map (fun path -> { board = board; path = path }))
        |> Result.bind (fun m -> checkBoard m.board; Ok m)
    with e ->
        Error e.Message

let save (path: string) (m: Mapping) = System.IO.File.WriteAllText(path, write m)

let load (path: string) : Result<Mapping, string> =
    if System.IO.File.Exists path then
        parse (System.IO.File.ReadAllText path)
    else
        Error $"{path}: no such file"

/// A mapping from a preset by name, or a board file's path.
let ofBoard (nameOrFile: string) (path: DataPath) : Result<Mapping, string> =
    match preset nameOrFile with
    | Ok b -> Ok { board = b; path = path }
    | Error _ when System.IO.File.Exists nameOrFile -> loadBoard nameOrFile |> Result.map (fun b -> { board = b; path = path })
    | Error why -> Error why

/// Where a design's mapping for a board lives: `gain.json` on the `kv260`
/// → `gain.kv260.json`, beside it.
let fileFor (designPath: string) (boardName: string) : string =
    let dir = System.IO.Path.GetDirectoryName designPath
    let stem = System.IO.Path.GetFileNameWithoutExtension designPath
    System.IO.Path.Combine(dir, $"{stem}.{boardName}.json")

/// The mapping a design names as its default, resolved beside the design's
/// file.
let defaultFor (designPath: string) (mappingName: string option) : Result<Mapping, string> =
    match mappingName with
    | None -> Error $"{designPath}: the design names no mapping — it maps to the simulator only; `-- build design.json <preset> pins|memory <dir>` builds it for a board"
    | Some name -> load (System.IO.Path.Combine(System.IO.Path.GetDirectoryName designPath, name))

// ---------------------------------------------------------------------------
// The board as the F# a person would have written, for the export.

let private quote (s: string) = "\"" + s.Replace("\"", "\\\"") + "\""

let private showOption (f: 'a -> string) (o: 'a option) =
    match o with
    | Some v -> $"Some({f v})"
    | None -> "None"

/// The board record as an F# literal, every axis spelled out, so the export
/// is complete and reads as the dialog did.
let showBoard (b: Board) : string =
    let family =
        match b.part.family with
        | UltraScalePlus -> "UltraScalePlus"
        | Ice40UltraPlus -> "Ice40UltraPlus"

    let tool =
        match b.tool with
        | Vivado -> "Vivado"
        | OpenFlow -> "OpenFlow"

    let clock =
        match b.clock with
        | PsClock i -> $"PsClock %d{i}"
        | Crystal hz -> $"Crystal %d{hz}"
        | Oscillator hz -> $"Oscillator %d{hz}"

    let host =
        match b.host with
        | NoHost -> "NoHost"
        | AxiLiteAt a -> $"AxiLiteAt 0x%X{a}UL"
        | UartAt baud -> $"UartAt %d{baud}"

    let loading =
        match b.loading with
        | OsApp d -> $"OsApp {quote d}"
        | Sram -> "Sram"
        | Flash -> "Flash"

    let role (r: DeviceRole) =
        match r with
        | I2sSeparateCodecs -> "I2sSeparateCodecs"
        | I2sSharedBus -> "I2sSharedBus"
        | HostUart -> "HostUart"
        | ClockIn -> "ClockIn"
        | Leds -> "Leds"

    let pin (port: string, p: Pin) =
        $"{quote port}, {{ pin = {quote p.pin}; standard = {showOption quote p.standard} }}"

    let connector (c: Connector) =
        let pins = c.pins |> List.map pin |> String.concat "; "
        $"{{ role = {role c.role}; pins = [ {pins} ] }}"

    let connectors = b.connectors |> List.map (fun c -> "          " + connector c) |> String.concat "\n"

    let memory =
        b.hostMemory
        |> showOption (fun m -> $"{{ port = {quote m.port}; width = %d{m.width}; arenaBytes = %d{m.arenaBytes} }}")

    String.concat
        "\n"
        [ "    { name = " + quote b.name
          $"      part = {{ family = {family}; device = {quote b.part.device}; package = {quote b.part.package}; boardPart = {showOption quote b.part.boardPart} }}"
          $"      tool = {tool}"
          $"      clock = {clock}"
          $"      fabricHz = %d{b.fabricHz}"
          $"      hostMemory = {memory}"
          $"      host = {host}"
          $"      loading = {loading}"
          "      connectors ="
          "        ["
          connectors
          "        ] }" ]
