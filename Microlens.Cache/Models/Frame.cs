namespace Microlens.Cache.Models;

internal sealed class Frame(EntryBase current, Frame? parent) {
    internal readonly EntryBase Current = current;

    internal readonly Frame? Parent = parent;
}
