namespace Microlens.Cache.Models;

internal class Victim(object key, EntryBase entry) {
    internal static readonly Comparison<Victim> Order = static (x, y) => x.Frequency != y.Frequency ? x.Frequency.CompareTo(y.Frequency) : x.CreatedAt.CompareTo(y.CreatedAt);

    internal readonly object Key = key;

    internal readonly EntryBase Entry = entry;

    internal readonly int Frequency = entry.Frequency;

    internal readonly long CreatedAt = entry.CreatedAt;
}
