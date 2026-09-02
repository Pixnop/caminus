# Caminus : découpage en lots livrables

Proposition du 2026-09-02, après vérification de l'API 1.22.7 (`api-1.22.7-verification.md`) et de la
ModDB (`moddb-survey-2026-09-02.md`). Chaque lot est jouable seul et laisse quelque chose de testable.
Cible : 1.22.7, mod précompilé (Harmony n'est pas accessible aux mods source), .NET 10.

## Décisions structurantes issues de la vérification

1. Le moteur thermique est un projet .NET pur, sans référence au jeu, testé en xunit. Le mod n'est
   qu'un adaptateur : lecture des blocs, sources, calendrier, persistance, réseau.
2. Lots 1 à 3 s'appuient sur le `RoomRegistry` vanilla (limite 14 blocs par axe, thread principal
   uniquement). Le flood-fill maison est un lot à part, pas un prérequis.
3. Deux rebranchements se font sans Harmony : la température corporelle par patch JSON de
   `player.json` (sous-classe du behavior), et la péremption par `InventoryBase.OnAcquireTransitionSpeed`
   ou surcharge de `InWorldContainer`. Harmony n'entre qu'en dernier recours, isolé dans un seul fichier.
4. Persistance par chunk via `LiveModData` + `SetModdata<T>` à l'unload (`ChunkColumnUnloaded` est
   déclenché avant l'unload, les chunks sont encore lisibles). Nœud terre profonde par région serveur.
5. Ne pas casser les serres : critère vanilla `SkylightCount > NonSkylightCount && ExitCount == 0`.

## Lot 0 : squelette (1 à 2 jours)

Contenu : dépôt, `Caminus.csproj` net10.0 référençant les dll 1.22.7, `modinfo.json` (`caminus`),
`Caminus.Core` (moteur pur) + `Caminus.Core.Tests` (xunit), CI build + tests, script de déploiement vers un
profil VSL de test dédié, commande `/caminus version`.
Testable : le mod charge côté client et serveur sans erreur dans les logs, CI verte.

## Lot 1 : le thermomètre honnête (1 à 2 semaines)

Le plus petit truc jouable : chaque pièce vanilla a une température qui bouge.

- Moteur : nœuds = pièces + extérieur (`GetClimateAt`), capacité ∝ volume d'air, arêtes = parois avec U
  par `EnumBlockMaterial` (table JSON dans les assets), ouvertures = conductance élevée, sources = tout
  `IHeatSource` dans la pièce converti en puissance (firepit 10 → quelques kW, table JSON).
  Euler implicite, pas de 1 s de jeu, résolution par Gauss-Seidel ou Cholesky dense (≤ 50 nœuds).
