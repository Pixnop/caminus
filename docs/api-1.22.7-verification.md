# Vintage Story 1.22.7 API verification for Caminus

Source: local decompilation (ilspycmd 10.1.1) of `VintagestoryAPI.dll`, `VintagestoryLib.dll`,
`Mods/VSEssentials.dll`, `Mods/VSSurvivalMod.dll` from 1.22.7 (`~/.config/VSLGameVersions/1.22.7/`).
Game target runtime: net10.0. Every claim below was re-checked against the decompiled source on 2026-09-02.

## 1. Climate

`IBlockAccessor.GetClimateAt` (IBlockAccessor.cs:537-552), three overloads:

```csharp
ClimateCondition GetClimateAt(BlockPos pos, EnumGetClimateMode mode = EnumGetClimateMode.NowValues, double totalDays = 0.0);
ClimateCondition GetClimateAt(BlockPos pos, ClimateCondition baseClimate, EnumGetClimateMode mode, double totalDays);
ClimateCondition GetClimateAt(BlockPos pos, int climate);
```

`EnumGetClimateMode`: `WorldGenValues`, `NowValues`, `ForSuppliedDateValues`, `ForSuppliedDate_TemperatureOnly`, `ForSuppliedDate_TemperatureRainfallOnly`.

`ClimateCondition`: public fields `Temperature`, `WorldgenRainfall`, `WorldGenTemperature` (capital G), `Rainfall`, `Fertility`, `ForestDensity`, `ShrubDensity`, `GeologicActivity`, `Biome`.

Chain: `BlockAccessorBase` (lib, l.757) replaces `totalDays` with `Calendar.TotalDays` in `NowValues` mode, then `ServerWorldMap.GetClimateAt` (l.552) reads the worldgen value and fires `EventManager.TriggerOnGetClimate`. The instantaneous temperature is set by `ModTemperature.Event_OnGetClimate` (VSSurvivalMod, private): latitude, season, diurnal amplitude `18 - Rainfall*13`, two simplex noises.

**Clean hook**: `IEventAPI.OnGetClimate` (IEventAPI.cs:64), delegate
`void OnGetClimateDelegate(ref ClimateCondition climate, BlockPos pos, EnumGetClimateMode mode, double totalDays)`.
Subscribers are called in sequence and can mutate `climate`. Order controlled by `ModSystem.ExecuteOrder`.

**Gotcha**: server-side, `GetClimateAt` returns `null` if the region isn't loaded, except in
`ForSuppliedDate_TemperatureOnly` mode (fallback 4 °C). Always test for `null`.

## 2. Room detection

`RoomRegistry` and `Room` live in **VSEssentials**, namespace `Vintagestory.GameContent`.

- `api.ModLoader.GetModSystem<RoomRegistry>()` (8 vanilla callers).
- `public Room GetRoomForPosition(BlockPos pos)` (RoomRegistry.cs:286). The only publicly useful method.
- Loaded on both server AND client (`ShouldLoad => true`), no network sync: each side recomputes independently.
- `Room`: `int ExitCount`, `bool IsSmallRoom`, `int SkylightCount`, `int NonSkylightCount`,
  `int CoolingWallCount`, `int NonCoolingWallCount`, `Cuboidi Location`, `byte[] PosInRoom`, `int AnyChunkUnloaded`.

Private flood-fill constants (RoomRegistry.cs:32-44):

| Constant | Value | Role |
|---|---|---|
| ARRAYSIZE | 29 | 29³ visited-blocks array |
| MAXROOMSIZE | 14 | beyond 14 blocks on one axis, counts as an exit and stops |
| MAXCELLARSIZE | 7 | IsSmallRoom if bbox ≤ 7³ |
| ALTMAXCELLARSIZE / VOLUME | 9 / 150 | tolerance of 9 on one axis if volume ≤ 150 |

Walls classified by `Block.GetRetention(pos, facing, EnumRetentionType.Heat)`: >0 NonCoolingWall, <0 CoolingWall.
Invalidated by `api.Event.ChunkDirty` (any SetBlock). BFS up to 24,389 positions via a `[ThreadStatic]` `ICachingBlockAccessor`.

