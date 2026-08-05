# Provider Integration Migration

Deferred Reality Framework is provider-neutral. Gameplay integrations belong in
their consuming mod repositories and are not compiled, packaged, or referenced by
this project. A provider owns its map parent/generator, provider-scoped factory,
identity claim, transfer host, materialization/compression stages, task evidence,
legacy migration, and provider-specific diagnostics.

## Required integration surface

Providers should consume the public contracts in `Source/API/RealityContracts.cs`
and the world APIs for:

- `RealityProviderRegistration` and `RealityProviderRegistry.Register`;
- `RealityMaterializationRequest`, typed `RealityAdjacentMapMetadata`, and
  transaction-scoped map-creation intents;
- `IRealityMapIdentityProvider`, `IRealityMapFactory`, and
  `IMaterializationProvider`/`ICompressionProvider`;
- `IAdjacentRegionTransferHost` and `RealityAdjacentTransferRequest`;
- `BeginExcursion`, `AttachExcursion`, `HeartbeatExcursion`,
  `CompleteExcursion`, `CancelExcursion`, and `RequestImmediateReturn`;
- `IRealityExcursionTaskProvider` and optional
  `IRealityExcursionTaskCleanupProvider`;
- meaningful `TouchAdjacentMap` access and provider-neutral diagnostic APIs;
- `IRealityExactlyOnceProvider`, `DeclareExactlyOnceDomain`, and
  `AdvanceExactlyOnceCursor` for replay-safe operation retention.

Provider-specific job names, map parents, population records, task fingerprints,
gameplay behavior, and compatibility migrations must remain in the
consuming provider repository. The framework supplies only universal safety
classification and exact Pawn/map identity checks.

## Existing saves

Removing a provider assembly does not delete its saved populations, anchors,
constraints, payloads, processes, journals, adjacent markers, or excursions. The
framework retains missing-provider state and quarantines work it cannot safely
execute. Reinstall the provider and register the same stable provider ID to resume
it. Provider migrations must preserve stable IDs and must not guess ownership of
ambiguous records.

Legacy operation-ID-only markers are intentionally durable. A provider may enable
compaction only after declaring a domain sequence policy and advancing a persisted
cursor. Old tick-only watermarks do not authorize deletion or replay acceptance.
