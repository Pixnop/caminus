# Vérification de l'API Vintage Story 1.22.7 pour Caminus

Source : décompilation locale (ilspycmd 10.1.1) de `VintagestoryAPI.dll`, `VintagestoryLib.dll`,
`Mods/VSEssentials.dll`, `Mods/VSSurvivalMod.dll` de la 1.22.7 (`~/.config/VSLGameVersions/1.22.7/`).
Runtime cible du jeu : net10.0. Chaque affirmation ci-dessous a été relue dans le décompilé le 2026-09-02.

## 1. Climat

`IBlockAccessor.GetClimateAt` (IBlockAccessor.cs:537-552), trois surcharges :

```csharp
ClimateCondition GetClimateAt(BlockPos pos, EnumGetClimateMode mode = EnumGetClimateMode.NowValues, double totalDays = 0.0);
ClimateCondition GetClimateAt(BlockPos pos, ClimateCondition baseClimate, EnumGetClimateMode mode, double totalDays);
ClimateCondition GetClimateAt(BlockPos pos, int climate);
```

`EnumGetClimateMode` : `WorldGenValues`, `NowValues`, `ForSuppliedDateValues`, `ForSuppliedDate_TemperatureOnly`, `ForSuppliedDate_TemperatureRainfallOnly`.

`ClimateCondition` : champs publics `Temperature`, `WorldgenRainfall`, `WorldGenTemperature` (G majuscule), `Rainfall`, `Fertility`, `ForestDensity`, `ShrubDensity`, `GeologicActivity`, `Biome`.

Chaîne : `BlockAccessorBase` (lib, l.757) remplace `totalDays` par `Calendar.TotalDays` en mode `NowValues`, puis `ServerWorldMap.GetClimateAt` (l.552) lit la valeur worldgen et déclenche `EventManager.TriggerOnGetClimate`. La température instantanée est posée par `ModTemperature.Event_OnGetClimate` (VSSurvivalMod, privé) : latitude, saison, amplitude diurne `18 - Rainfall*13`, deux bruits simplex.

**Hook propre** : `IEventAPI.OnGetClimate` (IEventAPI.cs:64), délégué
`void OnGetClimateDelegate(ref ClimateCondition climate, BlockPos pos, EnumGetClimateMode mode, double totalDays)`.
Les abonnés sont appelés en séquence et peuvent muter `climate`. Ordre piloté par `ModSystem.ExecuteOrder`.

**Piège** : côté serveur `GetClimateAt` retourne `null` si la région n'est pas chargée, sauf en mode
`ForSuppliedDate_TemperatureOnly` (fallback 4 °C). Toujours tester `null`.

## 2. Détection de pièces

`RoomRegistry` et `Room` vivent dans **VSEssentials**, namespace `Vintagestory.GameContent`.

- `api.ModLoader.GetModSystem<RoomRegistry>()` (8 appelants vanilla).
- `public Room GetRoomForPosition(BlockPos pos)` (RoomRegistry.cs:286). Seule méthode publique utile.
- Chargé serveur ET client (`ShouldLoad => true`), aucune synchro réseau : chaque côté recalcule.
- `Room` : `int ExitCount`, `bool IsSmallRoom`, `int SkylightCount`, `int NonSkylightCount`,
  `int CoolingWallCount`, `int NonCoolingWallCount`, `Cuboidi Location`, `byte[] PosInRoom`, `int AnyChunkUnloaded`.

Constantes privées du flood-fill (RoomRegistry.cs:32-44) :

| Constante | Valeur | Rôle |
|---|---|---|
| ARRAYSIZE | 29 | tableau de visite 29³ |
| MAXROOMSIZE | 14 | au-delà de 14 blocs sur un axe, compte un exit et s'arrête |
| MAXCELLARSIZE | 7 | IsSmallRoom si bbox ≤ 7³ |
| ALTMAXCELLARSIZE / VOLUME | 9 / 150 | tolérance 9 sur un axe si volume ≤ 150 |

Murs classés par `Block.GetRetention(pos, facing, EnumRetentionType.Heat)` : >0 NonCoolingWall, <0 CoolingWall.
Invalidation par `api.Event.ChunkDirty` (tout SetBlock). BFS jusqu'à 24 389 positions via un `ICachingBlockAccessor` `[ThreadStatic]`.

