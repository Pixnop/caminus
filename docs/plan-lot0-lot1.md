# Plan d'implémentation : lots 0 et 1

## Lot 0 : squelette

- `Caminus.sln`, `Directory.Build.props` (net10.0, version 0.1.0, `VintageStoryDir` = `$VINTAGE_STORY`).
- `src/Caminus.Core` : moteur pur, aucune référence au jeu. Contrat `ThermalNetwork` (nœuds, nœuds fixes, arêtes, sources, `Step`).
- `src/Caminus` : le mod. Références `Private=false` vers VintagestoryAPI, VintagestoryLib, VSEssentials, VSSurvivalMod, protobuf-net, Newtonsoft, 0Harmony. Cible `PackageMod` en Release : `dist/caminus_<version>.zip` (Caminus.dll, Caminus.Core.dll, modinfo.json, assets/).
- `tests/Caminus.Core.Tests` : xunit 2.9.3, runner 2.8.2, Test.Sdk 17.14.1, coverlet.
- CI GitHub Actions « Build & tests » : télécharge le serveur 1.22.7 du CDN (cache), build, tests avec couverture opencover, Sonar `Pixnop_caminus` (org `leon-fievet-pixnop`) si le secret `SONAR_TOKEN` est présent, zip en artefact.
- `tools/deploy.sh` : build + copie du zip dans le dossier Mods d'un profil VSL de test.

## Lot 1 : le thermomètre honnête

### Moteur (`Caminus.Core`)
`ThermalNetwork` : Euler implicite, `(C/dt + L) T⁺ = (C/dt) T + Q`, nœuds fixes en Dirichlet, solveur dense
(élimination de Gauss ou Cholesky, n ≤ 100). Tests : équilibre analytique, constante de temps, stabilité à
grand pas, conservation de l'énergie en système fermé, signe des flux, nœud fixe immuable.

### Adaptateur (`Caminus`)
- `RoomThermalSystem` (serveur) : tick 1 s. Pour chaque joueur, `RoomRegistry.GetRoomForPosition` (thread
  principal uniquement). Une pièce = un nœud, identifiée par son objet `Room` (le registre rend la même
  instance jusqu'à invalidation par `ChunkDirty`) ; si l'instance change, la géométrie est recalculée.
- Géométrie : positions de la pièce via `Room.Contains`. Volume = nombre de blocs d'air. Pour chaque face
  de bloc d'air vers l'extérieur de la pièce : paroi (bloc solide sur cette face) → U par `EnumBlockMaterial`
  depuis `assets/caminus/config/thermal.json` ; sinon ouverture → conductance d'ouverture. Aire = 1 m² par face.
- Sources : `Block.GetInterface<IHeatSource>` sur la bbox élargie d'un bloc, `GetHeatStrength` × W par unité.
- Nœud extérieur fixe : `GetClimateAt(centre, NowValues)` à chaque tick, `null` toléré.
- Pièces oubliées après 5 min sans joueur. Pas de persistance (lot 2), pas de réseau (lot 5).
- Commandes : `/caminus version`, `/caminus temp` (température de la pièce, extérieur, volume, pertes par
  matériau en W, sources en W).

### Vérification
Tests unitaires du moteur en CI. Serveur dédié 1.22.7 lancé en headless avec le zip : chargement sans
erreur. Validation en jeu par Pixnop : cabane avec foyer, porte ouverte, pierre contre bois.
