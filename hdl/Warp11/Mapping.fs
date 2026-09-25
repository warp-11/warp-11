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
      path: DataPath
      /// Each harness and the socket it is plugged into.
      plugged: (Harness * string) list }

/// The board a mapping builds for: its board with every harness plugged in.
let boardOf (m: Mapping) : Board =
    m.plugged |> List.fold (fun board (harness, socket) -> plug harness socket board) m.board

/// The preset a board equals, if any: the provenance a dialog shows.
let presetOf (board: Board) : string option =
    presets |> List.tryFind (fun (_, b) -> b = board) |> Option.map fst

// ---------------------------------------------------------------------------
// The board as JSON.

let private str (s: string) : JsonNode = JsonValue.Create s
let private num (n: int) : JsonNode = JsonValue.Create n

let private roleText (r: DeviceRole) =
    match r with
    | I2sSeparateCodecs -> "I2sSeparateCodecs"
    | I2sSharedBus -> "I2sSharedBus"
    | HostUart -> "HostUart"
    | ClockIn -> "ClockIn"
    | Leds -> "Leds"

let private readRole (text: string) : Result<DeviceRole, string> =
    match text with
    | "I2sSeparateCodecs" -> Ok I2sSeparateCodecs
    | "I2sSharedBus" -> Ok I2sSharedBus
    | "HostUart" -> Ok HostUart
    | "ClockIn" -> Ok ClockIn
    | "Leds" -> Ok Leds
    | other -> Error $"'role': no device role called '{other}'"

/// A pin as JSON. `activeLow` is written only when it is true, so a board
/// file from before it existed reads the same and writes the same bytes.
let private pinNode (pin: Pin) : JsonNode =
    let po = JsonObject()
    po["pin"] <- JsonValue.Create pin.pin
    pin.standard |> Option.iter (fun st -> po["standard"] <- JsonValue.Create st)
    if pin.activeLow then po["activeLow"] <- JsonValue.Create true
    po

let private boardNode (b: Board) : JsonObject =
    let o = JsonObject()
    o["name"] <- str b.name

    let part = JsonObject()
    part["family"] <- str (match b.part.family with UltraScalePlus -> "UltraScalePlus" | Ice40UltraPlus -> "Ice40UltraPlus" | Family.Ecp5 -> "Ecp5")
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
        hm["writeOutstanding"] <- num m.writeOutstanding
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

                   co["role"] <- str (roleText c.role)

                   let pins = JsonObject()

                   for port, pin in c.pins do
                       pins[port] <- pinNode pin

                   co["pins"] <- pins
                   co :> JsonNode |]
        )

    // Sockets only when there are any, so a board file written before they
    // existed round-trips to the same bytes.
    if not b.sockets.IsEmpty then
        o["sockets"] <-
            JsonArray(
                [| for socket in b.sockets ->
                       let so = JsonObject()
                       so["name"] <- str socket.name
                       so["kind"] <- str socket.kind
                       let positions = JsonObject()

                       for position, pin in socket.positions do
                           positions[string position] <- pinNode pin

                       so["positions"] <- positions
                       so :> JsonNode |]
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

let private asBool (what: string) (n: JsonNode) : Result<bool, string> =
    try
        Ok(n.GetValue<bool>())
    with _ ->
        Error $"{what}: expected true or false"

let private readPin (po: JsonNode) : Result<Pin, string> =
    getString po "pin"
    |> Result.bind (fun pin ->
        (match optional po "standard" with
         | Some st -> asString "'standard'" st |> Result.map Some
         | None -> Ok None)
        |> Result.bind (fun standard ->
            (match optional po "activeLow" with
             | Some low -> asBool "'activeLow'" low
             | None -> Ok false)
            |> Result.map (fun activeLow -> { pin = pin; standard = standard; activeLow = activeLow })))

/// Each entry of a JSON object keyed by position number, read by `read`.
let private readPositions (what: string) (read: JsonNode -> Result<'a, string>) (o: JsonNode) : Result<(int * 'a) list, string> =
    match o with
    | :? JsonObject as positions ->
        positions
        |> List.ofSeq
        |> List.fold
            (fun acc (KeyValue(key, node)) ->
                acc
                |> Result.bind (fun items ->
                    match System.Int32.TryParse key with
                    | true, position -> read node |> Result.map (fun item -> items @ [ position, item ])
                    | _ -> Error $"{what}: '{key}' is not a position number"))
            (Ok [])
    | _ -> Error $"{what}: expected an object keyed by position"

let private readSockets (o: JsonNode) : Result<Socket list, string> =
    match optional o "sockets" with
    | None -> Ok []
    | Some(:? JsonArray as arr) ->
        arr
        |> List.ofSeq
        |> List.fold
            (fun acc so ->
                acc
                |> Result.bind (fun sockets ->
                    getString so "name"
                    |> Result.bind (fun name ->
                        getString so "kind"
                        |> Result.bind (fun kind ->
                            field so "positions"
                            |> Result.bind (readPositions $"socket '{name}'" readPin)
                            |> Result.map (fun positions -> sockets @ [ { name = name; kind = kind; positions = positions } ])))))
            (Ok [])
    | Some _ -> Error "'sockets': expected an array"

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
                 | "Ecp5" -> Ok Family.Ecp5
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
                                                                     |> Result.map (fun arena ->
                                                                         // Optional: board files written before the field
                                                                         // existed still load, at the HP port's sweet spot.
                                                                         let outstanding =
                                                                             match optional hm "writeOutstanding" |> Option.bind (fun v -> asInt "writeOutstanding" v |> Result.toOption) with
                                                                             | Some n -> n
                                                                             | None -> 16

                                                                         Some { port = port; width = width; arenaBytes = arena; writeOutstanding = outstanding }))
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
                                                                                                         readRole role
                                                                                                         >>= fun role ->
                                                                                                             (match field c "pins" with
                                                                                                              | Ok(:? JsonObject as pins) ->
                                                                                                                  pins
                                                                                                                  |> List.ofSeq
                                                                                                                  |> List.fold
                                                                                                                      (fun acc (KeyValue(port, po)) ->
                                                                                                                          acc
                                                                                                                          >>= fun ps ->
                                                                                                                              readPin po |> Result.map (fun pin -> ps @ [ port, pin ]))
                                                                                                                      (Ok [])
                                                                                                              | _ -> Error "'pins': expected an object")
                                                                                                             |> Result.map (fun pins -> cs @ [ { role = role; pins = pins } ]))
                                                                                             (Ok [])
                                                                                     | _ -> Error "'connectors': expected an array")
                                                                                    >>= fun connectors ->
                                                                                    readSockets o
                                                                                    |> Result.map (fun sockets ->
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
                                                                                          connectors = connectors
                                                                                          sockets = sockets })

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

