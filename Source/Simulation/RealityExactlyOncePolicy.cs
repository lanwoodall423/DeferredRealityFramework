using System;

namespace DeferredReality.Simulation
{
    /// <summary>Pure rules for provider/domain sequence acceptance and replay-safe advancement.</summary>
    public static class RealityExactlyOncePolicy
    {
        public static bool CanAccept(long cursor, long sequence, bool allowGaps)
        {
            if (sequence < 0 || sequence <= cursor) return false;
            return allowGaps || sequence == cursor + 1;
        }

        public static bool CanAdvance(long cursor, long sequence, bool allowGaps)
        {
            return CanAccept(cursor, sequence, allowGaps);
        }

        public static bool IsProvenExpired(long operationSequence, bool sequenceMode, long cursor,
            string proof, long operationTick, long now, long retentionTicks)
        {
            if (!sequenceMode || operationSequence < 0 || cursor < operationSequence || string.IsNullOrWhiteSpace(proof))
                return false;
            if (retentionTicks < 0 || now < operationTick) return false;
            return now - operationTick >= retentionTicks;
        }

        public static bool IsTerminalCancelled(long terminalTick, bool cancelled)
        {
            return cancelled && terminalTick >= 0;
        }
    }
}
