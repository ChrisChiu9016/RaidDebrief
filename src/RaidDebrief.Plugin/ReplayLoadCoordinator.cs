using System;
using System.Threading;
using System.Threading.Tasks;
using RaidDebrief.Core;

namespace RaidDebrief.Plugin;

internal sealed class ReplayLoadCoordinator : IDisposable
{
    private readonly Func<PullRecord, CancellationToken, ReplaySession> createSession;
    private PendingReplayLoad? pending;
    private long requestGeneration;
    private bool disposed;

    public ReplayLoadCoordinator(Func<PullRecord, ReplaySession>? createSession = null)
    {
        this.createSession = createSession is null
            ? static (record, _) => new ReplaySession(record)
            : (record, _) => createSession(record);
    }

    internal ReplayLoadCoordinator(
        Func<PullRecord, CancellationToken, ReplaySession> createSession)
    {
        this.createSession = createSession
            ?? throw new ArgumentNullException(nameof(createSession));
    }

    public bool IsLoading => this.pending is not null;

    public ReplaySourceMode? PendingMode => this.pending?.Mode;

    public long? PendingSourceGeneration => this.pending?.SourceGeneration;

    public Guid? PendingCaptureId => this.pending?.CaptureId;

    public void Start(
        PullRecord record,
        ReplaySourceMode mode,
        long sourceGeneration,
        string sourceDetail,
        string successMessage)
    {
        ArgumentNullException.ThrowIfNull(record);
        this.Start(
            () => record,
            record.CaptureId,
            mode,
            sourceGeneration,
            sourceDetail,
            successMessage);
    }

    public void Start(
        Func<PullRecord> loadRecord,
        Guid captureId,
        ReplaySourceMode mode,
        long sourceGeneration,
        string sourceDetail,
        string successMessage)
    {
        this.ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(loadRecord);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDetail);
        ArgumentException.ThrowIfNullOrWhiteSpace(successMessage);

        this.CancelPending();
        var requestGeneration = ++this.requestGeneration;
        var cancellation = new CancellationTokenSource();
        var cancellationToken = cancellation.Token;
        var task = Task.Run(() =>
        {
            var resolvedCaptureId = captureId;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var record = loadRecord();
                resolvedCaptureId = record.CaptureId;
                cancellationToken.ThrowIfCancellationRequested();
                var session = this.createSession(record, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                return new ReplayLoadCompletion(
                    requestGeneration,
                    mode,
                    sourceGeneration,
                    record.CaptureId,
                    sourceDetail,
                    successMessage,
                    session,
                    null);
            }
            catch (Exception exception)
            {
                return new ReplayLoadCompletion(
                    requestGeneration,
                    mode,
                    sourceGeneration,
                    resolvedCaptureId,
                    sourceDetail,
                    successMessage,
                    null,
                    exception);
            }
        });
        this.pending = new PendingReplayLoad(
            requestGeneration,
            mode,
            sourceGeneration,
            captureId,
            task,
            cancellation);
    }

    public void Invalidate()
    {
        if (this.disposed)
        {
            return;
        }

        this.requestGeneration++;
        this.CancelPending();
    }

    public bool TryTakeCompleted(out ReplayLoadCompletion completion)
    {
        if (this.disposed || this.pending is not { Task.IsCompleted: true } pending)
        {
            completion = default;
            return false;
        }

        completion = pending.Task.GetAwaiter().GetResult();
        pending.Cancellation.Dispose();
        this.pending = null;
        return completion.RequestGeneration == pending.RequestGeneration;
    }

    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        this.requestGeneration++;
        this.CancelPending();
    }

    private void CancelPending()
    {
        if (this.pending is not { } pending)
        {
            return;
        }

        this.pending = null;
        pending.Cancellation.Cancel();
        _ = pending.Task.ContinueWith(
            static (_, state) => ((CancellationTokenSource)state!).Dispose(),
            pending.Cancellation,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
    }

    private sealed record PendingReplayLoad(
        long RequestGeneration,
        ReplaySourceMode Mode,
        long SourceGeneration,
        Guid CaptureId,
        Task<ReplayLoadCompletion> Task,
        CancellationTokenSource Cancellation);
}

internal readonly record struct ReplayLoadCompletion(
    long RequestGeneration,
    ReplaySourceMode Mode,
    long SourceGeneration,
    Guid CaptureId,
    string SourceDetail,
    string SuccessMessage,
    ReplaySession? Session,
    Exception? Error);