**Pièges** : `currentVisited` et `skyLightXZChecked` sont des champs d'instance non protégés et
`FindRoomForPosition` s'exécute hors verrou. Appeler uniquement depuis le thread serveur principal.
`Cuboidi.SizeX = MaxX - MinX` sans +1.

**Conséquence** : le registre vanilla ne sait pas gérer une salle de plus de 14 blocs. Caminus devra
avoir son propre flood-fill pour les grands volumes, ou accepter la limite en v1.

## 3. Sources de chaleur et température corporelle

`IHeatSource` (VSSurvivalMod, IHeatSource.cs:6) :
`float GetHeatStrength(IWorldAccessor world, BlockPos heatSourcePos, BlockPos heatReceiverPos);`

| Implémentation | Valeur |
|---|---|
| BlockEntityFirepit (l.831) | 10 si IsBurning, 0.25 si IsSmoldering |
| BlockEntityCoalPile (l.525) | 10 si IsBurning et non cokable |
| BlockEntityForge / Bloomery | 7 si IsBurning |
| BlockEntityGroundStorage (virtual) / PitKiln | 10 si brûle |
| BlockEntityOven (l.401) | (T-20)/(300-20)*8 |
| JSON `heatStrength` | fire 10, lava 12, ember 6, boilingwater 3, pitkiln 10 |

Torche et bouilloire ne sont pas des sources. Découverte par `Block.GetInterface<T>(world, pos)` (bloc, behavior, BE, behaviors de BE).

`EntityBehaviorBodyTemperature` (VSSurvivalMod, code `"bodytemperature"`, enregistré dans SurvivalCoreSystem.cs:865) :
- public : `CurBodyTemperature`, `Wetness` (W majuscule), `NormalBodyTemperature`, `BodyTempUpdateTotalHours`.
- `nearHeatSourceStrength` protected ; `clothingBonus`, `inEnclosedRoom`, `tempChange`, `bodyTemperatureResistance` privés.
- `protected void updateBodyTemperature()` (l.216) NON virtuel ; `getNearHeatSourceStrength()` privé.
- Seuls `Initialize`, `OnGameTick`, `OnEntityDespawn`, `OnEntityRevive`, `PropertyName` sont override.
- Balayage toutes les ~3 s serveur : bbox complète de la pièce si `inEnclosedRoom` (`ExitCount == 0 || SkylightCount < NonSkylightCount`), sinon ±3 blocs. Atténuation `min(1, 9/(8 + d^p))`, p = 0.875 en pièce close, 1.25 dehors.
- Formule (l.266) : `tempChange = nearHeatSourceStrength + (inEnclosedRoom ? 1f : -vent + num6)`.
  **En pièce close, le climat et le vent sont ignorés** : forfait +1/h.

`ClassRegistry.RegisterentityBehavior` (lib, l.442) utilise `Dictionary.Add` : impossible de ré-enregistrer
`"bodytemperature"` avec un autre type. Voie propre : patch JSON de `game:entities/humanoid/player.json`
(lignes 326 et 4305) pour remplacer le code du behavior par le nôtre, sous-classe avec `OnGameTick` réécrit.

## 4. Péremption

**`BlockEntityContainer.GetPerishRate()` n'existe pas.** Réel : `InWorldContainer.GetPerishRate()`
(VSEssentials, InWorldContainer.cs:172, `public virtual`). `BlockEntityContainer` (VSSurvivalMod) tient un
`protected InWorldContainer container` créé dans le constructeur. Tick de transition : 10 s.

Formule vanilla :

```
pos.Y = SeaLevel                      // la profondeur n'entre PAS via le climat
T = GetClimateAt(pos, ForSuppliedDate_TemperatureOnly).Temperature   (caché côté serveur)
skyRel = Skylight / max(1, Skylight + NonSkylight)
cellarRel = IsSmallRoom ? 1 - 0.4*skyRel - 0.5*clamp(NonCooling/max(1,Cooling), 0, 1) : 0
f = 0.1 + (IsSmallRoom ? 0.3*cellarRel + 1.75*skyRel : Exit <= 0.1*murs ? 1.25*skyRel : 0.5*skyRel), clamp 0..1.5
tEff = T + clamp(sunLight-11, 0, 10) * f
v = min(lerp(tEff, 5, cellarRel), tEff)
rate = clamp(3^(v/19 - 1.2) - 0.1, 0.1, 2.4)
```

