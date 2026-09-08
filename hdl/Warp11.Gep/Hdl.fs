/// The GEP hardware, F# side — P2 of the port campaign. Each design here is
/// judged by the software oracle in this project (`Rng`, `Fixed`, …), and the
/// project's `diff` mode puts every design through the Verilator differential.
module Warp11.Gep.Hdl

open Warp11
open Warp11.Stdlib

/// The xoshiro128++ core at ports: load a pre-expanded state, pulse step,
/// read the word. The Sim check walks it against GepRng word-for-word; the
/// differential walks the same design against Verilator.
let xoshiroWalk =
    defModule
        "XoshiroWalk"
        (fun p ->
            (p.inPort "load" 1,
             List.init 4 (fun i -> p.inPort $"s{i}" 32),
             p.inPort "step" 1,
             p.outPort "word" 32))
        (fun (load, sIn, step, word) -> xoshiro128pp "Xoshiro128pp" "prng" load sIn step ==> word)

/// The 512-entry reciprocal table as the first initialized memory: contents
/// from `Fixed.fxRecipTable` (u31 words — one BRAM18 in fabric), sync read.
/// The Sim check reads every entry against the table; the differential proves
/// the emitted `initial` block matches.
let recipRomWalk =
    defModule
        "RecipRomWalk"
        (fun p -> (p.inPort "addr" 9, p.outPort "value" 31))
        (fun (addr, value) ->
            // 512 x 31, read synchronously — Vivado already places this in block RAM
            // (measured), and saying so keeps the decision out of the tool's hands.
            let table = blockRom "recip_table" 31 (Array.map uint64 Fixed.fxRecipTable)
            (memReadPort table addr).data ==> value)

/// Registers inside `divRecipArm`: the quotient is a combinational cone off
/// the arm's final registers, valid this many cycles after the operand
/// signals update.
let gepDivArmLatency = 12

/// Cycles through the standalone `gepDivRecip` module: input regs + arm +
/// output reg.
let gepDivLatency = gepDivArmLatency + 2

/// The reciprocal-table divide pipeline — the fabric arm of `fxDivRecip`,
/// which it matches bit-for-bit. Built as a reusable arm so a host ALU can
/// share its own input registers with it; II=1, every register updates every
/// cycle. `aPat`/`bPat` are 32-bit Q16.16 bit-pattern signals, already
/// registered by the host.
///
/// The SInt/UInt reinterprets vanish here — the IR is
/// width-only, signedness lives in the operations (slices as
/// narrowing arithmetic shifts). Stage map (see GepDivRecip.kt for the full
/// derivation): 1–2 magnitude/sign/guard + normalize (m at bit 31, n = clz),
/// 3 ROM sync read, 4 re-register r0 (keeps the BRAM output register out of
/// the DSP's reach — without it the sync-read pattern dissolves to LUTROM),
/// 5–6 t = m·r0, 7 Newton residual sliced, 8–9 r0·(d>>22), 10 refined r,
/// 11–12 p = ua·r, then the combinational shift-back/sign/saturate/guard.
let divRecipArm (aPat: Expr) (bPat: Expr) (prefix: string) : Expr =
    // 512 x 31 and synchronously read, which Vivado places in block RAM. Note
    // the standing trap this one sits in: a sync-read ROM feeding a DSP multiply
    // has its output register absorbed as the DSP's input register, so the
    // re-registration below is not optional.
    let table = blockRom $"{prefix}RecipRom" 31 (Array.map uint64 Fixed.fxRecipTable)

    // Stage 1 — magnitudes (negate-by-wrap gives |MIN| = 2^31, which compares
    // > eps exactly like the spec's saturated fxAbs), result sign, guard, and
    // the first two normalize steps.
    let negA = wire $"{prefix}_negA" 32
    lit 0UL 32 - aPat ==> negA
    let negB = wire $"{prefix}_negB" 32
    lit 0UL 32 - bPat ==> negB
    let bAbs = wire $"{prefix}_bAbs" 32
    // A magnitude is unsigned, and `asUInt` is where the signed pattern becomes
    // one — the bits do not move.
    mux (slice 31 31 bPat) negB (asUInt bPat) ==> bAbs
    let z16 = wireBit $"{prefix}_z16"
    eq (slice 31 16 bAbs) (lit 0UL 16) ==> z16
    let x16 = wire $"{prefix}_x16" 32
    mux z16 (cat (slice 15 0 bAbs) (lit 0UL 16)) bAbs ==> x16
    let z8 = wireBit $"{prefix}_z8"
    eq (slice 31 24 x16) (lit 0UL 8) ==> z8
    let a1x = reg $"{prefix}_a1_x" 32
    mux z8 (cat (slice 23 0 x16) (lit 0UL 8)) x16 ==> a1x
    let a1n16 = regBit $"{prefix}_a1_n16"
    z16 ==> a1n16
    let a1n8 = regBit $"{prefix}_a1_n8"
    z8 ==> a1n8
    let a1ua = reg $"{prefix}_a1_ua" 32
    mux (slice 31 31 aPat) negA (asUInt aPat) ==> a1ua
    let a1neg = regBit $"{prefix}_a1_neg"
    (slice 31 31 aPat ^^^ slice 31 31 bPat) ==> a1neg
    let a1guard = regBit $"{prefix}_a1_guard"
    // |b| <= eps, as the unsigned integer compare |b| < eps + 1.
    lt bAbs (lit (uint64 (Fixed.fxDivEps + 1)) 32) ==> a1guard

    // Stage 2 — normalize by 4/2/1; m has its leading 1 at bit 31, and
    // n = {n16, n8, n4, n2, n1} is exactly the concatenated bits.
    let z4 = wireBit $"{prefix}_z4"
    eq (slice 31 28 a1x) (lit 0UL 4) ==> z4
    let x4 = wire $"{prefix}_x4" 32
    mux z4 (cat (slice 27 0 a1x) (lit 0UL 4)) a1x ==> x4
    let z2 = wireBit $"{prefix}_z2"
    eq (slice 31 30 x4) (lit 0UL 2) ==> z2
    let x2 = wire $"{prefix}_x2" 32
    mux z2 (cat (slice 29 0 x4) (lit 0UL 2)) x4 ==> x2
    let z1 = wireBit $"{prefix}_z1"
    eq (slice 31 31 x2) (lit 0UL 1) ==> z1
    let a2m = reg $"{prefix}_a2_m" 32
    mux z1 (cat (slice 30 0 x2) (lit 0UL 1)) x2 ==> a2m
    let a2n = reg $"{prefix}_a2_n" 5
    cat (cat (cat (cat a1n16 a1n8) z4) z2) z1 ==> a2n
    let a2ua = reg $"{prefix}_a2_ua" 32
    a1ua ==> a2ua
    let a2neg = regBit $"{prefix}_a2_neg"
    a1neg ==> a2neg
    let a2guard = regBit $"{prefix}_a2_guard"
    a1guard ==> a2guard

    // Stage 3 — reciprocal seed: the ROM's sync-read register IS this stage.
    let r0 = (memReadPort table (slice 30 22 a2m)).data
    let a3m = reg $"{prefix}_a3_m" 32
    a2m ==> a3m
    let a3n = reg $"{prefix}_a3_n" 5
    a2n ==> a3n
    let a3ua = reg $"{prefix}_a3_ua" 32
    a2ua ==> a3ua
    let a3neg = regBit $"{prefix}_a3_neg"
    a2neg ==> a3neg
    let a3guard = regBit $"{prefix}_a3_guard"
    a2guard ==> a3guard

    // Stage 4 — re-register the ROM output (see the doc comment).
    let a4r0 = reg $"{prefix}_a4_r0" 31
    r0 ==> a4r0
    let a4m = reg $"{prefix}_a4_m" 32
    a3m ==> a4m
    let a4n = reg $"{prefix}_a4_n" 5
    a3n ==> a4n
    let a4ua = reg $"{prefix}_a4_ua" 32
    a3ua ==> a4ua
    let a4neg = regBit $"{prefix}_a4_neg"
    a3neg ==> a4neg
    let a4guard = regBit $"{prefix}_a4_guard"
    a3guard ==> a4guard

    // Stages 5–6 — t = m * r0 (u63), double-registered for the DSP cascade.
    let a5t = reg $"{prefix}_a5_t" 63
    a4m * a4r0 ==> a5t
    let a5r0 = reg $"{prefix}_a5_r0" 31
    a4r0 ==> a5r0
    let a5n = reg $"{prefix}_a5_n" 5
    a4n ==> a5n
    let a5ua = reg $"{prefix}_a5_ua" 32
    a4ua ==> a5ua
    let a5neg = regBit $"{prefix}_a5_neg"
    a4neg ==> a5neg
    let a5guard = regBit $"{prefix}_a5_guard"
    a4guard ==> a5guard

    let a6t = reg $"{prefix}_a6_t" 63
    a5t ==> a6t
    let a6r0 = reg $"{prefix}_a6_r0" 31
    a5r0 ==> a6r0
    let a6n = reg $"{prefix}_a6_n" 5
    a5n ==> a6n
    let a6ua = reg $"{prefix}_a6_ua" 32
    a5ua ==> a6ua
    let a6neg = regBit $"{prefix}_a6_neg"
    a5neg ==> a6neg
    let a6guard = regBit $"{prefix}_a6_guard"
    a5guard ==> a6guard

    // Stage 7 — Newton residual d = 2^62 − t (64-bit two's complement), then
    // d >> 22 taken as d[53:22]: |d| < 2^53, so the 32-bit slice IS the
    // arithmetic shift. r0 rides on widened to 32 bits so stage 8's signed
    // multiply gets a declared 32-bit operand.
    let d = wire $"{prefix}_d" 64
    lit (1UL <<< 62) 64 - cat (lit 0UL 1) a6t ==> d
    let a7dsh = reg $"{prefix}_a7_dsh" (SInt 32)
    slice 53 22 d ==> a7dsh
    let a7r0 = reg $"{prefix}_a7_r0" (SInt 32)
    cat (lit 0UL 1) a6r0 ==> a7r0
    let a7n = reg $"{prefix}_a7_n" 5
    a6n ==> a7n
    let a7ua = reg $"{prefix}_a7_ua" 32
    a6ua ==> a7ua
    let a7neg = regBit $"{prefix}_a7_neg"
    a6neg ==> a7neg
    let a7guard = regBit $"{prefix}_a7_guard"
    a6guard ==> a7guard

    // Stages 8–9 — corr = r0 * (d >> 22), signed, double-registered.
    let a8corr = reg $"{prefix}_a8_corr" (SInt 64)
    mul a7r0 a7dsh ==> a8corr
    let a8r0 = reg $"{prefix}_a8_r0" (SInt 32)
    a7r0 ==> a8r0
    let a8n = reg $"{prefix}_a8_n" 5
    a7n ==> a8n
    let a8ua = reg $"{prefix}_a8_ua" 32
    a7ua ==> a8ua
    let a8neg = regBit $"{prefix}_a8_neg"
    a7neg ==> a8neg
    let a8guard = regBit $"{prefix}_a8_guard"
    a7guard ==> a8guard

    let a9corr = reg $"{prefix}_a9_corr" 64
    a8corr ==> a9corr
    let a9r0 = reg $"{prefix}_a9_r0" 32
    a8r0 ==> a9r0
    let a9n = reg $"{prefix}_a9_n" 5
    a8n ==> a9n
    let a9ua = reg $"{prefix}_a9_ua" 32
    a8ua ==> a9ua
    let a9neg = regBit $"{prefix}_a9_neg"
    a8neg ==> a9neg
    let a9guard = regBit $"{prefix}_a9_guard"
    a8guard ==> a9guard

    // Stage 10 — refined reciprocal r = r0 + (corr >> 40). corr[63:40] is the
    // narrowing arithmetic shift; r < 2^32, so the 33-bit sum's low 32 bits
    // are the exact value.
    let corrSh = wire $"{prefix}_corrSh" 24
    slice 63 40 a9corr ==> corrSh
    let rSum = wire $"{prefix}_rSum" 33
    // The correction is signed and the running remainder is not; the sum is
    // known non-negative (r < 2^32), so it accumulates as bits.
    cat (lit 0UL 1) a9r0 + asUInt (signExtend 33 corrSh) ==> rSum
    let a10r = reg $"{prefix}_a10_r" 32
    slice 31 0 rSum ==> a10r
    let a10n = reg $"{prefix}_a10_n" 5
    a9n ==> a10n
    let a10ua = reg $"{prefix}_a10_ua" 32
    a9ua ==> a10ua
    let a10neg = regBit $"{prefix}_a10_neg"
    a9neg ==> a10neg
    let a10guard = regBit $"{prefix}_a10_guard"
    a9guard ==> a10guard

    // Stages 11–12 — p = ua * r (u64), double-registered.
    let a11p = reg $"{prefix}_a11_p" 64
    a10ua * a10r ==> a11p
    let a11n = reg $"{prefix}_a11_n" 5
    a10n ==> a11n
    let a11neg = regBit $"{prefix}_a11_neg"
    a10neg ==> a11neg
    let a11guard = regBit $"{prefix}_a11_guard"
    a10guard ==> a11guard

    let a12p = reg $"{prefix}_a12_p" 64
    a11p ==> a12p
    let a12n = reg $"{prefix}_a12_n" 5
    a11n ==> a12n
    let a12neg = regBit $"{prefix}_a12_neg"
    a11neg ==> a12neg
    let a12guard = regBit $"{prefix}_a12_guard"
    a11guard ==> a12guard

    // Final combinational cone — qMag = p >> (46 − n) as a fixed >>21 plus a
    // 5-stage barrel shift by (25 − n); then sign, saturate, guard mux.
    let pre = wire $"{prefix}_pre" 43
    slice 63 21 a12p ==> pre
    let sh = wire $"{prefix}_sh" 5
    lit 25UL 5 - a12n ==> sh
    let y16 = wire $"{prefix}_y16" 43
    mux (slice 4 4 sh) (cat (lit 0UL 16) (slice 42 16 pre)) pre ==> y16
    let y8 = wire $"{prefix}_y8" 43
    mux (slice 3 3 sh) (cat (lit 0UL 8) (slice 42 8 y16)) y16 ==> y8
    let y4 = wire $"{prefix}_y4" 43
    mux (slice 2 2 sh) (cat (lit 0UL 4) (slice 42 4 y8)) y8 ==> y4
    let y2 = wire $"{prefix}_y2" 43
    mux (slice 1 1 sh) (cat (lit 0UL 2) (slice 42 2 y4)) y4 ==> y2
    let qMag = wire $"{prefix}_qMag" 43
    mux (slice 0 0 sh) (cat (lit 0UL 1) (slice 42 1 y2)) y2 ==> qMag

    let big = wireBit $"{prefix}_big"
    bnot (eq (slice 42 31 qMag) (lit 0UL 12)) ==> big
    let qPos = wire $"{prefix}_qPos" 32
    slice 31 0 qMag ==> qPos
    let qNeg = wire $"{prefix}_qNeg" 32
    lit 0UL 32 - qPos ==> qNeg
    let satVal = wire $"{prefix}_satVal" 32
    mux a12neg (lit 0x80000000UL 32) (lit 0x7FFFFFFFUL 32) ==> satVal
    let signedQ = wire $"{prefix}_signedQ" 32
    mux a12neg qNeg qPos ==> signedQ
    let result = wire $"{prefix}_result" 32
    mux a12guard (lit (uint64 Fixed.fxOne) 32) (mux big satVal signedQ) ==> result
    result