**Gotchas**: `currentVisited` and `skyLightXZChecked` are unprotected instance fields, and
`FindRoomForPosition` runs without a lock. Call only from the main server thread.
`Cuboidi.SizeX = MaxX - MinX` with no +1.

**Consequence**: the vanilla registry can't handle a room larger than 14 blocks. Caminus will need
its own flood-fill for large volumes, or accept the limit in v1.

## 3. Heat sources and body temperature

`IHeatSource` (VSSurvivalMod, IHeatSource.cs:6):
`float GetHeatStrength(IWorldAccessor world, BlockPos heatSourcePos, BlockPos heatReceiverPos);`

| Implementation | Value |
|---|---|
| BlockEntityFirepit (l.831) | 10 if IsBurning, 0.25 if IsSmoldering |
| BlockEntityCoalPile (l.525) | 10 if IsBurning and not cokable |
| BlockEntityForge / Bloomery | 7 if IsBurning |
| BlockEntityGroundStorage (virtual) / PitKiln | 10 if burning |
| BlockEntityOven (l.401) | (T-20)/(300-20)*8 |
| JSON `heatStrength` | fire 10, lava 12, ember 6, boilingwater 3, pitkiln 10 |

Torches and kettles are not sources. Discovery via `Block.GetInterface<T>(world, pos)` (block, behavior, BE, BE behaviors).

`EntityBehaviorBodyTemperature` (VSSurvivalMod, code `"bodytemperature"`, registered in SurvivalCoreSystem.cs:865):
- public: `CurBodyTemperature`, `Wetness` (capital W), `NormalBodyTemperature`, `BodyTempUpdateTotalHours`.
- `nearHeatSourceStrength` protected; `clothingBonus`, `inEnclosedRoom`, `tempChange`, `bodyTemperatureResistance` private.
- `protected void updateBodyTemperature()` (l.216) NOT virtual; `getNearHeatSourceStrength()` private.
- Only `Initialize`, `OnGameTick`, `OnEntityDespawn`, `OnEntityRevive`, `PropertyName` are overridden.
- Sweeps roughly every 3 s server-side: full room bbox if `inEnclosedRoom` (`ExitCount == 0 || SkylightCount < NonSkylightCount`), otherwise ±3 blocks. Attenuation `min(1, 9/(8 + d^p))`, p = 0.875 in an enclosed room, 1.25 outdoors.
- Formula (l.266): `tempChange = nearHeatSourceStrength + (inEnclosedRoom ? 1f : -vent + num6)`.
  **In an enclosed room, climate and wind are ignored**: flat +1/h.

`ClassRegistry.RegisterentityBehavior` (lib, l.442) uses `Dictionary.Add`: impossible to re-register
`"bodytemperature"` with a different type. Clean route: JSON patch of `game:entities/humanoid/player.json`
(lines 326 and 4305) to swap the behavior's code for ours, a subclass with `OnGameTick` rewritten.
Done in milestone 3; the exact paths, the patch loader's rules and the two balance quirks are in
section 13.

## 4. Spoilage

**`BlockEntityContainer.GetPerishRate()` doesn't exist.** The real one: `InWorldContainer.GetPerishRate()`
(VSEssentials, InWorldContainer.cs:172, `public virtual`). `BlockEntityContainer` (VSSurvivalMod) holds a
`protected InWorldContainer container` created in its constructor. Transition tick: 10 s.

Vanilla formula:

```
pos.Y = SeaLevel                      // depth does NOT enter via climate
T = GetClimateAt(pos, ForSuppliedDate_TemperatureOnly).Temperature   (hidden server-side)
skyRel = Skylight / max(1, Skylight + NonSkylight)
cellarRel = IsSmallRoom ? 1 - 0.4*skyRel - 0.5*clamp(NonCooling/max(1,Cooling), 0, 1) : 0
f = 0.1 + (IsSmallRoom ? 0.3*cellarRel + 1.75*skyRel : Exit <= 0.1*walls ? 1.25*skyRel : 0.5*skyRel), clamp 0..1.5
tEff = T + clamp(sunLight-11, 0, 10) * f
v = min(lerp(tEff, 5, cellarRel), tEff)
rate = clamp(3^(v/19 - 1.2) - 0.1, 0.1, 2.4)
```

`ConstantPerishRateContainer : InWorldContainer` overrides `GetPerishRate`: overriding is the intended mechanism.

