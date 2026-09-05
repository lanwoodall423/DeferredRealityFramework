using System.Reflection;

[assembly: AssemblyVersion(DeferredReality.API.DeferredRealityFrameworkInfo.AssemblyVersion)]
[assembly: AssemblyFileVersion(DeferredReality.API.DeferredRealityFrameworkInfo.AssemblyVersion)]
#if DEBUG
[assembly: AssemblyInformationalVersion(DeferredReality.API.DeferredRealityFrameworkInfo.Version + "+development")]
#else
[assembly: AssemblyInformationalVersion(DeferredReality.API.DeferredRealityFrameworkInfo.Version + "+release-candidate")]
#endif
