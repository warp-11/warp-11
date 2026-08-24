/// The oracle catalog's desktop head: every design in `Warp11.Designs`, opened
/// in the step-through debugger.
///
/// A separate project rather than a verb on `Warp11.Designs`, for the reason
/// `Warp11.SimView.Desktop` is separate from `Warp11.SimView`: this carries
/// Avalonia and the platform backends, and `Warp11.Designs` is what
/// `run_differential.sh` invokes and what every living-check run builds. A GUI
/// stack underneath the bug oracle would be paid for on every run and buy it
/// nothing.
///
/// The window offers `source` and `verilog` but not `about`. That is not an
/// omission: this catalog carries no pages on purpose — teaching is
/// `Warp11.Tutorial`'s job, and these designs are shaped by what the
/// differential needs rather than by what reads well — so `Pages.about` would
/// add an empty tab to every window. The pair it does offer is the one worth
/// having here: what the design is written as, and what it emits.
module Warp11.Designs.App

open Warp11.SimView

/// The catalog's own spelling of a label, matched case-insensitively.
///
/// The view resolves its opening entry with an exact `List.contains` and falls
/// back to the first one when nothing matches, so a mistyped label opens the
/// Counter and says nothing about why. Resolving here instead means a typo is
/// an error with the list attached, and a label typed in the wrong case still
/// opens what was asked for.
let private canonical (label: string) =
    Registry.designs
    |> List.tryFind (fun e -> System.String.Equals(e.label, label, System.StringComparison.OrdinalIgnoreCase))
    |> Option.map (fun e -> e.label)

[<EntryPoint>]
let main argv =
    match Array.tryHead argv with
    | Some label when (canonical label).IsNone ->
        eprintfn $"no design labelled '{label}'. This catalog holds:"

        for e in Registry.designs do
            eprintfn $"  {e.label}"

        1
    | initial ->
        Warp11.SimView.Desktop.run
            (View.FromCatalog(Registry.catalog, Option.bind canonical initial))
            [ Pages.source Registry.catalog; Pages.verilog ]