/// The standalone divide FU: input registers + `divRecipArm` + output
/// register, `gepDivLatency` cycles total, II=1. The Sim check streams
/// operands against `fxDivRecip`; the differential is the Verilator target.
let gepDivRecip =
    defModule
        "GepDivRecip32"
        (fun p -> (p.inPort "a" 32, p.inPort "b" 32, p.outPort "q" 32))
        (fun (a, b, q) ->
            let s0a = reg "s0_a" 32
            let s0b = reg "s0_b" 32
            a ==> s0a
            b ==> s0b
            let out = reg "s_out_q" 32
            divRecipArm s0a s0b "dv" ==> out
            out ==> q)

/// Cycles from an operand entering the pipelined ALU to its result appearing.
/// The barrel scheduler interleaves at least this many independent threads to
/// keep the pipe full; any `valid` travelling with the data delays the same.
let gepAluPipelineLatency = 4

/// Latency of the withDiv ALU: stage 1 + the 12-register `divRecipArm` with
/// its final cone combinational into the result select. Every other arm rides
/// a delay chain to the same depth — latency costs threads, not throughput.
let gepAluDivPipelineLatency = 1 + gepDivArmLatency

let gepAluLatency (withDiv: bool) =
    if withDiv then gepAluDivPipelineLatency else gepAluPipelineLatency

/// The GEP ALU datapath, Q16.16 in 32 bits, sliced into registered stages so
/// the multiply — the cone that pins a shallow engine's clock — stands alone
/// between registers and the tool can retime it into the DSP cascade
/// (MREG/PREG). Same values as the software `applyOp`, delayed
/// `gepAluLatency withDiv` cycles, II=1: every register updates every cycle,
/// so latency costs threads (context), not throughput.
///
/// Stage 1 registers the inputs; stage 2 runs the multiply beside the cheap
/// arms (all registered to stay aligned); stage 3 is the second product
/// register; stage 4 normalises the product, selects by op, registers the
/// result. An opcode outside the arm list selects the delayed `a` — a
/// terminal node carries its value on `a`, exactly the software default.
/// With `withDiv`, `divRecipArm` runs from the stage-1 bit patterns and every
/// other arm rides delay chains to its depth; the final select is
/// combinational on same-cycle registers.
let gepAluPipelined (withDiv: bool) (prefix: string) (op: Expr) (a: Expr) (b: Expr) : Expr =
    // Stage 1 — register the inputs.
    let s1op = reg $"{prefix}_s1_op" 8
    op ==> s1op
    let s1a = reg $"{prefix}_s1_a" (SInt 32)
    a ==> s1a
    let s1b = reg $"{prefix}_s1_b" (SInt 32)
    b ==> s1b

    // Stage 2 — the multiply stands alone; the cheaper arms compute in
    // parallel and are registered so they stay aligned with the product.
    // saturate slices, so the wide add/sub/neg pass through wires first.
    let aWide = wire $"{prefix}_aWide" (SInt 33)
    signExtend 33 s1a ==> aWide
    let bWide = wire $"{prefix}_bWide" (SInt 33)
    signExtend 33 s1b ==> bWide
    let sum = wire $"{prefix}_sum" (SInt 33)
    aWide + bWide ==> sum
    let diff = wire $"{prefix}_diff" (SInt 33)
    aWide - bWide ==> diff
    let negated = wire $"{prefix}_negated" (SInt 33)
    lit 0UL 33 - aWide ==> negated
    let negSat = wire $"{prefix}_negSat" (SInt 32)
    saturate 32 negated ==> negSat
    let zero = litS 0UL 32
    let one = litS (1UL <<< Fixed.fxFracBits) 32

    let s2prod = reg $"{prefix}_s2_prod" (SInt 64)
    mul s1a s1b ==> s2prod
    let s2add = reg $"{prefix}_s2_add" (SInt 32)
    saturate 32 sum ==> s2add
    let s2sub = reg $"{prefix}_s2_sub" (SInt 32)
    saturate 32 diff ==> s2sub
    let s2neg = reg $"{prefix}_s2_neg" (SInt 32)
    negSat ==> s2neg
    let s2abs = reg $"{prefix}_s2_abs" (SInt 32)
    mux (lt s1a zero) negSat s1a ==> s2abs
    let s2min = reg $"{prefix}_s2_min" (SInt 32)
    mux (lt s1a s1b) s1a s1b ==> s2min
    let s2max = reg $"{prefix}_s2_max" (SInt 32)
    mux (lt s1b s1a) s1a s1b ==> s2max
    let s2gt = reg $"{prefix}_s2_gt" (SInt 32)
    mux (lt s1b s1a) one zero ==> s2gt
    let s2lt = reg $"{prefix}_s2_lt" (SInt 32)
    mux (lt s1a s1b) one zero ==> s2lt
    let s2op = reg $"{prefix}_s2_op" 8
    s1op ==> s2op
    let s2a = reg $"{prefix}_s2_a" (SInt 32)
    s1a ==> s2a

    // Stage 3 — the second product register: a 32x32 product spans two
    // cascaded DSP48s plus a fabric shift-add; back-to-back product registers
    // let the tools retime one into the DSP pipeline. The cheap arms ride
    // along to stay aligned.
    let s3prod = reg $"{prefix}_s3_prod" (SInt 64)
    s2prod ==> s3prod
    let s3add = reg $"{prefix}_s3_add" (SInt 32)
    s2add ==> s3add
    let s3sub = reg $"{prefix}_s3_sub" (SInt 32)
    s2sub ==> s3sub
    let s3neg = reg $"{prefix}_s3_neg" (SInt 32)
    s2neg ==> s3neg
    let s3abs = reg $"{prefix}_s3_abs" (SInt 32)
    s2abs ==> s3abs
    let s3min = reg $"{prefix}_s3_min" (SInt 32)
    s2min ==> s3min
    let s3max = reg $"{prefix}_s3_max" (SInt 32)
    s2max ==> s3max
    let s3gt = reg $"{prefix}_s3_gt" (SInt 32)
    s2gt ==> s3gt
    let s3lt = reg $"{prefix}_s3_lt" (SInt 32)
    s2lt ==> s3lt
    let s3op = reg $"{prefix}_s3_op" 8
    s2op ==> s3op
    let s3a = reg $"{prefix}_s3_a" (SInt 32)
    s2a ==> s3a

    // Stage 4 — normalise the product (the arithmetic >>16 is a slice of the
    // signed product), select the arm, register the result.
    let scaled = wire $"{prefix}_scaled" (SInt 48)
    slice 63 Fixed.fxFracBits s3prod ==> scaled
    let mulResult = wire $"{prefix}_mulResult" (SInt 32)
    saturate 32 scaled ==> mulResult

    let arms =
        [ Opcodes.ADD, s3add
          Opcodes.SUB, s3sub
          Opcodes.MUL, mulResult
          Opcodes.MIN, s3min
          Opcodes.MAX, s3max
          Opcodes.GT, s3gt
          Opcodes.LT, s3lt
          Opcodes.NEG, s3neg
          Opcodes.ABS, s3abs ]

    let selected =
        List.foldBack
            (fun (code, value) fallback -> mux (eq s3op (lit (uint64 code) 8)) value fallback)
            arms
            s3a

    let s4res = reg $"{prefix}_s4_res" (SInt 32)
    selected ==> s4res

    if not withDiv then
        s4res
    else
        // The divide arm runs from stage-1 bit patterns; everything else
        // rides delay chains to the arm's depth, and the final select is
        // combinational on same-cycle registers.
        let divQ = divRecipArm s1a s1b $"{prefix}_dv"

        let mutable resChain = s4res

        for k in 5 .. gepAluDivPipelineLatency do
            let r = reg $"{prefix}_d%d{k}_res" 32
            resChain ==> r
            resChain <- r

        let mutable opChain = s3op

        for k in 4 .. gepAluDivPipelineLatency do
            let r = reg $"{prefix}_d%d{k}_op" 8
            opChain ==> r
            opChain <- r

        mux (eq opChain (lit (uint64 Opcodes.DIV) 8)) divQ resChain

/// The Karva compiler's states. Qualified because every machine in this file
/// has an `Idle` and a `Done`, and which one a call site means should not
/// depend on the order the unions were declared in.
[<RequireQualifiedAccess>]
type private Karva =
    | Idle
    /// Walk the open reading frame in BFS order, recording each node's
    /// first-child position and the level boundaries.
    | Scan
    /// Walk levels deepest-first: this node's own symbol.
    | EmitSelf
    | EmitOperandA
    | EmitOperandB
    /// The assembled instruction word into the record.
    | EmitWrite
    /// The root resolved into the record header.
    | Header
    | Done

/// The fabric Karva compiler: compiles a gene — read symbol-by-symbol from an
/// external async port (the operator engine's gene buffer) — into the packed
/// execution record's header + instruction words, emission-order-identical to
/// `MicroProgram.compileGene`, so records are word-comparable against the
/// host reference. Constants are NOT the compiler's business.
///
/// The reference's two passes as an FSM (~1 cycle/symbol + ~4 cycles/function
/// node): SCAN walks the open reading frame in BFS order (Karva allocates
/// children sequentially, so one pass computes each node's first-child
/// position and the level boundaries; the frame ends when the walk catches
/// the allocation pointer); EMIT walks levels deepest-first, positions
/// ascending; HDR resolves the root into the record header
/// `nInstr | src<<8 | idx<<10`. Arity comes from the opcode's class bits,
/// terminal/constant from bit 5, index from [4:0] — no ROMs, pure slicing.
///
/// Returns the compiler's boundary as an anonymous record: `symAddr` into the
/// caller's gene buffer (whose async read feeds `symData` back), the `rec*`
/// memory write port (enable/addr/data, no handshake), `busy`/`finished`/
/// `nInstr`.
let gepKarvaCompiler (prefix: string) (start: Expr) (symData: Expr) =
    let st =
        machine
            $"{prefix}_st"
            [ Karva.Idle
              Karva.Scan
              Karva.EmitSelf
              Karva.EmitOperandA
              Karva.EmitOperandB
              Karva.EmitWrite
              Karva.Header
              Karva.Done ]

    let pos = reg $"{prefix}_pos" 6
    let alloc = regInit $"{prefix}_alloc" 6 1UL // Karva child-allocation pointer
    let level = reg $"{prefix}_level" 6
    let levelEnd = regInit $"{prefix}_levelEnd" 6 1UL
    let n = reg $"{prefix}_n" 6
    let emitLv = reg $"{prefix}_emitLv" 6
    let emitPos = reg $"{prefix}_emitPos" 6
    let emitEnd = reg $"{prefix}_emitEnd" 6
    let opReg = reg $"{prefix}_opReg" 8
    let cb = reg $"{prefix}_cb" 6
    let a2 = regBit $"{prefix}_a2"
    let aReg = reg $"{prefix}_aReg" 8
    let bReg = reg $"{prefix}_bReg" 8

    let childBase = distributedMem $"{prefix}_childBase" 6 6
    let resultIndex = distributedMem $"{prefix}_resultIndex" 6 6
    let starts = distributedMem $"{prefix}_starts" 6 6

    // Symbol decode — pure slices of the opcode layout.
    let arity = wire $"{prefix}_arity" 2
    slice 7 6 symData ==> arity
    let isTermB = eq arity (lit 0UL 2)
    let isConstB = slice 5 5 symData
    let tIdx = wire $"{prefix}_tIdx" 6
    cat (lit 0UL 1) (slice 4 0 symData) ==> tIdx

    let cbP1 = wire $"{prefix}_cbP1" 6
    cb + lit 1UL 6 ==> cbP1

    let symAddr = wire $"{prefix}_symAddr" 6

    mux
            (st.Is Karva.Scan)
            pos
            (mux (st.Is Karva.EmitSelf) emitPos (mux (st.Is Karva.EmitOperandA) cb (mux (st.Is Karva.EmitOperandB) cbP1 (lit 0UL 6))))
    ==> symAddr

    // Operand encode `src | idx<<2` for the symbol on the port (RESULT index
    // from the map at the same operand position).
    let riAddr = wire $"{prefix}_riAddr" 6
    mux (st.Is Karva.EmitOperandA) cb (mux (st.Is Karva.EmitOperandB) cbP1 (lit 0UL 6)) ==> riAddr
    let ri = wire $"{prefix}_ri" 6
    memRead resultIndex riAddr ==> ri
    let encTerm = wire $"{prefix}_encTerm" 8
    cat tIdx (mux isConstB (lit 1UL 2) (lit 0UL 2)) ==> encTerm
    let enc = wire $"{prefix}_enc" 8
    mux isTermB encTerm (cat ri (lit 2UL 2)) ==> enc

    // Record write port: instruction words during EMIT_WR, the header at HDR.
    let instrWord = wire $"{prefix}_instrWord" 32
    cat (lit 0UL 8) (cat (cat bReg aReg) opReg) ==> instrWord
    let hdrWord = wire $"{prefix}_hdrWord" 32
    cat (lit 0UL 16) (cat (cat (slice 7 2 enc) (slice 1 0 enc)) (cat (lit 0UL 2) n)) ==> hdrWord
    let recEn = wireBit $"{prefix}_recEn"
    mux (st.Is Karva.EmitWrite) (lit 1UL 1) (mux (st.Is Karva.Header) (lit 1UL 1) (lit 0UL 1)) ==> recEn
    let nP1 = wire $"{prefix}_nP1" 6
    n + lit 1UL 6 ==> nP1
    let recAddr = wire $"{prefix}_recAddr" 6
    mux (st.Is Karva.Header) (lit 0UL 6) nP1 ==> recAddr
    let recData = wire $"{prefix}_recData" 32
    mux (st.Is Karva.Header) hdrWord instrWord ==> recData

    let busy = wireBit $"{prefix}_busy"
    mux (st.Is Karva.Idle) (lit 0UL 1) (mux (st.Is Karva.Done) (lit 0UL 1) (lit 1UL 1)) ==> busy
    let finished = wireBit $"{prefix}_done"
    st.Is Karva.Done ==> finished

    let lvM1 = wire $"{prefix}_lvM1" 6
    emitLv - lit 1UL 6 ==> lvM1
    let posP1 = wire $"{prefix}_posP1" 6
    emitPos + lit 1UL 6 ==> posP1

    // Advance within the emission walk: next position, or drop a level, or —
    // past the last position of level 0 — the header.
    let advanceEmit (continueState: Karva) =
        ifElse [(eq posP1 emitEnd, fun () ->
            ifElse [(eq emitLv (lit 0UL 6), fun () -> st.Goto Karva.Header); (otherwise, fun () ->
                memRead starts lvM1 ==> emitPos
                memRead starts emitLv ==> emitEnd
                lvM1 ==> emitLv
                st.Goto continueState) ]); (otherwise, fun () ->
            posP1 ==> emitPos
            st.Goto continueState) ]

    let initOnStart () =
        If start (fun () ->
            st.Goto Karva.Scan
            lit 0UL 6 ==> pos
            lit 1UL 6 ==> alloc
            lit 0UL 6 ==> level
            lit 1UL 6 ==> levelEnd
            lit 0UL 6 ==> n
            memWrite starts (lit 0UL 6) (lit 0UL 6) (lit 1UL 1))

    st.Switch
        [ Karva.Idle, (fun () -> initOnStart ())
          Karva.Done, (fun () -> initOnStart ())

          Karva.Scan, (fun () ->
        ifElse [(eq pos alloc, fun () ->
            // Frame complete: current `level` is the deepest; emit it first.
            memWrite starts (level + lit 1UL 6) alloc (lit 1UL 1)
            level ==> emitLv
            memRead starts level ==> emitPos
            alloc ==> emitEnd
            st.Goto Karva.EmitSelf); (eq pos levelEnd, fun () ->
                // Level boundary: everything allocated so far is the next level.
                memWrite starts (level + lit 1UL 6) levelEnd (lit 1UL 1)
                level + lit 1UL 6 ==> level
                alloc ==> levelEnd); (otherwise, fun () ->
                memWrite childBase pos alloc (lit 1UL 1)
                alloc + cat (lit 0UL 4) arity ==> alloc
                pos + lit 1UL 6 ==> pos) ])

          Karva.EmitSelf, (fun () ->
        ifElse [(isTermB, fun () -> advanceEmit Karva.EmitSelf); (otherwise, fun () ->
            symData ==> opReg
            memRead childBase emitPos ==> cb
            mux (eq arity (lit 2UL 2)) (lit 1UL 1) (lit 0UL 1) ==> a2
            st.Goto Karva.EmitOperandA) ])

          Karva.EmitOperandA, (fun () ->
        enc ==> aReg
        st.Goto Karva.EmitOperandB)

          Karva.EmitOperandB, (fun () ->
        mux a2 enc (lit 0UL 8) ==> bReg
        st.Goto Karva.EmitWrite)

          Karva.EmitWrite, (fun () ->
        memWrite resultIndex emitPos n (lit 1UL 1)
        nP1 ==> n
        advanceEmit Karva.EmitSelf)

          Karva.Header, (fun () -> st.Goto Karva.Done) ]

    {| symAddr = symAddr
       recEn = recEn
       recAddr = recAddr
       recData = recData
       busy = busy
       finished = finished
       nInstr = n |}

