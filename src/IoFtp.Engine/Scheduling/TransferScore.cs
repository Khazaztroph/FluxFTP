using IoFtp.Engine.Models;

namespace IoFtp.Engine.Scheduling;

public static class TransferScore
{
    public static double Calculate(TransferWorkItem item, SitePolicy? source, SitePolicy? destination, DateTimeOffset now)
    {
        var age = Math.Max(0, (now - (item.QueuedAt ?? now)).TotalSeconds);
        var sizeBenefit = item.Size > 0 ? Math.Log2(item.Size + 1) : 0;
        var targetNeed = (1 - Math.Clamp(item.TargetProgress, 0, 1)) * 200;
        var userShare = Math.Clamp(item.UploadedByUserRatio, 0, 1) * 75;
        return item.JobPriority * 1000
             + (destination?.Priority ?? 0) * 100
             + (source?.Priority ?? 0) * 25
             + targetNeed + userShare + sizeBenefit + Math.Min(age, 300);
    }
}