/// The path's preset name, or the pair it is when it has none. Public
/// because the canvas names paths in its target section too.
let pathText (p: DataPath) =
    // A preset is recognised by the carriers it binds by kind, so a mapping
    // that rebinds one need by name still prints as the preset it started
    // from plus that binding — which is what a file has to round-trip.
    let byKind kind =
        p.bindings |> List.tryFind (fun b -> b.need = kind) |> Option.map (fun b -> b.carrier)

    match byKind EveryStreamIn, byKind EveryStreamOut with
    | Some OnPins, Some OnPins -> "pins"
    | Some InHostRows, Some InHostRows -> "memory"
    | Some AsBeatCount, Some InHostRows -> "count"
    | Some AsBeatCount, Some InHostRowsAt -> "scatter"
    // A combination with no preset name prints as the carriers it is, so a
    // mapping file can still carry one once `boardTop` builds it.
    | inCarrier, outCarrier -> $"%A{inCarrier}/%A{outCarrier}"

let private readPath (text: string) : Result<DataPath, string> =
    match text with
    | "pins" -> Ok viaPins
    | "memory" -> Ok viaHostMemory
    | "count" -> Ok viaCount
    | "scatter" -> Ok viaScatter
    | other -> Error $"'path': a data path is pins, memory or count, not '{other}'"

/// A carrier as a mapping file names it: the case, and for a pin, which.
let carrierText (c: Carrier) =
    match c with
    | OnPins -> "OnPins"
    | AsBeatCount -> "AsBeatCount"
    | InHostRows -> "InHostRows"
    | InHostRowsAt -> "InHostRowsAt"
    | InRegisterMap -> "InRegisterMap"
    | InLinkFrames -> "InLinkFrames"
    | OnPin port -> $"OnPin:{port}"

let readCarrier (text: string) : Result<Carrier, string> =
    match text with
    | "OnPins" -> Ok OnPins
    | "AsBeatCount" -> Ok AsBeatCount
    | "InHostRows" -> Ok InHostRows
    | "InHostRowsAt" -> Ok InHostRowsAt
    | "InRegisterMap" -> Ok InRegisterMap
    | "InLinkFrames" -> Ok InLinkFrames
    | pin when pin.StartsWith "OnPin:" && pin.Length > 6 -> Ok(OnPin(pin.Substring 6))
    | other -> Error $"'bind': no carrier called '{other}'"

// ---------------------------------------------------------------------------
// A harness as JSON.

let private harnessNode (h: Harness) : JsonObject =
    let o = JsonObject()
    o["name"] <- str h.name
    o["plug"] <- str h.plug
    let positions = JsonObject()

    for position, (role, port) in h.positions do
        let po = JsonObject()
        po["role"] <- str (roleText role)
        po["port"] <- str port
        positions[string position] <- po

    o["positions"] <- positions
    o

let private readHarness (o: JsonNode) : Result<Harness, string> =
    getString o "name"
    |> Result.bind (fun name ->
        getString o "plug"
        |> Result.bind (fun plugKind ->
            field o "positions"
            |> Result.bind (
                readPositions $"harness '{name}'" (fun po ->
                    getString po "role"
                    |> Result.bind readRole
                    |> Result.bind (fun role -> getString po "port" |> Result.map (fun port -> role, port)))
            )
            |> Result.map (fun positions -> { name = name; plug = plugKind; positions = positions })))

