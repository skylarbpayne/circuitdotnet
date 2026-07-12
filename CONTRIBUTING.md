# Contributing

1. Install the .NET 10.0.301 SDK through `mise`.
2. Run `mise exec dotnet@10.0.301 -- dotnet restore`.
3. Run `mise exec dotnet@10.0.301 -- dotnet build` and `mise exec dotnet@10.0.301 -- dotnet test` before opening a PR.
4. Use `mise exec dotnet@10.0.301 -- dotnet tool run fantomas .` for F# formatting checks.
