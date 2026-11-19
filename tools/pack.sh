#!/bin/sh
# Packs every library for release into artifacts/packages (.nupkg + .snupkg symbols).
#   tools/pack.sh 0.1.0      a release version (default: the Version in Directory.Build.props)
# Publish with: dotnet nuget push "artifacts/packages/*.nupkg" --api-key <key> --source https://api.nuget.org/v3/index.json
set -e
cd "$(dirname "$0")/.."
export MSBUILDDISABLENODEREUSE=1
version=${1:+-p:Version=$1}
rm -rf artifacts/packages
for project in src/*/*.csproj; do
    case "$project" in *Scanner*) continue ;; esac # build-time code generator, not shipped
    # ContinuousIntegrationBuild: deterministic paths in the binaries and symbols (no local machine paths).
    dotnet pack "$project" -c Release -m:4 --nologo -v q -p:ContinuousIntegrationBuild=true $version
done
ls artifacts/packages
