# Releasing

1. Set the version: `<Version>` in `Directory.Build.props` (or pass it to the pack script), and move the
   `CHANGELOG.md` entry from "unreleased" to the version and date.
2. Run `tools/ci.sh`.
3. `tools/pack.sh 0.1.0` builds every package into `artifacts/packages`, with symbol packages
   (`.snupkg`) and deterministic builds. Check a package by referencing `artifacts/packages` from a
   fresh project (see `nuget.config` in any demo).
4. Publish (needs an API key from nuget.org, scoped to the `oopshell*` packages):

   ```sh
   dotnet nuget push "artifacts/packages/*.nupkg" --api-key <key> --source https://api.nuget.org/v3/index.json
   ```

   The `.snupkg` symbol packages are uploaded alongside automatically. A version can never be reused on
   nuget.org, only unlisted.
5. Tag the commit (`git tag v0.1.0`) and build the docs (`tools/docs.sh`) for hosting.
