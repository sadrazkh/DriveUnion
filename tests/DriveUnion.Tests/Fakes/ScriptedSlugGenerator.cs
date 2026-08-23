using DriveUnion.Core.Sharing;

namespace DriveUnion.Tests.Fakes;

/// <summary>
/// Hands out slugs in a written order, then falls back to the real generator.
///
/// A collision is a one-in-2.8-trillion event, which means the retry that handles it is never
/// exercised by chance. Scripting the collision is the only way to find out whether it produces a
/// working link or a 500 on somebody's first share.
/// </summary>
public sealed class ScriptedSlugGenerator(params string[] slugs) : ISlugGenerator
{
    private readonly Queue<string> _scripted = new(slugs);
    private readonly SlugGenerator _fallback = new();

    public int CallCount { get; private set; }

    public string Next()
    {
        CallCount++;
        return _scripted.Count > 0 ? _scripted.Dequeue() : _fallback.Next();
    }
}
