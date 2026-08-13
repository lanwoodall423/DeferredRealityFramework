using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using DeferredReality.API;
using DeferredReality.Diagnostics;
using DeferredReality.Materialization;
using UnityEngine;
using Verse;

namespace DeferredReality.Testing
{
    /// <summary>Runs the framework's provider-neutral live-world checks after a DevBridge quicktest map is ready.</summary>
    public sealed class DeferredRealityInGameTestComponent : GameComponent
    {
        private const int SettleTicks = 30;
        private readonly bool requested;
        private int playableTicks;
        private bool completed;

        public DeferredRealityInGameTestComponent(Game game)
        {
            requested = RealityInGameTestRunner.AutoRunRequested;
        }

        public override void GameComponentTick()
        {
            if (completed || !requested || !RealityInGameTestRunner.IsPlayable()) return;
            playableTicks++;
            if (playableTicks < SettleTicks) return;
            completed = true;
            RealityInGameTestRunner.Execute();
        }
    }

    internal static class RealityInGameTestRunner
    {
        private const string ResultFileName = "DeferredReality.InGameTests.json";

        internal static bool AutoRunRequested
        {
            get
            {
                return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DEVBRIDGE_LAUNCH_ID")) ||
                    IsTrue(Environment.GetEnvironmentVariable("DEFERRED_REALITY_AUTO_TESTS"));
            }
        }

        internal static bool IsPlayable()
        {
            if (!RealityThreadGuard.IsMainThread) return false;
            try
            {
                return GenScene.InPlayScene && Current.Game != null && Find.CurrentMap != null &&
                    Find.TickManager != null;
            }
            catch
            {
                return false;
            }
        }

        internal static void Execute()
        {
            RealityInGameTestReport report;
            try
            {
                report = Run();
            }
            catch (Exception exception)
            {
                report = NewReport();
                report.results.Add(RealityInGameTestResult.Failed("runner", exception));
            }

            Persist(report);
            LogReport(report);
        }

