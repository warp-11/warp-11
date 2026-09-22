module Warp11.Designs.Tests.RegistryTests

open Expecto
open Warp11
open Warp11.Designs

let tests =
    testList
        "Registry"
        [ testCase "every catalog entry elaborates and has inspectable signals" <| fun _ ->
              for entry in Registry.designs do
                  Expect.isNotEmpty (entry.label.Trim()) "Catalog labels must not be blank"
                  let design = entry.build ()
                  let sim = Sim design
                  sim.Tick()
                  let inventory = Inventory.ofDesign design
                  Expect.equal inventory.topName design.name $"Catalog entry '{entry.label}' has the wrong top name"
                  Expect.isNonEmpty inventory.signals $"Catalog entry '{entry.label}' should expose signals"

          testCase "every catalog binding has embedded source" <| fun _ ->
              for entry in Registry.designs do
                  let source =
                      Expect.wantSome
                          (Registry.catalog.source entry.binding)
                          $"Catalog binding '{entry.binding}' should be a top-level embedded source binding"

                  Expect.isNotEmpty (source.Trim()) $"Catalog binding '{entry.binding}' should have nonempty source"
                  Expect.stringContains source entry.binding $"Source slice should contain binding '{entry.binding}'"

          testCase "catalog labels are unique" <| fun _ ->
              let labels = Registry.designs |> List.map (fun entry -> entry.label)
              Expect.equal (List.distinct labels).Length labels.Length "Debugger labels must be unique" ]
