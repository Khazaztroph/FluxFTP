using IoFtp.Engine.Abstractions;
using IoFtp.Engine.Models;

namespace IoFtp.Engine.Scheduling;

public sealed class GlobalTransferEngine : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly ITransferExecutor _executor;
    private readonly Dictionary<Guid, SiteRuntime> _sites = [];
    private readonly Dictionary<Guid, WorkRuntime> _work = [];
    private readonly CancellationTokenSource _shutdown = new();
    private bool _pumpScheduled;
    private int _maxLocalDownloads = 1, _maxLocalUploads = 1, _activeLocalDownloads, _activeLocalUploads;

    public GlobalTransferEngine(ITransferExecutor executor) => _executor = executor;
    public event EventHandler? StateChanged;

    public void ConfigureLocalSlots(int maxDownloads, int maxUploads)
    {
        if (maxDownloads < 0 || maxUploads < 0) throw new ArgumentOutOfRangeException();
        lock (_gate) { _maxLocalDownloads = maxDownloads; _maxLocalUploads = maxUploads; }
        RequestPump();
    }

    public void RegisterOrUpdateSite(SitePolicy policy)
    {
        if (policy.MaxSlots < 1 || policy.MaxDownloads < 0 || policy.MaxUploads < 0)
            throw new ArgumentOutOfRangeException(nameof(policy), "Slot limits must be valid.");
        lock (_gate)
        {
            if (_sites.TryGetValue(policy.SiteId, out var runtime)) { runtime.Policy = policy; runtime.Enabled = true; }
            else _sites.Add(policy.SiteId, new SiteRuntime(policy));
        }
        RequestPump();
    }

    public void DisconnectSite(Guid siteId)
    {
        lock (_gate)
        {
            if (_sites.TryGetValue(siteId, out var site)) site.Enabled = false;
            foreach (var work in _work.Values.Where(work => work.Item.SourceSiteId == siteId || work.Item.DestinationSiteId == siteId))
            {
                if (work.State == TransferWorkState.Running) work.Cancellation?.Cancel();
                else if (work.State == TransferWorkState.Queued) work.State = TransferWorkState.Paused;
            }
        }
        RaiseStateChanged();
    }

    public void Enqueue(IEnumerable<TransferWorkItem> items)
    {
        lock (_gate)
            foreach (var item in items)
                if (!_work.ContainsKey(item.Id)) _work.Add(item.Id, new WorkRuntime(item));
        RaiseStateChanged(); RequestPump();
    }

    public void Pause(Guid workId)
    {
        lock (_gate)
        {
            if (!_work.TryGetValue(workId, out var work)) return;
            if (work.State == TransferWorkState.Running) work.Cancellation?.Cancel();
            else if (work.State == TransferWorkState.Queued) work.State = TransferWorkState.Paused;
        }
        RaiseStateChanged();
    }

    public void Resume(Guid workId)
    {
        lock (_gate)
            if (_work.TryGetValue(workId, out var work) && work.State is TransferWorkState.Paused or TransferWorkState.Failed)
            { work.State = TransferWorkState.Queued; work.Error = null; }
        RaiseStateChanged(); RequestPump();
    }

    public void Remove(Guid workId)
    {
        lock (_gate)
        {
            if (!_work.TryGetValue(workId, out var work)) return;
            work.Cancellation?.Cancel();
            _work.Remove(workId);
        }
        RaiseStateChanged();
    }

    public void Clear()
    {
        lock (_gate)
        {
            foreach (var work in _work.Values) work.Cancellation?.Cancel();
            _work.Clear();
        }
        RaiseStateChanged();
    }

    public IReadOnlyList<TransferWorkStatus> Snapshot()
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            return _work.Values.Select(work => new TransferWorkStatus(work.Item, work.State, Score(work.Item, now), work.Error))
                .OrderByDescending(status => status.Score).ToList();
        }
    }

    public void NotifySiteStateChanged() => RequestPump();

    private void RequestPump()
    {
        lock (_gate) { if (_pumpScheduled) return; _pumpScheduled = true; }
        _ = Task.Run(PumpAsync);
    }

    private Task PumpAsync()
    {
        while (true)
        {
            WorkRuntime? selected; SlotReservation? reservation;
            lock (_gate)
            {
                _pumpScheduled = false;
                var now = DateTimeOffset.UtcNow;
                selected = _work.Values.Where(work => work.State == TransferWorkState.Queued && IsAllowed(work.Item))
                    .OrderByDescending(work => Score(work.Item, now)).FirstOrDefault(work => CanReserve(work.Item));
                if (selected is null) return Task.CompletedTask;
                reservation = Reserve(selected.Item);
                selected.State = TransferWorkState.Running;
                selected.Cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                _pumpScheduled = true;
            }
            RaiseStateChanged();
            _ = ExecuteAsync(selected, reservation!);
        }
    }

    private async Task ExecuteAsync(WorkRuntime work, SlotReservation reservation)
    {
        try
        {
            await _executor.ExecuteAsync(work.Item, work.Cancellation!.Token);
            lock (_gate) work.State = TransferWorkState.Completed;
        }
        catch (OperationCanceledException) when (work.Cancellation?.IsCancellationRequested == true)
        { lock (_gate) work.State = TransferWorkState.Paused; }
        catch (Exception exception)
        { lock (_gate) { work.State = TransferWorkState.Failed; work.Error = exception.Message; } }
        finally
        {
            lock (_gate)
            {
                Release(reservation); work.Cancellation?.Dispose(); work.Cancellation = null;
            }
            RaiseStateChanged(); RequestPump();
        }
    }

    private bool IsAllowed(TransferWorkItem item)
    {
        if (item.SourceSiteId is { } source && (!_sites.TryGetValue(source, out var sourceRuntime) || !sourceRuntime.Enabled)) return false;
        if (item.DestinationSiteId is { } destination && (!_sites.TryGetValue(destination, out var destinationRuntime) || !destinationRuntime.Enabled)) return false;
        if (item.SourceSiteId is { } a && item.DestinationSiteId is { } b)
            return _sites[a].Policy.AllowsTarget(b) && _sites[b].Policy.AllowsSource(a);
        return true;
    }

    private bool CanReserve(TransferWorkItem item) =>
        (item.SourceSiteId is { } source ? _sites[source].CanDownload : _activeLocalUploads < _maxLocalUploads) &&
        (item.DestinationSiteId is { } destination ? _sites[destination].CanUpload : _activeLocalDownloads < _maxLocalDownloads);

    private SlotReservation Reserve(TransferWorkItem item)
    {
        if (item.SourceSiteId is { } source) { _sites[source].ActiveSlots++; _sites[source].ActiveDownloads++; }
        else _activeLocalUploads++;
        if (item.DestinationSiteId is { } destination) { _sites[destination].ActiveSlots++; _sites[destination].ActiveUploads++; }
        else _activeLocalDownloads++;
        return new SlotReservation(item.SourceSiteId, item.DestinationSiteId);
    }

    private void Release(SlotReservation reservation)
    {
        if (reservation.Source is { } source) { _sites[source].ActiveSlots--; _sites[source].ActiveDownloads--; }
        else _activeLocalUploads--;
        if (reservation.Destination is { } destination) { _sites[destination].ActiveSlots--; _sites[destination].ActiveUploads--; }
        else _activeLocalDownloads--;
    }

    private double Score(TransferWorkItem item, DateTimeOffset now) => TransferScore.Calculate(item,
        item.SourceSiteId is { } source && _sites.TryGetValue(source, out var sourceSite) ? sourceSite.Policy : null,
        item.DestinationSiteId is { } destination && _sites.TryGetValue(destination, out var destinationSite) ? destinationSite.Policy : null, now);

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
    public ValueTask DisposeAsync() { _shutdown.Cancel(); _shutdown.Dispose(); return ValueTask.CompletedTask; }

    private sealed class SiteRuntime(SitePolicy policy)
    {
        public SitePolicy Policy { get; set; } = policy;
        public int ActiveSlots { get; set; }
        public int ActiveDownloads { get; set; }
        public int ActiveUploads { get; set; }
        public bool Enabled { get; set; } = true;
        public bool CanDownload => Enabled && ActiveSlots < Policy.MaxSlots && ActiveDownloads < Policy.MaxDownloads;
        public bool CanUpload => Enabled && ActiveSlots < Policy.MaxSlots && ActiveUploads < Policy.MaxUploads;
    }
    private sealed class WorkRuntime(TransferWorkItem item)
    {
        public TransferWorkItem Item { get; } = item;
        public TransferWorkState State { get; set; } = TransferWorkState.Queued;
        public string? Error { get; set; }
        public CancellationTokenSource? Cancellation { get; set; }
    }
    private sealed record SlotReservation(Guid? Source, Guid? Destination);
}
