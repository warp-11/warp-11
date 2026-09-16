# Vendored dependencies

Third-party source this repo builds itself, each a git submodule of a fork we
control, because the published package cannot be used as-is. Each entry says
why, and what would let it go back to being a package reference.

## NodeEditor — `vendor/NodeEditor`

The node-graph canvas behind `Warp11.Placement.Canvas`. Fork:
`warp-11/NodeEditor`, branch `avalonia-12`, which is upstream
`wieslawsoltes/NodeEditor` PR #75 unchanged.

**Why vendored:** the published `NodeEditorAvalonia 12.0.0` depends on
`Avalonia.Controls.PanAndZoom 11.3`, which references
`Avalonia.Controls.Primitives.IScrollable` — a type Avalonia 12 removed — so
its `Editor` control throws `TypeLoadException` in its static initializer on
every Avalonia 12 (upstream issues #73, #74; measured here 2026-09-15).
PR #75 moves it to the renamed `PanAndZoom 12.x` and `Xaml.Behaviors.Avalonia
12.x` packages; that branch builds and runs against Avalonia 12.1 on desktop
and on wasm.

**What ends it:** an upstream release containing PR #75 (or equivalent). Then
the three `ProjectReference`s in the two `Warp11.Placement.Canvas*` projects
become `PackageReference`s and this submodule goes.

**Not in `Warp11.sln` on purpose:** the projects are multi-targeted
(`net8.0;net10.0`); the `ProjectReference`s build them at the referencing
project's framework, and listing them would build the rest.

**On wasm** the publish trims the library's compiled XAML unless the
assemblies are named in `<TrimmerRootAssembly>` — the browser head does that.
