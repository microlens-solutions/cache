using Microlens.Cache.Contracts;
using System;
using System.Diagnostics;
using System.Threading;

namespace Microlens.Cache.Models;

internal abstract class EntryBase {
    private readonly CancellationTokenSource? _cancellation;

    private int _settled;

    protected EntryBase(CacheExpiration expiration, bool cancellable) {
        Expiration = expiration;
        _cancellation = cancellable ? new CancellationTokenSource() : null;
    }

    internal readonly long CreatedAt = Stopwatch.GetTimestamp();

    internal readonly CacheExpiration Expiration;

    internal int Frequency;

    internal abstract Type ValueType { get; }

    internal CancellationToken Token => _cancellation?.Token ?? CancellationToken.None;

    internal void Cancel() {
        if (_cancellation is null || Interlocked.Exchange(ref _settled, 1) != 0) {
            return;
        }

        _cancellation.Cancel();
    }

    internal void Release() {
        if (_cancellation is null || Interlocked.Exchange(ref _settled, 1) != 0) {
            return;
        }

        _cancellation.Dispose();
    }
}
