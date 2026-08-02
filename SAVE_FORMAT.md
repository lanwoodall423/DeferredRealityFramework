# Save Format

The framework stores one `DeferredRealityWorldComponent` under the world save.
The root schema key is `deferredRealitySaveSchema`; the current root schema is
`1`. All framework child records carry their own `schemaVersion`.

## Root collections

- `deferredRealityRegions`: descriptors, fidelity, observations, environment, and provider blobs.
- `deferredRealityTopology`: directed or conditional region links.
- `deferredRealityPopulations`: aggregate objective populations and uncertainty.
- `deferredRealityAnchors`: identity-bearing objects and typed provider payloads.
- `deferredRealityConstraints`: unresolved outcomes and causal links.
- `deferredRealityProcesses`: scheduled analytical processes.
- `deferredRealityObservations`: bounded player/sensor/inferred evidence history.
- `deferredRealityProviderPayloads`: explicitly opaque, versioned provider blobs.
- `deferredRealityMapAliases`: legacy `Map.uniqueID` to stable region aliases.
- `deferredRealityMigrations`: idempotent provider migration markers.
- `deferredRealityAppliedOperations`: exactly-once mutation IDs.
- `deferredRealityConflicts`: explicit incompatible-fact reports.
- `deferredRealityQuarantine`: invalid, missing-Def, missing-provider, or duplicate records.
- `deferredRealityTransferJournals`: save-safe adjacent transfer transactions.

`Map.uniqueID` is only stored in an alias or active-map link. A latent region ID
never depends on it. Region IDs round-trip as `rr1|...` strings and include tile,
layer, local slot, instance, parent, and provider namespace.

## Migration and resilience

Loading repairs null lists and duplicate IDs deterministically while retaining an
audit quarantine record. Unknown provider data is preserved as a string blob.
Missing Defs do not erase a population or anchor. Missing providers pause their
processes and leave their payloads inspectable. A provider-specific migration is
committed only after all imported records validate; repeating a load is safe.
Interrupted adjacent transfers remain journaled; completed IDs are idempotent and
incomplete transactions fall back until an explicit host recovery handles them.

Legacy Wildlife, Aquaculture, and Horticulture keys remain owned by their original
assemblies. Their Deferred Reality adapters import into new records and do not
rewrite or delete the legacy collections in v1.
