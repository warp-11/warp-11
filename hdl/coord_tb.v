// Behavioral check for CoordPipe — a two-field payload under backpressure. The
// single-field test proved the handshake; this proves the *fields stay associated*:
// each x arrives with its own brightened lum, in order, through fill, stall, drain.
//
//   run: verilator --binary -Wno-DECLFILENAME --top-module coord_tb coord_tb.v <emitted>.v
//   then: ./obj_dir/Vcoord_tb
module coord_tb;
    reg clk = 0, rst = 1;
    reg [7:0] in_x = 0, in_lum = 0;
    reg in_valid = 0, out_ready = 0;
    wire [7:0] out_x, out_lum;
    wire in_ready, out_valid;

    CoordPipe pipe (
        .clk(clk), .rst(rst),
        .in_x(in_x), .in_lum(in_lum), .in_valid(in_valid), .in_ready(in_ready),
        .out_x(out_x), .out_lum(out_lum), .out_valid(out_valid), .out_ready(out_ready)
    );

    task tick;
        begin
            #1 clk = 1;
            #1 clk = 0;
        end
    endtask

    initial begin
        tick;  // reset
        rst = 0;

        // two beats into a stalled pipe: (x=1, lum=10) then (x=2, lum=20)
        in_x = 1;
        in_lum = 10;
        in_valid = 1;
        tick;
        in_x = 2;
        in_lum = 20;
        tick;

        if (in_ready !== 1'b0) $fatal(1, "full pipe should not be ready");
        if (out_valid !== 1'b1) $fatal(1, "head should be valid");
        if (out_x !== 8'd1 || out_lum !== 8'd11)
            $fatal(1, "head should be (1,11), got (%0d,%0d)", out_x, out_lum);
        in_valid = 0;

        // drain — fields must stay paired and in order
        out_ready = 1;
        tick;
        if (out_valid !== 1'b1) $fatal(1, "second beat should be valid");
        if (out_x !== 8'd2 || out_lum !== 8'd21)
            $fatal(1, "second beat should be (2,21), got (%0d,%0d)", out_x, out_lum);
        tick;
        if (out_valid !== 1'b0) $fatal(1, "pipe should be empty");

        $display("COORD OK: two-field beats stay associated through fill, stall and drain");
        $finish;
    end
endmodule