`ConstantPerishRateContainer : InWorldContainer` surcharge `GetPerishRate` : la surcharge est le mécanisme prévu.

Accroches sans Harmony, par propreté :
1. `InventoryBase.OnAcquireTransitionSpeed` (event `CustomGetTransitionSpeedMulDelegate(EnumTransitionType, ItemStack, float mulByConfig)`), composé par `mul *= handler(...)` (InventoryBase.cs:795). Multiplicatif, ne remplace pas.
2. `GlobalConstants.PerishSpeedModifier` (global).
3. `CollectibleBehavior.UpdateAndGetTransitionStates` avec `PreventSubsequent` (un behavior par collectible).
4. Sous-classer chaque BE conteneur pour remplacer `container`.

Remplacer la formule globalement = Harmony postfix sur `InWorldContainer.GetPerishRate`. Le mod ModDB
« Fix Perish Rate » patche exactement cette méthode (ligne `sealevelpos.Y = SeaLevel`).

`PerishableFactorByFoodCategory` n'est pas sur `InventoryBase` mais sur `InventoryGeneric` et `InventoryInfinite`.

## 5. Persistance

`IWorldChunk` : `SetModdata(string, byte[])`, `GetModdata(string)`, `SetModdata<T>`, `GetModdata<T>(key, default)`,
`RemoveModdata`, `MarkModified()`, et `Dictionary<string, object> LiveModData` : non sérialisé en continu,
poussé dans la moddata à l'unload, NON repeuplé au load (à recharger soi-même via `GetModdata`).
`SetModdataObject` n'existe pas.

`IServerChunk.SetServerModdata / GetServerModdata` : serveur seul, non synchronisé.
`IMapRegion.SetModdata / GetModdata<T>` : serveur uniquement, pas de `defaultValue`.
`ISaveGame.StoreData / GetData<T>` via `sapi.WorldManager.SaveGame`.

