[<AutoOpen>]
module Warp11.Keywords

/// Identifiers Verilog and SystemVerilog reserve, which a warp11 name therefore
/// cannot be. Nothing else in the toolchain stops one: the DSL accepts any
/// string, the Sim has its own namespace, and the emitter writes the name out
/// verbatim — so a design named against this list passes its entire sim suite
/// and produces Verilog that will not parse, surfacing as a Verilator complaint
/// about generated code (`cross` during the signed work, `matches` during the
/// wide-Sim work — both cost a debugging session this check now refuses at the
/// declaration). Both language versions are included: warp11 emits `.v`, but
/// Verilator and Vivado parse the newer keywords too. Matching is exact and
/// case-sensitive, Verilog's own rule. Port of Kotlin's `VerilogKeywords.kt`.
let verilogKeywords: Set<string> =
    Set.ofList
        [ // --- Verilog-2005 ---
          "always"; "and"; "assign"; "automatic"; "begin"; "buf"; "bufif0"; "bufif1"
          "case"; "casex"; "casez"; "cell"; "cmos"; "config"; "deassign"; "default"
          "defparam"; "design"; "disable"; "edge"; "else"; "end"; "endcase"
          "endconfig"; "endfunction"; "endgenerate"; "endmodule"; "endprimitive"
          "endspecify"; "endtable"; "endtask"; "event"; "for"; "force"; "forever"
          "fork"; "function"; "generate"; "genvar"; "highz0"; "highz1"; "if"
          "ifnone"; "incdir"; "include"; "initial"; "inout"; "input"; "instance"
          "integer"; "join"; "large"; "liblist"; "library"; "localparam"
          "macromodule"; "medium"; "module"; "nand"; "negedge"; "nmos"; "nor"
          "noshowcancelled"; "not"; "notif0"; "notif1"; "or"; "output"; "parameter"
          "pmos"; "posedge"; "primitive"; "pull0"; "pull1"; "pulldown"; "pullup"
          "pulsestyle_ondetect"; "pulsestyle_onevent"; "rcmos"; "real"; "realtime"
          "reg"; "release"; "repeat"; "rnmos"; "rpmos"; "rtran"; "rtranif0"
          "rtranif1"; "scalared"; "showcancelled"; "signed"; "small"; "specify"
          "specparam"; "strong0"; "strong1"; "supply0"; "supply1"; "table"; "task"
          "time"; "tran"; "tranif0"; "tranif1"; "tri"; "tri0"; "tri1"; "triand"
          "trior"; "trireg"; "unsigned"; "use"; "uwire"; "vectored"; "wait"; "wand"
          "weak0"; "weak1"; "while"; "wire"; "wor"; "xnor"; "xor"

          // --- SystemVerilog additions ---
          "alias"; "always_comb"; "always_ff"; "always_latch"; "assert"; "assume"
          "before"; "bind"; "bins"; "binsof"; "bit"; "break"; "byte"; "chandle"
          "class"; "clocking"; "const"; "constraint"; "context"; "continue"; "cover"
          "covergroup"; "coverpoint"; "cross"; "dist"; "do"; "endclass"
          "endclocking"; "endgroup"; "endinterface"; "endpackage"; "endprogram"
          "endproperty"; "endsequence"; "enum"; "expect"; "export"; "extends"
          "extern"; "final"; "first_match"; "foreach"; "forkjoin"; "iff"
          "ignore_bins"; "illegal_bins"; "import"; "inside"; "int"; "interface"
          "intersect"; "join_any"; "join_none"; "local"; "logic"; "longint"
          "matches"; "modport"; "new"; "null"; "package"; "packed"; "priority"
          "program"; "property"; "protected"; "pure"; "rand"; "randc"; "randcase"
          "randsequence"; "ref"; "return"; "sequence"; "shortint"; "shortreal"
          "solve"; "static"; "string"; "struct"; "super"; "tagged"; "this"
          "throughout"; "timeprecision"; "timeunit"; "type"; "typedef"; "union"
          "unique"; "var"; "virtual"; "void"; "wait_order"; "wildcard"; "with"
          "within" ]

/// Reject `name` if Verilog reserves it. `what` describes the thing being
/// named, so the message points at the declaration rather than at generated
/// code.
let internal requireNotVerilogKeyword (name: string) (what: string) =
    if Set.contains name verilogKeywords then
        failwith (
            $"'{name}' is a Verilog/SystemVerilog reserved word and cannot name {what}. "
            + "The name is emitted verbatim, so this would produce Verilog that does not parse. "
            + $"Pick another — '{name}Reg', '{name}_' or a domain word."
        )
