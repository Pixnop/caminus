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