Événements (IServerEventAPI) : `ChunkColumnLoaded(Vec2i, IWorldChunk[])`, `ChunkColumnUnloaded(Vec3i)`
(déclenché AVANT la boucle d'unload, ServerSystemUnloadChunks.cs:287 : les chunks sont encore lisibles),
`BeginChunkColumnLoadChunkThread`. `BeforeChunkColumnUnloaded` n'existe pas.
`MapRegionLoaded(Vec2i, IMapRegion)` / `MapRegionUnloaded` sur IEventAPI.

## 6. Tick et calendrier

`long RegisterGameTickListener(Action<float>, int millisecondInterval, int initialDelayOffsetMs = 0)` plus
variantes avec `Action<Exception> errorHandler` et `BlockPos` (tick seulement si chunk chargé).
`RegisterCallback`, `UnregisterGameTickListener(long)`. Thread : `sapi.Server.AddServerThread(string, IAsyncServerSystem)`
(`OffThreadInterval`, `OnSeparateThreadTick`, `ThreadDispose`). Tick serveur 33,3 ms.
`IGameCalendar` : `TotalHours`, `TotalDays`, `HourOfDay`, `DayOfYear`, `DaysPerYear`, `SpeedOfTime`,
`ElapsedSeconds`, `YearRel`, `GetSeason(pos)`, `GetSeasonRel(pos)`.

## 7. Réseau

`api.Network.RegisterChannel(name)` ; `IServerNetworkChannel.RegisterMessageType<T>()`,
`SetMessageHandler<T>(NetworkClientMessageHandler<T>)`, `SendPacket<T>(T, params IServerPlayer[])`,
`BroadcastPacket<T>`. Client : `SetMessageHandler<T>(NetworkServerMessageHandler<T>)`, `SendPacket<T>`.
`INetworkChannel` est déclaré dans `Vintagestory.API.Client`. Sérialisation protobuf-net (`[ProtoContract]`).
Ids de message attribués par ordre d'appel : même ordre client et serveur obligatoire. 256 types max par canal.

## 8. Harmony

`Lib/0Harmony.dll` = Lib.Harmony.Thin **2.4.2** (net10.0), MonoMod.Core 1.3.3. Le moteur n'applique aucun
patch (seul usage : attribution des crashs aux mods qui ont patché, CrashReporter.cs). Résolution d'assembly
depuis `<jeu>/` et `<jeu>/Lib/`. **`0Harmony.dll` n'est pas dans les références de compilation des mods
source** (ModCompilationContext.cs:20-39) : Caminus doit être un mod précompilé. VSEssentials et
VSSurvivalMod sont référencés. Convention : `PatchAll` dans `StartPre`, `UnpatchAll(id)` dans `Dispose`,
garde `Harmony.HasAnyPatches(id)` (solo = client + serveur dans le même processus).

## 9. Blocs

`Block.BlockMaterial` (champ, défaut Stone) et `GetBlockMaterial(accessor, pos, stack)` (virtuel, thread-safe requis).
`EnumBlockMaterial` : Air, Soil, Gravel, Sand, Wood, Leaves, Stone, Ore, Water, Snow, Ice, Metal, Mantle,
Plant, Glass, Ceramic, Cloth, Lava, Brick, Fire, Meta, Other.
`Block.SideSolid` : `struct SmallBoolArray` indexée par `BlockFacing.Index`. `Attributes` hérité de `CollectibleObject`.

`Block.GetRetention(pos, facing, EnumRetentionType)` (Block.cs:2025) : behaviors d'abord, puis pour Heat :
Ore/Stone/Soil/Ceramic solides = -1, autres solides = +1, face non solide = 0.
`BlockBehavior.GetRetention(pos, facing, type, ref EnumHandling)` (BlockBehavior.cs:360) : point d'extension
propre, injectable par patch JSON. Directement consommé par le flood-fill vanilla.

`BlockEntityFirepit` : `IsBurning`, `IsSmoldering`, `fuelBurnTime`, `maxFuelBurnTime`, `furnaceTemperature`,
`maxTemperature` (= BurnTemperature * HeatModifier), `smokeLevel`.

**`BlockChimney` n'existe pas.** `BlockBehaviorChimney` (VSSurvivalMod) ne surcharge que
`ShouldReceiveClientParticleTicks` : purement cosmétique. Bloc : `claybrickchimney`
(`assets/survival/blocktypes/clay/chimneycourse.json`, matériau Ceramic).

## 10. Serres

`BlockEntityFastForwardGrowth.GetRoomness()` (protected virtual, l.120) : serre si
`SkylightCount > NonSkylightCount && ExitCount == 0` à la position au-dessus du bloc, et bloc couvert
(`GetRainMapHeightAt > Y`). Effet : `+5 °C` sur le climat de croissance. `IsSmallRoom` n'intervient pas.
Même critère dans `BlockEntityBerryBush` et `BlockEntityBeehive`. À ne pas casser.

## Noms cités de mémoire vs réalité

| Cité | Réel |
|---|---|
| `ClimateCondition.WorldgenTemperature` | `WorldGenTemperature` |
| `EntityBehaviorBodyTemperature.wetness` | `Wetness` |
| `clothingBonus` public | privé |
| `BlockEntityContainer.GetPerishRate()` | `InWorldContainer.GetPerishRate()` |
| `InventoryBase.PerishableFactorByFoodCategory` | `InventoryGeneric` / `InventoryInfinite` |
| `IWorldChunk.SetModdataObject` | `SetModdata<T>` + `LiveModData` |
| `BeforeChunkColumnUnloaded` | `ChunkColumnUnloaded(Vec3i)` déjà pré-unload |
| `RegisterGameTickListener(..., initialDelay)` | `initialDelayOffsetMs`, retour `long` |
| `BlockChimney` | `BlockBehaviorChimney` (cosmétique) + bloc `claybrickchimney` |
| Serre via `IsSmallRoom` | `Skylight > NonSkylight && Exit == 0` |

Rien n'est `sealed` ni `internal` parmi les classes clés. Les obstacles réels : membres privés de
`EntityBehaviorBodyTemperature`, `Dictionary.Add` du registre de behaviors, `container` protected des
conteneurs, constantes privées du flood-fill, Harmony absent des mods source.