/// The breeding policy for one offspring: a Bernoulli threshold per operator
/// (all 32-bit `round(p·2^32)` words), plus the two spans the creep and
/// constant draws are scaled by. `rangeFx` must be <= 2^20−1 so the constant
/// span fits the Lemire draw's 21-bit bound.
type GepBreedRatesBus =
    { onePoint: Expr
      twoPoint: Expr
      geneRecomb: Expr
      mutation: Expr
      constReplace: Expr
      creep: Expr
      inversion: Expr
      isTrans: Expr
      risTrans: Expr
      sigmaFx: Expr
      rangeFx: Expr }

/// The parent-genome load bus: symbols and constants into the A/B buffers.
type GepParentLoadBus =
    { ldSym: Expr
      ldPar: Expr
      ldAddr: Expr
      ldSdata: Expr
      ldConst: Expr
      ldCdata: Expr }

/// The fabric operator engine: `hwBreedOffspring` — the normative draw order
/// over the shared xoshiro128++ stream — as an FSM, diffed against it
/// word-for-word on shared seeds. One pairing produces one offspring: parents
/// load into A/B buffers, COPY seeds child (= A) and shadow (= B), the three
/// recombination gates run pairwise swaps across both, and the variation
/// chain edits the child in place.
///
/// Hardware shapes: every bounded draw shares ONE Lemire multiplier
/// (`(word · bound) >> 32` — a 32×21 product, top bits, no division);
/// Bernoulli gates are one unsigned compare against a threshold input; creep
/// is the Irwin–Hall block (12 accumulate cycles + one signed
/// multiply/shift/saturate — the 64×64 product rides the Sim's wide path);
/// symbol draws index elaboration-time ROM mux trees built from the
/// configured sets. In-buffer swaps ping-pong over two cycles (one write
/// port); IS and RIS share the capture/shift/insert states via a return
/// flag.
///
/// v1 restrictions, matching the deployed config: `geneCount = 1`
/// (gene transposition elides exactly like the spec's no-draw skip; "which
/// gene" draws consume their word and resolve to gene 0) and `headLen >= 2`.
/// The operator engine's 39 states, in encoding order. `Insertion` is GEP's IS
/// transposition and `RootInsertion` its RIS variant; the two share the
/// capture / shift / insert states through `risRet`. A `*Gate` state is the
/// Bernoulli draw that decides whether its operator runs at all.
[<RequireQualifiedAccess>]
type private Operator =
    | Idle
    | Copy
    | OnePointGate
    | OnePointCut
    | TwoPointGate
    | TwoPointFirstCut
    | TwoPointSecondCut
    | GeneRecombineGate
    | GeneRecombineGene
    | RecombineSweep
    | RecombineConstants
    | MutationGate
    | MutationSymbol
    | ConstantReplaceGate
    | ConstantReplaceDraw
    | CreepGate
    | CreepAccumulate
    | CreepApply
    | InversionGate
    | InversionGene
    | InversionStart
    | InversionEnd
    | InversionSwapRead
    | InversionSwapWrite
    | InsertionGate
    | InsertionLength
    | InsertionSourceGene
    | InsertionSourceOffset
    | InsertionTargetGene
    | InsertionTarget
    | TransposonCapture
    | TransposonShift
    | TransposonInsert
    | RootInsertionGate
    | RootInsertionGene
    | RootInsertionStart
    | RootInsertionScan
    | RootInsertionLength
    | Done

/// The chromosome's geometry — what a program *is*, as against how big the
/// machine that runs one is (`GepUnitShape`).
///
/// A record because the five travelled together through every constructor as
/// positional arguments, three of them adjacent bare ints: `17 8 4` is a gene
/// of 17 symbols with a head of 8 and four constants, and any permutation of
/// those three elaborates, emits, and breeds a differently-shaped population
/// that nothing here would call an error. The two sets are arrays and would
/// have caught their own swap; the ints would not.
[<NoEquality; NoComparison>]
type GepGenome =
    { functionSet: int[]
      terminalSet: int[]
      geneLen: int
      headLen: int
      constCount: int }

