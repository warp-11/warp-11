/// Driving a design's ready/valid ports from a test, as a lazy sequence.
///
/// A design that presents a `Stream` is tested by offering beats on one port
/// group and collecting them from another, which is a handshake loop: hold
/// `valid` while there is something to offer, watch `ready` to know it was
/// taken, hold `ready` on the way out, read on `valid`. Written by hand that is
/// a dozen lines that look the same every time and are wrong in interesting
/// ways when they are not — offering a beat that was never accepted, or reading
/// one twice.
///
/// **The sequence is lazy, and that is the design rather than an optimisation.**
/// Pulling a beat advances the simulation exactly as far as it must to produce
/// one, so `Seq.take 3` runs three beats' worth of cycles and stops. A test
/// reads as a pipeline — values in, design in the middle, take what you want
/// off the end — with no cycle budget stated anywhere, and the input may be
/// infinite because it is pulled on demand too.
///
/// A background thread would give the same shape and cost determinism: `Sim` is
/// single-threaded mutable state, so it would need a lock around every poke,
/// and a test that races is worse than a test that is verbose.
[<AutoOpen>]
module Warp11.SimStream

/// The wire names of one stream port group, as a design declared them.
///
/// `streamInputPorts p "in" layout` emits `in_valid`, `in_ready` and one wire
/// per layout field — so the names are derivable from the prefix and the
/// layout, and a test that spells them out by hand is a test that goes stale
/// when a field is added.
type StreamPins =
    { valid: string
      ready: string
      fields: string list }

/// Derive a port group's wire names. The same call serves an input group and an
/// output one — which of `valid`/`ready` the design drives differs, but the
/// names do not.
let streamPins (prefix: string) (layout: Layout<'p>) : StreamPins =
    { valid = $"{prefix}_valid"
      ready = $"{prefix}_ready"
      fields = layout.fields |> List.map (fun (name, _) -> $"{prefix}_{name}") }

/// Offer `values` to `input` and yield whatever leaves `output`, one beat at a
/// time, ticking only as far as each pulled beat requires.
///
/// `stallEvery` injects backpressure: 0 never stalls, `n` refuses to offer or
/// accept on every nth cycle. A stage that behaves differently under stalls has
/// a real defect — it is the shape the audio signal-shift bug lived in — so a
/// check that only ever runs unstalled is checking the easy half.
///
/// `idleLimit` bounds how long the run will wait for a beat that never comes,
/// so a design that deadlocks fails the test instead of hanging it.
let streamThroughWith (sim: Sim) (input: StreamPins) (output: StreamPins) (stallEvery: int) (idleLimit: int) (values: uint64 list seq) : seq<uint64 list> =
    seq {
        use source = values.GetEnumerator()
        let mutable pending = if source.MoveNext() then Some source.Current else None
        let mutable cycle = 0
        let mutable idle = 0

        while idle < idleLimit do
            let stalling = stallEvery > 0 && cycle % stallEvery = 0
            let offering = pending.IsSome && not stalling
            let taking = not stalling

            sim.Poke(input.valid, (if offering then 1UL else 0UL))
            sim.Poke(output.ready, (if taking then 1UL else 0UL))

            match pending with
            | Some beat when offering -> List.iter2 (fun name value -> sim.Poke(name, value)) input.fields beat
            | _ -> ()

            // Both decisions are read before the tick, because a handshake is
            // about the values on the wires during the cycle, not after it.
            let accepted = offering && sim.Peek input.ready = 1UL
            let arriving = taking && sim.Peek output.valid = 1UL
            let arrival = if arriving then Some(output.fields |> List.map sim.Peek) else None

            sim.Tick()
            cycle <- cycle + 1

            if accepted then
                pending <- if source.MoveNext() then Some source.Current else None

            match arrival with
            | Some beat ->
                idle <- 0
                yield beat
            | None -> idle <- idle + 1
    }

/// The common case: no injected stalls, and a generous idle bound.
let streamThrough (sim: Sim) (input: StreamPins) (output: StreamPins) (values: uint64 list seq) =
    streamThroughWith sim input output 0 100_000 values
