namespace IoFtp.Engine.Models;

public enum TransferWorkState { Queued, Running, Paused, Completed, Failed }

public sealed record TransferWorkItem(
    Guid Id,
    Guid JobId,
    string Name,
    Guid? SourceSiteId,
    Guid? DestinationSiteId,
    string SourcePath,
    string DestinationPath,
    long Size,
    int JobPriority = 0,
    double TargetProgress = 0,
    double UploadedByUserRatio = 0,
    DateTimeOffset? QueuedAt = null);

public sealed record TransferWorkStatus(
    TransferWorkItem Item,
    TransferWorkState State,
    double Score,
    string? Error = null);
