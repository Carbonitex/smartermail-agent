using System.Runtime.CompilerServices;

// Serial on purpose. Several in-process hosts (WebApplicationFactory, CatalogFixture) building the
// tool registry at the same moment race inside Microsoft.Extensions.AI's static function-descriptor
// cache, and a tool can come out with userContext in its schema. One process has one registry, so
// production never sees it; the suite takes a few seconds either way.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace SmarterMailAgent.Tests;

/// <summary>
/// Runs once, before any test: every in-process agent (<c>WebApplicationFactory&lt;Program&gt;</c>)
/// starts in server mode by default, so point its DATA_DIR at a throwaway directory instead of the
/// project tree. Tests that need their own directory or mode override it with <c>UseSetting</c>.
/// </summary>
internal static class TestEnvironment
{
    public static string DataDir { get; } = Path.Combine(Path.GetTempPath(), $"sma-tests-{Guid.NewGuid():N}");

    [ModuleInitializer]
    internal static void Initialize() => Environment.SetEnvironmentVariable("DATA_DIR", DataDir);

    /// <summary>A fresh, empty directory for one test.</summary>
    public static string NewDataDir() => Path.Combine(Path.GetTempPath(), $"sma-test-{Guid.NewGuid():N}");
}