let gepOperatorEngine
    (genome: GepGenome)
    (maxTransposon: int)
    (prefix: string)
    (start: Expr)
    (sIn: Expr list)
    (rates: GepBreedRatesBus)
    (load: GepParentLoadBus)
    (rdSaddr: Expr)
    (rdCaddr: Expr)
    =
    let functionSet, terminalSet = genome.functionSet, genome.terminalSet
    let geneLen, headLen, constCount = genome.geneLen, genome.headLen, genome.constCount

    if geneLen < 2 || geneLen > 64 then
        failwith $"geneLen must be 2..64, got %d{geneLen}"

    if headLen < 2 || headLen >= geneLen then
        failwith $"headLen must be 2..geneLen-1, got %d{headLen}"

    if constCount < 1 || constCount > 64 then
        failwith $"constCount must be 1..64, got %d{constCount}"

    let symCount = functionSet.Length + terminalSet.Length


    // Parent load + child read ports.
    let symA = distributedMem $"{prefix}_symA" 6 8
    let symB = distributedMem $"{prefix}_symB" 6 8
    let symC = distributedMem $"{prefix}_symC" 6 8
    let symS = distributedMem $"{prefix}_symS" 6 8
    let constA = distributedMem $"{prefix}_constA" 6 32
    let constB = distributedMem $"{prefix}_constB" 6 32
    let constC = distributedMem $"{prefix}_constC" 6 32
    let constS = distributedMem $"{prefix}_constS" 6 32
    memWrite symA load.ldAddr load.ldSdata (load.ldSym &&& bnot load.ldPar)
    memWrite symB load.ldAddr load.ldSdata (load.ldSym &&& load.ldPar)
    memWrite constA load.ldAddr load.ldCdata (load.ldConst &&& bnot load.ldPar)
    memWrite constB load.ldAddr load.ldCdata (load.ldConst &&& load.ldPar)

    // PRNG: state loads on `start`; `word` is combinational; step closes
    // through a declared wire below.
    let stepW = wireBit $"{prefix}_stepW"
    let word = wire $"{prefix}_word" 32
    xoshiro128pp "Xoshiro128pp" $"{prefix}_prng" start sIn stepW ==> word

    let st =
        machine
            $"{prefix}_st"
            [ Operator.Idle
              Operator.Copy
              Operator.OnePointGate
              Operator.OnePointCut
              Operator.TwoPointGate
              Operator.TwoPointFirstCut
              Operator.TwoPointSecondCut
              Operator.GeneRecombineGate
              Operator.GeneRecombineGene
              Operator.RecombineSweep
              Operator.RecombineConstants
              Operator.MutationGate
              Operator.MutationSymbol
              Operator.ConstantReplaceGate
              Operator.ConstantReplaceDraw
              Operator.CreepGate
              Operator.CreepAccumulate
              Operator.CreepApply
              Operator.InversionGate
              Operator.InversionGene
              Operator.InversionStart
              Operator.InversionEnd
              Operator.InversionSwapRead
              Operator.InversionSwapWrite
              Operator.InsertionGate
              Operator.InsertionLength
              Operator.InsertionSourceGene
              Operator.InsertionSourceOffset
              Operator.InsertionTargetGene
              Operator.InsertionTarget
              Operator.TransposonCapture
              Operator.TransposonShift
              Operator.TransposonInsert
              Operator.RootInsertionGate
              Operator.RootInsertionGene
              Operator.RootInsertionStart
              Operator.RootInsertionScan
              Operator.RootInsertionLength
              Operator.Done ]

    let i = reg $"{prefix}_i" 6
    let k = reg $"{prefix}_k" 6
    let fromR = reg $"{prefix}_fromR" 6
    let endR = reg $"{prefix}_endR" 6
    let xret = reg $"{prefix}_xret" 2
    let cutA = reg $"{prefix}_cutA" 6
    let lenR = reg $"{prefix}_lenR" 6
    let srcR = reg $"{prefix}_srcR" 6
    let tgtR = reg $"{prefix}_tgtR" 6
    let sA = reg $"{prefix}_sA" 6
    let lo = reg $"{prefix}_lo" 6
    let hi = reg $"{prefix}_hi" 6
    let tmp = reg $"{prefix}_tmp" 8
    let sum = reg $"{prefix}_sum" 36
    let bndReg = regInit $"{prefix}_bndReg" 21 1UL
    let risRet = regBit $"{prefix}_risRet"
    let run = [ for r in 0 .. maxTransposon - 1 -> reg $"{prefix}_run%d{r}" 8 ]

    // The shared Lemire draw: (word * bound) >> 32.
    let prod = wire $"{prefix}_prod" 53
    word * bndReg ==> prod
    let bnd21 = wire $"{prefix}_bnd21" 21
    slice 52 32 prod ==> bnd21
    let bnd6 = wire $"{prefix}_bnd6" 6
    slice 5 0 bnd21 ==> bnd6

    // Gate threshold + hit for the current state. Each gate state draws
    // against exactly one rate; RIS is the fall-through arm.
    let gates =
        [ Operator.OnePointGate, rates.onePoint
          Operator.TwoPointGate, rates.twoPoint
          Operator.GeneRecombineGate, rates.geneRecomb
          Operator.MutationGate, rates.mutation
          Operator.ConstantReplaceGate, rates.constReplace
          Operator.CreepGate, rates.creep
          Operator.InversionGate, rates.inversion
          Operator.InsertionGate, rates.isTrans ]

    let gateTh = wire $"{prefix}_gateTh" 32
    List.foldBack (fun (s, r) acc -> mux (st.Is s) r acc) gates rates.risTrans ==> gateTh
    let hitB = lt word gateTh

    // PRNG step: exactly the states that consume a word this cycle.
    let alwaysStep =
        [ Operator.OnePointGate; Operator.TwoPointGate; Operator.GeneRecombineGate; Operator.OnePointCut; Operator.TwoPointFirstCut; Operator.TwoPointSecondCut; Operator.GeneRecombineGene; Operator.MutationSymbol; Operator.ConstantReplaceDraw; Operator.CreepAccumulate
          Operator.InversionGate; Operator.InversionGene; Operator.InversionStart; Operator.InversionEnd; Operator.InsertionGate; Operator.InsertionLength; Operator.InsertionSourceGene; Operator.InsertionSourceOffset; Operator.InsertionTargetGene; Operator.InsertionTarget
          Operator.RootInsertionGate; Operator.RootInsertionGene; Operator.RootInsertionStart; Operator.RootInsertionLength ]

    let stepBase =
        List.fold (fun acc s -> mux (st.Is s) (lit 1UL 1) acc) (lit 0UL 1) alwaysStep

    let stepMut =
        mux (st.Is Operator.MutationGate &&& bnot (eq i (lit (uint64 geneLen) 6))) (lit 1UL 1) stepBase

    let stepCr =
        mux (st.Is Operator.ConstantReplaceGate &&& bnot (eq k (lit (uint64 constCount) 6))) (lit 1UL 1) stepMut

    mux (st.Is Operator.CreepGate &&& bnot (eq k (lit (uint64 constCount) 6))) (lit 1UL 1) stepCr
    ==> stepW

    // Symbol ROMs as literal mux trees (head: functions ++ terminals; tail:
    // terminals only).
    let romMux (values: int[]) (idx: Expr) =
        let mutable e = lit (uint64 (Array.last values)) 8

        for v in values.Length - 2 .. -1 .. 0 do
            e <- mux (eq idx (lit (uint64 v) 6)) (lit (uint64 values[v]) 8) e

        e

    let headSym = wire $"{prefix}_headSym" 8
    romMux (Array.append functionSet terminalSet) bnd6 ==> headSym
    let tailSym = wire $"{prefix}_tailSym" 8
    romMux terminalSet bnd6 ==> tailSym

    let busy = wireBit $"{prefix}_busy"
    mux (st.Is Operator.Idle) (lit 0UL 1) (mux (st.Is Operator.Done) (lit 0UL 1) (lit 1UL 1)) ==> busy
    let finished = wireBit $"{prefix}_done"
    st.Is Operator.Done ==> finished

    let initOnStart () =
        If start (fun () ->
            st.Goto Operator.Copy
            lit 0UL 6 ==> i
            lit 0UL 6 ==> k)
    // Constant replacement.
    let spanW = wire $"{prefix}_spanW" 21
    cat (slice 19 0 rates.rangeFx) (lit 0UL 1) + lit 1UL 21 ==> spanW

    let centU = wire $"{prefix}_centU" 37
    cat (lit 0UL 1) sum - lit (6UL <<< 32) 37 ==> centU

    let cent64 = wire $"{prefix}_cent64" 64
    cat (mux (slice 36 36 centU) (lit ((1UL <<< 27) - 1UL) 27) (lit 0UL 27)) centU ==> cent64

    let sig64 = wire $"{prefix}_sig64" 64
    cat (mux (slice 31 31 rates.sigmaFx) (lit 0xFFFFFFFFUL 32) (lit 0UL 32)) rates.sigmaFx ==> sig64

    let prodW = wire $"{prefix}_prodW" 128
    cent64 * sig64 ==> prodW
    let deltaU = wire $"{prefix}_deltaU" 32
    slice 63 32 prodW ==> deltaU
    let creepA32 = wire $"{prefix}_creepA32" 32
    memRead constC k ==> creepA32
    let creepSum = wire $"{prefix}_creepSum" (SInt 33)
    signExtend 33 creepA32 + signExtend 33 deltaU ==> creepSum
    let creepRes = wire $"{prefix}_creepRes" (SInt 32)
    saturate 32 creepSum ==> creepRes

    let runSel = wire $"{prefix}_runSel" 8

    (run
         |> List.indexed
         |> List.take (run.Length - 1)
         |> List.foldBack (fun (r, v) acc -> mux (eq i (lit (uint64 r) 6)) v acc)
         <| List.last run)
    ==> runSel

    let scanSym = wire $"{prefix}_scanSym" 8
    memRead symC sA ==> scanSym
    let scanTerm = eq (slice 7 6 scanSym) (lit 0UL 2)
    let dHead = wire $"{prefix}_dHead" 6
    lit (uint64 headLen) 6 - sA ==> dHead


    st.Switch

        [ Operator.Idle, (fun () -> initOnStart ())
          Operator.Done, (fun () -> initOnStart ())

          // COPY: child = parentA, shadow = parentB (symbols + constants in parallel).
          Operator.Copy, (fun () ->
        memWrite symC i (memRead symA i) (lit 1UL 1)
        memWrite symS i (memRead symB i) (lit 1UL 1)

        If (lt i (lit (uint64 constCount) 6)) (fun () ->
            memWrite constC i (memRead constA i) (lit 1UL 1)
            memWrite constS i (memRead constB i) (lit 1UL 1))

        ifElse [(eq i (lit (uint64 (geneLen - 1)) 6), fun () ->
            st.Goto Operator.OnePointGate
            lit 0UL 6 ==> i); (otherwise, fun () -> i + lit 1UL 6 ==> i) ])

          // Recombination gates + shared pairwise sweep.
          Operator.OnePointGate, (fun () ->
        ifElse [(hitB, fun () ->
            lit (uint64 geneLen) 21 ==> bndReg
            st.Goto Operator.OnePointCut); (otherwise, fun () -> st.Goto Operator.TwoPointGate) ])

          Operator.OnePointCut, (fun () ->
        bnd6 ==> fromR
        lit (uint64 geneLen) 6 ==> endR
        bnd6 ==> i
        lit 0UL 2 ==> xret
        st.Goto Operator.RecombineSweep)

          Operator.TwoPointGate, (fun () ->
        ifElse [(hitB, fun () ->
            lit (uint64 geneLen) 21 ==> bndReg
            st.Goto Operator.TwoPointFirstCut); (otherwise, fun () -> st.Goto Operator.GeneRecombineGate) ])

          Operator.TwoPointFirstCut, (fun () ->
        bnd6 ==> cutA
        st.Goto Operator.TwoPointSecondCut)

          Operator.TwoPointSecondCut, (fun () ->
        let mn = mux (lt cutA bnd6) cutA bnd6
        let mx = mux (lt cutA bnd6) bnd6 cutA
        mn ==> fromR
        mn ==> i
        mx + lit 1UL 6 ==> endR
        lit 1UL 2 ==> xret
        st.Goto Operator.RecombineSweep)

          Operator.GeneRecombineGate, (fun () ->
        ifElse [(hitB, fun () ->
            lit 1UL 21 ==> bndReg
            st.Goto Operator.GeneRecombineGene); (otherwise, fun () ->
            st.Goto Operator.MutationGate
            lit 0UL 6 ==> i) ])

          Operator.GeneRecombineGene, (fun () ->
        // gene = bounded(1) = 0 consumed; whole-gene crossover of gene 0.
        lit 0UL 6 ==> fromR
        lit (uint64 geneLen) 6 ==> endR
        lit 0UL 6 ==> i
        lit 2UL 2 ==> xret
        st.Goto Operator.RecombineSweep)

          Operator.RecombineSweep, (fun () ->
        ifElse [(eq i endR, fun () ->
            st.Goto Operator.RecombineConstants
            lit 0UL 6 ==> k); (otherwise, fun () ->
            memWrite symC i (memRead symS i) (lit 1UL 1)
            memWrite symS i (memRead symC i) (lit 1UL 1)
            i + lit 1UL 6 ==> i) ])

          Operator.RecombineConstants, (fun () ->
        let whole = eq fromR (lit 0UL 6) &&& eq endR (lit (uint64 geneLen) 6)

        let exitSt =
            mux (eq xret (lit 0UL 2)) (st.Code Operator.TwoPointGate) (mux (eq xret (lit 1UL 2)) (st.Code Operator.GeneRecombineGate) (st.Code Operator.MutationGate))

        ifElse [(whole &&& bnot (eq k (lit (uint64 constCount) 6)), fun () ->
            memWrite constC k (memRead constS k) (lit 1UL 1)
            memWrite constS k (memRead constC k) (lit 1UL 1)
            k + lit 1UL 6 ==> k); (otherwise, fun () ->
            exitSt ==> st.Value
            lit 0UL 6 ==> i
            lit 0UL 6 ==> k) ])

          // Mutation.
          Operator.MutationGate, (fun () ->
        ifElse [(eq i (lit (uint64 geneLen) 6), fun () ->
            st.Goto Operator.ConstantReplaceGate
            lit 0UL 6 ==> k); (hitB, fun () ->
                mux (lt i (lit (uint64 headLen) 6)) (lit (uint64 symCount) 21) (lit (uint64 terminalSet.Length) 21)
                ==> bndReg

                st.Goto Operator.MutationSymbol); (otherwise, fun () -> i + lit 1UL 6 ==> i) ])

          Operator.MutationSymbol, (fun () ->
        memWrite symC i (mux (lt i (lit (uint64 headLen) 6)) headSym tailSym) (lit 1UL 1)
        i + lit 1UL 6 ==> i
        st.Goto Operator.MutationGate)

          Operator.ConstantReplaceGate, (fun () ->
        ifElse [(eq k (lit (uint64 constCount) 6), fun () ->
            st.Goto Operator.CreepGate
            lit 0UL 6 ==> k); (hitB, fun () ->
                spanW ==> bndReg
                st.Goto Operator.ConstantReplaceDraw); (otherwise, fun () -> k + lit 1UL 6 ==> k) ])

          Operator.ConstantReplaceDraw, (fun () ->
        memWrite constC k (cat (lit 0UL 11) bnd21 - rates.rangeFx) (lit 1UL 1)
        k + lit 1UL 6 ==> k
        st.Goto Operator.ConstantReplaceGate)

          // Creep: gate, 12-word Irwin–Hall accumulate, signed apply. The
          // `(s * sigmaFx) >> 32` of the spec is 64-bit WRAPPED math and the
          // shifted value always fits 32 bits, so fxSat is an identity there:
          // sign-extend both operands to 64-bit bit patterns, multiply (the low 64
          // bits equal the signed product mod 2^64), take bits [63:32].
          Operator.CreepGate, (fun () ->
        ifElse [(eq k (lit (uint64 constCount) 6), fun () -> st.Goto Operator.InversionGate); (hitB, fun () ->
            lit 0UL 36 ==> sum
            lit 0UL 6 ==> i
            st.Goto Operator.CreepAccumulate); (otherwise, fun () -> k + lit 1UL 6 ==> k) ])

          Operator.CreepAccumulate, (fun () ->
        sum + cat (lit 0UL 4) word ==> sum

        ifElse [(eq i (lit 11UL 6), fun () -> st.Goto Operator.CreepApply); (otherwise, fun () -> i + lit 1UL 6 ==> i) ])

          Operator.CreepApply, (fun () ->
        memWrite constC k creepRes (lit 1UL 1)
        k + lit 1UL 6 ==> k
        st.Goto Operator.CreepGate)

          // Inversion (2-cycle ping-pong swaps; single write port on symC).
          Operator.InversionGate, (fun () ->
        ifElse [(hitB, fun () ->
            lit 1UL 21 ==> bndReg
            st.Goto Operator.InversionGene); (otherwise, fun () -> st.Goto Operator.InsertionGate) ])

          Operator.InversionGene, (fun () ->
        // gene draw consumed (gene 0)
        lit (uint64 (headLen - 1)) 21 ==> bndReg
        st.Goto Operator.InversionStart)

          Operator.InversionStart, (fun () ->
        bnd6 ==> sA
        cat (lit 0UL 15) (lit (uint64 (headLen - 1)) 6 - bnd6) ==> bndReg
        st.Goto Operator.InversionEnd)

          Operator.InversionEnd, (fun () ->
        sA ==> lo
        sA + lit 1UL 6 + bnd6 ==> hi
        st.Goto Operator.InversionSwapRead)

          Operator.InversionSwapRead, (fun () ->
        memRead symC lo ==> tmp
        memWrite symC lo (memRead symC hi) (lit 1UL 1)
        st.Goto Operator.InversionSwapWrite)

          Operator.InversionSwapWrite, (fun () ->
        memWrite symC hi tmp (lit 1UL 1)

        ifElse [(lt (lo + lit 2UL 6) hi, fun () ->
            lo + lit 1UL 6 ==> lo
            hi - lit 1UL 6 ==> hi
            st.Goto Operator.InversionSwapRead); (otherwise, fun () -> st.Goto Operator.InsertionGate) ])

          // IS transposition.
          Operator.InsertionGate, (fun () ->
        ifElse [(hitB, fun () ->
            lit (uint64 (min maxTransposon (headLen - 1))) 21 ==> bndReg
            st.Goto Operator.InsertionLength); (otherwise, fun () -> st.Goto Operator.RootInsertionGate) ])

          Operator.InsertionLength, (fun () ->
        bnd6 + lit 1UL 6 ==> lenR
        lit 1UL 21 ==> bndReg
        st.Goto Operator.InsertionSourceGene)

          Operator.InsertionSourceGene, (fun () ->
        // source-gene draw consumed (gene 0); next bound = geneLen − length + 1.
        cat (lit 0UL 15) (lit (uint64 (geneLen + 1)) 6 - lenR) ==> bndReg
        st.Goto Operator.InsertionSourceOffset)

          Operator.InsertionSourceOffset, (fun () ->
        bnd6 ==> srcR
        lit 1UL 21 ==> bndReg
        st.Goto Operator.InsertionTargetGene)

          Operator.InsertionTargetGene, (fun () ->
        cat (lit 0UL 15) (lit (uint64 headLen) 6 - lenR) ==> bndReg
        st.Goto Operator.InsertionTarget)

          Operator.InsertionTarget, (fun () ->
        bnd6 + lit 1UL 6 ==> tgtR
        lit 0UL 6 ==> i
        lit 0UL 1 ==> risRet
        st.Goto Operator.TransposonCapture)

          // Shared capture / shift / insert (IS: src/tgt as drawn; RIS: src=sA, tgt=0).
          Operator.TransposonCapture, (fun () ->
        for r in 0 .. maxTransposon - 1 do
            If (eq i (lit (uint64 r) 6)) (fun () -> memRead symC (srcR + i) ==> run[r])

        ifElse [(eq (i + lit 1UL 6) lenR, fun () ->
            lit (uint64 (headLen - 1)) 6 ==> i
            st.Goto Operator.TransposonShift); (otherwise, fun () -> i + lit 1UL 6 ==> i) ])

          Operator.TransposonShift, (fun () ->
        ifElse [(lt i (tgtR + lenR), fun () ->
            lit 0UL 6 ==> i
            st.Goto Operator.TransposonInsert); (otherwise, fun () ->
            memWrite symC i (memRead symC (i - lenR)) (lit 1UL 1)
            i - lit 1UL 6 ==> i) ])

          Operator.TransposonInsert, (fun () ->
        memWrite symC (tgtR + i) runSel (lit 1UL 1)

        ifElse [(eq (i + lit 1UL 6) lenR, fun () ->
            mux risRet (st.Code Operator.Done) (st.Code Operator.RootInsertionGate) ==> st.Value); (otherwise, fun () -> i + lit 1UL 6 ==> i) ])

          // RIS transposition (root insertion; geneCount=1 ⇒ gene transposition elides).
          Operator.RootInsertionGate, (fun () ->
        ifElse [(hitB, fun () ->
            lit 1UL 21 ==> bndReg
            st.Goto Operator.RootInsertionGene); (otherwise, fun () -> st.Goto Operator.Done) ])

          Operator.RootInsertionGene, (fun () ->
        // gene draw consumed
        lit (uint64 headLen) 21 ==> bndReg
        st.Goto Operator.RootInsertionStart)

          Operator.RootInsertionStart, (fun () ->
        bnd6 ==> sA
        st.Goto Operator.RootInsertionScan)

          Operator.RootInsertionScan, (fun () ->
        ifElse [(eq sA (lit (uint64 headLen) 6), fun () -> st.Goto Operator.Done); (scanTerm, fun () -> sA + lit 1UL 6 ==> sA); (otherwise, fun () ->
            cat (lit 0UL 15) (mux (lt dHead (lit (uint64 maxTransposon) 6)) dHead (lit (uint64 maxTransposon) 6))
            ==> bndReg

            st.Goto Operator.RootInsertionLength) ])

          Operator.RootInsertionLength, (fun () ->
        bnd6 + lit 1UL 6 ==> lenR
        sA ==> srcR
        lit 0UL 6 ==> tgtR
        lit 0UL 6 ==> i
        lit 1UL 1 ==> risRet
        st.Goto Operator.TransposonCapture) ]

    {| childSym = memRead symC rdSaddr
       childConst = memRead constC rdCaddr
       busy = busy
       finished = finished |}

/// Transposon length limit every breeder here builds its operator engine with.
/// Three symbols, which is what the GEP literature's IS/RIS operators use and
/// what every call in this file has always passed.
let breederMaxTransposon = 3

/// The deployed v1 geometry, written once for the three walk designs that
/// share it: head 8 / gene 17, 3 variables + 4 constants, the full function
/// set. Three copies of `17 8 4` beside three copies of the same terminal
/// array was three chances for the walks to stop testing the same chromosome.
let v1Genome =
    { functionSet = Opcodes.functionSet
      terminalSet =
        [| Opcodes.variable 0
           Opcodes.variable 1
           Opcodes.variable 2
           Opcodes.constant 0
           Opcodes.constant 1
           Opcodes.constant 2
           Opcodes.constant 3 |]
      geneLen = 17
      headLen = 8
      constCount = 4 }

