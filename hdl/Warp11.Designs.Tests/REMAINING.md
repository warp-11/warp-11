# Test Migration Complete

Current state: `Warp11.Designs.Tests` has 148 passing Expecto tests. Run from `hdl/`:

```sh
dotnet run --project Warp11.Designs.Tests
```

All checks listed for migration now live in the Expecto project. Artifact generation (`diff`, `firrtl-foreign`, `firrtl-sim`, WAV conversion), performance reports, the differential oracle, hardware checks, and command handlers remain outside it.

Verification:

```sh
cd hdl
dotnet run --project Warp11.Designs.Tests
dotnet build Warp11.sln
```

The solution currently builds with pre-existing duplicate/downgraded `FSharp.Core` warnings from `Warp11.Placement.Canvas`.
