// Rung one of the iCEBreaker bring-up ladder: a 440 Hz tone into the Pmod
// I2S2's DAC, with nothing listening.
//
// Build it first. A silent bench here narrows to the PLL, the divisors, the pin
// map or the DAC — the ADC is not in the picture at all, which is the one thing
// the passthru next door cannot say.
//
// Port names are the design's own, so this file, `audio_tone.pcf` and the
// emitted `AudioToneIce.v` all spell each pin the same way and a wrong pin is a
// diff rather than a translation.

`default_nettype none

module icebreaker_tone_top (
    input  wire clk12,          // 12 MHz crystal

    // Pmod I2S2 top row: the D/A converter.
    output wire mclk,
    output wire lrclk,
    output wire sclk,
    output wire sdin,

    // The board's own indicators, active low.
    output wire ledr_n,
    output wire ledg_n
);

    `include "icebreaker_pll_reset.vh"

    wire led_activity;
    wire led_signal;

    AudioToneIce design (
        .clk(clk24),
        .rst(rst),
        .mclk(mclk),
        .sclk(sclk),
        .lrclk(lrclk),
        .sdin(sdin),
        .led_activity(led_activity),
        .led_signal(led_signal)
    );

    // The board's LEDs are wired to ground, so the polarity flip lives here
    // rather than in the design: a design that knew its indicator was inverted
    // would be carrying one board's schematic in hardware that is otherwise
    // portable.
    assign ledg_n = ~led_activity;
    assign ledr_n = ~led_signal;

endmodule

`default_nettype wire
