# Caminus: milestone roadmap

Proposal from 2026-09-02, after verifying the 1.22.7 API (`api-1.22.7-verification.md`) and the
ModDB (`moddb-survey-2026-09-02.md`). Each milestone is playable on its own and leaves something
testable. Target: 1.22.7, precompiled mod (Harmony isn't accessible to source mods), .NET 10.

## Structural decisions from the verification

1. The thermal engine is a pure .NET project, no reference to the game, tested with xunit. The mod
   is just an adapter: reading blocks, sources, calendar, persistence, networking.
2. Milestones 1 through 3 build on the vanilla `RoomRegistry` (14-block-per-axis limit, main thread
   only). The homemade flood-fill is a separate milestone, not a prerequisite.
3. Two rewirings happen without Harmony: body temperature via a JSON patch of
   `player.json` (behavior subclass), and spoilage via `InventoryBase.OnAcquireTransitionSpeed`
   or an `InWorldContainer` override. Harmony only comes in as a last resort, isolated in a single file.
4. Per-chunk persistence via `LiveModData` + `SetModdata<T>` on unload (`ChunkColumnUnloaded` fires
   before the unload, chunks are still readable). Deep ground node per server region.
5. Don't break greenhouses: vanilla criterion `SkylightCount > NonSkylightCount && ExitCount == 0`.

## Status and reordering (2026-09-03)

Milestones 0, 1 and 2 are on `main` with Atlas scenarios. After the first in-game test the order
changed:

- **Milestone 2b, in progress**: wind (speed, direction, windward faces and openings leak more),
  analytic vertical stratification (the ceiling is warmer than the floor and loses more), and
  rooms that keep living without a player (tracked while their chunk is loaded, discovered by
  their own containers).
- **Milestone 5 (overlay) moves right after 2b**: block highlights coloured by heat flow, particles
  drifting along the flow, small HUD, toggled by a hotkey. It is the visual check for everything
  above.
- Then milestone 3 (player body temperature), then milestone 4 (chimney draft).

## Milestone 0: skeleton (1 to 2 days)

Contents: repo, `Caminus.csproj` net10.0 referencing the 1.22.7 dlls, `modinfo.json` (`caminus`),
`Caminus.Core` (pure engine) + `Caminus.Core.Tests` (xunit), CI build + tests, deployment script to a
dedicated test VSL profile, `/caminus version` command.
Testable: the mod loads client- and server-side with no errors in the logs, CI green.

## Milestone 1: the honest thermometer (1 to 2 weeks)

The smallest playable thing: every vanilla room has a temperature that moves.

- Engine: nodes = rooms + outdoors (`GetClimateAt`), capacity ∝ air volume, edges = walls with U
  values by `EnumBlockMaterial` (JSON table in assets), openings = high conductance, sources = every
  `IHeatSource` in the room converted to power (firepit 10 → a few kW, JSON table).
  Implicit Euler, 1 s game step, solved via Gauss-Seidel or dense Cholesky (≤ 50 nodes).
- Game: 1 s server tick, room → node cache invalidated by `ChunkDirty`, `caminus:thermometer` item
  (or `/caminus temp` command first) showing the temperature of the room the player is in,
  server → client packet for the display.
- No persistence: on reload, the room resets to the outdoor temperature.

Testable: solver unit tests (analytic equilibrium of a two-wall room, stability at a large time step,
energy conservation, step-size independence). In-game: a closed cabin with a lit firepit, the
temperature rises then plateaus; open the door, it drops; a stone wall against a wood wall, a
measurable gap.

## Milestone 2: cellars and spoilage (1 to 2 weeks)

- Deep ground node per region: Kusuda & Achenbach from the mean annual temperature of
  `GetClimateAt(WorldGenValues)`, depth = distance to the surface level (`GetRainMapHeightAt`).
  Ground contact of underground rooms = conductance toward this node.
- Persistence: node temperatures and last-tick timestamp in chunk moddata; exponential relaxation
  toward equilibrium on reload.
- Spoilage: the vanilla rate is replaced by a Q₁₀ on the room's actual temperature.
  Route 1: `OnAcquireTransitionSpeed` subscription on container inventories (multiplicative,
  divide out the vanilla rate). Route 2 if route 1 turns out too hacky: Harmony postfix on
  `InWorldContainer.GetPerishRate`, a single file, compatibility test with Fix Perish Rate.
  The thermometer also shows the estimated shelf life of food in that room.

Testable: unit tests for Kusuda (damping and phase shift by depth), for offline relaxation
(extrapolating 1000 steps = integrating 1000 steps, within tolerance). In-game: a cellar 3 blocks
deep vs. 10 blocks deep in summer and winter, two baskets of meat side by side inside and outside.

## Milestone 3: the player gets cold (1 week)

- `caminus:bodytemperature` behavior (subclass, JSON patch of `player.json`): air temperature =
  the node's, plus a radiant term from the firepit (distance, line of sight in the room), plus clo
  from existing clothing, wind only outside a room. No more flat +1/h in an enclosed room.
- Simple single-node model first (the game's, but with a real air temperature); Fanger and
  Gagge stay in reserve.

Testable: unit test on the balance (a room at 5 °C with no fire cools the player, a firepit 2 blocks
away warms them). In-game: winter, a fireless cabin means freezing; light it and warm back up.
Compatibility to check with Immersive Body Temperature and RealisticTemperatures (same behavior targeted).

## Milestone 4: the chimney (2 weeks, the heart of the gameplay)

- Duct detection: a column of `claybrickchimney` (or hollow blocks) above the firepit, height and
  cross-section. A firepit without a duct smokes into the room (penalty); with a duct, some of the
  power goes into the draft.
- Draft: `Q = Cd·A·√(2·g·ΔH·ΔT/T)` between the room and the outdoors via the duct; the air drawn in
  enters through low openings (infiltration flow added to the edges toward outdoors or the room
  below). Same nonlinear term for vertical links between stacked rooms (an open stairwell).
  Linearized at each step around the current state, implicit integration still holds.
- Analytic stratification `T(y) = T_avg + gradient·(y − y_floor)`: what the thermometer reads
  depends on the player's height.

Testable: unit test of the draft (flow increasing with height, zero at ΔT = 0). In-game: the same
firepit, a 3- vs. 8-block duct; a low opening vs. none; an attic above that warms up.

## Milestone 5: reading your building (1 to 2 weeks)

- Client overlay: color by room, flow per wall (W), sources, incoming/outgoing air. Protocol:
  the server only sends nodes for rooms near the player who enabled the overlay, at 1 Hz.
- Interface for the placed thermometer (block) with min/max history, in the style of Self-Recording
  Thermometer.

Testable: measured bandwidth with the overlay active on a 30-room base; reference screenshot.

## Milestone 6: large volumes and robustness (2 weeks)

- Homemade flood-fill for rooms beyond 14 blocks (block budget, run on a worker thread with
  `ICachingBlockAccessor`, merge into "outdoors" when the limit is hit). The vanilla `RoomRegistry`
  stays in use for the game's greenhouses and cellars.
- Compatibility tested against ModDB neighbors (list in the survey), Real Smoke first.
- Balancing: U-value table, power levels, thresholds, from playtest feedback.

## Reserve (v2)

Linden two-layer model per room, Fanger/Gagge comfort, humidity, coarse-grid hybrid version if
analytic stratification isn't enough.

## Deliberately out of scope for v1

Client-side simulation, chiseled block mods (a chiseled block's U-value = that of its majority
material, nothing more), a public API for other mods (comes when someone asks for it).
