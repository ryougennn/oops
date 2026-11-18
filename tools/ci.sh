#!/bin/sh
# Build, test, and AOT-publish every sample; fails on any trim/AOT warning (spec §2 rule 6).
set -e
cd "$(dirname "$0")/.."
dotnet build oopshell.slnx -c Release
# The window-capture test compositor (needs wlroots 0.20); CaptureTests skip if it cannot be built.
tests/fixtures/capturewl/build.sh || echo "ci: capturewl not built, CaptureTests will skip" >&2
for t in oopshell.Scanner.Tests oopshell.Tests oopshell.Services.Tests oopshell.IntegrationTests; do
    dotnet test --project tests/$t
done
for s in oops-info HelloWindow HelloBar HelloSkia oops-stress; do
    out=$(dotnet publish samples/$s -c Release -r linux-x64 -p:PublishAot=true -o artifacts/publish/$s 2>&1)
    if echo "$out" | grep -qE "warning (IL|AOT)[0-9]+"; then
        echo "$out" | grep -E "warning (IL|AOT)[0-9]+"
        echo "AOT/trim warnings in $s" >&2
        exit 1
    fi
done
demo/oops95/build.sh publish
# OopsOSX uses the packages oops95's build just packed.
rm -rf demo/oopsosx/.packages/oopshell*
out=$(dotnet publish demo/oopsosx/src/oopsosx -c Release -r linux-x64 -p:PublishAot=true -o demo/oopsosx/out 2>&1) || { echo "$out"; exit 1; }
if echo "$out" | grep -qE "warning (IL|AOT)[0-9]+"; then
    echo "$out" | grep -E "warning (IL|AOT)[0-9]+"
    echo "AOT/trim warnings in oopsosx" >&2
    exit 1
fi
echo "ci: ok"
