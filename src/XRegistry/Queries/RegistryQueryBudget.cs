// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

namespace XRegistry.Queries;

/// <summary>A caller-owned cumulative budget shared by evaluation, source calls and host preparation.</summary>
/// <remarks>Not thread-safe. Dispose after evaluation and projection; disposal does not dispose the source.</remarks>
public sealed class RegistryQueryBudget : IDisposable
{
    private readonly TimeProvider _clock;
    private readonly CancellationToken _callerToken;
    private readonly CancellationTokenSource _deadline;
    private readonly CancellationTokenSource _linked;
    private readonly long _started;
    private long _work;
    private long _sourceBytes;
    private long _factBytes;
    private int _sourceReads;
    private int _members;
    private int _facts;
    private bool _disposed;

    /// <summary>Starts a finite deadline. Reuse this instance to share limits across evaluations and host work.</summary>
    public RegistryQueryBudget(RegistryQueryEvaluationLimits? limits = null, TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        Limits = limits ?? new();
        Limits.Validate();
        _clock = timeProvider ?? TimeProvider.System;
        _callerToken = cancellationToken;
        _started = _clock.GetTimestamp();
        _deadline = new(Limits.MaxDuration, _clock);
        _linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _deadline.Token);
    }

    /// <summary>Gets the immutable limits for this budget.</summary>
    public RegistryQueryEvaluationLimits Limits { get; }
    /// <summary>Gets caller cancellation combined with the shared deadline, for cooperative source work.</summary>
    public CancellationToken CancellationToken => _linked.Token;
    /// <summary>Gets the remaining monotonic duration; reads do not extend it.</summary>
    public TimeSpan Remaining => Limits.MaxDuration - _clock.GetElapsedTime(_started, _clock.GetTimestamp());

    /// <summary>Charges work and checks cancellation/deadline. Exhaustion is too_large or server_busy.</summary>
    public void Spend(long count = 1)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        _callerToken.ThrowIfCancellationRequested();
        if (count > Limits.MaxWork - _work)
        {
            throw Diagnostics.Error("too_large", "", "The query work budget is exhausted.");
        }

        _work += count;
        if (_deadline.IsCancellationRequested || Remaining <= TimeSpan.Zero)
        {
            throw Diagnostics.Error("server_busy", "", "The query preparation deadline is exhausted.");
        }
    }

    /// <summary>Executes a cooperative source/policy callback within this same deadline; no worker task is launched.</summary>
    public async ValueTask<T> RunAsync<T>(Func<CancellationToken, ValueTask<T>> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        Spend();
        try
        {
            var result = await action(_linked.Token).AsTask().WaitAsync(Remaining, _clock, _linked.Token).ConfigureAwait(false);
            Spend(0);
            return result;
        }
        catch (TimeoutException exception)
        {
            await _deadline.CancelAsync().ConfigureAwait(false);
            _callerToken.ThrowIfCancellationRequested();
            throw new RegistryException(new("server_busy", "", "The query callback deadline is exhausted."), exception);
        }
        catch (OperationCanceledException exception) when (!_callerToken.IsCancellationRequested)
        {
            throw new RegistryException(new("server_busy", "", "The query callback did not complete before its deadline."), exception);
        }
    }

    /// <summary>Executes a cooperative preparation callback using the same cumulative deadline.</summary>
    public async ValueTask RunAsync(Func<CancellationToken, ValueTask> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        await RunAsync(async ct =>
        {
            await action(ct).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);
    }

    internal async ValueTask<T> ReadAsync<T>(Func<CancellationToken, ValueTask<T>> action)
    {
        Spend();
        try
        {
            // A source owns live leases/enumerators. Observe its cooperative cancellation and
            // completion before disposal, rather than racing DisposeAsync against MoveNextAsync.
            var result = await action(_linked.Token).ConfigureAwait(false);
            Spend(0);
            return result;
        }
        catch (OperationCanceledException exception) when (!_callerToken.IsCancellationRequested)
        {
            throw new RegistryException(new("server_busy", "", "The query source deadline is exhausted."), exception);
        }
    }

    internal void SourceRead()
    {
        Spend();
        if (_sourceReads++ >= Limits.MaxSourceReads)
        {
            throw Diagnostics.Error("too_large", "", "The query source-read budget is exhausted.");
        }
    }

    internal void SourceBytes(long bytes)
    {
        Spend(0);
        if (bytes > Limits.MaxSourceBytes - _sourceBytes)
        {
            throw Diagnostics.Error("too_large", "", "The query source byte budget is exhausted.");
        }

        _sourceBytes += bytes;
    }

    internal void Member(string path)
    {
        Spend();
        if (_members++ >= Limits.MaxCollectionMembers)
        {
            throw Diagnostics.Error("too_large", "", "The query collection-member budget is exhausted.");
        }

        SourceBytes(System.Text.Encoding.UTF8.GetByteCount(path));
    }

    internal void Fact()
    {
        Spend();
        if (_facts++ >= Limits.MaxEntities)
        {
            throw Diagnostics.Error("too_large", "", "The query entity-fact budget is exhausted.");
        }
    }

    internal void FactBytes(long bytes)
    {
        Spend(0);
        if (bytes > Limits.MaxFactBytes - _factBytes)
        {
            throw Diagnostics.Error("too_large", "", "The query entity-fact byte budget is exhausted.");
        }

        _factBytes += bytes;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _linked.Dispose();
            _deadline.Dispose();
        }
    }
}