        private static RealityInGameTestReport Run()
        {
            var report = NewReport();
            DeferredRealityWorldComponent world = DeferredRealityWorldComponent.Current;
            Map currentMap = Find.CurrentMap;

            Check(report, "main-thread-established", () =>
            {
                RealityThreadGuard.RequireMainThread();
                return "main thread is established";
            });
            Check(report, "world-component-available", () =>
            {
                Require(world != null, "DeferredRealityWorldComponent.Current is null");
                return "world component is attached";
            });
            Check(report, "current-map-available", () =>
            {
                Require(currentMap != null, "Find.CurrentMap is null");
                Require(Find.Maps != null && Find.Maps.Contains(currentMap), "current map is not in Find.Maps");
                return "map=" + currentMap.uniqueID;
            });
            Check(report, "current-map-has-stable-identity", () =>
            {
                Require(world != null && currentMap != null, "world or current map is unavailable");
                Require(world.TryRegionForProjectionMapId(currentMap.uniqueID, out RealityRegionId regionId) && regionId.IsValid,
                    "current map has no valid framework region identity");
                Require(world.TryGetRegion(regionId, out RealityRegionSnapshot snapshot) && snapshot != null,
                    "current map region snapshot is unavailable");
                Require(snapshot.authority == RealityRegionAuthority.LiveProjection &&
                    snapshot.projectionMapUniqueId == currentMap.uniqueID,
                    "current map region is not the live projection authority");
                Require(RealityRegionId.Parse(regionId.ToString()) == regionId,
                    "current map region identity does not round-trip");
                return "map=" + currentMap.uniqueID + " region=" + regionId;
            });
            Check(report, "all-loaded-maps-have-stable-identities", () =>
            {
                Require(world != null, "world component is unavailable");
                List<Map> maps = LoadedMaps();
                foreach (Map map in maps)
                {
                    Require(map.Tile.Valid, "map " + map.uniqueID + " has an invalid world tile");
                    Require(world.TryRegionForProjectionMapId(map.uniqueID, out RealityRegionId regionId) && regionId.IsValid,
                        "map " + map.uniqueID + " has no stable region identity");
                }
                return "maps=" + maps.Count;
            });
            Check(report, "diagnostics-are-readable-and-repeatable", () =>
            {
                Require(world != null, "world component is unavailable");
                RealityDiagnosticsSnapshot first = RealityDiagnostics.Snapshot(world);
                string firstDump = RealityDiagnostics.Dump(world);
                string secondDump = RealityDiagnostics.Dump(world);
                Require(first != null, "diagnostics snapshot is null");
                Require(!string.IsNullOrEmpty(firstDump), "diagnostics dump is empty");
                Require(firstDump == secondDump, "diagnostics dump changed without a mutation");
                return "revision=" + first.revision + " regions=" + first.regions + " providers=" + first.providers;
            });
            Check(report, "audit-has-no-errors", () =>
            {
                RealityAuditReport audit = RealityAuditService.Audit(world);
                Require(audit != null && audit.Passed, audit == null ? "audit returned null" : audit.Text);
                return "errors=0 warnings=" + audit.warnings.Count;
            });
            Check(report, "provider-registrations-have-stable-ids", () =>
            {
                IReadOnlyList<RealityProviderRegistration> registrations = RealityProviderRegistry.Registrations();
                HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (RealityProviderRegistration registration in registrations)
                {
                    Require(registration != null && !string.IsNullOrWhiteSpace(registration.providerId),
                        "provider registration has no stable ID");
                    Require(ids.Add(registration.providerId), "duplicate provider registration: " + registration.providerId);
                }
                return "providers=" + registrations.Count;
            });
            Check(report, "adjacent-markers-have-explicit-ownership", () =>
            {
                Require(world != null, "world component is unavailable");
                List<Map> maps = LoadedMaps();
                IReadOnlyList<RealityAdjacentMapRecord> markers = world.AdjacentMapSnapshots();
                foreach (RealityAdjacentMapRecord marker in markers)
                {
                    Require(marker != null && !string.IsNullOrWhiteSpace(marker.providerId),
                        "adjacent marker has no provider owner");
                    Require(RealityRegionId.TryParse(marker.regionId, out _),
                        "adjacent marker has an invalid region identity");
                    if (marker.lifecycle == RealityAdjacentMapLifecycle.Active)
                    {
                        Map map = maps.FirstOrDefault(item => item.uniqueID == marker.mapUniqueId);
                        Require(map != null, "active adjacent marker has no loaded map: " + marker.mapUniqueId);
                        Require(RealityAdjacentConstructionGuards.IsBlocked(map),
                            "active adjacent map is not construction-blocked: " + marker.mapUniqueId);
                    }
                }
                return "markers=" + markers.Count;
            });
            Check(report, "construction-guard-separates-ordinary-maps", () =>
            {
                Require(world != null, "world component is unavailable");
                List<Map> maps = LoadedMaps();
                foreach (Map map in maps)
                {
                    bool adjacent = world.IsAdjacentMap(map);
                    Require(RealityAdjacentConstructionGuards.IsBlocked(map) == adjacent,
                        "construction guard disagrees with map role: " + map.uniqueID);
                }
                return "maps=" + maps.Count;
            });
            Check(report, "adjacent-diagnostics-are-readable", () =>
            {
                IReadOnlyList<string> lines = RealityProjectionDiagnostics.Lines(world);
                Require(lines != null && lines.All(item => item != null), "adjacent diagnostics contained a null line");
                return "lines=" + lines.Count;
            });
            Check(report, "nonterminal-excursion-ownership-is-unique", () =>
            {
                Require(world != null, "world component is unavailable");
                IReadOnlyList<RealityExcursionTicket> active = world.ExcursionSnapshots()
                    .Where(item => item != null && !RealityRetentionPolicy.IsTerminalExcursion(item)).ToList();
                foreach (RealityExcursionTicket ticket in active)
                {
                    Require(!string.IsNullOrWhiteSpace(ticket.excursionId), "active excursion has no ID");
                    Require(!string.IsNullOrWhiteSpace(ticket.providerId), "active excursion has no provider");
                    Require(!string.IsNullOrWhiteSpace(ticket.pawnLoadId), "active excursion has no Pawn load ID");
                    Require(!string.IsNullOrWhiteSpace(ticket.taskId), "active excursion has no provider task ID");
                }
                Require(active.GroupBy(item => item.pawnLoadId, StringComparer.Ordinal).All(group => group.Count() == 1),
                    "a Pawn is owned by multiple active excursions");
                return "activeExcursions=" + active.Count;
            });
            Check(report, "map-creation-intents-have-stable-identity", () =>
            {
                Require(world != null, "world component is unavailable");
                IReadOnlyList<RealityMapCreationIntentRecord> intents = world.MapCreationIntentSnapshots();
                HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (RealityMapCreationIntentRecord intent in intents)
                {
                    Require(intent != null && !string.IsNullOrWhiteSpace(intent.transactionId),
                        "map creation intent has no transaction ID");
                    Require(ids.Add(intent.transactionId), "duplicate map creation transaction: " + intent.transactionId);
                    Require(!string.IsNullOrWhiteSpace(intent.providerId),
                        "map creation intent has no provider owner");
                    Require(RealityRegionId.TryParse(intent.regionId, out _),
                        "map creation intent has an invalid region identity");
                }
                return "intents=" + intents.Count;
            });
            Check(report, "read-only-diagnostics-do-not-mutate-world", () =>
            {
                Require(world != null, "world component is unavailable");
                int revision = world.Revision;
                int regionCount = world.RegionSnapshots().Count;
                int adjacentCount = world.AdjacentMapSnapshots().Count;
                int excursionCount = world.ExcursionSnapshots().Count;
                _ = RealityDiagnostics.Snapshot(world);
                _ = RealityDiagnostics.Dump(world);
                _ = RealityAuditService.Audit(world);
                _ = RealityProjectionDiagnostics.Lines(world);
                Require(world.Revision == revision, "read-only diagnostics changed world revision");
                Require(world.RegionSnapshots().Count == regionCount &&
                    world.AdjacentMapSnapshots().Count == adjacentCount &&
                    world.ExcursionSnapshots().Count == excursionCount,
                    "read-only diagnostics changed persisted collection counts");
                return "revision=" + revision;
            });
            return report;
        }

