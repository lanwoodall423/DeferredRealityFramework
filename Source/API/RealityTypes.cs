using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace DeferredReality.API
{
    /// <summary>Logical space occupied by a region. Custom layers use <see cref="RealityRegionId.CustomLayer"/>.</summary>
    public enum RealityLayer
    {
        Surface,
        Interior,
        Underground,
        Vehicle,
        Ship,
        Custom
    }

    /// <summary>Simulation fidelity. Observation is stored independently on the region.</summary>
    public enum RealityFidelity
    {
        Dormant,
        Statistical,
        Narrative,
        Materialized
    }

    /// <summary>Precision with which a fact is known or spatially protected.</summary>
    public enum RealityObservationPrecision
    {
        Rumor,
        Region,
        Habitat,
        Area,
        Edge,
        Cell,
        Exact
    }

    /// <summary>Lifecycle state of a persisted framework record.</summary>
    public enum RealityLifecycleState
    {
        Active,
        Dormant,
        Migrating,
        Orphaned,
        Quarantined,
        Retired
    }

    /// <summary>Categories understood by the framework scheduler.</summary>
    public enum RealityProcessKind
    {
        PopulationGrowth,
        Migration,
        Predation,
        Habitat,
        Season,
        SeedDispersal,
        WaterExchange,
        Expedition,
        ProviderDefined
    }

    /// <summary>Policy used when two established constraints cannot both be applied.</summary>
    public enum RealityConflictPolicy
    {
        ReportOnly,
        PreferEstablished,
        PreferHigherPriority,
        ProviderDefined
    }

    /// <summary>Resolution state of a deferred constraint.</summary>
    public enum RealityConstraintStatus
    {
        Unresolved,
        Resolved,
        Conflicted,
        Expired,
        Quarantined
    }

    /// <summary>Lifecycle of an identity-bearing anchor.</summary>
    public enum RealityAnchorLifecycle
    {
        Present,
        Traveling,
        Missing,
        Dead,
        Consumed,
        Retired,
        Quarantined
    }

    /// <summary>Durable state of an adjacent-region transfer transaction.</summary>
    public enum RealityTransferStatus
    {
        Prepared,
        Acquiring,
        Committing,
        Completed,
        RolledBack,
        Fallback
    }

    /// <summary>Capabilities advertised by a provider.</summary>
    [Flags]
    public enum RealityProviderCapability
    {
        None = 0,
        Regions = 1 << 0,
        Processes = 1 << 1,
        Populations = 1 << 2,
        Anchors = 1 << 3,
        Constraints = 1 << 4,
        Materialization = 1 << 5,
        Compression = 1 << 6,
        Observations = 1 << 7,
        Diagnostics = 1 << 8
    }

    /// <summary>Stable, deterministic identity helpers shared by all providers.</summary>
    public static class RealityDeterminism
    {
        /// <summary>Computes a stable FNV-style hash independent of the CLR process.</summary>
        public static int StableHash(string value)
        {
            unchecked
            {
                uint hash = 2166136261u;
                string text = value ?? string.Empty;
                for (int i = 0; i < text.Length; i++)
                {
                    hash ^= text[i];
                    hash *= 16777619u;
                }
                return (int)(hash ^ (hash >> 16));
            }
        }

        /// <summary>Combines stable string parts without relying on collection order or object hashes.</summary>
        public static int Combine(params string[] parts)
        {
            unchecked
            {
                int result = 17;
                for (int i = 0; i < (parts?.Length ?? 0); i++) result = result * 31 + StableHash(parts[i]);
                return result;
            }
        }

        /// <summary>Returns a non-negative deterministic seed for a provider operation.</summary>
        public static int Seed(string worldSeed, string regionId, string providerId, string operationId, long epoch)
        {
            int value = Combine(worldSeed, regionId, providerId, operationId, epoch.ToString(CultureInfo.InvariantCulture));
            return value == int.MinValue ? int.MaxValue : Math.Abs(value);
        }
    }

    /// <summary>Small deterministic RNG stream. It never reads or mutates Verse.Rand.</summary>
    public sealed class RealityRandomStream
    {
        private uint state;

        /// <summary>Creates a stream from a stable seed.</summary>
        public RealityRandomStream(int seed)
        {
            state = unchecked((uint)seed) + 0x9E3779B9u;
            if (state == 0u) state = 0xA341316Cu;
        }

        /// <summary>Returns the next unsigned value.</summary>
        public uint NextUInt()
        {
            unchecked
            {
                uint z = (state += 0x9E3779B9u);
                z = (z ^ (z >> 16)) * 0x85EBCA6Bu;
                z = (z ^ (z >> 13)) * 0xC2B2AE35u;
                return z ^ (z >> 16);
            }
        }

        /// <summary>Returns a value in the half-open integer interval.</summary>
        public int NextInt(int minimumInclusive, int maximumExclusive)
        {
            if (maximumExclusive <= minimumInclusive) return minimumInclusive;
            uint range = unchecked((uint)(maximumExclusive - minimumInclusive));
            return minimumInclusive + (int)(NextUInt() % range);
        }

        /// <summary>Returns a value in the half-open floating point interval.</summary>
        public float NextFloat(float minimumInclusive = 0f, float maximumExclusive = 1f)
        {
            if (maximumExclusive <= minimumInclusive) return minimumInclusive;
            float unit = (NextUInt() >> 8) / 16777216f;
            return minimumInclusive + unit * (maximumExclusive - minimumInclusive);
        }

        /// <summary>Returns true with the supplied probability.</summary>
        public bool Chance(float probability) => NextFloat() < Math.Max(0f, Math.Min(1f, probability));
    }

    /// <summary>Stable identity for a surface, interior, underground, vehicle, ship, or custom region.</summary>
    public readonly struct RealityRegionId : IEquatable<RealityRegionId>
    {
        private const string Prefix = "rr1";
        private readonly string serialized;

        /// <summary>World tile, or -1 when the layer has no world tile.</summary>
        public readonly int WorldTile;

        /// <summary>Logical layer.</summary>
        public readonly RealityLayer Layer;

        /// <summary>Provider-defined layer name for custom layers.</summary>
        public readonly string CustomLayer;

        /// <summary>Stable local slot, such as a water body or portal slot.</summary>
        public readonly string LocalSlot;

        /// <summary>Stable instance identity within the local slot.</summary>
        public readonly string InstanceId;

        /// <summary>Serialized parent region identity, if any.</summary>
        public readonly string ParentRegionId;

        /// <summary>Namespace owning the identity.</summary>
        public readonly string ProviderNamespace;

        /// <summary>Creates a stable region identity. Map.uniqueID must not be passed as a durable component.</summary>
        public RealityRegionId(int worldTile, RealityLayer layer, string localSlot, string instanceId = null,
            string parentRegionId = null, string providerNamespace = "core", string customLayer = null)
        {
            WorldTile = worldTile;
            Layer = layer;
            CustomLayer = Normalize(customLayer);
            LocalSlot = Normalize(localSlot);
            InstanceId = Normalize(instanceId);
            ParentRegionId = Normalize(parentRegionId);
            ProviderNamespace = Normalize(providerNamespace) ?? "core";
            serialized = BuildSerialized(WorldTile, Layer, CustomLayer, LocalSlot, InstanceId, ParentRegionId, ProviderNamespace);
        }

        /// <summary>Creates the canonical surface region for a world tile.</summary>
        public static RealityRegionId Surface(int worldTile, string parentRegionId = null, string providerNamespace = "core")
        {
            return new RealityRegionId(worldTile, RealityLayer.Surface, "surface", null, parentRegionId, providerNamespace);
        }

        /// <summary>Creates an identity for a provider-owned local region.</summary>
        public static RealityRegionId Child(RealityRegionId parent, RealityLayer layer, string localSlot,
            string instanceId, string providerNamespace, string customLayer = null)
        {
            return new RealityRegionId(parent.WorldTile, layer, localSlot, instanceId, parent.ToString(), providerNamespace, customLayer);
        }

        /// <summary>Whether the identity contains enough information to be used as a key.</summary>
        public bool IsValid => !string.IsNullOrEmpty(serialized) && !string.IsNullOrEmpty(LocalSlot) && !string.IsNullOrEmpty(ProviderNamespace);

        /// <summary>Parses the stable string representation.</summary>
        public static RealityRegionId Parse(string value)
        {
            if (!TryParse(value, out RealityRegionId result)) throw new FormatException("Invalid RealityRegionId: " + value);
            return result;
        }

        /// <summary>Attempts to parse a stable string representation.</summary>
        public static bool TryParse(string value, out RealityRegionId result)
        {
            result = default(RealityRegionId);
            if (string.IsNullOrEmpty(value)) return false;
            string[] parts = value.Split('|');
            if (parts.Length < 2 || parts[0] != Prefix) return false;
            Dictionary<string, string> fields = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 1; i < parts.Length; i++)
            {
                int separator = parts[i].IndexOf('=');
                if (separator <= 0) continue;
                string key = parts[i].Substring(0, separator);
                string encoded = parts[i].Substring(separator + 1);
                fields[key] = Decode(encoded);
            }
            if (!fields.TryGetValue("tile", out string tileText) || !int.TryParse(tileText, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int tile)) return false;
            if (!fields.TryGetValue("layer", out string layerText) || !Enum.TryParse(layerText, true, out RealityLayer layer)) return false;
            fields.TryGetValue("custom", out string custom);
            fields.TryGetValue("slot", out string slot);
            fields.TryGetValue("instance", out string instance);
            fields.TryGetValue("parent", out string parent);
            fields.TryGetValue("provider", out string provider);
            result = new RealityRegionId(tile, layer, slot, instance, parent, provider, custom);
            return result.IsValid;
        }

        /// <inheritdoc />
        public override string ToString() => serialized ?? string.Empty;

        /// <inheritdoc />
        public bool Equals(RealityRegionId other) => string.Equals(ToString(), other.ToString(), StringComparison.Ordinal);

        /// <inheritdoc />
        public override bool Equals(object obj) => obj is RealityRegionId && Equals((RealityRegionId)obj);

        /// <inheritdoc />
        public override int GetHashCode() => RealityDeterminism.StableHash(ToString());

        /// <summary>Stable equality operator.</summary>
        public static bool operator ==(RealityRegionId left, RealityRegionId right) => left.Equals(right);

        /// <summary>Stable inequality operator.</summary>
        public static bool operator !=(RealityRegionId left, RealityRegionId right) => !left.Equals(right);

        private static string BuildSerialized(int tile, RealityLayer layer, string custom, string slot, string instance,
            string parent, string provider)
        {
            return Prefix + "|tile=" + Encode(tile.ToString(CultureInfo.InvariantCulture)) +
                "|layer=" + Encode(layer.ToString()) + "|custom=" + Encode(custom) +
                "|slot=" + Encode(slot) + "|instance=" + Encode(instance) +
                "|parent=" + Encode(parent) + "|provider=" + Encode(provider);
        }

        private static string Normalize(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        private static string Encode(string value) => Uri.EscapeDataString(value ?? string.Empty);

        private static string Decode(string value)
        {
            try { return Uri.UnescapeDataString(value ?? string.Empty); }
            catch (UriFormatException) { return value; }
        }
    }

    /// <summary>Thread policy for all public mutating framework calls.</summary>
    public static class RealityThreadGuard
    {
        private static int mainThreadId;

        /// <summary>Registers the current thread as the RimWorld main thread.</summary>
        public static void InitializeMainThread()
        {
            if (mainThreadId == 0) mainThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        /// <summary>Whether the current caller is the registered main thread.</summary>
        public static bool IsMainThread => mainThreadId == 0 || Thread.CurrentThread.ManagedThreadId == mainThreadId;

        /// <summary>Throws when a caller attempts to mutate Verse state from a worker thread.</summary>
        public static void RequireMainThread()
        {
            InitializeMainThread();
            if (!IsMainThread) throw new InvalidOperationException("Deferred Reality mutations must run on RimWorld's main thread.");
        }
    }
}
