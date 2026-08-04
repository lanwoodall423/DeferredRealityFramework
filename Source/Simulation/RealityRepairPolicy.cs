using System;

namespace DeferredReality.Simulation
{
    /// <summary>Deterministic duplicate repair policy shared by save repair and pure tests.</summary>
    public static class RealityRepairPolicy
    {
        /// <summary>
        /// The first serialized valid record keeps its list position. A candidate replaces its value only when an
        /// explicit schema version or update tick proves it newer; otherwise serialized order is authoritative.
        /// </summary>
        public static bool ShouldReplaceDuplicate(int existingSchemaVersion, int candidateSchemaVersion,
            long? existingUpdateTick, long? candidateUpdateTick)
        {
            if (candidateSchemaVersion != existingSchemaVersion)
                return candidateSchemaVersion > existingSchemaVersion;
            if (existingUpdateTick.HasValue && candidateUpdateTick.HasValue &&
                candidateUpdateTick.Value != existingUpdateTick.Value)
                return candidateUpdateTick.Value > existingUpdateTick.Value;
            return false;
        }
    }
}
