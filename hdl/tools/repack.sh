#!/usr/bin/env bash
# Pack the three shipping packages into hdl/artifacts, for consumers on a local
# package source (a downstream repo's nuget.config pointing here).
#
# The reason this is a script rather than a `dotnet pack` in the shell history:
# NuGet caches by (id, version), so re-packing the SAME version after a fix is
# silently ignored — the consumer's restore serves the cached copy and the fix
# appears not to have worked. Clearing the cached copy is the half people
# forget, so it happens here, always.
set -euo pipefail

cd "$(dirname "$0")/.."
export PATH="$HOME/.dotnet:$PATH"

version=$(grep -oPm1 '(?<=<Version>)[^<]+' Directory.Build.props)
echo "packing $version"

for project in Warp11 Warp11.SimView Warp11.SimView.Desktop; do
    dotnet pack "$project/$project.fsproj" -c Release -o artifacts
done

# The cached copies, so the next restore downstream actually sees this build.
for cached in "$HOME"/.nuget/packages/warp11*/"$version"; do
    [ -d "$cached" ] || continue
    echo "clearing cache $cached"
    rm -rf "$cached"
done

echo
echo "packed $version into $(pwd)/artifacts"
echo "consumers restore this via their nuget.config's local source"
