using System;
using System.IO;
using System.Reflection;
using DeferredReality.API;
using DeferredReality.Materialization;
using Verse;

namespace DeferredReality.PureTests
{
    internal static class Program
    {
        private static int Main()
        {
            try
            {
                AppDomain.CurrentDomain.AssemblyResolve += ResolveRimWorldAssembly;
                RegionRoundTrip();
                DeterministicSeedAndStream();
                ProviderScopedFactoryIsolation();
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception.ToString());
                return 1;
            }
        }

        private static Assembly ResolveRimWorldAssembly(object sender, ResolveEventArgs args)
        {
            string name = new AssemblyName(args.Name).Name + ".dll";
            string path = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", "1.6", "Assemblies", name));
            if (File.Exists(path)) return Assembly.LoadFrom(path);
            string rimWorldPath = Path.Combine(
                @"C:\Games\Steam\steamapps\common\RimWorld\RimWorldWin64_Data\Managed", name);
            return File.Exists(rimWorldPath) ? Assembly.LoadFrom(rimWorldPath) : null;
        }

        private static void RegionRoundTrip()
        {
            RealityRegionId original = new RealityRegionId(42, RealityLayer.Custom, "water|north", "body=7",
                "rr-parent", "provider.test", "subterranean");
            RealityRegionId parsed = RealityRegionId.Parse(original.ToString());
            Require(original == parsed, "region identity did not round-trip");
            Require(original.GetHashCode() == parsed.GetHashCode(), "region hash changed after round-trip");
        }

        private static void DeterministicSeedAndStream()
        {
            int left = RealityDeterminism.Seed("world", "region", "provider", "process", 7);
            int right = RealityDeterminism.Seed("world", "region", "provider", "process", 7);
            Require(left == right, "stable seed changed");
            RealityRandomStream a = new RealityRandomStream(left);
            RealityRandomStream b = new RealityRandomStream(right);
            for (int i = 0; i < 32; i++) Require(a.NextUInt() == b.NextUInt(), "random stream changed at " + i);
        }

        private static void ProviderScopedFactoryIsolation()
        {
            var first = new TestFactory();
            var second = new TestFactory();
            Require(RealityMapFactoryRegistry.Register("pure.first", first), "first factory did not register");
            Require(RealityMapFactoryRegistry.Register("pure.second", second), "second factory did not register");
            Require(RealityMapFactoryRegistry.TryGet("pure.first", out IRealityMapFactory resolvedFirst) &&
                ReferenceEquals(first, resolvedFirst), "first provider factory was not isolated");
            Require(RealityMapFactoryRegistry.TryGet("pure.second", out IRealityMapFactory resolvedSecond) &&
                ReferenceEquals(second, resolvedSecond), "second provider factory was not isolated");
            Require(RealityMapFactoryRegistry.Unregister("pure.first", first), "first factory did not unregister");
            Require(!RealityMapFactoryRegistry.TryGet("pure.first", out _), "unregistered factory remained visible");
            RealityMapFactoryRegistry.Unregister("pure.second", second);
        }

        private sealed class TestFactory : IRealityMapFactory
        {
            public bool TryCreateMap(RealityRegionId regionId, RealityMaterializationPlan plan, out Map map, out string diagnostic)
            {
                map = null;
                diagnostic = "pure test factory";
                return false;
            }

            public void RemoveMap(Map map) { }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