`InWorldContainer` members a patch can reach: `Room`, `Inventory` (calls `inventorySupplier()`) and
`Inventory.Api` / `Inventory.Pos` are public; `positionProvider` is **protected** and has no
property, so a postfix needs `AccessTools.FieldRefAccess<InWorldContainer,
Vintagestory.GameContent.PositionProviderDelegate>("positionProvider")`. Qualify that delegate:
`Vintagestory.API.Common` declares another one of the same name returning `Vec3d`.

Non-Harmony hooks, in order of cleanliness:
1. `InventoryBase.OnAcquireTransitionSpeed` (event `CustomGetTransitionSpeedMulDelegate(EnumTransitionType, ItemStack, float mulByConfig)`), composed via `mul *= handler(...)` (InventoryBase.cs:795). Multiplicative, doesn't replace.
2. `GlobalConstants.PerishSpeedModifier` (global).
3. `CollectibleBehavior.UpdateAndGetTransitionStates` with `PreventSubsequent` (one behavior per collectible).
4. Subclass each container BE to swap out `container`.

Globally replacing the formula = Harmony postfix on `InWorldContainer.GetPerishRate`. The ModDB mod
"Fix Perish Rate" patches exactly this method (the `sealevelpos.Y = SeaLevel` line).

`PerishableFactorByFoodCategory` isn't on `InventoryBase` but on `InventoryGeneric` and `InventoryInfinite`.

## 5. Persistence

`IWorldChunk`: `SetModdata(string, byte[])`, `GetModdata(string)`, `SetModdata<T>`, `GetModdata<T>(key, default)`,
`RemoveModdata`, `MarkModified()`, and `Dictionary<string, object> LiveModData`: not continuously serialized,
pushed into moddata on unload, NOT repopulated on load (must be reloaded yourself via `GetModdata`).
`SetModdataObject` doesn't exist.

`IServerChunk.SetServerModdata / GetServerModdata`: server only, not synced. **Gotcha**: unlike
`WorldChunk.SetModdata` (which calls `MarkModified`), `ServerChunk.SetServerModdata` (l.659) only
writes the dictionary. Both `TryUnloadChunk` and the autosave skip chunks whose `DirtyForSaving` is
false, so a `chunk.MarkModified()` after every write is mandatory or the data never reaches the save.
`IMapRegion.SetModdata / GetModdata<T>`: server only, no `defaultValue`.
`ISaveGame.StoreData / GetData<T>` via `sapi.WorldManager.SaveGame`.

Events (IServerEventAPI): `ChunkColumnLoaded(Vec2i, IWorldChunk[])`, `ChunkColumnUnloaded(Vec3i)`
(fired BEFORE the unload loop, ServerSystemUnloadChunks.cs:287: chunks are still readable),
`BeginChunkColumnLoadChunkThread`. `BeforeChunkColumnUnloaded` doesn't exist.
`MapRegionLoaded(Vec2i, IMapRegion)` / `MapRegionUnloaded` on IEventAPI.
`GameWorldSave` (`Action`) fires on the autosave (ServerSystemAutoSaveGame.cs:77) **and** on the
save-on-shutdown (ServerSystemLoadAndSaveGame.cs:204), before the chunks are written: the one hook
that covers "persist everything before we go away".

## 6. Tick and calendar

`long RegisterGameTickListener(Action<float>, int millisecondInterval, int initialDelayOffsetMs = 0)` plus
variants with `Action<Exception> errorHandler` and `BlockPos` (ticks only if the chunk is loaded).
`RegisterCallback`, `UnregisterGameTickListener(long)`. Thread: `sapi.Server.AddServerThread(string, IAsyncServerSystem)`
(`OffThreadInterval`, `OnSeparateThreadTick`, `ThreadDispose`). Server tick 33.3 ms.
`IGameCalendar`: `TotalHours`, `TotalDays`, `HourOfDay`, `DayOfYear`, `DaysPerYear`, `SpeedOfTime`,
`ElapsedSeconds`, `YearRel`, `GetSeason(pos)`, `GetSeasonRel(pos)`.

## 7. Networking

