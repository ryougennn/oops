#!/bin/sh
# Build, test, and AOT-publish every sample; fails on any trim/AOT warning (spec §2 rule 6).
set -e
cd "$(dirname "$0")/.."
dotnet build oopshell.slnx -c Release
for t in oopshell.Scanner.Tests oopshell.Tests oopshell.Services.Tests oopshell.IntegrationTests; do
    dotnet test --project tests/$t
done
for s in oops-info HelloWindow HelloBar HelloSkia; do
    out=$(dotnet publish samples/$s -c Release -r linux-x64 -p:PublishAot=true -o artifacts/publish/$s 2>&1)
    if echo "$out" | grep -qE "warning (IL|AOT)[0-9]+"; then
        echo "$out" | grep -E "warning (IL|AOT)[0-9]+"
        echo "AOT/trim warnings in $s" >&2
        exit 1
    fi
done
demo/oops95/build.sh publish
echo "ci: ok"