        private static RealityInGameTestReport NewReport()
        {
            return new RealityInGameTestReport
            {
                launchId = Environment.GetEnvironmentVariable("DEVBRIDGE_LAUNCH_ID"),
                generation = ParseGeneration(),
                completedUtc = DateTime.UtcNow,
                gameTick = Find.TickManager?.TicksGame ?? 0
            };
        }

        private static void Check(RealityInGameTestReport report, string id, Func<string> action)
        {
            try
            {
                report.results.Add(RealityInGameTestResult.Passed(id, action() ?? "ok"));
            }
            catch (Exception exception)
            {
                report.results.Add(RealityInGameTestResult.Failed(id, exception));
                Log.Error("[DeferredReality][InGameTests] FAIL " + id + ": " + exception);
            }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static List<Map> LoadedMaps()
        {
            return (Find.Maps ?? new List<Map>()).Where(item => item != null).ToList();
        }

        private static int ParseGeneration()
        {
            return int.TryParse(Environment.GetEnvironmentVariable("DEVBRIDGE_GENERATION"), out int value) ? value : 0;
        }

        private static bool IsTrue(string value)
        {
            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
        }

        private static void Persist(RealityInGameTestReport report)
        {
            string[] candidates = OutputPaths().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (string path in candidates)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    report.outputPath = path;
                    WriteAtomic(path, BuildJson(report));
                    return;
                }
                catch (Exception exception)
                {
                    Log.Warning("[DeferredReality][InGameTests] Could not write " + path + ": " + exception.Message);
                }
            }
            Log.Error("[DeferredReality][InGameTests] No writable test-result path was available.");
        }

        private static IEnumerable<string> OutputPaths()
        {
            string configured = Environment.GetEnvironmentVariable("DEFERRED_REALITY_TEST_RESULTS");
            if (!string.IsNullOrWhiteSpace(configured) && TryGetFullPath(configured, out string configuredPath))
                yield return configuredPath;

            string assemblyLocation = null;
            try { assemblyLocation = typeof(DeferredRealityInGameTestComponent).Assembly.Location; }
            catch { }
            if (!string.IsNullOrWhiteSpace(assemblyLocation) && TryGetFullPath(assemblyLocation, out string assemblyPath))
            {
                string assemblyDirectory = Path.GetDirectoryName(assemblyPath);
                if (!string.IsNullOrWhiteSpace(assemblyDirectory))
                    yield return Path.Combine(assemblyDirectory, "..", "..", "TestResults", ResultFileName);
            }

            if (!string.IsNullOrWhiteSpace(Application.persistentDataPath))
                yield return Path.Combine(Application.persistentDataPath, "DeferredReality", "TestResults", ResultFileName);
        }

