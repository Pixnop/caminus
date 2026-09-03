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

Milestones 0, 1, 2, 2b, 5, 3 and 4 are on `main` with Atlas scenarios (29 engine tests, 20 scenarios).
After the first in-game test the order changed:

- **Milestone 2b, done**: wind (speed, direction, windward faces and openings leak more),
  analytic vertical stratification (the ceiling is warmer than the floor and loses more), and
  rooms that keep living without a player (tracked while their chunk is loaded, discovered by
  their own containers).
- **Milestone 5 (overlay) moved right after 2b, done**: block highlights coloured by heat flow,
  particles drifting along the flow, small HUD, toggled by a hotkey. It is the visual check for
  everything above.
- **Milestone 3, done (2026-09-03)**: `EntityBehaviorCaminusBodyTemperature` subclasses the vanilla
  behavior and is swapped in by a JSON patch of `game:entities/humanoid/player.json`. The whole
  vanilla update is ported line by line; the flat +1 °C per hour every enclosed room used to grant
  is gone, and the room node's air at the player's eye height feeds the same comfort term vanilla
  applies outdoors. Details and what was verified: `api-1.22.7-verification.md` section 13.
- **Milestone 4, done (2026-09-03)**: a flue is a vertical run of blocks carrying the `Chimney`
  behavior starting on a ceiling face of the room; valid if its top sees the sky, blocked otherwise.
  Stack draft `Q = Cd·A_eff·√(2·g·ΔH·ΔT/T_abs)`, with the envelope's own leakage as the inlet in
  series with the flue's own section; the draft is added as an extra conductance from the room to
  the outside node, and the share of a smoking source's power set by `flueLossFraction` leaves with
  it instead of heating the room. A per-room `Smoke` value (0..1) rises with lit smoke sources
  (`BlockFirepit`, `BlockPitkiln`) and decays with the air changes the draft (or the bare envelope)
  provides; the `/caminus temp` report gained `Flue:` and `Smoke:` lines and the `Sources:` line
  notes the flue's share, the overlay highlights the stack and drifts particles up it. No room-to-room
  edge was needed for a stacked cabin: two rooms connected by open air are already one room to the
  vanilla `RoomRegistry`, so the "vertical links between stacked rooms" item from the original brief
  had nothing to attach to yet. What the brief called "a low opening vs. none" as the draft's inlet
  is still just the envelope's flat per-face leakage: a real opening's position and height only
  matter once milestone 6's own room detection can keep a hole in a wall without losing the room, so
  that refinement moves there. Tested: `Chimney_is_detected_with_its_height` (a 4-block stack reads
  as one flue, correct height, no heavy smoke once it draws), `Taller_chimney_draws_more` (8 blocks
  draft more than 4, compared as draft/√ΔT to stay robust to the two rooms not settling on quite the
  same temperature), `Hearth_without_chimney_fills_the_room_with_smoke`, `Blocked_chimney_counts_as_none`
  (a stone cap over the stack reads as blocked, not as no flue at all). Engine coverage in
  `Caminus.Core.Tests.ChimneyTests`. Details: `api-1.22.7-verification.md` section 14.
- Next: milestone 6.

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

## Milestone 3: the player gets cold (done, 2026-09-03)

- `caminusbodytemperature` behavior (subclass of `EntityBehaviorBodyTemperature`, JSON patch of
  `game:entities/humanoid/player.json`): air temperature = the room node's at eye height, plus
  vanilla's own radiant scan of the room bbox, plus clo from existing clothing, wind only outside a
  room. No more flat +1/h in an enclosed room.
- Simple single-node model (the game's, but with a real air temperature); Fanger and Gagge stay in
  reserve.

Tested: Atlas scenarios `Player_body_temperature_uses_our_behavior` (the entity carries exactly one
body temperature behavior and it is ours; the air the body reads is the room's eye-height value, not
the climate) and `Unheated_cold_room_cools_the_player` (a room forced to -25 °C drains the player
while an identical room in the normal climate does not). In-game: winter, a fireless cabin means
freezing; light it and warm back up.
Compatibility still to check with Immersive Body Temperature and RealisticTemperatures: they target
the same behavior entry, and whichever patch runs last wins the `code` field.

## Milestone 4: the chimney (done, 2026-09-03)

- Duct detection: a column of blocks carrying the `Chimney` behavior above the firepit (any block
  with that behavior, not just `claybrickchimney` by name), grouped into one flue per connected
  horizontal footprint on the ceiling; valid if its top sees the sky, blocked otherwise. A firepit
  without a working duct smokes into the room; with one, `flueLossFraction` of its power leaves
  with the draft instead of heating the room.
- Draft: `Q = Cd·A_eff·√(2·g·ΔH·ΔT/T_abs)` between the room and the outdoors via the duct, where
  `A_eff` is the flue's own section in series with the envelope's leakage (the inlet); added as an
  extra conductance from the room node to the outside node, recomputed from the current
  temperatures every tick.
- Per-room `Smoke` (0..1): rises with lit smoke sources, decays with the air changes the draft (or
  the bare envelope) provides, integrated in closed form so a strongly drawing flue does not
  oscillate at a 1 s step.
- Analytic stratification `T(y) = T_avg + gradient·(y − y_floor)` (done earlier, milestone 2b): what
  the thermometer reads depends on the player's height, and it is what feeds the flue the ceiling's
  own temperature rather than the room's mean.

Vertical links between stacked rooms and low openings as draft inlets were in the original brief;
neither turned out to be needed yet. See the status section above for why, and for what was tested.

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
