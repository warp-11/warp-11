// Behavioral check for the stateful designs — lint proves structure, this proves
// behavior: the pipeline has latency exactly 2 and the counter counts and holds.
//
//   run: verilator --binary -Wno-DECLFILENAME --top-module tb behavior_tb.v <emitted>.v
//   then: ./obj_dir/Vtb
//
// Expected values are hand-computed (3*4 + satinc(5)*7 = 12 + 42 = 54), so this is
// an assertion harness, not a differential oracle — the spike has no simulator to
// differ against.
module tb;
    reg clk = 0, rst = 1, enable = 1;
    reg [7:0] a = 3, b = 4, c = 5, d = 7;
    wire [15:0] out;
    wire [7:0] value;

    PipelinedDot dot (.clk(clk), .rst(rst), .a(a), .b(b), .c(c), .d(d), .out(out));
    GatedCounter ctr (.clk(clk), .rst(rst), .enable(enable), .value(value));

    task tick;
        begin
            #1 clk = 1;
            #1 clk = 0;
        end
    endtask

    initial begin
        tick;  // reset
        rst = 0;
        tick;
        tick;
        if (out !== 16'd54) $fatal(1, "pipe loaded: expected 54, got %0d", out);
        if (value !== 8'd2) $fatal(1, "counter enabled: expected 2, got %0d", value);
        a = 10;
        b = 10;
        enable = 0;
        tick;
        if (out !== 16'd54) $fatal(1, "pipe latency: expected still 54, got %0d", out);
        tick;
        if (out !== 16'd142) $fatal(1, "pipe new value: expected 142, got %0d", out);
        if (value !== 8'd2) $fatal(1, "counter held: expected 2, got %0d", value);
        $display("BEHAVIOR OK: latency-2 pipeline and gated counter both correct");
        $finish;
    end
endmodule