/// The operator engine at ports, at the deployed v1 geometry: head 8 / gene
/// 17, 3 variables + 4 constants, the full function set. The oracle check
/// breeds against `hwBreedOffspring` on shared seeds.
let operatorEngineWalk =
    defModule
        "GepOperatorEngineWalk"
        (fun p ->
            (p.inPort "start" 1,
             List.init 4 (fun idx -> p.inPort $"s{idx}" 32),
             { onePoint = p.inPort "th_1p" 32
               twoPoint = p.inPort "th_2p" 32
               geneRecomb = p.inPort "th_gr" 32
               mutation = p.inPort "th_mut" 32
               constReplace = p.inPort "th_cr" 32
               creep = p.inPort "th_creep" 32
               inversion = p.inPort "th_inv" 32
               isTrans = p.inPort "th_is" 32
               risTrans = p.inPort "th_ris" 32
               sigmaFx = p.inPort "sigma_fx" 32
               rangeFx = p.inPort "range_fx" 32 },
             { ldSym = p.inPort "ld_sym" 1
               ldPar = p.inPort "ld_par" 1
               ldAddr = p.inPort "ld_addr" 6
               ldSdata = p.inPort "ld_sdata" 8
               ldConst = p.inPort "ld_const" 1
               ldCdata = p.inPort "ld_cdata" 32 },
             p.inPort "rd_saddr" 6,
             p.inPort "rd_caddr" 6,
             p.outPort "child_sym" 8,
             p.outPort "child_const" 32,
             p.outPort "busy" 1,
             p.outPort "done" 1))
        (fun (start, sIn, rates, load, rdSaddr, rdCaddr, childSym, childConst, busy, doneOut) ->
            let engine =
                gepOperatorEngine
                    v1Genome
                    breederMaxTransposon
                    "oe"
                    start
                    sIn
                    rates
                    load
                    rdSaddr
                    rdCaddr

            engine.childSym ==> childSym
            engine.childConst ==> childConst
            engine.busy ==> busy
            engine.finished ==> doneOut)

/// The compiler at ports: a host-loadable gene buffer feeding the FSM, the
/// record captured in a mem the Sim's PeekMem reads back — the walk the
/// oracle check and the differential both drive.
let karvaCompilerWalk =
    defModule
        "GepKarvaCompilerWalk"
        (fun p ->
            (p.inPort "load_en" 1,
             p.inPort "load_addr" 6,
             p.inPort "load_data" 8,
             p.inPort "start" 1,
             p.outPort "busy" 1,
             p.outPort "done" 1,
             p.outPort "n_instr" 6))
        (fun (loadEn, loadAddr, loadData, start, busy, doneOut, nInstr) ->
            let geneMem = distributedMem "gene_mem" 6 8
            memWrite geneMem loadAddr loadData loadEn

            let symData = wire "symData" 8
            let compiler = gepKarvaCompiler "kc" start symData
            memRead geneMem compiler.symAddr ==> symData

            let recMem = distributedMem "rec_mem" 6 32
            memWrite recMem compiler.recAddr compiler.recData compiler.recEn

            compiler.busy ==> busy
            compiler.finished ==> doneOut
            compiler.nInstr ==> nInstr)

/// The plain ALU at ports — the P4 ladder's first rung.
let gepAluPlain =
    defModule
        "GepAluPipelined32"
        (fun p -> (p.inPort "op" 8, p.inPort "a" 32, p.inPort "b" 32, p.outPort "result" 32))
        (fun (op, a, b, result) -> gepAluPipelined false "alu" op a b ==> result)

/// The withDiv ALU at ports — the divide arm beside the delay chains.
let gepAluDiv =
    defModule
        "GepAluPipelinedDiv32"
        (fun p -> (p.inPort "op" 8, p.inPort "a" 32, p.inPort "b" 32, p.outPort "result" 32))
        (fun (op, a, b, result) -> gepAluPipelined true "alu" op a b ==> result)

/// Combinational select of variable port idx — a live vector, not a memory.
let private varMux (vars: Expr list) (idx: Expr) =
    let idxW = width idx
    let mutable e = List.last vars

    for k in vars.Length - 2 .. -1 .. 0 do
        e <- mux (eq idx (lit (uint64 k) idxW)) vars[k] e

    e

/// The packed-instruction fields, split by slicing — the layout of
/// `MicroProgram.packInstruction`.
let private decodeWord (prefix: string) (instr: Expr) (addrW: int) =
    let op = wire $"{prefix}_op" 8
    slice 7 0 instr ==> op
    let aSrc = wire $"{prefix}_aSrc" 2
    slice 9 8 instr ==> aSrc
    let aIdx = wire $"{prefix}_aIdx" addrW
    slice (9 + addrW) 10 instr ==> aIdx
    let bSrc = wire $"{prefix}_bSrc" 2
    slice 17 16 instr ==> bSrc
    let bIdx = wire $"{prefix}_bIdx" addrW
    slice (17 + addrW) 18 instr ==> bIdx
    {| op = op; aSrc = aSrc; aIdx = aIdx; bSrc = bSrc; bIdx = bIdx |}

/// Words per packed individual record: header + padded program + constants,
/// 16 B-aligned.
let gepUnitIndivWords (capacity: int) (constCount: int) = (1 + capacity + constCount + 3) / 4 * 4

/// The dispatcher's fill bus: one 128-bit beat plus the selects saying which
/// bank memory it lands in. `commit` closes the record and flips the fill
/// bank.
type GepUnitFillBus =
    { beat: Expr
      indivEn: Expr
      indivAddr: Expr
      commit: Expr
      unitId: Expr }

/// How a unit engine gets its fitness cases: queue mode rides the fill beat
/// bus into the double-buffered run bank; resident mode holds ONE
/// epoch-loaded table across every bank flip, loaded over a broadcast bus
/// (the WarpCPU cluster lane).
type GepUnitCaseFill =
    | QueueCases of en: Expr * sel: Expr * addr: Expr
    | ResidentCases of ldCase: Expr * caseAddr: Expr * caseField: Expr * caseData: Expr

/// The DIV link's operand and result ports — the shape both ends agree on, so
/// the lane's socket and the pod build their layouts from one description.
let gepDivIssuePorts = [ ("a", 32); ("b", 32) ]
let gepDivWritebackPorts = [ ("q", 32) ]

/// A lane's divide, wired. This is `FuSharing` after the cluster has resolved
/// it: `Pooled` arrives carrying the pod's writeback stream, so a lane cannot
/// be told it is pooled and then left without a link.
[<NoEquality; NoComparison>]
type GepLaneDiv =
    /// No divide arm at all — a DIV opcode falls through to the terminal
    /// default, exactly as the software `applyOp` does for an unknown op.
    | NoDiv
    /// `FuSharing.PerLane`: the arm lives in this lane's ALU pipeline and the
    /// other arms delay-align to it. No socket, no FIFO, no stall — a result
    /// lands in a statically known slot of the barrel schedule, so latency
    /// costs threads (context) and nothing costs throughput.
    | ResidentDiv
    /// `FuSharing.Pooled`: the ALU drops the arm and DIV ops ship to a shared
    /// pod over a tagged socket, the pod's writeback stream coming back here.
    | PodDiv of writeback: Stream<FuBeat>

/// The shared reciprocal-divide pod: `warpFu` around one unchanged divide
/// core, servicing every pooled lane's socket. The socket's thread id rides
/// through as the routing tag; `a`/`b` feed the core and the result is its
/// own `q`. One divider plus one reciprocal ROM replaces the per-lane
/// resident arms.
///
/// Whether that trade pays is `FuSharing`'s doc comment, and for this core the
/// measured answer is no — a divider is cheap, and cheap FUs want `PerLane`.
/// The pod exists because the ratio is a knob: the second warped unit (sqrt,
/// CORDIC — big and rare) is this call with a different core.
let gepDivPod (prefix: string) (issues: Stream<FuBeat> list) : Stream<FuBeat> list =
    warpFu prefix gepDivWritebackPorts
        (fun operands ->
            match operands with
            | [ a; b ] ->
                // The standalone FU's shape: input registers keep the issue
                // arbiter's mux cone out of the arm's stage-1 logic, and the
                // output register closes the combinational quotient cone.
                let s0a = reg $"{prefix}_core_s0_a" 32
                let s0b = reg $"{prefix}_core_s0_b" 32
                a ==> s0a
                b ==> s0b
                let q = reg $"{prefix}_core_q" 32
                divRecipArm s0a s0b $"{prefix}_core_dv" ==> q
                // Input registers + the arm + the output register, which is
                // what `gepDivLatency` counts — stated here, where the stages
                // it counts are, rather than handed to the wrapper.
                [ q ], gepDivLatency
            | _ -> failwith $"gepDivPod '%s{prefix}': the divide core takes two operands")
        issues

/// The work-queue evaluation engine: the barrel (C-slow) datapath fed from
/// the double-buffered 4-lane beat-wide unit banks. Programs are fetched by
/// sync read straight from the bank; constants copy into a register file
/// during the per-individual CFG state; the config header latches the same
/// way — so SOURCE completes in one cycle and issue→writeback latency stays
/// `gepAluLatency withDiv + 2`.
///
/// Unit layout in the bank (16 B-aligned): each case is 2 beats (vars in
/// beat 0's four lanes, target in beat 1 lane 0); the individual block lands
/// verbatim in the 4-lane bank, one record per `gepUnitIndivWords` stride:
/// word 0 = config header (`nInstr | outSrc<<8 | outIdx<<10`), words
/// 1..capacity = packed instructions, then the constants.
///
/// Lifecycle: IDLE (run bank valid) → CFG (latch header + copy constants) →
/// ISSUE (the barrel wave loop, pc bounded by this individual's nInstr) →
/// DRAIN (writebacks land) → EMIT (present `(fit, unit, m)` until taken),
/// then the next individual or bank release.
///
/// **The divide is a sharing ratio** (`GepLaneDiv`), and the two settings cost
/// different things here. `ResidentDiv` changes nothing below: the arm is in
/// the ALU and its result rides the one writeback path like every other op.
/// `PodDiv` adds the socket — SOURCE intercepts each DIV (suppressing its ALU
/// writeback) into an `nThreads`-deep issue FIFO drained through the tagged
/// issue stream, and when the wave wraps the scheduler parks in WAITR until
/// every writeback beat has landed in the results memory. Writebacks borrow
/// the results write port on cycles the ALU is not using it. The barrel issues
/// one pc across all threads back to back, so a DIV instruction is a burst of
/// `nThreads` offloads and the FIFO absorbs a whole wave — pod backpressure
/// never stalls the barrel mid-wave. **Writeback order is free**: any
/// interleaving and latency is correct, because the tag says which thread a
/// result belongs to. Values are `fxDivRecip` either way, so only timing
/// differs between the settings and fitnesses stay bit-identical.
/// The unit engine's wave states. Qualified: every machine in this file has an
/// `Idle`.
[<RequireQualifiedAccess>]
type private Wave =
    | Idle
    /// Latch the individual's record header and constants.
    | Configure
    /// One instruction per thread per cycle, across the wave.
    | Issue
    /// Let the pipeline empty before the fitness accumulator is read.
    | Drain
    | Emit
    /// Pooled divide only: the wave's offloads have not all returned.
    | WaitRemote

/// How big one evaluation unit is: the program it can hold, the fitness cases
/// it can hold, how many threads share its pipeline, and how much of the
/// individual store one record may span.
///
/// Six numbers that arrived positionally — `gepUnitEngine 32 4 4 8 64 512` is
/// six adjacent bare ints and was the largest such call in this repository.
/// Four of them are powers of two, so a transposition passes every
/// `log2Exact` check on the way in and shows up as a lane that quietly scores
/// the wrong thing.
type GepUnitShape =
    { /// Instructions one compiled program may reach.
      capacity: int
      /// Constants in the individual's record.
      constCount: int
      /// Independent variables the terminal set may name.
      varCount: int
      /// Threads interleaved through the one ALU cone.
      nThreads: int
      /// Fitness cases the unit can hold at once.
      caseCapacity: int
      /// Words of the individual store one record may span.
      indivCapacity: int }

