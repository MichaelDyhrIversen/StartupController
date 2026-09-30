using System.Collections.Frozen;

namespace StartupController
{
    // Immutable snapshot of the list: every name in display order, plus the names StartupController launches.
    // Name comparisons are case-insensitive, like registry value names.
    // Fingerprints (D7): name -> SHA-256 hex of the Run value data that was approved when the name was enabled.
    // FingerprintsKnown is false only when no EnabledFingerprints value exists yet (migration): then a listed
    // enabled name without a fingerprint is accepted once and gets its fingerprint on the next save.
    public sealed record StoredOrder(
        IReadOnlyList<string> Order,
        IReadOnlySet<string> Enabled,
        IReadOnlyDictionary<string, string> Fingerprints,
        bool FingerprintsKnown)
    {
        public static StoredOrder Empty { get; } = Create(Array.Empty<string>(), Array.Empty<string>(), fingerprintsKnown: true);

        // Copies the inputs into read-only collections that can't be cast back to something mutable.
        // Duplicate fingerprint names: the first one wins.
        public static StoredOrder Create(
            IEnumerable<string> order,
            IEnumerable<string> enabled,
            IEnumerable<KeyValuePair<string, string>>? fingerprints = null,
            bool fingerprintsKnown = false)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in fingerprints ?? Enumerable.Empty<KeyValuePair<string, string>>())
                map.TryAdd(pair.Key, pair.Value);

            return new StoredOrder(
                Array.AsReadOnly(order.ToArray()),
                enabled.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
                map.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
                fingerprintsKnown);
        }

        // Enabled names in display order (what the EnabledPrograms value stores)
        public IReadOnlyList<string> EnabledInOrder()
        {
            return Order.Where(Enabled.Contains).ToList();
        }
    }
}
