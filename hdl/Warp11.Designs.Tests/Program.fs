module Warp11.Designs.Tests.Program

open Expecto

[<EntryPoint>]
let main argv =
    testList
        "Warp11.Designs"
        [ DslTests.tests
          MemoryTests.tests
          RegMapTests.tests
          StreamTests.tests
          ImageTests.tests
          SemanticTests.tests
          RegistryTests.tests
          StructuralTests.tests
          EmitterTests.tests ]
    |> runTestsWithCLIArgs [] argv
