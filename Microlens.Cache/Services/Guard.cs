using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Microlens.Cache.Services;

internal static class Guard {
    internal static void NotNull<T>([NotNull] T? argument, [CallerArgumentExpression(nameof(argument))] string? name = null) {
        if (argument is null) {
            throw new ArgumentNullException(name);
        }
    }
}
