namespace LintelAI.Services.Security.Advanced;

/// <summary>
/// How much the model reads. Every level has the same safeguards; a deeper level only gives the
/// model more files to look at, so it takes longer and can find more.
/// </summary>
public enum AiDepth
{
    /// <summary>Judges the medium alerts the rules raised. Nothing else is read.</summary>
    Quick,

    /// <summary>Quick, plus the first part of a few risky files, looking for what the rules missed.</summary>
    Balanced,

    /// <summary>Balanced, over many more files and their whole length.</summary>
    Thorough
}

/// <param name="Discover">Whether to read files looking for things the rules missed.</param>
/// <param name="Files">The most files read in that search.</param>
/// <param name="Parts">The most pieces of each file read (a piece is one settings-sized chunk).</param>
public sealed record DepthPlan(bool Discover, int Files, int Parts)
{
    public static DepthPlan For(AiDepth depth) => depth switch
    {
        AiDepth.Quick => new DepthPlan(false, 0, 0),
        AiDepth.Thorough => new DepthPlan(true, 25, 3),
        _ => new DepthPlan(true, 8, 1)
    };
}
