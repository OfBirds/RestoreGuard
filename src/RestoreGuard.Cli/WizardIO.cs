namespace RestoreGuard.Cli;

/// <summary>
/// The wizard's console seam. Production uses the real console; tests inject a
/// scripted reader + capture writer, which is what makes every interactive flow
/// (retry loops, keep-anyway, skipped sections) assertable.
/// </summary>
public sealed class WizardIO(TextReader input, TextWriter output)
{
    public static WizardIO RealConsole { get; } = new(Console.In, Console.Out);

    public string? ReadLine() => input.ReadLine();

    public void Write(string text) => output.Write(text);

    public void WriteLine(string text = "") => output.WriteLine(text);
}
