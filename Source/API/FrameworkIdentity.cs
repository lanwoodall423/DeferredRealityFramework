using System;
using System.Linq;
using System.Reflection;

namespace DeferredReality.API
{
    /// <summary>Authoritative framework release and provider-contract identity.</summary>
    public static class DeferredRealityFrameworkInfo
    {
        /// <summary>Semantic framework version for this release candidate.</summary>
        public const string Version = "0.1.0-rc.1";

        /// <summary>CLR assembly version used for binary compatibility.</summary>
        public const string AssemblyVersion = "0.1.0.0";

        /// <summary>Provider registration API version supported by this framework.</summary>
        public const int SupportedProviderApiVersion = 1;

        /// <summary>Assembly informational version, including build identity.</summary>
        public static string InformationalVersion => LoadedInformationalVersion;

        /// <summary>Exact release or development identity exposed by the built assembly.</summary>
        public static string BuildIdentity => LoadedInformationalVersion;

        /// <summary>Whether this assembly was built as a development build.</summary>
        public static bool IsDevelopmentBuild => !LoadedInformationalVersion.EndsWith("+release-candidate", StringComparison.Ordinal);

        private static readonly string LoadedInformationalVersion = ReadInformationalVersion();

        private static string ReadInformationalVersion()
        {
            Assembly assembly = typeof(DeferredRealityFrameworkInfo).Assembly;
            AssemblyInformationalVersionAttribute attribute = assembly
                .GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false)
                .OfType<AssemblyInformationalVersionAttribute>()
                .FirstOrDefault();
            return attribute?.InformationalVersion ?? Version;
        }
    }
}