`api.Network.RegisterChannel(name)`; `IServerNetworkChannel.RegisterMessageType<T>()`,
`SetMessageHandler<T>(NetworkClientMessageHandler<T>)`, `SendPacket<T>(T, params IServerPlayer[])`,
`BroadcastPacket<T>`. Client: `SetMessageHandler<T>(NetworkServerMessageHandler<T>)`, `SendPacket<T>`.
`INetworkChannel` is declared in `Vintagestory.API.Client`. Serialization is protobuf-net (`[ProtoContract]`).
Message ids are assigned in call order: client and server must register in the same order. 256 types max per channel.

## 8. Harmony

`Lib/0Harmony.dll` = Lib.Harmony.Thin **2.4.2** (net10.0), MonoMod.Core 1.3.3. The engine applies no
patch itself (only use: attributing crashes to the mods that patched, CrashReporter.cs). Assembly
resolution from `<game>/` and `<game>/Lib/`. **`0Harmony.dll` is not among the compilation references
of source mods** (ModCompilationContext.cs:20-39): Caminus must be a precompiled mod. VSEssentials and
VSSurvivalMod are referenced. Convention: `PatchAll` in `StartPre`, `UnpatchAll(id)` in `Dispose`,
guard with `Harmony.HasAnyPatches(id)` (singleplayer = client + server in the same process).

## 9. Blocks

`Block.BlockMaterial` (field, default Stone) and `GetBlockMaterial(accessor, pos, stack)` (virtual, must be thread-safe).
`EnumBlockMaterial`: Air, Soil, Gravel, Sand, Wood, Leaves, Stone, Ore, Water, Snow, Ice, Metal, Mantle,
Plant, Glass, Ceramic, Cloth, Lava, Brick, Fire, Meta, Other.
`Block.SideSolid`: `struct SmallBoolArray` indexed by `BlockFacing.Index`. `Attributes` inherited from `CollectibleObject`.

Surface height, two maps with near-identical names (IBlockAccessor.cs:474-506):
`GetTerrainMapheightAt(pos)` reads `IMapChunk.WorldGenTerrainHeightMap`, the topmost solid Y as
worldgen left it, never updated afterwards. `GetRainMapHeightAt(pos)` reads `RainHeightMap`, which
IS updated on every block placement, so the roof of a building becomes the "surface" and the
building's own walls then read as buried. For "is this face against natural ground", the worldgen
map is the right one. Both return 0 when the map chunk isn't loaded, and both are meaningless
outside the normal dimension. `BlockAccessorReadLockfree.GetRainMapHeightAt(BlockPos)` (l.306)
actually returns the *worldgen* map: the two accessors disagree on that overload.

`Block.GetRetention(pos, facing, EnumRetentionType)` (Block.cs:2025): behaviors first, then for Heat:
solid Ore/Stone/Soil/Ceramic = -1, other solids = +1, non-solid face = 0.
`BlockBehavior.GetRetention(pos, facing, type, ref EnumHandling)` (BlockBehavior.cs:360): clean extension
point, injectable via JSON patch. Consumed directly by the vanilla flood-fill.

`BlockEntityFirepit`: `IsBurning`, `IsSmoldering`, `fuelBurnTime`, `maxFuelBurnTime`, `furnaceTemperature`,
`maxTemperature` (= BurnTemperature * HeatModifier), `smokeLevel`.

**`BlockChimney` doesn't exist.** `BlockBehaviorChimney` (VSSurvivalMod) only overrides
`ShouldReceiveClientParticleTicks`: purely cosmetic. Block: `claybrickchimney`
(`assets/survival/blocktypes/clay/chimneycourse.json`, Ceramic material).

## 10. Greenhouses

`BlockEntityFastForwardGrowth.GetRoomness()` (protected virtual, l.120): greenhouse if
`SkylightCount > NonSkylightCount && ExitCount == 0` at the position above the block, and the block is
covered (`GetRainMapHeightAt > Y`). Effect: `+5 °C` on growth climate. `IsSmallRoom` doesn't factor in.
Same criterion in `BlockEntityBerryBush` and `BlockEntityBeehive`. Must not be broken.

## 11. Wind

`IBlockAccessor.GetWindSpeedAt(BlockPos)` / `(Vec3d)` (IBlockAccessor.cs:559-566). Server side it lands
on `ServerWorldMap.GetWindSpeedAt` (l.598), which starts from a zero `Vec3d` and fires
`EventManager.TriggerOnGetWindSpeed`; the `BlockPos` overload just forwards `new Vec3d(pos.X, pos.Y, pos.Z)`,
so the dimension is dropped. Never returns null.

