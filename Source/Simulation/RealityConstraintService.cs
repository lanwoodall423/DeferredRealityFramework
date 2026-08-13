using System;
using System.Collections.Generic;
using System.Linq;
using DeferredReality.API;

namespace DeferredReality.Simulation
{
    /// <summary>Result of deterministic deferred-constraint processing.</summary>
    public sealed class RealityConstraintResolutionResult
    {
        public int considered;
        public int resolved;
        public int expired;
        public int conflicted;
        public int unresolved;
    }

    /// <summary>Resolves deferred outcomes without selecting unnecessary detail early.</summary>
    public static class RealityConstraintService
    {
        /// <summary>Processes constraints in stable priority/ID order and reports conflicts explicitly.</summary>
        public static RealityConstraintResolutionResult Resolve(DeferredRealityWorldComponent world, RealityRegionId regionId, long now)
        {
            RealityThreadGuard.RequireMainThread();
            var result = new RealityConstraintResolutionResult();
            if (world == null) return result;
            List<RealityConstraint> values = world.ConstraintSnapshots(regionId.ToString()).Where(item => item != null).ToList();
            result.considered = values.Count;
            foreach (RealityConstraint constraint in values)
            {
                RealityConstraint actual = world.ConstraintRecord(constraint.constraintId);
                if (actual == null) continue;
                if (!actual.AppliesAt(now))
                {
                    if (actual.expiryTick >= 0 && now > actual.expiryTick && actual.status != RealityConstraintStatus.Expired)
                    {
                        actual.status = RealityConstraintStatus.Expired;
                        result.expired++;
                    }
                    continue;
                }
                if (actual.status == RealityConstraintStatus.Conflicted || actual.status == RealityConstraintStatus.Quarantined) continue;
                if (FindConflict(world, actual, values, now))
                {
                    actual.status = RealityConstraintStatus.Conflicted;
                    result.conflicted++;
                    continue;
                }
                if (!RealityProviderRegistry.TryGetCapability(actual.providerId, out IConstraintResolver resolver))
                {
                    result.unresolved++;
                    continue;
                }
                bool resolved;
                var vetoes = new List<RealityVeto>();
                try { resolved = resolver.CanResolve(actual) && resolver.Resolve(new RealityProviderContext(world, actual.providerId, now), actual.Clone(), vetoes); }
                catch (Exception exception)
                {
                    resolved = false;
                    vetoes.Add(new RealityVeto("provider.exception", exception.Message, actual.providerId, 3));
                }
                if (resolved && vetoes.Count == 0) { actual.status = RealityConstraintStatus.Resolved; result.resolved++; }
                else result.unresolved++;
            }
            return result;
        }

        /// <summary>Returns whether two constraints explicitly or conservatively share a conflict domain.</summary>
        public static bool ConflictDomainsIntersect(RealityConstraint left, RealityConstraint right)
        {
            if (left == null || right == null) return false;
            return ConflictDomains(left).Intersect(ConflictDomains(right), StringComparer.Ordinal).Any();
        }

        /// <summary>Returns whether differing payloads describe incompatible facets in a shared domain.</summary>
        public static bool AreSemanticallyIncompatible(RealityConstraint left, RealityConstraint right)
        {
            if (left == null || right == null || string.Equals(left.payload, right.payload, StringComparison.Ordinal)) return false;
            HashSet<string> leftFacets = Keys(left.conflictFacetKeys);
            HashSet<string> rightFacets = Keys(right.conflictFacetKeys);
            return leftFacets.Count == 0 || rightFacets.Count == 0 || leftFacets.Intersect(rightFacets, StringComparer.Ordinal).Any();
        }

        private static bool FindConflict(DeferredRealityWorldComponent world, RealityConstraint value,
            IReadOnlyList<RealityConstraint> all, long now)
        {
            foreach (RealityConstraint other in all)
            {
                if (other.constraintId == value.constraintId || !other.AppliesAt(now) || other.status == RealityConstraintStatus.Expired) continue;
                if (!ConflictDomainsIntersect(value, other) || !AreSemanticallyIncompatible(value, other)) continue;
                world.AddConflict(value.regionId, value.constraintId, other.constraintId,
                    "Established constraints describe incompatible deferred outcomes.", now);
                if (ValueWins(value, other))
                {
                    RealityConstraint otherActual = world.ConstraintRecord(other.constraintId);
                    if (otherActual != null) otherActual.status = RealityConstraintStatus.Conflicted;
                    return false;
                }
                return true;
            }
            return false;
        }

        private static HashSet<string> ConflictDomains(RealityConstraint value)
        {
            var explicitDomains = Keys(value.conflictDomainKeys);
            if (explicitDomains.Count > 0)
                return new HashSet<string>(explicitDomains.Select(item => "explicit:" + item), StringComparer.Ordinal);

            string prefix = (value.providerId ?? string.Empty) + "|" + (value.typeId ?? string.Empty);
            HashSet<string> facets = Keys(value.conflictFacetKeys);
            IEnumerable<string> subjects = (value.affectedAnchorIds ?? new List<string>())
                .Concat(value.affectedPopulationIds ?? new List<string>())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.Ordinal);
            if (subjects.Any())
                return new HashSet<string>(subjects.Select(subject => "implicit:" + prefix + "|subject:" + subject), StringComparer.Ordinal);
            if (facets.Count > 0)
                return new HashSet<string>(facets.Select(facet => "implicit:" + prefix + "|facet:" + facet), StringComparer.Ordinal);
            return new HashSet<string>(new[] { "implicit:" + prefix + "|region:" + (value.regionId ?? string.Empty) }, StringComparer.Ordinal);
        }

        private static HashSet<string> Keys(IEnumerable<string> values)
        {
            return new HashSet<string>((values ?? Enumerable.Empty<string>())
                .Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item.Trim()), StringComparer.Ordinal);
        }

        private static bool ValueWins(RealityConstraint value, RealityConstraint other)
        {
            RealityConflictPolicy policy = value.conflictPolicy != RealityConflictPolicy.ReportOnly
                ? value.conflictPolicy : other.conflictPolicy;
            if (policy == RealityConflictPolicy.PreferHigherPriority)
            {
                if (value.priority != other.priority) return value.priority > other.priority;
                if (Math.Abs(value.certainty - other.certainty) > 0.0001f) return value.certainty > other.certainty;
            }
            else if (policy == RealityConflictPolicy.PreferEstablished)
            {
                if (Math.Abs(value.certainty - other.certainty) > 0.0001f) return value.certainty > other.certainty;
                if (value.createdTick != other.createdTick) return value.createdTick < other.createdTick;
            }
            return false;
        }
    }
}
