// Rung two: line in to line out through the Pmod I2S2, at 46 875 Hz.
//
// The ADC joins the picture here, and with it the second set of clock pins —
// `mclk2`/`sclk2`/`lrclk2`, which are the same three clocks on the connector's
// other row. One generator drives both rows; a second would drift against the
// first. Three shipped KV260 designs once had the ADC unclocked because a top
// forgot that trio, which is why `i2sPins` declares them together.

`default_nettype none

module icebreaker_passthru_top (
    input  wire clk12,          // 12 MHz crystal

    // Pmod I2S2 top row: the D/A converter.
    output wire mclk,
    output wire lrclk,
    output wire sclk,
    output wire sdin,

    // Pmod I2S2 bottom row: the A/D converter, on its own clock pins.
    output wire mclk2,
    output wire lrclk2,
    output wire sclk2,
    input  wire sdout,

    // The board's own indicators, active low.
    output wire ledr_n,
    output wire ledg_n
);

    `include "icebreaker_pll_reset.vh"

    wire led_activity;
    wire led_signal;

    AudioPassthruIce design (
        .clk(clk24),
        .rst(rst),
        .sdout(sdout),
        .mclk(mclk),
        .sclk(sclk),
        .lrclk(lrclk),
        .sdin(sdin),
        .mclk2(mclk2),
        .sclk2(sclk2),
        .lrclk2(lrclk2),
        .led_activity(led_activity),
        .led_signal(led_signal)
    );

    assign ledg_n = ~led_activity;
    assign ledr_n = ~led_signal;

endmodule

`default_nettype wire