The only vanilla subscriber is `WeatherSystemBase.Event_OnGetWindSpeed` (l.62):

```csharp
windSpeed.X = WeatherDataSlowAccess.GetWindSpeed(pos);
```

**The wind is one signed number on the X axis.** Y and Z stay zero, so vanilla wind always blows east
(positive) or west (negative), and `wind · n` is zero on every north/south face. `ModSystemDevastationEffects`
is the only other subscriber and only inside the devastation area.

Value: `WeatherSimulationRegion.GetWindSpeed(posY)` (l.351) = the current wind pattern's `Strength`,
then `× max(1, 0.9 + (y − seaLevel) / 100)` capped at **1.5** above sea level, or `/ (1 + (seaLevel − y) / 4)`
below it. Pattern strengths (`assets/game/config/windpatterns/`): still 0 ± 0.05, lightbreeze 0.15,
mediumbreeze 0.3, strongbreeze 0.6, storm 1.0 ± 0.1, each (except still) plus a simplex noise term of 0 to 1.
So the usable range is 0 to about 1.5, and 0.15 is the threshold vanilla itself treats as "windy"
(`EntityBehaviorBodyTemperature`, l.266: `max((wind.Length() − 0.15) × 2, 0)` cools an entity, but only
outside a room, since an enclosed room takes a flat +1/h instead).

**There is no indoor damping**: the value depends on the position's height and its map region, not on
whether there are walls around it. `WeatherDataReaderBase.pgetWindSpeed` bilinearly blends the four map
region sims around the position, and a region that is not loaded contributes `ws.dummySim`.

Caminus reads the wind two blocks above the roof and samples the incoming air's climate up to 64
blocks upwind, at `max(upwind terrain height + 1, room centre Y)`: `GetClimateAt` cools with
altitude, so sampling at ground level would give a building perched in the air a warm wind that has
nothing to do with the air around it, while terrain higher than the room does mean the air came over
it.

Driving it from a test or a command: `/weather setw <pattern>` (`WeatherSystemCommands`, l.128, requires
a player) or, in process, `WeatherSystemBase.weatherSimByMapRegion` (public field) and
`WeatherSimulationRegion.SetWindPattern(code, updateInstant)` (public), plus `dummySim`.

## 12. Sun, geology and forest

`ClimateCondition.GeologicActivity` and `ForestDensity` are both normalized 0..1 and both filled only
when `getWorldGenClimateAt` is called with `temperatureRainfallOnly = false` (ServerWorldMap.cs:610-643),
i.e. in every mode **except** `ForSuppliedDate_TemperatureOnly` and `ForSuppliedDate_TemperatureRainfallOnly`.
Both are worldgen values that never move, so one sample per room is enough.

Geologic activity is `(rnd/255)^(1/strength) × 255`, `strength` being the `geologicActivity` world
config (NoiseClimateRealistic.cs:66, GenMaps.cs:222; dropdown `0 / 0.05 / 0.1 / 0.2 / 0.4`, default
0.05 "Rare"). At the default the exponent is 20, so the value is near zero almost everywhere:
`P(activity > a) = 1 − a^0.05`, i.e. 21 % of positions above 0.01, 11 % above 0.1, 3.4 % above 0.5.
The geologically interesting places, the ones where `GenRivulets` puts lava, are that top few percent.

Sunlight: `IBlockAccessor.GetLightLevel(BlockPos, EnumLightLevelType.OnlySunLight)` returns the stored
sun level, 0 to `IWorldAccessor.SunBrightness` = **24** server side (ServerMain.cs:223), with the
day/night cycle taken out, so it is a fixed geometric sky exposure. An unloaded chunk answers
SunBrightness (BlockAccessorBase.cs:669-674).

