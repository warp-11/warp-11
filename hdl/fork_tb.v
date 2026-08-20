// Behavioral check for ForkJoin — the fork/merge semantics the oracle cannot
// judge (a beat dropped by both sim and Verilog identically would still DIFF
// PASS). Each input beat must yield exactly two output beats, bumped-then-plain
// (round-robin from lastA=0), through free flow and through a stall.
//
//   run: verilator --binary -Wno-DECLFILENAME --top-module fork_tb fork_tb.v <emitted>.v
//   then: ./obj_dir/Vfork_tb
module fork_tb;
    reg clk = 0, rst = 1;
    reg [7:0] in_data = 0;
    reg in_valid = 0, out_ready = 1;
    wire [7:0] out_data;
    wire in_ready, out_valid;

    ForkJoin dut (
        .clk(clk), .rst(rst),
        .in_data(in_data), .in_valid(in_valid), .in_ready(in_ready),
        .out_data(out_data), .out_valid(out_valid), .out_ready(out_ready)
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
        if (in_ready !== 1'b1) $fatal(1, "empty: source should be accepted");
        if (out_valid !== 1'b0) $fatal(1, "empty: no output yet");

        // free flow: one beat in, two beats out, bumped side first
        in_data = 10;
        in_valid = 1;
        tick;
        in_valid = 0;
        if (out_valid !== 1'b1 || out_data !== 8'd11)
            $fatal(1, "first copy should be 11, got v=%b d=%0d", out_valid, out_data);
        tick;
        if (out_valid !== 1'b1 || out_data !== 8'd10)
            $fatal(1, "second copy should be 10, got v=%b d=%0d", out_valid, out_data);
        tick;
        if (out_valid !== 1'b0) $fatal(1, "exactly two copies, no more");
        if (in_ready !== 1'b1) $fatal(1, "drained: source should be accepted again");

        // stall: the pair is held, the source blocks, nothing is lost or duplicated
        in_data = 20;
        in_valid = 1;
        out_ready = 0;
        tick;
        in_valid = 0;
        if (in_ready !== 1'b0) $fatal(1, "full+stalled: source must block");
        tick;
        tick;
        if (out_valid !== 1'b1 || out_data !== 8'd21)
            $fatal(1, "stall must hold the bumped copy, got v=%b d=%0d", out_valid, out_data);
        out_ready = 1;
        tick;
        if (out_valid !== 1'b1 || out_data !== 8'd20)
            $fatal(1, "drain: plain copy should follow, got v=%b d=%0d", out_valid, out_data);
        tick;
        if (out_valid !== 1'b0) $fatal(1, "drained again: exactly two copies");
        if (in_ready !== 1'b1) $fatal(1, "ready again after drain");

        $display("FORK OK: two copies per beat, round-robin order, lossless under stall");
        $finish;
    end
endmodule