- Jeu : tick serveur 1 s, cache pièce → nœud invalidé par `ChunkDirty`, item `caminus:thermometer`
  (ou commande `/caminus temp` d'abord) qui affiche la température de la pièce où se trouve le joueur,
  paquet serveur → client pour l'affichage.
- Pas de persistance : à la recharge, la pièce repart à la température extérieure.

Testable : tests unitaires du solveur (équilibre analytique d'une pièce à deux parois, stabilité à
grand pas, conservation de l'énergie, indépendance au pas). En jeu : cabane fermée avec foyer
allumé, la température monte puis plafonne ; on ouvre la porte, elle chute ; mur en pierre contre
mur en bois, écart mesurable.

## Lot 2 : caves et conservation (1 à 2 semaines)

- Nœud terre profonde par région : Kusuda & Achenbach depuis la température annuelle moyenne
  de `GetClimateAt(WorldGenValues)`, profondeur = distance au niveau de la surface (`GetRainMapHeightAt`).
  Contact au sol des pièces enterrées = conductance vers ce nœud.
- Persistance : température des nœuds et date du dernier tick en moddata de chunk ; relaxation
  exponentielle vers l'équilibre à la recharge.
- Péremption : le taux vanilla est remplacé par un Q₁₀ sur la température réelle de la pièce.
  Voie 1 : abonnement `OnAcquireTransitionSpeed` sur les inventaires de conteneurs (multiplicatif,
  on divise par le taux vanilla). Voie 2 si la voie 1 est trop bricolée : Harmony postfix sur
  `InWorldContainer.GetPerishRate`, un seul fichier, test de compatibilité avec Fix Perish Rate.
  Le thermomètre indique aussi la durée de vie estimée d'un aliment dans cette pièce.

Testable : tests unitaires de Kusuda (amortissement et déphasage selon la profondeur), de la
relaxation hors ligne (extrapoler 1000 pas = intégrer 1000 pas, à la tolérance près). En jeu : cave à
3 blocs vs 10 blocs en été et en hiver, deux paniers de viande côte à côte dedans et dehors.

## Lot 3 : le joueur a froid (1 semaine)

- Behavior `caminus:bodytemperature` (sous-classe, patch JSON de `player.json`) : température d'air =
  celle du nœud, plus terme radiant du foyer (distance, ligne de vue dans la pièce), plus clo des
  vêtements existants, vent seulement hors pièce. Plus de forfait +1/h en pièce close.
- Modèle simple à un nœud d'abord (celui du jeu, avec une vraie température d'air) ; Fanger et
  Gagge restent en réserve.

Testable : unitaire sur le bilan (une pièce à 5 °C sans feu refroidit le joueur, le foyer à 2 blocs
le réchauffe). En jeu : hiver, cabane sans feu = on gèle ; on allume = on remonte. Compatibilité à
vérifier avec Immersive Body Temperature et RealisticTemperatures (même behavior visé).

## Lot 4 : la cheminée (2 semaines, le cœur du gameplay)

- Détection du conduit : colonne de `claybrickchimney` (ou blocs creux) au-dessus du foyer, hauteur
  et section. Le foyer sans conduit fume dans la pièce (malus) ; avec conduit, une part de la
  puissance part dans le tirage.
- Tirage : `Q = Cd·A·√(2·g·ΔH·ΔT/T)` entre la pièce et l'extérieur via le conduit ; l'air aspiré
  entre par les ouvertures basses (débit d'infiltration ajouté aux arêtes vers l'extérieur ou la
  pièce voisine du dessous). Même terme non linéaire pour les liaisons verticales entre pièces
  superposées (escalier ouvert). Linéarisation à chaque pas autour de l'état courant, l'implicite
  reste valable.
- Stratification analytique `T(y) = T_moy + gradient·(y − y_sol)` : ce que lit le thermomètre dépend
  de la hauteur du joueur.

Testable : unitaire du tirage (débit croissant avec la hauteur, nul à ΔT = 0). En jeu : même foyer,
conduit de 3 vs 8 blocs ; ouverture basse vs pas d'ouverture ; grenier au-dessus qui se réchauffe.

## Lot 5 : lire son bâtiment (1 à 2 semaines)

- Overlay client : couleur par pièce, flux par paroi (W), sources, air entrant/sortant. Protocole
  : le serveur n'envoie que les nœuds des pièces proches du joueur qui a activé l'overlay, à 1 Hz.
- Interface du thermomètre posé (bloc) avec historique min/max, à la manière de Self-Recording
  Thermometer.

Testable : bande passante mesurée avec l'overlay actif sur une base de 30 pièces ; capture
d'écran de référence.

## Lot 6 : grands volumes et robustesse (2 semaines)

- Flood-fill maison pour les pièces au-delà de 14 blocs (budget de blocs, exécution sur thread de
  travail avec `ICachingBlockAccessor`, fusion quand la limite est atteinte : la pièce devient
  extérieure). Le `RoomRegistry` vanilla reste utilisé pour les serres et les caves du jeu.
- Compatibilité testée avec les voisins de la ModDB (liste dans le survey), avec Real Smoke en
  priorité.
- Équilibrage : table U, puissances, seuils, sur retours de jeu.

## Réserve (v2)

Modèle à deux couches Linden par pièce, confort Fanger/Gagge, humidité, version hybride grille
grossière si la stratification analytique ne suffit pas.

## Ce qui est volontairement hors de la v1

Simulation client, mods de blocs ciselés (U d'un bloc ciselé = celui de son matériau majoritaire,
sans plus), API publique pour d'autres mods (viendra quand quelqu'un la demandera).
