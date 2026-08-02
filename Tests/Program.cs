using System;
using System.IO;
using System.Reflection;
using DeferredReality.API;

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
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
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

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
