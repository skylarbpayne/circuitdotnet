using Xunit;

namespace Circuit.Interop.Tests;

/// <summary>Smoke tests for the Circuit package.</summary>
public sealed class SmokeTests
{
    /// <summary>Verifies the package test assembly builds and runs.</summary>
    [Fact]
    public void Circuit_package_builds()
    {
        Assert.True(true);
    }
}
