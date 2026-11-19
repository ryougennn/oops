#!/bin/sh
# Builds the HTML documentation into artifacts/docs: the API reference generated from the XML doc comments
# in src/, plus docs/guide. `tools/docs.sh --serve` also serves it at http://localhost:8080.
set -e
cd "$(dirname "$0")/.."
export MSBUILDDISABLENODEREUSE=1
dotnet tool restore > /dev/null
if [ "$1" = "--serve" ]; then
    dotnet docfx docs/docfx.json --serve
else
    dotnet docfx docs/docfx.json
fi
