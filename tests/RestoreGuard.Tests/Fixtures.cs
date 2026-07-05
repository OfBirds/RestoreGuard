namespace RestoreGuard.Tests;

/// <summary>
/// Golden files captured from the real lab on 2026-07-04 (secrets redacted).
/// See docs/live-verification-2026-06-29.md for the lab ground truth they encode.
/// </summary>
public static class Fixtures
{
    public static string Read(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}
