namespace IoFtp.Engine.Models;

public sealed record SitePolicy(
    Guid SiteId,
    string Name,
    int MaxSlots = 2,
    int MaxDownloads = 2,
    int MaxUploads = 2,
    int Priority = 0,
    IReadOnlySet<Guid>? AllowedPartners = null,
    IReadOnlySet<Guid>? BlockedPartners = null,
    IReadOnlySet<Guid>? BlockedSources = null,
    IReadOnlySet<Guid>? BlockedTargets = null)
{
    private bool Allows(Guid partner) =>
        (AllowedPartners is null || AllowedPartners.Count == 0 || AllowedPartners.Contains(partner)) &&
        (BlockedPartners is null || !BlockedPartners.Contains(partner));
    public bool AllowsSource(Guid partner) => Allows(partner) && (BlockedSources is null || !BlockedSources.Contains(partner));
    public bool AllowsTarget(Guid partner) => Allows(partner) && (BlockedTargets is null || !BlockedTargets.Contains(partner));
}
