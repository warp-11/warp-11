//! The software twin, ported from Mandelbrot.fs: the lane's arithmetic in host
//! integers, exact to the bit — every truncation, wrap and compare the same.
//! GEP's oracle pattern: the fabric is right when it matches this, per pixel,
//! with no tolerance. Shared by the bridge test and the first-light binary, so
//! the assertion made against the F# Sim is the same one made against silicon.

use warp11_runtime::mandel_layout as layout;

const MASK32: u64 = 0xFFFF_FFFF;

fn sext32(v: u64) -> i64 {
    v as u32 as i32 as i64
}

pub fn twin() -> Vec<u8> {
    let frac = 28u32;
    let step = (3u64 << frac) / 64; // 3/64, exact in Q4.28
    let x_min = (-2.25 * f64::from(1u32 << frac)) as i64 as u64 & MASK32;
    let y_min = (-1.125 * f64::from(1u32 << frac)) as i64 as u64 & MASK32;
    let threshold = 1i64 << 58; // 4.0 in Q8.56

    let mut frame = Vec::with_capacity(layout::FB_PIXELS);
    for py in 0..layout::FRAME_HEIGHT as u64 {
        for px in 0..layout::FRAME_WIDTH as u64 {
            let cx = ((px * step) & MASK32).wrapping_add(x_min) & MASK32;
            let cy = ((py * step) & MASK32).wrapping_add(y_min) & MASK32;
            let (mut zx, mut zy, mut iter) = (0u64, 0u64, 0u32);
            loop {
                let (zxs, zys) = (sext32(zx), sext32(zy));
                let zx2 = zxs.wrapping_mul(zxs) as u64;
                let zy2 = zys.wrapping_mul(zys) as u64;
                if threshold < zx2.wrapping_add(zy2) as i64 || iter == layout::MAX_ITER {
                    break;
                }
                let z_real = zx2.wrapping_sub(zy2);
                let xy = zxs.wrapping_mul(zys) as u64;
                zx = ((z_real >> frac) & MASK32).wrapping_add(cx) & MASK32;
                zy = ((xy >> (frac - 1)) & MASK32).wrapping_add(cy) & MASK32;
                iter += 1;
            }
            frame.push(iter as u8);
        }
    }
    frame
}
