using System;
using System.Collections.Generic;
using System.Linq;

namespace DeferredReality.Materialization
{
    /// <summary>Pure transaction seams used to keep provider selection and compensation deterministic.</summary>
    public static class RealityTransitionPolicy
    {
        /// <summary>Returns successfully prepared stages in the only safe compensation order.</summary>
        public static IReadOnlyList<T> ReversePrepared<T>(IList<T> prepared)
        {
            if (prepared == null || prepared.Count == 0) return Array.Empty<T>();
            return prepared.Reverse().ToList();
        }

        /// <summary>Filters one explicit owner before any provider lifecycle method is invoked.</summary>
        public static IReadOnlyList<T> SelectOwner<T>(IEnumerable<T> providers, string owner,
            Func<T, string> ownerSelector, Func<T, int> orderSelector)
        {
            if (providers == null || ownerSelector == null || orderSelector == null || string.IsNullOrEmpty(owner))
                return Array.Empty<T>();
            return providers.Where(provider => string.Equals(ownerSelector(provider), owner, StringComparison.Ordinal))
                .OrderBy(orderSelector).ThenBy(ownerSelector, StringComparer.Ordinal).ToList();
        }
    }
}
