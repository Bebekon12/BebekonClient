namespace Bebekon.Core;

/// <summary>Deferring a release lasts only for this application session.</summary>
public sealed class UpdatePromptSession
{
    private readonly HashSet<string> deferred = new(StringComparer.Ordinal);
    public bool ShouldShow(string? version, bool explicitRequest = false) => version is not null && (explicitRequest || !deferred.Contains(version));
    public void Defer(string version) => deferred.Add(version);
}