/// A harness as JSON text.
let writeHarness (h: Harness) : string = harnessNode(h).ToJsonString options

/// JSON text as a harness, or the first thing wrong with it.
let parseHarness (text: string) : Result<Harness, string> =
    try
        readHarness (JsonNode.Parse text)
    with e ->
        Error e.Message

let loadHarness (path: string) : Result<Harness, string> =
    if System.IO.File.Exists path then
        parseHarness (System.IO.File.ReadAllText path)
    else
        Error $"{path}: no such file"

let write (m: Mapping) : string =
    let o = JsonObject()
    o["board"] <- boardNode m.board
    o["path"] <- str (pathText m.path)

    // What the mapping binds by name, over the preset — the per-need part a
    // board switch actually edits. Written only when there is any, so a
    // preset-only mapping reads and writes as it always did.
    let named =
        m.path.bindings
        |> List.choose (fun b ->
            match b.need with
            | ByName need -> Some(need, b.carrier)
            | _ -> None)

    if not named.IsEmpty then
        let bind = JsonObject()

        for need, carriers in named |> List.groupBy fst do
            bind[need] <- JsonArray([| for _, c in carriers -> str (carrierText c) |])

        o["bind"] <- bind

    if not m.plugged.IsEmpty then
        o["plugged"] <-
            JsonArray(
                [| for harness, socket in m.plugged ->
                       let po = JsonObject()
                       po["socket"] <- str socket
                       po["harness"] <- harnessNode harness
                       po :> JsonNode |]
            )

    o.ToJsonString options

let private readBind (root: JsonNode) : Result<Binding list, string> =
    match optional root "bind" with
    | None -> Ok []
    | Some(:? JsonObject as bind) ->
        bind
        |> List.ofSeq
        |> List.fold
            (fun acc (KeyValue(need, carriers)) ->
                acc
                |> Result.bind (fun bindings ->
                    match carriers with
                    | :? JsonArray as arr ->
                        arr
                        |> List.ofSeq
                        |> List.fold
                            (fun acc c ->
                                acc
                                |> Result.bind (fun bs ->
                                    asString $"'bind.{need}'" c
                                    |> Result.bind readCarrier
                                    |> Result.map (fun carrier -> bs @ [ { need = ByName need; carrier = carrier } ])))
                            (Ok bindings)
                    | _ -> Error $"'bind.{need}': expected a list of carriers"))
            (Ok [])
    | Some _ -> Error "'bind': expected an object keyed by need"

let private readPlugged (root: JsonNode) : Result<(Harness * string) list, string> =
    match optional root "plugged" with
    | None -> Ok []
    | Some(:? JsonArray as arr) ->
        arr
        |> List.ofSeq
        |> List.fold
            (fun acc po ->
                acc
                |> Result.bind (fun plugged ->
                    getString po "socket"
                    |> Result.bind (fun socket ->
                        field po "harness"
                        |> Result.bind readHarness
                        |> Result.map (fun harness -> plugged @ [ harness, socket ]))))
            (Ok [])
    | Some _ -> Error "'plugged': expected an array"

let parse (text: string) : Result<Mapping, string> =
    try
        let root = JsonNode.Parse text

        field root "board"
        |> Result.bind readBoard
        |> Result.bind (fun board ->
            getString root "path"
            |> Result.bind readPath
            |> Result.bind (fun path ->
                readBind root
                |> Result.bind (fun named ->
                    readPlugged root
                    |> Result.map (fun plugged ->
                        { board = board
                          path = { path with bindings = path.bindings @ named }
                          plugged = plugged }))))
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
    | Ok b -> Ok { board = b; path = path; plugged = [] }
    | Error _ when System.IO.File.Exists nameOrFile -> loadBoard nameOrFile |> Result.map (fun b -> { board = b; path = path; plugged = [] })
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
        | Family.Ecp5 -> "Ecp5"

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

    let pinValue (p: Pin) =
        let low = if p.activeLow then "true" else "false"
        $"{{ pin = {quote p.pin}; standard = {showOption quote p.standard}; activeLow = {low} }}"

    let pin (port: string, p: Pin) = $"{quote port}, {pinValue p}"

    let socket (s: Socket) =
        let positions = s.positions |> List.map (fun (n, p) -> $"%d{n}, {pinValue p}") |> String.concat "; "
        $"{{ name = {quote s.name}; kind = {quote s.kind}; positions = [ {positions} ] }}"

    let sockets = b.sockets |> List.map (fun s -> "          " + socket s) |> String.concat "\n"

    let connector (c: Connector) =
        let pins = c.pins |> List.map pin |> String.concat "; "
        $"{{ role = {role c.role}; pins = [ {pins} ] }}"

    let connectors = b.connectors |> List.map (fun c -> "          " + connector c) |> String.concat "\n"

    let memory =
        b.hostMemory
        |> showOption (fun m -> $"{{ port = {quote m.port}; width = %d{m.width}; arenaBytes = %d{m.arenaBytes}; writeOutstanding = %d{m.writeOutstanding} }}")

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
          "        ]"
          "      sockets ="
          "        ["
          sockets
          "        ] }" ]