let gepUnitEngine
    (shape: GepUnitShape)
    (div: GepLaneDiv)
    (prefix: string)
    (fill: GepUnitFillBus)
    (caseFill: GepUnitCaseFill)
    (nCases: Expr)
    (mCount: Expr)
    : {| res: Stream<Expr * Expr * Expr>
         canFill: Expr
         idle: Expr
         divIssue: Stream<FuBeat> option |} =
    let capacity, constCount, varCount = shape.capacity, shape.constCount, shape.varCount
    let nThreads, caseCapacity, indivCapacity = shape.nThreads, shape.caseCapacity, shape.indivCapacity

    let residentDiv =
        match div with
        | ResidentDiv -> true
        | NoDiv
        | PodDiv _ -> false

    let podWriteback =
        match div with
        | PodDiv wb -> Some wb
        | NoDiv
        | ResidentDiv -> None

    let latency = gepAluLatency residentDiv + 2

    let log2p n =
        if n <= 0 || n &&& (n - 1) <> 0 then failwith $"power of two required, got %d{n}"
        System.Numerics.BitOperations.Log2(uint n) |> int

    if varCount < 1 || varCount > 4 then
        failwith $"case beat 0 holds the vars; varCount must be 1..4, got %d{varCount}"

    // The cone and the interleave, together: `barrel` owns the
    // `nThreads > latency` invariant both barrels used to assert separately,
    // and the depth the writeback context rides.
    let cone = barrel latency nThreads

    let addrW = log2p capacity
    let threadW = log2p nThreads
    let slotW = threadW + addrW
    let constW = max 1 (log2p constCount)
    let caseAddrW = log2p caseCapacity
    let caseCountW = caseAddrW + 1
    let indivLineW = log2p indivCapacity - 2

    if indivLineW < addrW - 1 then
        failwith "indivCapacity too small for the program line index"

    let indivWords = gepUnitIndivWords capacity constCount
    let linesPerIndiv = indivWords / 4
    // CFG fetch plan: item 0 = the header at word 0, items 1..constCount =
    // the constants at words 1+capacity+c — each an elaboration constant.
    let cfgWordOff = [ 0; yield! [ for c in 0 .. constCount - 1 -> 1 + capacity + c ] ]
    let cfgSteps = constCount + 1
    let cfgStepW = 32 - System.Numerics.BitOperations.LeadingZeroCount(uint (cfgSteps + 1))

    let resident =
        match caseFill with
        | ResidentCases _ -> true
        | QueueCases _ -> false

    // ---- Memories ----
    let caseBankW = if resident then caseAddrW else caseAddrW + 1
    // One memory where there were varCount + 1: the fitness-case variables in
    // lanes 0..varCount-1 and the target in the top lane. The split existed to
    // buy per-field write enables, which is exactly what a lane mask is — so
    // the mask replaced the banking. `memWriteMasked` emits the byte-enable
    // template Vivado infers as one block RAM with per-lane write ports.
    let caseLanes = varCount + 1
    // **LUTRAM, deliberately.** 2026-08-19: leaving this to the tool put it in
    // block RAM, where 256 x 160 bits needs several cascaded RAMB36 per lane (a
    // block is 36 Kb with a 72-bit port) — and on silicon the host's case loads
    // did not land, so every lane evaluated its programs against an all-zero
    // table. The sim, Verilator and firtool all agreed the RTL was right, which
    // it is; the failure was in what the RTL became. Reproduced exactly: a Sim
    // run with the cases never loaded produces the board's fitness values digit
    // for digit.
    //
    // Distributed keeps the five 32-bit lanes in LUTs, which is what the five
    // separate per-variable memories this replaced were, and costs about 640
    // LUTs a lane.
    let caseMem = distributedMem $"{prefix}_caseMem" caseBankW (caseLanes * 32)
    // One 128-bit memory where there were four 32-bit lanes. The four were
    // always written together under one enable and always read together at one
    // address, then one was selected — which is one wide word and a slice, and
    // never needed to be four arrays at all.
    let indivMem = distributedMem $"{prefix}_indivMem" (indivLineW + 1) 128
    let results = distributedMem $"{prefix}_results" slotW 32
    let constRegs = distributedMem $"{prefix}_constRegs" constW 32

    // ---- Fill side ----
    let bankValid0 = regBit $"{prefix}_bankValid0"
    let bankValid1 = regBit $"{prefix}_bankValid1"
    let unitId0 = reg $"{prefix}_unitId0" 32
    let unitId1 = reg $"{prefix}_unitId1" 32
    let fillBank = regBit $"{prefix}_fillBank"
    let runBank = regBit $"{prefix}_runBank"

    (match caseFill with
     | ResidentCases (ldCase, caseAddr, caseField, caseData) ->
         // Broadcast epoch load: the bus value goes to every lane and the mask
         // says which one takes it — `caseField` decoded to at most one bit. A
         // field beyond the last lane decodes to an all-zero mask and writes
         // nothing, exactly as it addressed no memory before.
         let replicated = List.fold (fun acc _ -> cat acc caseData) caseData [ 1 .. caseLanes - 1 ]

         let laneHit f = eq caseField (lit (uint64 f) 8)
         let mask = List.fold (fun acc f -> cat (laneHit f) acc) (laneHit 0) [ 1 .. caseLanes - 1 ]

         memWriteMasked caseMem caseAddr replicated ldCase mask
     | QueueCases (en, sel, addr) ->
         // Two masked writes, mutually exclusive by `sel`, folding to one
         // priority-muxed site: the beat fills the var lanes, or its low word
         // lands in the target lane. Each write's data covers the whole word
         // and its mask says which lanes are meant.
         let total = caseLanes * 32

         let varData =
             if width fill.beat >= total then
                 slice (total - 1) 0 fill.beat
             else
                 cat (lit 0UL (total - width fill.beat)) fill.beat

         memWriteMasked caseMem (cat fillBank addr) varData (en &&& bnot sel) (lit ((1UL <<< varCount) - 1UL) caseLanes)

         let targetData = cat (slice 31 0 fill.beat) (lit 0UL (varCount * 32))
         memWriteMasked caseMem (cat fillBank addr) targetData (en &&& sel) (lit (1UL <<< varCount) caseLanes))

    memWrite indivMem (cat fillBank fill.indivAddr) fill.beat fill.indivEn

    If fill.commit (fun () ->
        ifElse [(bnot fillBank, fun () ->
            lit 1UL 1 ==> bankValid0
            fill.unitId ==> unitId0
            lit 1UL 1 ==> fillBank); (otherwise, fun () ->
            lit 1UL 1 ==> bankValid1
            fill.unitId ==> unitId1
            lit 0UL 1 ==> fillBank) ])

    let canFill = wireBit $"{prefix}_canFill"
    bnot (mux fillBank bankValid1 bankValid0) ==> canFill

    // ---- Run side ----
    let respReady = wireBit $"{prefix}_res_ready"
    registerStreamReady respReady

    // WAITREMOTE exists only where there is a divide pod to offload to — with
    // none, nothing transitions there, and the machine refuses a state with no
    // way in. It is last, so the other five keep codes 0..4 either way.
    let st =
        machine
            $"{prefix}_st"
            ([ Wave.Idle; Wave.Configure; Wave.Issue; Wave.Drain; Wave.Emit ]
             @ (if Option.isSome podWriteback then [ Wave.WaitRemote ] else []))

    let iBaseLine = reg $"{prefix}_iBaseLine" indivLineW
    let cfgStep = reg $"{prefix}_cfgStep" cfgStepW
    let waveBase = reg $"{prefix}_waveBase" caseCountW
    let drainCtr = reg $"{prefix}_drainCtr" 4
    let nInstrR = reg $"{prefix}_nInstrR" addrW

    // The three nested loops of the barrel, as counters rather than as nesting:
    // a thread per cycle, a program counter per wave, an individual per bank.
    // Each `wrap` is the boundary its enclosing loop used to test for by hand,
    // which is why the ISSUE block below is two levels shallower than it was —
    // and why the pooled-divide socket can say `thread.wrap` instead of
    // restating the wave-boundary compare.
    let thread = counter $"{prefix}_threadCtr" nThreads (st.Is Wave.Issue)
    let pc = counterTo $"{prefix}_pcCtr" nInstrR thread.wrap
    let individual = counterTo $"{prefix}_mIdx" (mCount - lit 1UL 8) (st.Is Wave.Emit &&& respReady)
    let outSrcR = reg $"{prefix}_outSrcR" 2
    let outIdxR = reg $"{prefix}_outIdxR" addrW
    let resValid = regBit $"{prefix}_resValid"
    let resFit = reg $"{prefix}_resFit" 64
    let resUnit = reg $"{prefix}_resUnit" 32
    let resM = reg $"{prefix}_resM" 8
    let fitAcc = reg $"{prefix}_fitAcc" 64

    // ---- Pooled-divide socket state. Both FIFOs are a whole wave deep, so a
    // wave's offloads always fit and pod backpressure cannot stall the barrel
    // mid-wave. `pending` counts offloads still in flight — issued but not yet
    // written back — and is what WAITR waits on. ----
    let socket =
        podWriteback
        |> Option.map (fun wb ->
            {| writeback = wb
               remoteWave = regBit $"{prefix}_divRemoteWave"
               pcRemote = reg $"{prefix}_divPcRemote" addrW
               pending = reg $"{prefix}_divPending" (threadW + 1)
               waveSettle = reg $"{prefix}_divWaveSettle" 2
               issueThread = distributedMem $"{prefix}_divIssueThread" threadW threadW
               issueA = distributedMem $"{prefix}_divIssueA" threadW 32
               issueB = distributedMem $"{prefix}_divIssueB" threadW 32
               issueWrPtr = reg $"{prefix}_divIssueWrPtr" threadW
               issueRdPtr = reg $"{prefix}_divIssueRdPtr" threadW
               issueCount = reg $"{prefix}_divIssueCount" (threadW + 1)
               wbThread = distributedMem $"{prefix}_divWbThread" threadW threadW
               wbResult = distributedMem $"{prefix}_divWbResult" threadW 32
               wbWrPtr = reg $"{prefix}_divWbWrPtr" threadW
               wbRdPtr = reg $"{prefix}_divWbRdPtr" threadW
               wbCount = reg $"{prefix}_divWbCount" (threadW + 1) |})

    let validSel = wireBit $"{prefix}_validSel"
    mux runBank bankValid1 bankValid0 ==> validSel
    let unitSel = wire $"{prefix}_unitSel" 32
    mux runBank unitId1 unitId0 ==> unitSel

    // ---- Bank read port: CFG items and program fetch share it ----
    let pcw = wire $"{prefix}_pcw" (addrW + 1)
    cat (lit 0UL 1) pc.count + lit 1UL (addrW + 1) ==> pcw
    let pcwLine = wire $"{prefix}_pcwLine" (addrW - 1)
    slice addrW 2 pcw ==> pcwLine

    let stepMux (values: int list) (w: int) =
        let mutable e = lit (uint64 (List.last values)) w

        for k in values.Length - 2 .. -1 .. 0 do
            e <- mux (eq cfgStep (lit (uint64 k) cfgStepW)) (lit (uint64 values[k]) w) e

        e

    let cfgLine = wire $"{prefix}_cfgLine" indivLineW
    stepMux (List.map (fun w -> w / 4) cfgWordOff) indivLineW ==> cfgLine
    let cfgLane = wire $"{prefix}_cfgLane" 2
    stepMux (List.map (fun w -> w % 4) cfgWordOff) 2 ==> cfgLane

    let isCfg = st.Is Wave.Configure
    let lineOff = wire $"{prefix}_lineOff" indivLineW

    mux isCfg cfgLine (if indivLineW = addrW - 1 then pcwLine else cat (lit 0UL (indivLineW - addrW + 1)) pcwLine)
    ==> lineOff

    let bankLine = wire $"{prefix}_bankLine" indivLineW
    iBaseLine + lineOff ==> bankLine
    let fetchLane = wire $"{prefix}_fetchLane" 2
    mux isCfg cfgLane (slice 1 0 pcw) ==> fetchLane
    let indivWord = (memReadPort indivMem (cat runBank bankLine)).data
    let indivLanes = [ for l in 0 .. 3 -> slice (l * 32 + 31) (l * 32) indivWord ]
    let laneD = reg $"{prefix}_laneD" 2
    fetchLane ==> laneD
    let bankData = wire $"{prefix}_bankData" 32

    mux
            (eq laneD (lit 0UL 2))
            indivLanes[0]
            (mux (eq laneD (lit 1UL 2)) indivLanes[1] (mux (eq laneD (lit 2UL 2)) indivLanes[2] indivLanes[3]))
    ==> bankData

    // ---- CFG captures (data for step s arrives at cfgStep = s+1) ----
    If (isCfg &&& eq cfgStep (lit 1UL cfgStepW)) (fun () ->
        slice (addrW - 1) 0 bankData ==> nInstrR
        slice 9 8 bankData ==> outSrcR
        slice (9 + addrW) 10 bankData ==> outIdxR)

    let constWrIdx = wire $"{prefix}_constWrIdx" cfgStepW
    cfgStep - lit 2UL cfgStepW ==> constWrIdx

    memWrite
        constRegs
        (slice (constW - 1) 0 constWrIdx)
        bankData
        (isCfg &&& bnot (lt cfgStep (lit 2UL cfgStepW)))

    // ---- Scheduler FSM ----
    let issuingB = st.Is Wave.Issue
    let isInstr = wireBit $"{prefix}_isInstr"
    (issuingB &&& lt pc.count nInstrR) ==> isInstr
    let isFit = wireBit $"{prefix}_isFit"
    (issuingB &&& eq pc.count nInstrR) ==> isFit
    let latchFitB = st.Is Wave.Drain &&& eq drainCtr (lit (uint64 latency) 4)

    st.If Wave.Idle (fun () ->
        If validSel (fun () ->
            st.Goto Wave.Configure
            lit 0UL cfgStepW ==> cfgStep
            lit 0UL 8 ==> individual.count
            lit 0UL indivLineW ==> iBaseLine
            lit 0UL threadW ==> thread.count
            lit 0UL addrW ==> pc.count
            lit 0UL caseCountW ==> waveBase
            lit 0UL 4 ==> drainCtr))

    If isCfg (fun () ->
        ifElse [(eq cfgStep (lit (uint64 cfgSteps) cfgStepW), fun () -> st.Goto Wave.Issue); (otherwise, fun () -> cfgStep + lit 1UL cfgStepW ==> cfgStep) ])

    // A finished program either advances to the next block of cases or, with
    // none left, drains.
    If pc.wrap (fun () ->
        ifElse [(lt (waveBase + lit (uint64 nThreads) caseCountW) nCases, fun () ->
            waveBase + lit (uint64 nThreads) caseCountW ==> waveBase); (otherwise, fun () ->
            st.Goto Wave.Drain
            lit 0UL 4 ==> drainCtr
            lit 0UL caseCountW ==> waveBase) ])

    // A wave that offloaded holds at its wrap — pc has already advanced —
    // until every result has come back. Placed after the ISSUE block so it
    // overrides that block's transition; the fitness pass is a MUL, so this
    // can never collide with the drain edge.
    socket
    |> Option.iter (fun s ->
        If (thread.wrap &&& s.remoteWave) (fun () ->
            st.Goto Wave.WaitRemote
            lit 0UL 2 ==> s.waveSettle)

        st.If Wave.WaitRemote (fun () ->
            If (bnot (eq s.waveSettle (lit 3UL 2))) (fun () -> s.waveSettle + lit 1UL 2 ==> s.waveSettle)

            // Two settle cycles guarantee the wave's last SOURCE-stage enqueue
            // has been counted into `pending` before the zero test believes it.
            If (eq s.pending (lit 0UL (threadW + 1)) &&& bnot (lt s.waveSettle (lit 2UL 2))) (fun () ->
                st.Goto Wave.Issue
                lit 0UL 1 ==> s.remoteWave)))

    st.If Wave.Drain (fun () ->
        ifElse [(eq drainCtr (lit (uint64 latency) 4), fun () ->
            lit 1UL 1 ==> resValid
            fitAcc ==> resFit
            unitSel ==> resUnit
            individual.count ==> resM
            st.Goto Wave.Emit); (otherwise, fun () -> drainCtr + lit 1UL 4 ==> drainCtr) ])

    st.If Wave.Emit (fun () ->
        If respReady (fun () ->
            lit 0UL 1 ==> resValid

            ifElse [(individual.wrap, fun () ->
                ifElse [(bnot runBank, fun () ->
                    lit 0UL 1 ==> bankValid0
                    lit 1UL 1 ==> runBank); (otherwise, fun () ->
                    lit 0UL 1 ==> bankValid1
                    lit 0UL 1 ==> runBank) ]

                st.Goto Wave.Idle); (otherwise, fun () ->
                iBaseLine + lit (uint64 linesPerIndiv) indivLineW ==> iBaseLine
                lit 0UL cfgStepW ==> cfgStep
                st.Goto Wave.Configure) ]))

    // ---- Datapath: SCHED presents case + program addresses; SOURCE decodes
    // and sources operands against the arrived data ----
    let caseIdx = wire $"{prefix}_caseIdx" caseCountW
    waveBase + cat (lit 0UL (caseCountW - threadW)) thread.count ==> caseIdx
    let caseIdxNarrow = wire $"{prefix}_caseIdxNarrow" caseAddrW
    slice (caseAddrW - 1) 0 caseIdx ==> caseIdxNarrow

    let caseRdAddr: Expr =
        if resident then caseIdxNarrow else cat runBank caseIdxNarrow

    let caseWord = (memReadPort caseMem caseRdAddr).data
    let vars = [ for f in 0 .. varCount - 1 -> slice (f * 32 + 31) (f * 32) caseWord ]
    let target = slice (varCount * 32 + 31) (varCount * 32) caseWord

    let sThread = reg $"{prefix}_s1_th" threadW
    thread.count ==> sThread
    let sPc = reg $"{prefix}_s1_pc" addrW
    pc.count ==> sPc
    let sIsInstrS = regBit $"{prefix}_s1_isInstr"
    isInstr ==> sIsInstrS
    let sIsFitS = regBit $"{prefix}_s1_isFit"
    isFit ==> sIsFitS

    let decoded = decodeWord $"{prefix}_de" bankData addrW

    let aAddr = wire $"{prefix}_aAddr" slotW
    cat sThread decoded.aIdx ==> aAddr
    let aVal = wire $"{prefix}_aVal" 32

    mux
            (eq decoded.aSrc (lit 0UL 2))
            (varMux vars decoded.aIdx)
            (mux
                (eq decoded.aSrc (lit 1UL 2))
                (memRead constRegs (slice (constW - 1) 0 decoded.aIdx))
                (memRead results aAddr))
    ==> aVal

    let bAddr = wire $"{prefix}_bAddr" slotW
    cat sThread decoded.bIdx ==> bAddr
    let bVal = wire $"{prefix}_bVal" 32

    mux
            (eq decoded.bSrc (lit 0UL 2))
            (varMux vars decoded.bIdx)
            (mux
                (eq decoded.bSrc (lit 1UL 2))
                (memRead constRegs (slice (constW - 1) 0 decoded.bIdx))
                (memRead results bAddr))
    ==> bVal

    // Pooled divide: a DIV reaching SOURCE enqueues `(thread, a, b)` rather
    // than issuing to the ALU, and flags the wave for the WAITR hold. Every hit
    // in one wave shares `sPc` — the barrel issues one pc across all threads —
    // so a single register holds the offloaded pc for the writeback address.
    let divHit =
        socket
        |> Option.map (fun s ->
            let hit = wireBit $"{prefix}_divHit"
            (sIsInstrS &&& eq decoded.op (lit (uint64 Opcodes.DIV) 8)) ==> hit

            memWrite s.issueThread s.issueWrPtr sThread hit
            memWrite s.issueA s.issueWrPtr aVal hit
            memWrite s.issueB s.issueWrPtr bVal hit

            If hit (fun () ->
                s.issueWrPtr + lit 1UL threadW ==> s.issueWrPtr
                sPc ==> s.pcRemote
                lit 1UL 1 ==> s.remoteWave)

            hit)

    let fitAddr = wire $"{prefix}_fitAddr" slotW
    cat sThread outIdxR ==> fitAddr
    let fitRes = wire $"{prefix}_fitRes" 32

    mux
            (eq outSrcR (lit 0UL 2))
            (varMux vars outIdxR)
            (mux
                (eq outSrcR (lit 1UL 2))
                (memRead constRegs (slice (constW - 1) 0 outIdxR))
                (memRead results fitAddr))
    ==> fitRes

    let sAVal = reg $"{prefix}_s0_aVal" (SInt 32)
    aVal ==> sAVal
    let sBVal = reg $"{prefix}_s0_bVal" (SInt 32)
    bVal ==> sBVal
    let sFitRes = reg $"{prefix}_s0_fitRes" (SInt 32)
    fitRes ==> sFitRes
    let sTarget = reg $"{prefix}_s0_target" (SInt 32)
    target ==> sTarget
    let sOp = reg $"{prefix}_s0_op" 8
    decoded.op ==> sOp
    let sIsFit = regBit $"{prefix}_s0_isfit"
    sIsFitS ==> sIsFit

    // The fitness subtract sits after the SOURCE registers, in the same short
    // cone as the ALU input mux.
    let errWide = wire $"{prefix}_errWide" (SInt 33)
    signExtend 33 sFitRes - signExtend 33 sTarget ==> errWide
    let err = wire $"{prefix}_err" (SInt 32)
    saturate 32 errWide ==> err

    let aluOp = mux sIsFit (lit (uint64 Opcodes.MUL) 8) sOp
    let aluA = mux sIsFit err sAVal
    let aluB = mux sIsFit err sBVal
    let aluResult = gepAluPipelined residentDiv $"{prefix}_alu" aluOp aluA aluB

    let wThread = cone.CarryTo (latency - 1) $"{prefix}_wb_th" threadW sThread
    let wPc = cone.CarryTo (latency - 1) $"{prefix}_wb_pc" addrW sPc

    // An offloaded DIV writes back through the socket, not the ALU path.
    let wbEnSrc =
        match divHit with
        | Some hit ->
            let w = wireBit $"{prefix}_wbEnIn"
            (sIsInstrS &&& bnot hit) ==> w
            w
        | None -> sIsInstrS

    let wInstr = cone.CarryTo (latency - 1) $"{prefix}_wb_instr" 1 wbEnSrc
    let wFit = cone.CarryTo (latency - 1) $"{prefix}_wb_fit" 1 sIsFitS

    let wbAddr = wire $"{prefix}_wbAddr" slotW
    cat wThread wPc ==> wbAddr
    memWrite results wbAddr aluResult wInstr

    let sq = wire $"{prefix}_sq" 64
    cat (lit 0UL 32) aluResult ==> sq

    // One untagged accumulator: the inter-individual drain guarantees every
    // writeback for individual m lands before the latch cycle.
    ifElse [(latchFitB, fun () -> lit 0UL 64 ==> fitAcc); (wFit, fun () -> fitAcc + sq ==> fitAcc)]

    // ---- The socket's two halves: the issue FIFO drains under ready/valid,
    // and tagged results land in the results memory on cycles the ALU path is
    // not writing it. The builder folds that into the ALU's own write site, so
    // the memory keeps its single write port. ----
    let divIssue =
        (socket, divHit)
        ||> Option.map2 (fun s hit ->
            let cw = threadW + 1
            let widen (b: Expr) = cat (lit 0UL threadW) b

            let issueTag = wire $"{prefix}_divIssueTag" threadW
            memRead s.issueThread s.issueRdPtr ==> issueTag
            let issueA = wire $"{prefix}_divIssueA_head" 32
            memRead s.issueA s.issueRdPtr ==> issueA
            let issueB = wire $"{prefix}_divIssueB_head" 32
            memRead s.issueB s.issueRdPtr ==> issueB

            let issueValid = wireBit $"{prefix}_divIssueValid"
            bnot (eq s.issueCount (lit 0UL cw)) ==> issueValid
            let issueReady = wireBit $"{prefix}_divIssueReady"
            registerStreamReady issueReady
            let issueFire = wireBit $"{prefix}_divIssueFire"
            (issueValid &&& issueReady) ==> issueFire
            If issueFire (fun () -> s.issueRdPtr + lit 1UL threadW ==> s.issueRdPtr)

            // Intake: the tag IS the thread. Depth is a whole wave, so `ready`
            // only ever drops if the pod breaks the outstanding-count contract.
            let wb = s.writeback

            let wbQ =
                match wb.payload.fields with
                | [ q ] -> q
                | fields -> failwith $"the divide writeback carries one result, got %d{fields.Length}"

            let wbReady = wireBit $"{prefix}_divWbReady"
            lt s.wbCount (lit (uint64 nThreads) cw) ==> wbReady
            wbReady ==> wb.ready
            let wbAccept = wireBit $"{prefix}_divWbAccept"
            (wb.valid &&& wbReady) ==> wbAccept
            memWrite s.wbThread s.wbWrPtr wb.payload.tag wbAccept
            memWrite s.wbResult s.wbWrPtr wbQ wbAccept
            If wbAccept (fun () -> s.wbWrPtr + lit 1UL threadW ==> s.wbWrPtr)

            let wbDrain = wireBit $"{prefix}_divWbDrain"
            (bnot (eq s.wbCount (lit 0UL cw)) &&& bnot wInstr) ==> wbDrain
            let divWbAddr = wire $"{prefix}_divWbAddr" slotW
            cat (memRead s.wbThread s.wbRdPtr) s.pcRemote ==> divWbAddr
            memWrite results divWbAddr (memRead s.wbResult s.wbRdPtr) wbDrain
            If wbDrain (fun () -> s.wbRdPtr + lit 1UL threadW ==> s.wbRdPtr)

            s.issueCount + widen hit - widen issueFire ==> s.issueCount
            s.wbCount + widen wbAccept - widen wbDrain ==> s.wbCount
            s.pending + widen hit - widen wbDrain ==> s.pending

            { payload = { tag = issueTag; fields = [ issueA; issueB ] }
              valid = issueValid
              ready = issueReady
              layout = fuLayout threadW gepDivIssuePorts })

    let idle = wireBit $"{prefix}_idle"
    st.Is Wave.Idle ==> idle

    {| res =
        { payload = (resFit, resUnit, resM)
          valid = resValid
          ready = respReady
          layout = layout3 ("fit", 64) ("unit", 32) ("m", 8) }
       canFill = canFill
       idle = idle
       divIssue = divIssue |}

