/// What the Mandelbrot designs are built for — **one board per app, not one
/// per project**, because this project's two tops are packaged as two apps
/// whose overlays pin different clocks on the same part.
///
/// That is the whole argument for a board being created where the design is
/// rather than shared from the library: `MandelFrameAxi` runs at 166.666672
/// MHz and `MandelPodAxi` at 99.999001 MHz, so any single "the KV260" would
/// have been right for one of them and silently wrong for the other. And
/// silently is the point — a wrong clock costs no pixels, it only makes every
/// time the host reports wrong, which is the kind of error that survives a
/// bit-exact check.
module Warp11.Mandelbrot.Board

open Warp11

/// The full-scale frame accelerator, as `mandelframe_bd_bd.tcl` programs it.
/// The headline divides by this: 715,938 cycles is 4.30 ms here and nowhere
/// else.
let frameBoard = kv260At 166_666_672

/// The one-shot pod, as `mandelfs_bd_bd.tcl` programs it. A slower clock for
/// the same silicon — the pod is the acceptance artifact rather than the
/// performance one, and it was closed at the PS default.
let podBoard = kv260At 99_999_001
