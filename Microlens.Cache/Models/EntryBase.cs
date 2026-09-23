using Microlens.Cache.Contracts;
using System.Diagnostics;

namespace Microlens.Cache.Models;

internal abstract class EntryBase(CacheExpiration expiration) {
    internal readonly long CreatedAt = Stopwatch.GetTimestamp();

    internal readonly CacheExpiration Expiration = expiration;

    internal int Frequency;

    internal abstract Type ValueType { get; }
}
