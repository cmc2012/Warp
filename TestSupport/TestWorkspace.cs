namespace Warp.Testing;

/// <summary>
/// Creates an isolated filesystem workspace for a test.
/// Test data stays alongside the test assembly instead of sharing the system temporary directory.
/// </summary>
internal static class TestWorkspace
{
    public static string CreateDirectory(string name)
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            ".test-workspaces",
            name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