/// The geometry both unit-engine walks are built at, stated once: capacity 32,
/// 4 constants, 4 variables, 8 threads, 64 cases, 512 bank words. The threaded
/// walk overrides `nThreads` and inherits the rest, so the two walks cannot
/// drift into testing different machines.
let v1UnitShape =
    { capacity = 32
      constCount = 4
      varCount = 4
      nThreads = 8
      caseCapacity = 64
      indivCapacity = 512 }

/// The unit engine at ports, queue mode, no div. The check fills real beats
/// and compares emitted fitnesses against the software evaluation.
let unitEngineWalk =
    defModule
        "GepUnitEngineWalk"
        (fun p ->
            ({ beat = p.inPort "fill_beat" 128
               indivEn = p.inPort "fill_indiv_en" 1
               indivAddr = p.inPort "fill_indiv_addr" 7
               commit = p.inPort "fill_commit" 1
               unitId = p.inPort "fill_unit_id" 32 },
             QueueCases(p.inPort "fill_case_en" 1, p.inPort "fill_case_sel" 1, p.inPort "fill_case_addr" 6),
             p.inPort "n_cases" 7,
             p.inPort "m_count" 8,
             p.outPort "can_fill" 1,
             p.outPort "idle" 1,
             streamOutputPorts p "res" (layout3 ("fit", 64) ("unit", 32) ("m", 8))))
        (fun (fill, caseFill, nCases, mCount, canFill, idle, resPorts) ->
            let engine = gepUnitEngine v1UnitShape NoDiv "ue" fill caseFill nCases mCount
            engine.canFill ==> canFill
            engine.idle ==> idle
            streamSink resPorts engine.res)

/// The same lane with a divide, at the same ports, once per sharing ratio —
/// `PerLane` puts the arm in the lane's ALU, `Pooled` puts it behind a
/// one-client `gepDivPod`. Nothing else in the design differs, which is the
/// claim the knob makes; the check runs both against one software evaluation
/// and against each other.
let unitEngineDivWalk (sharing: FuSharing) =
    let nThreads = 16
    let threadW = 4

    let name =
        match sharing with
        | PerLane -> "GepUnitEngineDivPerLaneWalk"
        | Pooled -> "GepUnitEngineDivPooledWalk"

    defModule
        name
        (fun p ->
            ({ beat = p.inPort "fill_beat" 128
               indivEn = p.inPort "fill_indiv_en" 1
               indivAddr = p.inPort "fill_indiv_addr" 7
               commit = p.inPort "fill_commit" 1
               unitId = p.inPort "fill_unit_id" 32 },
             QueueCases(p.inPort "fill_case_en" 1, p.inPort "fill_case_sel" 1, p.inPort "fill_case_addr" 6),
             p.inPort "n_cases" 7,
             p.inPort "m_count" 8,
             p.outPort "can_fill" 1,
             p.outPort "idle" 1,
             streamOutputPorts p "res" (layout3 ("fit", 64) ("unit", 32) ("m", 8))))
        (fun (fill, caseFill, nCases, mCount, canFill, idle, resPorts) ->
        let laneDiv =
            match sharing with
            | PerLane -> ResidentDiv
            | Pooled ->
                // The link is a cycle — the lane consumes what the pod produces
                // from what the lane produces — so one end is declared first and
                // driven once the other exists. Ports do this for free across a
                // module boundary; inside one design it is these four nets.
                let wbTag = wire "wb_tag" threadW
                let wbQ = wire "wb_q" 32
                let wbValid = wireBit "wb_valid"
                let wbReady = wireBit "wb_ready"
                registerStreamReady wbReady

                PodDiv
                    { payload = { tag = wbTag; fields = [ wbQ ] }
                      valid = wbValid
                      ready = wbReady
                      layout = fuLayout threadW gepDivWritebackPorts }

        let engine =
            gepUnitEngine { v1UnitShape with nThreads = nThreads } laneDiv "ue" fill caseFill nCases mCount

        match laneDiv with
        | PodDiv wb ->
            let podOut = gepDivPod "divpod" [ engine.divIssue.Value ]
            streamExport wb.payload wb.valid wb.ready podOut.Head
        | NoDiv
        | ResidentDiv -> ()

        engine.canFill ==> canFill
        engine.idle ==> idle
        streamSink resPorts engine.res)

/// The WarpCPU cluster's breeder block: parents + seed + thresholds →
/// `gepOperatorEngine` breeds the child → `gepKarvaCompiler` compiles it
/// (reading the child straight from the operator engine's buffer) → the
/// SERIALIZE stage streams the compiled record out as 128-bit lines in the
/// *unit-record format* — header word, packed instructions, child constants,
/// 16 B-padded — exactly what `gepUnitEngine`'s fill beat consumes. One line
/// per cycle under ready/valid, so backpressure stalls the serializer.
///
/// Instruction words beyond `nInstr` carry stale contents from earlier
/// offspring — dead bytes on the wire, never fetched. After the last line the
/// block holds in DONE with the child genome readable through the rd ports
/// for DDR writeback; `release` (or a new `start`) frees it.
/// One offspring's states, breeder-block side: the operator engine runs, the
/// karva compiler runs on its child, then the record serializes out.
[<RequireQualifiedAccess>]
type private Breeder =
    | Idle
    | Breed
    | Compile
    | Serialize
    | Done

