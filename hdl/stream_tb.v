// Behavioral check for StreamPipe — the ready/valid handshake under backpressure,
// which no lint can see: two beats fill the two stages while the sink stalls, the
// source is blocked, then both beats drain in order with the payload map applied.
//
//   run: verilator --binary -Wno-DECLFILENAME --top-module stream_tb stream_tb.v <emitted>.v
//   then: ./obj_dir/Vstream_tb
module stream_tb;
    reg clk = 0, rst = 1;
    reg [7:0] in_data = 0;
    reg in_valid = 0, out_ready = 0;
    wire [7:0] out_data;
    wire in_ready, out_valid;

    StreamPipe pipe (
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
        if (out_valid !== 1'b0) $fatal(1, "after reset: out_valid should be 0");
        if (in_ready !== 1'b1) $fatal(1, "empty pipe should be ready");

        // two beats into a stalled pipe
        in_data = 10;
        in_valid = 1;
        tick;
        in_data = 20;
        tick;

        // both stages full, sink stalled: source must be blocked, nothing lost
        if (in_ready !== 1'b0) $fatal(1, "full pipe should not be ready");
        if (out_valid !== 1'b1) $fatal(1, "head should be valid");
        if (out_data !== 8'd11) $fatal(1, "head should be 10+1, got %0d", out_data);
        in_valid = 0;

        // drain
        out_ready = 1;
        tick;
        if (out_valid !== 1'b1) $fatal(1, "second beat should be valid");
        if (out_data !== 8'd21) $fatal(1, "second beat should be 20+1, got %0d", out_data);
        tick;
        if (out_valid !== 1'b0) $fatal(1, "pipe should be empty");
        if (in_ready !== 1'b1) $fatal(1, "drained pipe should be ready again");

        $display("STREAM OK: fill, backpressure, block and in-order drain all correct");
        $finish;
    end
endmodule
