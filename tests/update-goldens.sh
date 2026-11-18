#!/bin/sh
# Regenerates golden screenshots and scanner output. Review the diff before committing.
set -e
cd "$(dirname "$0")/.."
UPDATE_GOLDENS=1 dotnet test --project tests/oopshell.Scanner.Tests
UPDATE_GOLDENS=1 dotnet test --project tests/oopshell.IntegrationTests
