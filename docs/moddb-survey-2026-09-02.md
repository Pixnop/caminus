# Recherche ModDB : mods voisins de Caminus (2026-09-02)

Méthode : API `https://mods.vintagestory.at/api/mods?text=...` sur ~30 mots-clés, fiches détaillées des
candidats, recherche web complémentaire.

## Contexte vanilla

Le jeu a un système de pièces (wiki `Room`) : détection close / serre / cave, « cellar score » qui module la
péremption, bonus passif de confort dans toute pièce close. Un fil du forum officiel (« A More Developed
Insulation and Heating System », topic 10421) demande plus de profondeur : les sources de chaleur vanilla
chauffent n'importe quel climat avec un minimum d'installation.

## Mods trouvés

| Mod | modid | Jeu | 1.22 | DL | Release | Source | Fait quoi |
|---|---|---|---|---|---|---|---|
| Real Smoke | realsmoke | 1.22.7 | oui | 222 792 | 2026-08-22 | Codeberg | particules de fumée physiques, visuel |
| Chiseled Block Retention | cbr | 1.22.7 | oui | 165 579 | 2026-08-18 | non | blocs ciselés toujours solides pour la détection |
| Immersive Body Temperature | warmweathereffects | 1.22.6 | oui | 30 366 | 2026-08-14 | non | rééquilibre chaud/froid joueur |
| Heat Retention | heatretention | 1.19.7 | non | 29 266 | 2024-04-22 | GitHub | oakum pour sceller les ciselés |
| Room Tools | roomtools | 1.22.3 | oui | 23 403 | 2026-05-31 | GitHub | overlay coloré caves/serres/pièces vanilla |
| Self-Recording Thermometer | airthermomod | 1.22.7 | oui | 23 449 | 2026-08-22 | GitHub | thermomètre enregistreur min/max |
| Bigger Cellars | biggercellars | 1.20.1 | non | 20 689 | 2025-01-21 | GitHub | supprime la limite de taille des caves |
| Cellar Door | cellardoor | 1.19.2 | non | 18 907 | 2023-12-06 | non | porte céramique opaque à la lumière |
| Cold Storage | coldstorage | 1.21.4 | non | 15 205 | 2025-10-21 | non | glace dans les conteneurs |
| Configurable Room Size | configurableroomsize | 1.22.2 | oui | 11 954 | 2026-05-05 | GitHub | taille max des pièces configurable |
| Pass-Thru Chutes | passthruchutes | 1.21.6 | proche | 10 907 | 2026-02-03 | non | trappes traversantes isolantes |
| I Want Smooth Temperature | iwantsmoothtemperature | 1.22.3 | oui | 7 889 | 2026-06-09 | GitHub | lisse le climat worldgen |
| NeverWinter | neverwinter | 1.21.1 | non | 7 835 | 2025-09-26 | GitHub | contrôle des saisons |
| Fix Perish Rate | fixperishrate | 1.22.7 | oui | 6 335 | 2026-08-17 | non | retire le test niveau de la mer dans `InWorldContainer.GetPerishRate` |
| Heat Retention (Continued) | heatretentioncontinued | 1.22.2 | oui | 4 913 | 2026-05-21 | GitHub | suite de Heat Retention |
| Immersive Inventory Spoilage | immersiveinventoryspoilage | 1.22.3 | oui | 4 755 | 2026-07-10 | GitHub | température → péremption dans l'inventaire |
| Room HUD | roomhud | 1.22.3 | oui | 1 420 | 2026-07-19 | GitHub | HUD pièce valide + température |
| Better insulation | betterinsulation | 1.22.6 | oui | 3 028 | 2026-08-14 | non | corrige la couverture des faces ciselées |
| Chiseled Blocks Always Insulate | chiseledblocksalwaysinsulate | 1.22.6 | oui | 654 | 2026-08-03 | GitHub | ciselés retiennent toujours |
| RealisticTemperatures | realistictemperatures | 1.22.0 | alpha | 5 345 | 2026-04-23 | non | supprime le réchauffement passif des pièces closes |
| Ice Cold Vessel | coldvessel | 1.22.5 | proche | 1 656 | 2026-07-30 | GitHub | glace dans les vases |
| Geothermal Natural Underground | geothermalnatural | 1.22.2 | oui | 1 251 | 2026-05-23 | non | température souterraine selon profondeur |
| WeatherStory | weatherstory | 1.22.3 | beta | 1 213 | 2026-07-11 | non | climat dynamique monde (pas bâtiment) |
| Ice Cellar | icecooledcellars | 1.22.7 | oui | 1 451 | 2026-08-28 | non | glace refroidit la cave |
| Galileo's Thermometer | thermometer | 1.16.5 | non | 1 294 | 2022 | non | thermomètre décoratif |
| Status HUD | statushud | 1.18.8 | non | 19 767 | 2023 | oui | HUD avec élément bodyheat, abandonné |

## Recouvrement

- Chauffage réel d'une pièce par un foyer selon les murs : personne. Les mods « retention » ne touchent
  que la détection binaire (ciselés solides ou non).
- Péremption par température réelle : partiel. Fix Perish Rate patche `InWorldContainer.GetPerishRate`
  (conflit certain si on patche la même méthode). Les mods « glace » ajoutent un multiplicateur.
- Thermomètre / overlay : Self-Recording Thermometer, Room HUD et Room Tools couvrent le thermomètre et
  l'affichage de pièce vanilla, pas une simulation propre.
- Confort joueur : Immersive Body Temperature et RealisticTemperatures touchent
  `EntityBehaviorBodyTemperature` (sources fermées, patch Harmony probable, non vérifié).
- Tirage de cheminée : aucun. Real Smoke est cosmétique mais omniprésent (222k DL) : à ne pas casser.

## Verdict

Créneau **libre** pour le système intégré. Terrain fragmenté par des tweaks ponctuels. Voisins à tester en
compatibilité : Fix Perish Rate, RealisticTemperatures, Immersive Body Temperature, les quatre mods
« ciselés », Room HUD, Room Tools, Real Smoke.