let gepBreederBlock
    (genome: GepGenome)
    (capacity: int)
    (prefix: string)
    (start: Expr)
    (release: Expr)
    (sIn: Expr list)
    (rates: GepBreedRatesBus)
    (load: GepParentLoadBus)
    (rdSaddr: Expr)
    (rdCaddr: Expr)
    =
    let geneLen, constCount = genome.geneLen, genome.constCount

    if capacity &&& (capacity - 1) <> 0 then
        failwith $"capacity must be a power of two, got %d{capacity}"

    let indivWords = gepUnitIndivWords capacity constCount

    if indivWords > 64 then
        failwith $"record must fit the 64-word transient memory, got %d{indivWords} words"

    let st =
        machine $"{prefix}_st" [ Breeder.Idle; Breeder.Breed; Breeder.Compile; Breeder.Serialize; Breeder.Done ]

    let accept = wireBit $"{prefix}_accept"
    ((st.Is Breeder.Idle &&& start) ||| (st.Is Breeder.Done &&& start)) ==> accept

    // The compiler reads the child straight from the operator engine; the
    // serializer borrows the constant port for the record's constant section;
    // the pool reads the genome through both ports in DONE.
    let engineRdS = wire $"{prefix}_rdS" 6
    let engineRdC = wire $"{prefix}_rdC" 6

    let engine =
        gepOperatorEngine genome breederMaxTransposon $"{prefix}_oe" accept sIn rates load engineRdS engineRdC

    let symData = wire $"{prefix}_symData" 8
    engine.childSym ==> symData
    let karvaStart = wireBit $"{prefix}_karvaStart"
    (st.Is Breeder.Breed &&& engine.finished) ==> karvaStart
    let compiler = gepKarvaCompiler $"{prefix}_kc" karvaStart symData

    let wordCtr = reg $"{prefix}_wordCtr" 6
    let constIdx = wire $"{prefix}_constIdx" 6
    wordCtr - lit (uint64 (capacity + 1)) 6 ==> constIdx
    mux (st.Is Breeder.Compile) compiler.symAddr rdSaddr ==> engineRdS
    mux (st.Is Breeder.Serialize) constIdx rdCaddr ==> engineRdC

    // Transient record: header + instructions from the compiler.
    let recMem = distributedMem $"{prefix}_recMem" 6 32
    memWrite recMem compiler.recAddr compiler.recData compiler.recEn

    // ---- Record serializer: one word per cycle into a 4-lane line
    // register; a full line holds until the consumer takes it. ----
    let laneCtr = reg $"{prefix}_laneCtr" 2
    let lineIdx = reg $"{prefix}_lineIdx" 4
    let pendValid = regBit $"{prefix}_pendValid"
    let pendLast = regBit $"{prefix}_pendLast"
    let lanes = [ for l in 0 .. 3 -> reg $"{prefix}_lineW%d{l}" 32 ]

    let recWord = wire $"{prefix}_recWord" 32

    mux
            (lt wordCtr (lit (uint64 (capacity + 1)) 6))
            (memRead recMem wordCtr)
            (mux (lt wordCtr (lit (uint64 (capacity + 1 + constCount)) 6)) engine.childConst (lit 0UL 32))
    ==> recWord

    let recReady = wireBit $"{prefix}_rec_ready"
    registerStreamReady recReady

    // ---- Block FSM ----
    If accept (fun () -> st.Goto Breeder.Breed)
    If (st.Is Breeder.Breed &&& engine.finished) (fun () -> st.Goto Breeder.Compile)

    If (st.Is Breeder.Compile &&& compiler.finished) (fun () ->
        st.Goto Breeder.Serialize
        lit 0UL 6 ==> wordCtr
        lit 0UL 2 ==> laneCtr
        lit 0UL 4 ==> lineIdx
        lit 0UL 1 ==> pendValid
        lit 0UL 1 ==> pendLast)

    If (st.Is Breeder.Serialize &&& bnot pendValid) (fun () ->
        for l in 0 .. 3 do
            If (eq laneCtr (lit (uint64 l) 2)) (fun () -> recWord ==> lanes[l])

        If (eq laneCtr (lit 3UL 2)) (fun () ->
            lit 1UL 1 ==> pendValid
            mux (eq wordCtr (lit (uint64 (indivWords - 1)) 6)) (lit 1UL 1) (lit 0UL 1) ==> pendLast)

        laneCtr + lit 1UL 2 ==> laneCtr
        wordCtr + lit 1UL 6 ==> wordCtr)

    If (st.Is Breeder.Serialize &&& pendValid &&& recReady) (fun () ->
        lit 0UL 1 ==> pendValid
        lineIdx + lit 1UL 4 ==> lineIdx
        If pendLast (fun () -> st.Goto Breeder.Done))

    If (st.Is Breeder.Done &&& release &&& bnot start) (fun () -> st.Goto Breeder.Idle)

    let line = wire $"{prefix}_line" 128
    catAll [ lanes[3]; lanes[2]; lanes[1]; lanes[0] ] ==> line

    let busy = wireBit $"{prefix}_busy"
    mux (st.Is Breeder.Idle) (lit 0UL 1) (mux (st.Is Breeder.Done) (lit 0UL 1) (lit 1UL 1)) ==> busy
    let finished = wireBit $"{prefix}_finished"
    st.Is Breeder.Done ==> finished

    {| rec_ =
        { payload = (line, lineIdx, pendLast)
          valid = pendValid
          ready = recReady
          layout = layout3 ("line", 128) ("line_idx", 4) ("last", 1) }
       childSym = engine.childSym
       childConst = engine.childConst
       busy = busy
       finished = finished |}

/// The breeder block at ports, at the deployed geometry — the record-line
/// stream out through the standard sink, the child readable in DONE.
let breederBlockWalk =
    defModule
        "GepBreederBlockWalk"
        (fun p ->
            (p.inPort "start" 1,
             p.inPort "rel" 1,
             List.init 4 (fun idx -> p.inPort $"s{idx}" 32),
             { onePoint = p.inPort "th_1p" 32
               twoPoint = p.inPort "th_2p" 32
               geneRecomb = p.inPort "th_gr" 32
               mutation = p.inPort "th_mut" 32
               constReplace = p.inPort "th_cr" 32
               creep = p.inPort "th_creep" 32
               inversion = p.inPort "th_inv" 32
               isTrans = p.inPort "th_is" 32
               risTrans = p.inPort "th_ris" 32
               sigmaFx = p.inPort "sigma_fx" 32
               rangeFx = p.inPort "range_fx" 32 },
             { ldSym = p.inPort "ld_sym" 1
               ldPar = p.inPort "ld_par" 1
               ldAddr = p.inPort "ld_addr" 6
               ldSdata = p.inPort "ld_sdata" 8
               ldConst = p.inPort "ld_const" 1
               ldCdata = p.inPort "ld_cdata" 32 },
             p.inPort "rd_saddr" 6,
             p.inPort "rd_caddr" 6,
             p.outPort "child_sym" 8,
             p.outPort "child_const" 32,
             p.outPort "busy" 1,
             p.outPort "done" 1,
             streamOutputPorts p "rec" (layout3 ("line", 128) ("line_idx", 4) ("last", 1))))
        (fun (start, release, sIn, rates, load, rdSaddr, rdCaddr, childSym, childConst, busy, doneOut, recPorts) ->
            let block =
                gepBreederBlock
                    v1Genome
                    32
                    "bb"
                    start
                    release
                    sIn
                    rates
                    load
                    rdSaddr
                    rdCaddr

            block.childSym ==> childSym
            block.childConst ==> childConst
            block.busy ==> busy
            block.finished ==> doneOut
            streamSink recPorts block.rec_)

/// The WarpCPU cluster's record router: binds each breeder record stream to a
/// free unit-engine lane and carries it point-to-point.
///
/// Binding: one grant per cycle pairs the lowest-index requesting breeder
/// with the lowest-index free lane (balanced `priorityPick` trees; a lane is
/// free when its `can_fill` is high and no stream is bound to it). One cycle
/// of arbitration against a ~40-cycle stream is noise. Streaming: after
/// binding, lines flow unconditionally through one register stage per lane;
/// ready back to the breeder is a registered binding bit, so no combinational
/// ready path exists. Commit: the cycle after the last line lands,
/// `fill_commit` pulses with the entry id as the unit id, then the binding
/// drops; the lane stays held through the commit-output cycle because its
/// `can_fill` is one cycle stale right then. The grant tap exposes each
/// binding for the pool's lane/bank bookkeeping.
let gepRecordRouter
    (nLanes: int)
    (lineIdxW: int)
    (prefix: string)
    (recs: Stream<Expr * Expr * Expr> list)
    (entryIds: Expr list)
    (canFills: Expr list)
    =
    let nBreeders = recs.Length

    if nBreeders < 1 || nLanes < 1 then
        failwith "gepRecordRouter needs at least one breeder and one lane"

    let bitsFor n =
        if n <= 1 then 1 else 32 - System.Numerics.BitOperations.LeadingZeroCount(uint (n - 1))

    let bW = bitsFor nBreeders
    let lW = bitsFor nLanes

    let recValid = [ for r in recs -> r.valid ]
    let recLine = [ for r in recs -> let line, _, _ = r.payload in line ]
    let recLineIdx = [ for r in recs -> let _, idx, _ = r.payload in idx ]
    let recLast = [ for r in recs -> let _, _, last = r.payload in last ]

    let bound = [ for b in 0 .. nBreeders - 1 -> regBit $"{prefix}_bound%d{b}" ]
    let boundLane = [ for b in 0 .. nBreeders - 1 -> reg $"{prefix}_boundLane%d{b}" lW ]
    let pendCommit = [ for b in 0 .. nBreeders - 1 -> regBit $"{prefix}_pendCommit%d{b}" ]
    let laneBusy = [ for l in 0 .. nLanes - 1 -> regBit $"{prefix}_laneBusy%d{l}" ]

    // ---- Binding: one grant per cycle, lowest-index-first both sides ----
    let reqs =
        [ for b in 0 .. nBreeders - 1 -> recValid[b] &&& bnot bound[b] &&& bnot pendCommit[b] ]

    let anyReq, pickedB = priorityPick reqs [ [ for b in 0 .. nBreeders - 1 -> lit (uint64 b) bW ] ]
    let frees = [ for l in 0 .. nLanes - 1 -> canFills[l] &&& bnot laneBusy[l] ]
    let anyFree, pickedL = priorityPick frees [ [ for l in 0 .. nLanes - 1 -> lit (uint64 l) lW ] ]
    let grant = wireBit $"{prefix}_grant"
    (anyReq &&& anyFree) ==> grant
    let grantB = wire $"{prefix}_grantB" bW
    pickedB[0] ==> grantB
    let grantL = wire $"{prefix}_grantL" lW
    pickedL[0] ==> grantL

    If grant (fun () ->
        for b in 0 .. nBreeders - 1 do
            If (eq grantB (lit (uint64 b) bW)) (fun () ->
                lit 1UL 1 ==> bound[b]
                grantL ==> boundLane[b])

        for l in 0 .. nLanes - 1 do
            If (eq grantL (lit (uint64 l) lW)) (fun () -> lit 1UL 1 ==> laneBusy[l]))

    // ---- Streaming + commit scheduling ----
    let consuming =
        [ for b in 0 .. nBreeders - 1 ->
              let w = wireBit $"{prefix}_consuming%d{b}"
              (bound[b] &&& recValid[b]) ==> w
              w ]

    for b in 0 .. nBreeders - 1 do
        bound[b] ==> recs[b].ready

        If (consuming[b] &&& recLast[b]) (fun () ->
            lit 0UL 1 ==> bound[b]
            lit 1UL 1 ==> pendCommit[b])

        If pendCommit[b] (fun () -> lit 0UL 1 ==> pendCommit[b])

    // ---- Per-lane registered output stage: at most one breeder is bound to
    // a lane, so the folds are one-hot muxes ahead of a register ----
    let laneFills =
        [ for l in 0 .. nLanes - 1 ->
              let laneFold (active: int -> Expr) (payload: int -> Expr) (zero: Expr) =
                  List.fold
                      (fun acc b -> mux (eq boundLane[b] (lit (uint64 l) lW) &&& active b) (payload b) acc)
                      zero
                      [ 0 .. nBreeders - 1 ]

              let enR = regBit $"{prefix}_l%d{l}_en_r"
              laneFold (fun b -> consuming[b]) (fun _ -> lit 1UL 1) (lit 0UL 1) ==> enR
              let beatR = reg $"{prefix}_l%d{l}_beat_r" 128
              laneFold (fun b -> consuming[b]) (fun b -> recLine[b]) (lit 0UL 128) ==> beatR
              let addrR = reg $"{prefix}_l%d{l}_addr_r" lineIdxW
              laneFold (fun b -> consuming[b]) (fun b -> recLineIdx[b]) (lit 0UL lineIdxW) ==> addrR
              let commitR = regBit $"{prefix}_l%d{l}_commit_r"
              laneFold (fun b -> pendCommit[b]) (fun _ -> lit 1UL 1) (lit 0UL 1) ==> commitR
              let unitR = reg $"{prefix}_l%d{l}_unit_r" 32
              laneFold (fun b -> pendCommit[b]) (fun b -> entryIds[b]) (lit 0UL 32) ==> unitR

              // Release only once the commit output has fired, so the next
              // grant sees a post-bank-flip can_fill.
              If commitR (fun () -> lit 0UL 1 ==> laneBusy[l])

              { beat = beatR
                indivEn = enR
                indivAddr = addrR
                commit = commitR
                unitId = unitR } ]

    let streamsActive = wire $"{prefix}_streams_active" (bW + 1)

    (bound |> List.map (fun b -> cat (lit 0UL bW) b) |> List.reduce (+))
    ==> streamsActive

    {| laneFills = laneFills
       streamsActive = streamsActive
       grantFire = grant
       grantB = grantB
       grantL = grantL |}

/// The router at ports: two hand-driven breeder streams, two fake lanes —
/// the binding, streaming, commit and release choreography observed directly.
let recordRouterWalk =
    defModule
        "GepRecordRouterWalk"
        (fun p ->
            let recLayout = layout3 ("line", 128) ("line_idx", 4) ("last", 1)

            ([ streamInputPorts p "b0_rec" recLayout; streamInputPorts p "b1_rec" recLayout ],
             [ p.inPort "b0_entry_id" 32; p.inPort "b1_entry_id" 32 ],
             [ p.inPort "l0_can_fill" 1; p.inPort "l1_can_fill" 1 ],
             List.init 2 (fun l ->
                 (p.outPort $"l{l}_fill_beat" 128,
                  p.outPort $"l{l}_fill_indiv_en" 1,
                  p.outPort $"l{l}_fill_indiv_addr" 4,
                  p.outPort $"l{l}_fill_commit" 1,
                  p.outPort $"l{l}_fill_unit_id" 32)),
             p.outPort "streams_active" 2,
             p.outPort "grant_fire" 1,
             p.outPort "grant_b" 1,
             p.outPort "grant_l" 1))
        (fun (recPorts, entryIds, canFills, laneOuts, streamsActive, grantFire, grantB, grantL) ->
            let recs = List.map streamSource recPorts
            let router = gepRecordRouter 2 4 "rr" recs entryIds canFills

            List.zip router.laneFills laneOuts
            |> List.iter (fun (fill, (beat, indivEn, indivAddr, commit, unitId)) ->
                fill.beat ==> beat
                fill.indivEn ==> indivEn
                fill.indivAddr ==> indivAddr
                fill.commit ==> commit
                fill.unitId ==> unitId)

            router.streamsActive ==> streamsActive
            router.grantFire ==> grantFire
            router.grantB ==> grantB
            router.grantL ==> grantL)
