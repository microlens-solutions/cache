using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Microlens.Cache.Services;

internal static class Guard {
    internal static void NotNull<T>([NotNull] T? argument, [CallerArgumentExpression(nameof(argument))] string? name = null) {
        if (argument is null) {
            throw new ArgumentNullException(name);
        }
    }

    // Null means "not set" and passes; a set value must be strictly positive.
    internal static void Positive(TimeSpan? argument, [CallerArgumentExpression(nameof(argument))] string? name = null) {
        if (argument is not { } value) {
            return;
        }

#if NET8_0_OR_GREATER
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero, name);
#else
        if (value <= TimeSpan.Zero) {
            throw new ArgumentOutOfRangeException(name, value, "Must be greater than zero.");
        }
#endif
    }
}
