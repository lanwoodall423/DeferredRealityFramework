using UnityEngine;
using Verse;

namespace DeferredReality.Materialization
{
    /// <summary>Persisted opt-in policy for temporary provider-owned projection sites.</summary>
    public sealed class DeferredRealityModSettings : ModSettings
    {
        private static DeferredRealityModSettings current;
        public bool enableAdjacentRegions;
        public int warmMapCacheLimit = 1;

        /// <summary>Current settings instance.</summary>
        public static DeferredRealityModSettings Current => current ?? (current = new DeferredRealityModSettings());

        /// <inheritdoc />
        public override void ExposeData()
        {
            Scribe_Values.Look(ref enableAdjacentRegions, "enableAdjacentRegions", false);
            Scribe_Values.Look(ref warmMapCacheLimit, "warmMapCacheLimit", 1);
            warmMapCacheLimit = Mathf.Clamp(warmMapCacheLimit, 0, 8);
        }

        internal static void SetCurrent(DeferredRealityModSettings value) => current = value;
    }

    /// <summary>Mod entry point for framework projection policy only.</summary>
    public sealed class DeferredRealityMod : Mod
    {
        public DeferredRealityMod(ModContentPack content) : base(content)
        {
            DeferredRealityModSettings.SetCurrent(GetSettings<DeferredRealityModSettings>());
        }

        public override string SettingsCategory() => "Deferred Reality Framework";

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(inRect);
            DeferredRealityModSettings settings = DeferredRealityModSettings.Current;
            listing.CheckboxLabeled("Enable experimental temporary excursion sites", ref settings.enableAdjacentRegions,
                "Disabled by default; enabled maps are temporary non-buildable work sites, not colony maps. Transfers use ordinary world travel only after exact rollback; unresolved failures remain recoverable.");
            settings.warmMapCacheLimit = Mathf.RoundToInt(listing.SliderLabeled("Warm map cache limit", settings.warmMapCacheLimit, 0, 8, 1f));
            listing.Label("Only marked maps are cached; background inspection does not refresh recency. Over-limit entries are removed only after provider compression and real factory eviction succeed.");
            listing.End();
        }
    }
}