        private static bool TryGetFullPath(string candidate, out string path)
        {
            try
            {
                path = Path.GetFullPath(candidate);
                return !string.IsNullOrWhiteSpace(path);
            }
            catch
            {
                path = null;
                return false;
            }
        }

        private static void WriteAtomic(string path, string contents)
        {
            string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temporary, contents, new UTF8Encoding(false));
                if (File.Exists(path))
                {
                    try { File.Replace(temporary, path, null); }
                    catch
                    {
                        File.Delete(path);
                        File.Move(temporary, path);
                    }
                }
                else File.Move(temporary, path);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        private static string BuildJson(RealityInGameTestReport report)
        {
            var builder = new StringBuilder();
            builder.Append("{\n")
                .Append("  \"schemaVersion\": 1,\n")
                .Append("  \"suite\": \"provider-neutral-live-world\",\n")
                .Append("  \"launchId\": ").Append(Quote(report.launchId)).Append(",\n")
                .Append("  \"generation\": ").Append(report.generation.ToString(CultureInfo.InvariantCulture)).Append(",\n")
                .Append("  \"completedUtc\": ").Append(Quote(report.completedUtc.ToString("O", CultureInfo.InvariantCulture))).Append(",\n")
                .Append("  \"gameTick\": ").Append(report.gameTick.ToString(CultureInfo.InvariantCulture)).Append(",\n")
                .Append("  \"passed\": ").Append(report.Passed ? "true" : "false").Append(",\n")
                .Append("  \"passedCount\": ").Append(report.results.Count(item => item.passed).ToString(CultureInfo.InvariantCulture)).Append(",\n")
                .Append("  \"failedCount\": ").Append(report.results.Count(item => !item.passed).ToString(CultureInfo.InvariantCulture)).Append(",\n")
                .Append("  \"outputPath\": ").Append(Quote(report.outputPath)).Append(",\n")
                .Append("  \"results\": [\n");
            for (int i = 0; i < report.results.Count; i++)
            {
                RealityInGameTestResult result = report.results[i];
                builder.Append("    {\"id\": ").Append(Quote(result.id))
                    .Append(", \"passed\": ").Append(result.passed ? "true" : "false")
                    .Append(", \"detail\": ").Append(Quote(result.detail)).Append('}');
                if (i + 1 < report.results.Count) builder.Append(',');
                builder.AppendLine();
            }
            return builder.Append("  ]\n}\n").ToString();
        }

        private static string Quote(string value)
        {
            if (value == null) return "null";
            var builder = new StringBuilder(value.Length + 2);
            builder.Append('"');
            foreach (char character in value)
            {
                switch (character)
                {
                    case '\\': builder.Append("\\\\"); break;
                    case '"': builder.Append("\\\""); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (character < 32) builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                        else builder.Append(character);
                        break;
                }
            }
            return builder.Append('"').ToString();
        }

        private static void LogReport(RealityInGameTestReport report)
        {
            Log.Message("[DeferredReality][InGameTests] " + (report.Passed ? "PASS" : "FAIL") +
                " passed=" + report.results.Count(item => item.passed) +
                " failed=" + report.results.Count(item => !item.passed) +
                " launch=" + (report.launchId ?? "none") +
                " report=" + (report.outputPath ?? "unwritten"));
            foreach (RealityInGameTestResult result in report.results)
                Log.Message("[DeferredReality][InGameTests] " + (result.passed ? "PASS " : "FAIL ") +
                    result.id + ": " + result.detail);
        }
    }

    internal sealed class RealityInGameTestReport
    {
        internal string launchId;
        internal int generation;
        internal DateTime completedUtc;
        internal long gameTick;
        internal string outputPath;
        internal readonly List<RealityInGameTestResult> results = new List<RealityInGameTestResult>();
        internal bool Passed => results.Count > 0 && results.All(item => item.passed);
    }

    internal sealed class RealityInGameTestResult
    {
        internal string id;
        internal bool passed;
        internal string detail;

        internal static RealityInGameTestResult Passed(string id, string detail)
        {
            return new RealityInGameTestResult { id = id, passed = true, detail = detail };
        }

        internal static RealityInGameTestResult Failed(string id, Exception exception)
        {
            return new RealityInGameTestResult
            {
                id = id,
                passed = false,
                detail = exception == null ? "unknown failure" : exception.Message
            };
        }
    }
}
