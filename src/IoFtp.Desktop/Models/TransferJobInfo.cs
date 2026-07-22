namespace IoFtp.Desktop.Models;

internal sealed record TransferJobInfo(
    Guid Id,
    string Queued,
    string Started,
    string Use,
    string Type,
    string Name,
    string Route,
    string Size,
    string Files,
    string Left,
    string Speed,
    string Done,
    string State,
    double ProgressPercent);
