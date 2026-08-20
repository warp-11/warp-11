//! The full-scale accelerator's software twin, ported from the F# side's
//! `stepTwin`/`laneTwin` (Step.fs / Lane.fs): one step of the fused `z² + c`
//! cone in host integers — every truncation, wrap and compare the same — and
//! the whole-pixel iteration with the lane's exact writeback rule (report the
//! issue iteration at escape or at maxIter−1). The fabric is right when it
//! matches this, per pixel, with no tolerance.

/// One pipelined-step's combinational reference: (zxNext, zyNext, escaped).
pub fn step_twin(frac_bits: u32, zx: u32, zy: u32, cx: u32, cy: u32) -> (u32, u32, bool) {
    let sh_w = 64 - frac_bits; // the shifted-product working width (36 for Q4.28)
    let mask_sh: u64 = (1u64 << sh_w) - 1;
    let sx = |v: u32| i64::from(v as i32);
    // Arithmetic shift of the exact 64-bit signed product, then the working
    // width — one part-select in the fabric, one shift-and-mask here.
    let q = |v: i64| ((v >> frac_bits) as u64) & mask_sh;
    let sext_sh = |v: u64| ((v << (64 - sh_w)) as i64) >> (64 - sh_w);

    let zxx = q(sx(zx) * sx(zx));
    let zyy = q(sx(zy) * sx(zy));
    let zxy = q(sx(zx) * sx(zy));
    let z_mag_sq = zxx.wrapping_add(zyy) & mask_sh;
    let escaped = sext_sh(4u64 << frac_bits) < sext_sh(z_mag_sq);
    let cx_sx = (sx(cx) as u64) & mask_sh;
    let cy_sx = (sx(cy) as u64) & mask_sh;
    let zx_next = zxx.wrapping_sub(zyy).wrapping_add(cx_sx) as u32;
    let zy_next = zxy.wrapping_add(zxy).wrapping_add(cy_sx) as u32;
    (zx_next, zy_next, escaped)
}

/// The whole-pixel twin: iterate from z=0 until the step escapes or the issue
/// iteration reaches maxIter−1, reporting the issue iteration — exactly the
/// barrel lane's writeback rule.
pub fn pixel_twin(frac_bits: u32, max_iter: u32, cx: u32, cy: u32) -> u32 {
    let (mut zx, mut zy, mut n) = (0u32, 0u32, 0u32);
    loop {
        let (zx_next, zy_next, escaped) = step_twin(frac_bits, zx, zy, cx, cy);
        if escaped || n == max_iter - 1 {
            return n;
        }
        zx = zx_next;
        zy = zy_next;
        n += 1;
    }
}

/// A whole frame, row-major at the PADDED width (the fabric pads rows to
/// 16-pixel beats; pad columns iterate like real ones and land in DDR).
pub fn frame_twin(
    width_padded: usize,
    height: usize,
    max_iter: u32,
    cx_origin: u32,
    cy_origin: u32,
    dx: u32,
    dy: u32,
) -> Vec<u8> {
    let mut frame = Vec::with_capacity(height * width_padded);
    let mut cy = cy_origin;
    for _ in 0..height {
        let mut cx = cx_origin;
        for _ in 0..width_padded {
            frame.push(pixel_twin(28, max_iter, cx, cy) as u8);
            cx = cx.wrapping_add(dx);
        }
        cy = cy.wrapping_add(dy);
    }
    frame
}
