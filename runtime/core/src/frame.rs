//! A frame a design draws on the batch contract's counted path: the host
//! asks for a number of chunk beats and reads rows of escape counts back.
//! The width and the chunk are the fabric's, stated by the design and read
//! from its seam (`FRAME_WIDTH`, `CHUNK_PIXELS`); the height is the host's
//! to choose, because it is only how many beats are asked for.

/// Pixels a 128-bit beat carries, a byte each.
pub const PIXELS_PER_BEAT: usize = 16;
/// Beats a burst holds: what the count asked for must be a multiple of.
pub const BEATS_PER_BURST: usize = 16;

/// A frame's shape: the width rounded up to whole chunks; the chunks the
/// fabric is asked for, padded to whole bursts — the rows past the frame are
/// drawn and dropped, as a recording's padding is; and the beats that come
/// back, a chunk's worth each.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct FrameShape {
    pub width: usize,
    pub height: usize,
    pub padded_width: usize,
    /// Chunks the frame holds.
    pub chunks: usize,
    /// What the host writes as the count: the chunks, padded to whole bursts.
    pub padded_chunks: usize,
    pub beats_per_chunk: usize,
}

impl FrameShape {
    pub fn new(width: usize, height: usize, chunk: usize) -> FrameShape {
        let padded_width = width.div_ceil(chunk) * chunk;
        let chunks = height * padded_width / chunk;
        let padded_chunks = chunks.div_ceil(BEATS_PER_BURST) * BEATS_PER_BURST;
        FrameShape { width, height, padded_width, chunks, padded_chunks, beats_per_chunk: chunk / PIXELS_PER_BEAT }
    }

    /// Beats the frame itself fills.
    pub fn beats(&self) -> usize {
        self.chunks * self.beats_per_chunk
    }

    /// Bytes the fabric writes back, padding included: what the destination
    /// has to hold.
    pub fn bytes_back(&self) -> usize {
        self.padded_chunks * self.beats_per_chunk * PIXELS_PER_BEAT
    }

    /// The pixels a client sees: `width × height`.
    pub fn pixels(&self) -> usize {
        self.width * self.height
    }

    /// The frame out of the bytes the fabric wrote: rows of the padded
    /// width, cropped to the frame's. The padding exists for whole chunks
    /// and means nothing past the host.
    pub fn crop_into(&self, written: &[u8], out: &mut [u8]) {
        for r in 0..self.height {
            out[r * self.width..(r + 1) * self.width]
                .copy_from_slice(&written[r * self.padded_width..r * self.padded_width + self.width]);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    extern crate std;
    use std::vec;

    /// Column 255 would be 0xFF under a plain `col as u8`, which collides with
    /// the padding sentinel below and makes a correct crop look wrong. 251 is
    /// the largest prime under 255, so the pattern stays positional (it
    /// repeats only every 251 columns, and 251 does not divide the stride)
    /// while leaving 0xFF exclusively to the padding.
    const PIXEL_MODULUS: usize = 251;
    const PADDING: u8 = 0xFF;

    fn shape() -> FrameShape {
        FrameShape::new(1400, 800, 128)
    }

    /// What the fabric writes, where every byte says where it lives: pixel
    /// (row, col) holds `col % 251`, padding holds 0xFF. A stride bug then
    /// shows up as a shear — the failure the first Mandelbrot render hit —
    /// rather than as bytes that merely look plausible.
    fn synthetic_written(s: &FrameShape) -> std::vec::Vec<u8> {
        let mut raw = vec![PADDING; s.bytes_back()];
        for row in 0..s.height {
            for col in 0..s.width {
                raw[row * s.padded_width + col] = (col % PIXEL_MODULUS) as u8;
            }
        }
        raw
    }

    #[test]
    fn the_shape_is_whole_chunks_and_whole_bursts() {
        let s = shape();
        assert_eq!(s.padded_width, 1408);
        assert_eq!(s.chunks, 800 * 11);
        assert_eq!(s.padded_chunks % BEATS_PER_BURST, 0);
        assert!(s.bytes_back() >= s.padded_width * s.height);
    }

    #[test]
    fn crop_drops_exactly_the_padding() {
        let s = shape();
        let mut out = vec![0u8; s.pixels()];
        s.crop_into(&synthetic_written(&s), &mut out);

        assert!(!out.contains(&PADDING), "a padding byte survived the crop");
        for row in 0..s.height {
            for col in 0..s.width {
                assert_eq!(out[row * s.width + col], (col % PIXEL_MODULUS) as u8, "pixel ({row}, {col}) came from the wrong offset");
            }
        }
    }

    #[test]
    fn the_shear_a_stride_bug_causes_is_visible_to_that_check() {
        // The negative control: copying the first width × height bytes passes
        // row 0 and shears from row 1 on, so the check above has to see it.
        let s = shape();
        let raw = synthetic_written(&s);
        let row1 = &raw[s.width..2 * s.width];
        assert_ne!(row1[0], 0, "row 1 of a naive crop must not start at column 0, or the padding is invisible");
    }
}
