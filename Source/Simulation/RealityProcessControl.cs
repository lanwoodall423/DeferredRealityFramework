using System;
using System.Linq;
using DeferredReality.API;

namespace DeferredReality.Simulation
{
    /// <summary>Explicit process pause, resume, cancellation, and provider controls.</summary>
    public static class RealityProcessControl
    {
        /// <summary>Pauses one process without deleting its payload.</summary>
        public static bool Pause(DeferredRealityWorldComponent world, string processId)
        {
            return Set(world, processId, true, false);
        }

        /// <summary>Resumes one paused process at its next interval.</summary>
        public static bool Resume(DeferredRealityWorldComponent world, string processId)
        {
            return Set(world, processId, false, false);
        }

        /// <summary>Cancels one process while retaining its record for diagnostics.</summary>
        public static bool Cancel(DeferredRealityWorldComponent world, string processId)
        {
            return Set(world, processId, true, true);
        }

        /// <summary>Pauses or resumes all processes owned by a provider.</summary>
        public static int SetProviderPaused(DeferredRealityWorldComponent world, string providerId, bool paused)
        {
            if (world == null || string.IsNullOrEmpty(providerId)) return 0;
            RealityThreadGuard.RequireMainThread();
            int changed = 0;
            foreach (RealityProcessSnapshot snapshot in world.ProcessSnapshots().Where(item => item.record.providerId == providerId))
                if (Set(world, snapshot.record.processId, paused, false)) changed++;
            return changed;
        }

        /// <summary>Pauses or resumes all processes scoped to one stable region.</summary>
        public static int SetRegionPaused(DeferredRealityWorldComponent world, string regionId, bool paused)
        {
            if (world == null || string.IsNullOrEmpty(regionId)) return 0;
            RealityThreadGuard.RequireMainThread();
            int changed = 0;
            foreach (RealityProcessSnapshot snapshot in world.ProcessSnapshots(regionId))
                if (Set(world, snapshot.record.processId, paused, false)) changed++;
            return changed;
        }

        private static bool Set(DeferredRealityWorldComponent world, string processId, bool paused, bool cancelled)
        {
            RealityThreadGuard.RequireMainThread();
            RealityProcessRecord record = world?.ProcessRecord(processId);
            if (record == null) return false;
            if (cancelled)
            {
                if (!RealityProcessPausePolicy.Cancel(record, world.Now)) return false;
            }
            else if (paused)
            {
                RealityProcessPausePolicy.SetManual(record);
                record.nextDueTick = world.Now + Math.Max(1, record.intervalTicks);
            }
            else if (!RealityProcessPausePolicy.Resume(record, world.Now + Math.Max(1, record.intervalTicks))) return false;
            world.RefreshProcessScheduleCache();
            world.Touch(cancelled ? "process.cancelled" : paused ? "process.paused" : "process.resumed",
                record.providerId, record.regionId, record.processId);
            return true;
        }
    }
}
