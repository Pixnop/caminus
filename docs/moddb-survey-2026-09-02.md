# ModDB survey: mods adjacent to Caminus (2026-09-02)

Method: `https://mods.vintagestory.at/api/mods?text=...` API over ~30 keywords, detailed pages of
candidates, supplementary web search.

## Vanilla context

The game has a room system (wiki `Room`): enclosed / greenhouse / cellar detection, a "cellar score"
that modulates spoilage, a passive comfort bonus in any enclosed room. A thread on the official forum
("A More Developed Insulation and Heating System," topic 10421) asks for more depth: vanilla heat
sources heat any climate with minimal setup.

## Mods found

| Mod | modid | Game | 1.22 | DL | Release | Source | What it does |
|---|---|---|---|---|---|---|---|
| Real Smoke | realsmoke | 1.22.7 | yes | 222,792 | 2026-08-22 | Codeberg | physical smoke particles, visual |
| Chiseled Block Retention | cbr | 1.22.7 | yes | 165,579 | 2026-08-18 | no | chiseled blocks always solid for detection |
| Immersive Body Temperature | warmweathereffects | 1.22.6 | yes | 30,366 | 2026-08-14 | no | rebalances player hot/cold |
| Heat Retention | heatretention | 1.19.7 | no | 29,266 | 2024-04-22 | GitHub | oakum to seal chiseled blocks |
| Room Tools | roomtools | 1.22.3 | yes | 23,403 | 2026-05-31 | GitHub | colored overlay for vanilla cellars/greenhouses/rooms |
| Self-Recording Thermometer | airthermomod | 1.22.7 | yes | 23,449 | 2026-08-22 | GitHub | min/max recording thermometer |
| Bigger Cellars | biggercellars | 1.20.1 | no | 20,689 | 2025-01-21 | GitHub | removes the cellar size limit |
| Cellar Door | cellardoor | 1.19.2 | no | 18,907 | 2023-12-06 | no | ceramic door opaque to light |
| Cold Storage | coldstorage | 1.21.4 | no | 15,205 | 2025-10-21 | no | ice in containers |
| Configurable Room Size | configurableroomsize | 1.22.2 | yes | 11,954 | 2026-05-05 | GitHub | configurable max room size |
| Pass-Thru Chutes | passthruchutes | 1.21.6 | close | 10,907 | 2026-02-03 | no | insulating pass-through chutes |
| I Want Smooth Temperature | iwantsmoothtemperature | 1.22.3 | yes | 7,889 | 2026-06-09 | GitHub | smooths worldgen climate |
| NeverWinter | neverwinter | 1.21.1 | no | 7,835 | 2025-09-26 | GitHub | season control |
| Fix Perish Rate | fixperishrate | 1.22.7 | yes | 6,335 | 2026-08-17 | no | removes the sea-level check in `InWorldContainer.GetPerishRate` |
| Heat Retention (Continued) | heatretentioncontinued | 1.22.2 | yes | 4,913 | 2026-05-21 | GitHub | continuation of Heat Retention |
| Immersive Inventory Spoilage | immersiveinventoryspoilage | 1.22.3 | yes | 4,755 | 2026-07-10 | GitHub | temperature → spoilage in inventory |
| Room HUD | roomhud | 1.22.3 | yes | 1,420 | 2026-07-19 | GitHub | valid-room + temperature HUD |
| Better insulation | betterinsulation | 1.22.6 | yes | 3,028 | 2026-08-14 | no | fixes chiseled face coverage |
| Chiseled Blocks Always Insulate | chiseledblocksalwaysinsulate | 1.22.6 | yes | 654 | 2026-08-03 | GitHub | chiseled blocks always retain |
| RealisticTemperatures | realistictemperatures | 1.22.0 | alpha | 5,345 | 2026-04-23 | no | removes passive warming of enclosed rooms |
| Ice Cold Vessel | coldvessel | 1.22.5 | close | 1,656 | 2026-07-30 | GitHub | ice in vessels |
| Geothermal Natural Underground | geothermalnatural | 1.22.2 | yes | 1,251 | 2026-05-23 | no | underground temperature by depth |
| WeatherStory | weatherstory | 1.22.3 | beta | 1,213 | 2026-07-11 | no | dynamic world climate (not building-level) |
| Ice Cellar | icecooledcellars | 1.22.7 | yes | 1,451 | 2026-08-28 | no | ice cools the cellar |
| Galileo's Thermometer | thermometer | 1.16.5 | no | 1,294 | 2022 | no | decorative thermometer |
| Status HUD | statushud | 1.18.8 | no | 19,767 | 2023 | yes | HUD with a bodyheat element, abandoned |

## Overlap

- Real heating of a room by a firepit based on walls: nobody. The "retention" mods only touch binary
  detection (chiseled blocks solid or not).
- Spoilage from real temperature: partial. Fix Perish Rate patches `InWorldContainer.GetPerishRate`
  (certain conflict if we patch the same method). The "ice" mods add a multiplier.
- Thermometer / overlay: Self-Recording Thermometer, Room HUD, and Room Tools cover the thermometer
  and vanilla room display, not an actual simulation.
- Player comfort: Immersive Body Temperature and RealisticTemperatures touch
  `EntityBehaviorBodyTemperature` (likely Harmony patch, closed source, not verified).
- Chimney draft: none. Real Smoke is cosmetic but ubiquitous (222k DL): must not be broken.

## Verdict

**Open** niche for an integrated system. The landscape is fragmented into one-off tweaks. Neighbors
to test for compatibility with: Fix Perish Rate, RealisticTemperatures, Immersive Body Temperature,
the four "chiseled" mods, Room HUD, Room Tools, Real Smoke.
