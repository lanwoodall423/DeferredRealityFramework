using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace DeferredReality.API
{
    /// <summary>Capability-based provider registry. Internal collections are never exposed.</summary>
    public static class RealityProviderRegistry
    {
        private sealed class RegisteredProvider
        {
            public IRealityProvider Provider;
            public RealityProviderRegistration Registration;
        }

        private static readonly Dictionary<string, RegisteredProvider> ProvidersById =
            new Dictionary<string, RegisteredProvider>(StringComparer.Ordinal);
        private static int revision;

        /// <summary>Registration revision for cache invalidation.</summary>
        public static int Revision => revision;

        /// <summary>Registers or replaces a provider with the same stable ID.</summary>
        public static bool Register(IRealityProvider provider)
        {
            RealityProviderRegistration registration = provider?.Registration?.Clone();
            if (registration == null || string.IsNullOrWhiteSpace(registration.providerId)) return false;
            if (!RealityThreadGuard.IsMainThread)
            {
                LongEventHandler.ExecuteWhenFinished(() => Register(provider, registration));
                return true;
            }
            return Register(provider, registration);
        }

        private static bool Register(IRealityProvider provider, RealityProviderRegistration registration)
        {
            registration.providerId = registration.providerId.Trim();
            if (registration.semanticApiVersion != DeferredRealityFrameworkInfo.SupportedProviderApiVersion)
            {
                Log.Error("[DeferredReality] Provider '" + registration.providerId +
                    "' requested unsupported semantic API version " + registration.semanticApiVersion +
                    "; supported version is " + DeferredRealityFrameworkInfo.SupportedProviderApiVersion +
                    ". Registration rejected.");
                return false;
            }
            registration.dependencies = Normalize(registration.dependencies);
            registration.orderingBefore = Normalize(registration.orderingBefore);
            registration.orderingAfter = Normalize(registration.orderingAfter);
            registration.compactableOperationKinds = Normalize(registration.compactableOperationKinds);
            if (ProvidersById.TryGetValue(registration.providerId, out RegisteredProvider existing) &&
                existing?.Provider != null && existing.Provider.GetType() != provider.GetType())
            {
                Log.Error("[DeferredReality] Provider ID " + registration.providerId +
                    " was already registered by " + existing.Provider.GetType().FullName +
                    "; refusing a conflicting provider installation " + provider.GetType().FullName + ".");
                return false;
            }
            ProvidersById[registration.providerId] = new RegisteredProvider
            {
                Provider = provider,
                Registration = registration
            };
            revision++;
            DeferredRealityWorldComponent world = DeferredRealityWorldComponent.Current;
            if (world != null) NotifyProvider(ProvidersById[registration.providerId], world);
            return true;
        }

        /// <summary>Finds a provider without exposing registry storage.</summary>
        public static bool TryGet(string providerId, out IRealityProvider provider)
        {
            if (ProvidersById.TryGetValue(providerId ?? string.Empty, out RegisteredProvider registered))
            {
                provider = registered.Provider;
                return true;
            }
            provider = null;
            return false;
        }

        /// <summary>Returns detached provider metadata for safe retention decisions.</summary>
        public static bool TryGetRegistration(string providerId, out RealityProviderRegistration registration)
        {
            if (ProvidersById.TryGetValue(providerId ?? string.Empty, out RegisteredProvider registered))
            {
                registration = registered.Registration.Clone();
                return true;
            }
            registration = null;
            return false;
        }

        /// <summary>Returns detached registration metadata in deterministic order.</summary>
        public static IReadOnlyList<RealityProviderRegistration> Registrations()
        {
            return OrderedProviders().Select(item => item.Registration.Clone()).ToList();
        }

        /// <summary>Returns providers implementing a capability interface in deterministic order.</summary>
        public static IReadOnlyList<T> OfType<T>() where T : class
        {
            return OrderedProviders().Where(item => item.Provider is T &&
                (!(item.Provider is IRealityCapabilitySource source) || source.ProvidesCapability(typeof(T))))
                .Select(item => (T)item.Provider).ToList();
        }

        /// <summary>Resolves a configured capability without exposing optional facade methods as false providers.</summary>
        public static bool TryGetCapability<T>(string providerId, out T capability) where T : class
        {
            capability = null;
            if (!TryGet(providerId, out IRealityProvider provider) || !(provider is T value)) return false;
            if (provider is IRealityCapabilitySource source && !source.ProvidesCapability(typeof(T))) return false;
            capability = value;
            return true;
        }

        internal static void NotifyWorldReady(DeferredRealityWorldComponent world)
        {
            foreach (RegisteredProvider provider in OrderedProviders()) NotifyProvider(provider, world);
        }

        private static void NotifyProvider(RegisteredProvider registered, DeferredRealityWorldComponent world)
        {
            try
            {
                string providerId = registered.Registration.providerId;
                registered.Provider.OnRegistered(new RealityProviderContext(world, providerId, world.Now));
                world.ReactivateProviderProcesses(providerId);
                if (registered.Provider is IRegionDescriptorProvider descriptorProvider &&
                    (!(registered.Provider is IRealityCapabilitySource source) ||
                     source.ProvidesCapability(typeof(IRegionDescriptorProvider))))
                {
                    foreach (RealityRegionDescriptor descriptor in descriptorProvider.DescribeRegions(
                        new RealityProviderContext(world, providerId, world.Now)) ?? Enumerable.Empty<RealityRegionDescriptor>())
                        if (descriptor != null) world.UpsertRegionDescriptor(descriptor);
                }
            }
            catch (Exception exception)
            {
                Log.ErrorOnce("Deferred Reality provider registration failed for " + registered.Registration.providerId + ": " + exception,
                    RealityDeterminism.StableHash("provider-register:" + registered.Registration.providerId));
            }
        }

        private static List<string> Normalize(IEnumerable<string> values)
        {
            return (values ?? Enumerable.Empty<string>()).Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item.Trim()).Distinct(StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal).ToList();
        }

        private static IReadOnlyList<RegisteredProvider> OrderedProviders()
        {
            List<RegisteredProvider> all = ProvidersById.Values.OrderBy(item => item.Registration.order)
                .ThenBy(item => item.Registration.providerId, StringComparer.Ordinal).ToList();
            var edges = all.ToDictionary(item => item.Registration.providerId, item => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
            foreach (RegisteredProvider provider in all)
            {
                RealityProviderRegistration registration = provider.Registration;
                foreach (string dependency in registration.dependencies ?? new List<string>())
                    if (edges.ContainsKey(dependency)) edges[dependency].Add(registration.providerId);
                foreach (string before in registration.orderingBefore ?? new List<string>())
                    if (edges.ContainsKey(before)) edges[registration.providerId].Add(before);
                foreach (string after in registration.orderingAfter ?? new List<string>())
                    if (edges.ContainsKey(after)) edges[after].Add(registration.providerId);
            }
            var incoming = all.ToDictionary(item => item.Registration.providerId, item => 0, StringComparer.Ordinal);
            foreach (HashSet<string> targets in edges.Values)
                foreach (string target in targets) incoming[target]++;
            var ready = all.Where(item => incoming[item.Registration.providerId] == 0)
                .OrderBy(item => item.Registration.order).ThenBy(item => item.Registration.providerId, StringComparer.Ordinal).ToList();
            var result = new List<RegisteredProvider>(all.Count);
            while (ready.Count > 0)
            {
                RegisteredProvider next = ready[0];
                ready.RemoveAt(0);
                result.Add(next);
                foreach (string target in edges[next.Registration.providerId].OrderBy(item => item, StringComparer.Ordinal))
                {
                    incoming[target]--;
                    if (incoming[target] == 0)
                        ready.Add(all.First(item => item.Registration.providerId == target));
                }
                ready = ready.OrderBy(item => item.Registration.order).ThenBy(item => item.Registration.providerId, StringComparer.Ordinal).ToList();
            }
            if (result.Count != all.Count)
                result.AddRange(all.Where(item => !result.Contains(item)).OrderBy(item => item.Registration.order)
                    .ThenBy(item => item.Registration.providerId, StringComparer.Ordinal));
            return result;
        }
    }

    /// <summary>Immutable lifecycle/domain event bus with disposal and provider isolation.</summary>
    public static class RealityEventBus
    {
        private static readonly List<Action<RealityEvent>> Subscribers = new List<Action<RealityEvent>>();

        /// <summary>Subscribes and returns a disposal token.</summary>
        public static IDisposable Subscribe(Action<RealityEvent> subscriber)
        {
            RealityThreadGuard.RequireMainThread();
            if (subscriber == null || Subscribers.Contains(subscriber)) return new Subscription(null);
            Subscribers.Add(subscriber);
            return new Subscription(subscriber);
        }

        /// <summary>Publishes an event to a stable snapshot of subscribers.</summary>
        public static void Publish(RealityEvent value)
        {
            if (value == null) return;
            RealityThreadGuard.RequireMainThread();
            Action<RealityEvent>[] snapshot = Subscribers.ToArray();
            for (int i = 0; i < snapshot.Length; i++)
            {
                try { snapshot[i](value); }
                catch (Exception exception)
                {
                    Log.ErrorOnce("Deferred Reality event consumer failed: " + exception,
                        RealityDeterminism.StableHash("event-consumer:" + snapshot[i].Method.Name));
                }
            }
        }

        private sealed class Subscription : IDisposable
        {
            private Action<RealityEvent> subscriber;

            internal Subscription(Action<RealityEvent> subscriber) { this.subscriber = subscriber; }

            public void Dispose()
            {
                if (subscriber == null) return;
                RealityThreadGuard.RequireMainThread();
                Subscribers.Remove(subscriber);
                subscriber = null;
            }
        }
    }
}
