using IoFtp.Engine.Models;

namespace IoFtp.Engine.Abstractions;

public interface ITransferExecutor
{
    Task ExecuteAsync(TransferWorkItem item, CancellationToken cancellationToken);
}
