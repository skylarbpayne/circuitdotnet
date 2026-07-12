# Contributing

1. Install the .NET 10.0.301 SDK through `mise`.
2. Restore local tools with `mise exec dotnet@10.0.301 -- dotnet tool restore --tool-manifest dotnet-tools.json`.
3. Restore packages in locked mode with `mise exec dotnet@10.0.301 -- dotnet restore CircuitDotNet.slnx --locked-mode`.
4. Run `mise exec dotnet@10.0.301 -- dotnet fantomas . --check --recurse` for F# formatting checks.
5. Run `mise exec dotnet@10.0.301 -- dotnet build CircuitDotNet.slnx -c Release --no-restore`.
6. Run `mise exec dotnet@10.0.301 -- dotnet test CircuitDotNet.slnx -c Release --no-restore --filter "Category!=Package"`.
7. Pack and run package smoke before release-sensitive changes:
   - `mise exec dotnet@10.0.301 -- dotnet pack CircuitDotNet.slnx -c Release --no-restore -o artifacts/packages`
   - `PackageDirectory=$PWD/artifacts/packages mise exec dotnet@10.0.301 -- dotnet test CircuitDotNet.slnx -c Release --no-build --filter "Category=Package"`
8. See `docs/reference/ci-and-release.md` for the full CI, compatibility, provider-contract, and release gates.
