using System;
using DeferredReality.API;

namespace DeferredReality.Materialization
{
    /// <summary>Pure checks for transaction-scoped adjacent map classification.</summary>
    public static class RealityMapCreationPolicy
    {
        public static bool IsOwnerClaimCompatible(RealityRegionId expectedRegion, string expectedOwner,
            RealityMapIdentityClaim claim)
        {
            return expectedRegion.IsValid && !string.IsNullOrWhiteSpace(expectedOwner) && claim != null &&
                string.Equals(expectedOwner.Trim(), claim.providerId, StringComparison.Ordinal) &&
                claim.regionId == expectedRegion && !string.IsNullOrEmpty(claim.identityKey);
        }

        public static bool CanClassifyMap(bool wasLiveBeforeIntent, bool alreadyMarked,
            string transactionId, int expectedMapUniqueId, int actualMapUniqueId)
        {
            if (string.IsNullOrEmpty(transactionId) || actualMapUniqueId < 0) return false;
            if (alreadyMarked) return expectedMapUniqueId < 0 || expectedMapUniqueId == actualMapUniqueId;
            return !wasLiveBeforeIntent && (expectedMapUniqueId < 0 || expectedMapUniqueId == actualMapUniqueId);
        }

        public static bool ShouldClearAfterLoad(RealityMapCreationIntentRecord intent, RealityAdjacentMapRecord marker)
        {
            return intent != null && marker != null && intent.transactionId == marker.transactionId &&
                intent.createdMapUniqueId >= 0 && intent.createdMapUniqueId == marker.mapUniqueId;
        }
    }
}
