# Wildlife Migration

The `DeferredReality.Wildlife` adapter registers provider `lan.wildlife` and
imports one generic population per stable region/species subject. It reads the
existing `RegionalWildlifeMapComponent.Records` and `RoamingAnimals` projections
without changing Wildlife Scribe keys.

## Moved semantic ownership

- regional anonymous animal abundance and uncertainty;
- analytical growth while a region is latent;
- stable population subjects independent of `Map.uniqueID`;
- tagged/notable roaming animals as framework anchors;
- departure constraints and local loss cooldown constraints;
- exactly-once local death, capture, and birth deltas.

## Retained active ownership

Herd AI, packs, pathfinding, hiding, local danger, memories, traditions, signal
rendering, map Things, current jobs/Lords/reservations, UI, and player policies
remain in Wildlife. The adapter suppresses only the regional ecology and local
roaming update methods once the provider migration is committed. It keeps the
existing component as a projection/reconciliation surface, so old UI and save
records remain available.

The adapter adds one narrow `ActiveMap` projection property to the existing
regional component. It does not serialize arbitrary pawn state or move a live
pawn into aggregate storage. Anchors retain the existing RimWorld load ID and
provider provenance; restoration is a provider responsibility and is not claimed
safe for generic pawns.

Wildlife trail excursions are the explicit exception for a provider-owned live
Pawn transfer. The adapter marks generated adjacent maps with typed metadata and
its `WildlifeDeferredMapParent.regionId` identity, then creates a durable framework
excursion ticket only after the transfer host commits the exact tracker Pawn on the
destination map. The ticket retains the origin map/cell, inverse edge, outbound and
return IDs, heartbeat/lease state, and diagnostics. Trail integrations should use
the world lease API rather than assuming Herds exposes a completion callback; the
framework returns the tracker only when the task is explicitly completed or a
conservative idle lease expires. Missing origins, provider removal, interrupted
transfers, duplicate ownership, and unsafe jobs retain the adjacent map and Pawn
for retry rather than falling back to a different map or reconstructing the Pawn.

## Legacy conversion

Migration consumers are `regional:<RealityRegionId>`, version `1`, with checksum
`wildlife-regional-v1`. The legacy regional collections remain in saves until a
future cleanup phase has validated multiple save/load cycles. Missing animal Defs
are quarantined by the framework instead of silently recreated.

Knowledge subject migration is intentionally additive in this phase. Existing
Wildlife Knowledge records keep their domain and subject IDs; a later bridge can
map legacy map-context claims to the stable region context after explicit
consumer-level aliases are registered.