**`Calendar.GetDayLightStrength` never returns 0.** It is `max(moonlight, sunlightTexture + zenith bonus)`
(GameCalendar.cs:394-414): at night the texture's leftmost pixel is (14,12,18)/255 = 0.057 and a full
moon adds up to 0.33. For "is the sun up", use `Calendar.GetSunPosition(pos, TotalDays).Y`, which is
`cos(zenith)` (l.215-222) and goes negative at night. The real spherical coordinates come from the
survival mod (`SurvivalCoreSystem.GetSolarSphericalCoords`, latitude and axial tilt, hooked at
`EnumServerRunPhase.GameReady`); without it `GameCalendar`'s own fallback returns zenith 0, i.e.
permanent noon.

Lava and boiling water carry `BlockBehaviorHeatSource` in their JSON (`survival/blocktypes/liquid/lava.json`
`heatStrength: 12`, `boilingwater.json` 3), and `Block.GetInterface<IHeatSource>` finds a block behavior
(Block.cs:2722). Liquids live in the fluid layer, but `GetBlock(pos)` with the default layer falls back
to it when the solid layer is empty, so a lava block is found without asking for `BlockLayersAccess.Fluid`.

## 13. Swapping the body temperature behavior (verified 2026-09-03)

### The entity file and the exact indices

`game:entities/humanoid/player.json` lives in `assets/game/entities/humanoid/player.json`, not in
`survival/`, and it is relaxed JSON: unquoted keys, `//` comments, trailing commas. Its two behavior
lists both carry the entry:

| Side | Path in the file | JsonPatch path | Line in 1.22.7 |
|---|---|---|---|
| Client | `client.behaviors[8]` | `/client/behaviors/8/code` | 326 |
| Server | `server.behaviors[10]` | `/server/behaviors/10/code` | 4305 |

Client list (13 entries): repulseagents, nametag, playerphysics, interpolateposition, playerrevivable,
aimingaccuracy, tiredness, extraskinnable, **bodytemperature**, breathe, drunktyping, idleanimations,
playerinventory.
Server list (15 entries): repulseagents, nametag, playerphysics, collectitems, health, hunger,
breathe, playerrevivable, aimingaccuracy, tiredness, **bodytemperature**, extraskinnable,
idleanimations, playerinventory, entitystatetags.

An index path is not a shortcut: `JsonPatch` (VSEssentials, `Vintagestory.ServerMods.NoObf`) has
`Op`, `File`, `FromPath`, `Path`, `DependsOn`, `Enabled`, `Side`, `Condition`, `Value` and no way to
address an array element by value. Vanilla patches this very file the same way
(`survival/patches/playerhealthpoints.json` writes `/server/behaviors/4/currenthealth`, index 4 =
`health`, which matches the list above). A mod that inserts a behavior before index 8 or 10 would
make our patch rewrite the wrong entry, hence the scenario that checks the entity type.

### Patch mechanics confirmed in the source

- **A `comment` key is safe.** `ModJsonPatchLoader.ApplyPatches` (l.66) reads the file with
  `asset.ToObject<JsonPatch[]>()` and no `JsonSerializerSettings`, so Newtonsoft's default
  `MissingMemberHandling.Ignore` applies: unknown keys are dropped silently.
- **The domain on `file` is mandatory for us.** `JsonUtil.ToObject` (JsonUtil.cs:95) registers an
  `AssetLocationJsonParser` bound to the asset's own domain whenever that domain is not `game`. Our
  patch lives in `caminus`, so a bare `"entities/humanoid/player.json"` would resolve to
  `caminus:entities/...` and be logged as a missing file. Vanilla patches get away with the bare
  path only because they are in the `game`/`survival` domains.
- **`Side` defaults to Universal** (field initializer on `JsonPatch.Side`), so one patch file covers
  the dedicated server and the client; a client patching `/server/...` is harmless and vice versa.
- **`op: replace` on a leaf** is a `Tavis` `ReplaceOperation` and throws `PathNotFoundException` if
  the path is wrong, which the loader counts as an error in the startup line.
- The loader's summary line is
  `JsonPatch Loader: N patches total, successfully applied N patches, ..., no errors`. Measured on a
  real dedicated server: **14 patches without Caminus, 16 with, both fully applied, no errors.**

### The behavior itself

