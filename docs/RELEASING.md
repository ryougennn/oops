# Releasing

1. Set the version: `<Version>` in `Directory.Build.props` (or pass it to the pack script), and move the
   `CHANGELOG.md` entry from "unreleased" to the version and date.
2. Run `tools/ci.sh`.
3. `tools/pack.sh 0.1.0` builds every package into `artifacts/packages`, with symbol packages
   (`.snupkg`) and deterministic builds. Check a package by referencing `artifacts/packages` from a
   fresh project (see `nuget.config` in any demo).
4. Publish. The normal way is to push a version tag: `.github/workflows/release.yml` runs the unit tests,
   packs, and publishes with nuget.org Trusted Publishing, so no API key is stored anywhere. One-time setup:
   - nuget.org > your profile > Trusted Publishing > add a policy: Repository Owner and Repository of this
     GitHub repo, Workflow File `release.yml`, Environment `release`.
   - GitHub repo > Settings > Secrets: `NUGET_USER` = your nuget.org profile name (not your email).
   - Optionally, Settings > Environments > `release` > Required reviewers, to approve each release.

   Then `git tag v0.1.0 && git push origin v0.1.0`. The `.snupkg` symbol packages go up with the packages.
   A version can never be reused on nuget.org, only unlisted.

   Without CI, an API key from nuget.org (scoped to `oopshell*`) works from the command line:
   `dotnet nuget push "artifacts/packages/*.nupkg" --api-key <key> --source https://api.nuget.org/v3/index.json`.
5. Build the docs (`tools/docs.sh`) for hosting.
