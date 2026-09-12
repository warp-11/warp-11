// The two things an iCE40 design needs that the elaborator cannot emit, in one
// place so the two tops below cannot drift apart on either.
//
// **Why this is hand-written Verilog at all.** Warp 11 has no blackbox or
// foreign-module facility — `Instance` holds a `ModuleDef`, not a name — so
// `SB_PLL40_PAD` cannot be instantiated from F#. That is a deliberate absence
// rather than a gap: a design that could name a vendor primitive would stop
// being the same design on the other board. The answer is a wrapper per board,
// which is also the natural home for the two facts that are about this
// *board* rather than about the design — that its clock is 12 MHz, and that
// its LEDs are wired to ground.
//
// **The PLL is not optional.** `Warp11.Effects.Ice` explains the arithmetic:
// MCLK is 256x the frame rate, the fastest MCLK a design can present is half
// its fabric clock, and a CS5343 will not lock to the 128x that the bare
// crystal would give. 24 MHz is what makes the ratio come out exactly.
//
// Parameters from `icepll -i 12 -o 24`: F_VCO 768 MHz, achieved 24.000 MHz.
// Not hand-derived — the tool is the authority on the filter range in
// particular, and it is cheap to re-run.

wire clk24;
wire pll_locked;

SB_PLL40_PAD #(
    .FEEDBACK_PATH("SIMPLE"),
    .DIVR(4'b0000),
    .DIVF(7'b0111111),
    .DIVQ(3'b101),
    .FILTER_RANGE(3'b001)
) pll (
    .PACKAGEPIN(clk12),
    .PLLOUTCORE(clk24),
    .LOCK(pll_locked),
    .RESETB(1'b1),
    .BYPASS(1'b0)
);

// The board has no reset pin, so the design gets one made here: held while the
// PLL is unlocked, and for fifteen cycles after it locks.
//
// **Releasing on lock rather than on a fixed count** is the part that matters.
// Every clock divider in the link starts from its reset value, and a divider
// that starts counting on an unlocked PLL's output is a divider whose first
// frame is the wrong length — which the converter sees as a frame error at the
// exact moment it is trying to acquire.
//
// The counter powers up at zero because iCE40 flip-flops honour their initial
// value out of the bitstream, which is what makes a power-on reset here a
// counter rather than an external RC network.
reg [3:0] por = 4'd0;

always @(posedge clk24) begin
    if (!pll_locked)
        por <= 4'd0;
    else if (por != 4'd15)
        por <= por + 4'd1;
end

wire rst = (por != 4'd15);
