using System;
using System.Collections.Generic;
using System.Linq;
using DeferredReality.API;

namespace DeferredReality.Materialization
{
    /// <summary>Pure selection rules for provider-owned map identity claims.</summary>
    public static class RealityMapIdentityPolicy
    {
        /// <summary>Accepts one unambiguous provider claim and fails closed for conflicting owners.</summary>
        public static bool TrySelectClaim(IEnumerable<RealityMapIdentityClaim> claims,
            out RealityMapIdentityClaim selected, out string diagnostic)
        {
            selected = null;
            diagnostic = null;
            List<RealityMapIdentityClaim> candidates = (claims ?? Enumerable.Empty<RealityMapIdentityClaim>()).ToList();
            if (candidates.Any(claim => !IsValid(claim)))
            {
                diagnostic = "Map identity claims contain an invalid provider or region identity.";
                return false;
            }
            List<RealityMapIdentityClaim> valid = candidates
                .OrderBy(item => item.providerId, StringComparer.Ordinal)
                .ThenBy(item => item.regionId.ToString(), StringComparer.Ordinal)
                .ThenBy(item => item.identityKey, StringComparer.Ordinal)
                .ToList();
            if (valid.Count == 0) return false;
            List<string> owners = valid.Select(item => item.providerId).Distinct(StringComparer.Ordinal).ToList();
            List<string> identities = valid.Select(item => item.StableKey).Distinct(StringComparer.Ordinal).ToList();
            if (owners.Count != 1 || identities.Count != 1)
            {
                diagnostic = "Map identity claims are ambiguous: " + string.Join(", ", identities.ToArray());
                return false;
            }
            selected = valid[0];
            return true;
        }

        private static bool IsValid(RealityMapIdentityClaim claim)
        {
            return claim != null && !string.IsNullOrWhiteSpace(claim.providerId) &&
                !string.IsNullOrWhiteSpace(claim.identityKey) && claim.regionId.IsValid;
        }
    }
}
