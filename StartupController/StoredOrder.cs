using System.Collections.Frozen;

namespace StartupController
{
    // Immutable snapshot of the list: every name in display order, plus the names StartupController launches.
    // Name comparisons are case-insensitive, like registry value names.
    public sealed record StoredOrder(IReadOnlyList<string> Order, IReadOnlySet<string> Enabled)
    {
        public static StoredOrder Empty { get; } = Create(Array.Empty<string>(), Array.Empty<string>());

        // Copies the inputs into read-only collections that can't be cast back to something mutable
        public static StoredOrder Create(IEnumerable<string> order, IEnumerable<string> enabled)
        {
            return new StoredOrder(
                Array.AsReadOnly(order.ToArray()),
                enabled.ToFrozenSet(StringComparer.OrdinalIgnoreCase));
        }

        // Enabled names in display order (what the current StartupOrder format stores)
        public IReadOnlyList<string> EnabledInOrder()
        {
            return Order.Where(Enabled.Contains).ToList();
        }
    }
}
