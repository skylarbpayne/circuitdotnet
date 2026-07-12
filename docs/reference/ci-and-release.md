# CI and release gates

Circuit ships through four GitHub Actions workflows:

- `ci.yml` runs on pushes and pull requests. It restores in locked mode, checks Fantomas formatting, builds on Ubuntu and Windows, runs non-package tests, enforces exact branch-coverage gates on Ubuntu (Core >=90%, MAF adapter >=80%), builds docs, packs once on Ubuntu, runs package smoke tests, validates SourceLink, scans for vulnerable and deprecated packages, and verifies the working tree stays clean.
- `maf-compat.yml` runs on a nightly schedule or by hand. It resolves the newest `Microsoft.Agents.AI` and `Microsoft.Agents.AI.Workflows` `1.x` versions from NuGet, overrides the central package versions for that run, executes the MAF adapter test suite, and uploads a compatibility report artifact. It does not open issues automatically.
- `provider-contract.yml` is a manual, credential-gated workflow for live provider checks. It is disabled on forks. Each matrix entry builds the checked-in `tests/Circuit.ProviderContract` harness with provider packages enabled, enforces a hard cost cap, records artifacts under `artifacts/provider-contract/<provider>/<UTC-date>/`, and avoids logging prompts, model output, or secrets.
- `release.yml` is a manual workflow for protected publishing. It checks out an existing `vX.Y.Z` tag, reruns all offline gates, packs the five NuGet packages, emits a CycloneDX SBOM, creates a GitHub build provenance attestation, and only pushes `.nupkg` and `.snupkg` artifacts after environment approval.

## Local validation

Use the same SDK and local tool manifest as CI:

```bash
mise exec dotnet@10.0.301 -- dotnet tool restore --tool-manifest dotnet-tools.json
mise exec dotnet@10.0.301 -- dotnet restore CircuitDotNet.slnx --locked-mode
mise exec dotnet@10.0.301 -- dotnet build CircuitDotNet.slnx -c Release --no-restore
mise exec dotnet@10.0.301 -- dotnet test CircuitDotNet.slnx -c Release --no-restore --filter "Category!=Package"
mise exec dotnet@10.0.301 -- dotnet pack CircuitDotNet.slnx -c Release --no-restore -o artifacts/packages
PackageDirectory=$PWD/artifacts/packages mise exec dotnet@10.0.301 -- dotnet test CircuitDotNet.slnx -c Release --no-build --filter "Category=Package"
```

## Package smoke rules

The package smoke tests require `PackageDirectory` to point at the packed artifacts folder. They assert that:

- exactly five package IDs and five symbol packages exist;
- every package carries README, license, icon, XML docs, SourceLink-friendly source paths, and repository metadata;
- package contents do not leak test assets, lock files, local `/home/...` or `/tmp/...` paths, or common secret file patterns;
- fresh C# and F# console apps restore from the local feed, build with warnings as errors, and execute an offline typed agent/circuit scenario through `CircuitDotNet.Testing.ScriptedRuntime` from the packed nupkgs;
- the offline ticket-triage samples still run.

`dotnet sourcelink test` also runs in CI and release validation. That command needs a clean, commit-addressable source tree, so it is enforced in GitHub Actions even when local package smoke is run from an uncommitted branch.

## Release prerequisites

Before the first protected publish:

1. create and push the release tag in the form `vX.Y.Z`;
2. make sure the release environment is configured with the required approval rule;
3. add the `NUGET_API_KEY` secret to the repository or environment.