`updateBodyTemperature` is `protected` but **not virtual**, and `tempTree`, `api`, `blockAccess`,
`accum`, `slowaccum`, `veryslowaccum`, `plrpos`, `tmpPos`, `inEnclosedRoom`, `tempChange`,
`clothingBonus`, `damagingFreezeHours`, `sprinterCounter`, `lastWearableHoursTotalUpdate`,
`bodyTemperatureResistance`, `firstTick` and `lastMoveMs` are all private, as are
`getNearHeatSourceStrength`, `updateFreezingAnimState` and `updateWearableConditions`. Only
`nearHeatSourceStrength` (protected), `NormalBodyTemperature`, `CurBodyTemperature`, `Wetness`,
`LastWetnessUpdateTotalHours` and `BodyTempUpdateTotalHours` are reachable. So the subclass overrides
`OnGameTick` and re-implements the whole update; `src/Caminus/BodyTemperature.cs` cites the vanilla
line number on every block.

Two quirks worth knowing before touching the balance:

- **Resistance is only read on the second life of an entity.** `Initialize` (l.131-143) creates the
  `bodyTemp` tree on the `if` branch and reads `world.Config["bodyTemperatureResistance"]` on the
  `else` branch only. A brand new player therefore runs its first session with a resistance of 0.
  Ported as is.
- **The comfort term is not centred on 20 °C.** With the default resistance of 0,
  `num5 = T - clamp(T, 0, 50)` is 0 for any T in 0..50, and the `if (num5 == 0)` fallback then sets
  `num5 = max(T - resistance, 0)`. So 0 °C is the neutral point, anything warmer *heats* the player
  (20 °C gives +3.3 °C/h, doubled to +6.7 by the `tempChange > 0.5` rule) and only air below 0 °C
  cools. Add to that vanilla's dead band: a `tempChange` between -0.5 and 0 changes nothing at all,
  so the air has to be under about -3 °C before an unclothed player starts losing heat. The
  milestone brief assumed 20 °C was neutral; the code follows vanilla, not the brief.

Caminus changes exactly one line of the port, vanilla l.266:

```csharp
// vanilla
tempChange = nearHeatSourceStrength + (inEnclosedRoom ? 1f : -wind + num6);
// Caminus
tempChange = nearHeatSourceStrength + num6 - (inEnclosedRoom || tracked ? 0f : wind);
```

with `num6` computed on `RoomThermalSystem.TryGetLocalTemperature(EyeBlockPos(entity))` when that
succeeds and on `GetClimateAt(plrpos).Temperature` otherwise. Rain, wetness, drying, sprint,
clothing, sleeping, the on-fire override, the freeze damage and the radiant scan are untouched.
`RoomThermalSystem` is a server-only mod system, so `GetModSystem<RoomThermalSystem>()` returns null
on the client and the client half falls back to the climate; it does not matter, since the update
only runs server side anyway.

### Testing it

A test player joins in **creative**, and vanilla returns early for creative and spectator players
after pinning the body at `NormalBodyTemperature` (l.228). Any body temperature scenario has to run
`/gamemode <name> survival` first. The embedded server also accepts 16 clients with no queue and test
players never leave, so scenarios that join extra players have to `IServerPlayer.Disconnect()` them.

## Names cited from memory vs. reality

| Cited | Real |
|---|---|
| `ClimateCondition.WorldgenTemperature` | `WorldGenTemperature` |
| `EntityBehaviorBodyTemperature.wetness` | `Wetness` |
| `clothingBonus` public | private |
| `BlockEntityContainer.GetPerishRate()` | `InWorldContainer.GetPerishRate()` |
| `InventoryBase.PerishableFactorByFoodCategory` | `InventoryGeneric` / `InventoryInfinite` |
| `IWorldChunk.SetModdataObject` | `SetModdata<T>` + `LiveModData` |
| `BeforeChunkColumnUnloaded` | `ChunkColumnUnloaded(Vec3i)`, already pre-unload |
| `RegisterGameTickListener(..., initialDelay)` | `initialDelayOffsetMs`, returns `long` |
| `BlockChimney` | `BlockBehaviorChimney` (cosmetic) + block `claybrickchimney` |
| Greenhouse via `IsSmallRoom` | `Skylight > NonSkylight && Exit == 0` |

Nothing among the key classes is `sealed` or `internal`. The real obstacles: private members of
`EntityBehaviorBodyTemperature`, the `Dictionary.Add` behavior registry, the protected `container` of
containers, private flood-fill constants, Harmony absent from source mods.
