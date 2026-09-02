# Simulation thermique pour Vintage Story (mod Caminus) : note de faisabilité

Objectif visé : un moteur thermique unique qui alimente le confort du joueur, la conservation
des aliments et des outils de lecture pour le bâtisseur. Cheminées, isolation, caves.

Statut : note préparatoire. Aucune décision d'architecture n'est figée.

---

## 1. Ce que le jeu fait déjà, et pourquoi ça limite

Le jeu n'a pas de champ de température. Il a trois mécanismes séparés qui donnent l'illusion
d'en avoir un.

`GetClimateAt(pos, mode)` échantillonne un champ climatique analytique généré au worldgen
(température, pluviométrie, fertilité). Ça varie avec la latitude, l'altitude, la saison et
l'heure. C'est une lecture, pas un état : rien n'est stocké, rien n'évolue.

Le `RoomRegistry` fait du flood-fill pour détecter un volume fermé et rend une `Room` qui
porte des compteurs (sorties, blocs de plafond exposés au ciel, blocs « froids »). Ces
compteurs servent à amortir la variation climatique dans le calcul de péremption
(`BlockEntityContainer.GetPerishRate()`) et à détecter les serres. L'intuition physique est
juste, c'est de l'inertie thermique, mais elle est codée en dur au lieu d'être intégrée.

`IHeatSource.GetHeatStrength(world, posSource, posRécepteur)` est implémentée par le foyer,
la forge, le four. Le comportement de température corporelle balaie une sphère autour du
joueur et somme les contributions par décroissance en distance. Aucun obstacle n'est
considéré : la chaleur traverse un plancher.

Conséquence directe : il n'y a rien à « améliorer » sur l'existant. Il faut ajouter un état
thermique persistant, et rebrancher les trois consommateurs dessus.

*Avertissement : les noms de membres ci-dessus sont écrits de mémoire. À revérifier dans
`VintagestoryAPI.dll` avec un décompilateur avant de coder quoi que ce soit.*

---

## 2. Le verdict de faisabilité, en une ligne par option

### Option A : champ voxel 3D, une température par bloc

Non viable. Un serveur charge typiquement plusieurs centaines de chunks de 32³ autour d'un
joueur, ce qui met l'ordre de grandeur bien au-dessus de la centaine de millions de cellules.
Même en ne simulant que l'intérieur des bâtiments, une base de joueur correcte fait déjà
plusieurs dizaines de milliers de blocs d'air, à faire diffuser plusieurs fois par seconde,
en C# managé, sur le thread serveur. Oxygen Not Included fait exactement ça et y arrive,
mais en 2D sur une grille d'environ 10⁴ à 10⁵ tuiles. Passer en 3D coûte deux à trois ordres
de grandeur.

### Option B : graphe nodal de pièces

Viable, et de loin le meilleur rapport résultat/coût. Une pièce détectée devient un nœud
avec une capacité thermique et une température d'état. Les parois deviennent des
conductances, les ouvertures des conductances élevées, le sol une conductance vers la terre
profonde. Une base de joueur ambitieuse fait vingt à quarante nœuds. Le coût CPU devient
négligeable et le budget passe entièrement dans la détection des pièces et le calcul des
surfaces d'échange, qui ne se refait qu'au changement de bloc.

C'est aussi, littéralement, ce que font les moteurs de simulation thermique du bâtiment
depuis quarante ans. On ne bricole pas, on applique.

### Option C : hybride nodal + grille grossière

Viable en second temps. Le graphe nodal donne la température moyenne de chaque pièce, et une
grille grossière (une cellule par 4³ ou 8³ blocs, uniquement à l'intérieur des volumes
détectés) porte la stratification verticale et les gradients locaux près des sources. À
garder en réserve : la stratification peut d'abord être approchée analytiquement, sans grille
du tout, par un simple gradient vertical dans la pièce.

**Recommandation : B en v1, C envisageable en v2 si le rendu manque de finesse.**

---

## 3. Sur quoi s'appuyer

### 3.1 Le modèle nodal RC : le cœur

C'est le domaine le mieux normalisé de tout ce dossier.

- **ISO 13790** définit un modèle horaire simplifié dit **5R1C** : cinq résistances
  thermiques et une capacité, pour une zone de bâtiment. C'est exactement le niveau
  d'abstraction qu'il te faut, et c'est écrit noir sur blanc dans une norme. Depuis
  remplacée par la série **ISO 52016-1:2017**, qui reprend le principe avec un découpage
  nodal plus fin (plusieurs nœuds par paroi).
  → Lire le 5R1C d'ISO 13790 en premier. C'est le plus simple et le plus proche du besoin.

- **Bacher & Madsen, « Identifying suitable models for the heat dynamics of buildings »,
  Energy and Buildings, 2011** (DTU). Papier très cité qui construit une hiérarchie de
  modèles RC, du plus grossier au plus détaillé, et montre lequel est justifié selon les
  données disponibles. Intérêt direct : il te dit à partir de quel niveau de complexité on
  arrête de gagner en réalisme. Pour un jeu, c'est exactement la question.

- Chercher aussi les revues sur les **modèles « grey-box » / RC networks** en thermique du
  bâtiment. C'est un champ dense, il y a plusieurs états de l'art disponibles.

### 3.2 Les flux d'air entre pièces : le tirage

Ce que tu appelles « le flux d'air » a un nom précis en physique du bâtiment : le
**tirage thermique** (stack effect), et il se modélise par un **réseau aéraulique multizone**.

- **CONTAM** (NIST) et le module **AirflowNetwork** d'EnergyPlus sont des implémentations
  libres et documentées du modèle multizone : des zones à pression uniforme reliées par des
  chemins de fuite, avec résolution de l'équilibre des pressions. La documentation
  d'EnergyPlus sur AirflowNetwork est en accès libre et très explicite sur les équations.
  → C'est là que tu trouveras la forme exacte du modèle à implémenter.
- L'ancêtre est **AIRNET** de G.N. Walton (NIST, fin des années 80). Référence exacte à
  vérifier.
- Le **ASHRAE Handbook, Fundamentals**, chapitre sur la ventilation et l'infiltration,
  donne la formule usuelle du débit par tirage,
  `Q = Cd·A·√(2·g·ΔH·ΔT/T)`, avec les valeurs de coefficient de décharge.
  C'est le minimum vital et ça suffit probablement pour la v1.
- **EN 13384** normalise le calcul thermo-aéraulique des conduits de fumée. Utile si tu veux
  que le tirage de la cheminée dépende vraiment de sa hauteur et de son diamètre, ce qui
  serait un excellent levier de gameplay.

### 3.3 La ventilation naturelle et les panaches : pour le réalisme du foyer

- **Linden, Lane-Serff & Smeed (1990), « Emptying filling boxes: the fluid mechanics of
  natural ventilation », Journal of Fluid Mechanics.** Le papier fondateur de la
  ventilation par déplacement. Il décrit le régime où une source de chaleur au sol crée
  une stratification à deux couches dans une pièce ventilée haut et bas, et donne la
  hauteur d'interface entre couche chaude et couche froide en fonction de la puissance et
  des surfaces d'ouverture. C'est *exactement* la physique d'une pièce avec un foyer et
  une cheminée.
- **Linden (1999), « The fluid mechanics of natural ventilation », Annual Review of Fluid
  Mechanics, vol. 31.** La revue de synthèse du même auteur. Point d'entrée plus lisible.
- **Morton, Taylor & Turner (1956), « Turbulent gravitational convection from maintained
  and instantaneous sources », Proceedings of the Royal Society A.** Le modèle « MTT » du
  panache thermique : comment un panache s'élargit et se dilue en montant. Si tu veux que
  le foyer chauffe le plafond avant les murs, c'est la loi à appliquer.

Ces trois-là sont le vrai trésor du dossier. Ils te donnent un modèle à deux couches par
pièce, analytique, sans grille et sans CFD, qui produit exactement le comportement
« l'air chaud monte, la cheminée l'évacue, l'air froid rentre par le bas ».

### 3.4 La température du sol : pour les caves

- **Kusuda & Achenbach (1965), « Earth temperature and thermal diffusivity at selected
  stations in the United States », ASHRAE Transactions.** Le modèle analytique standard de
  température du sol :

  `T(z, t) = T_moy − A·exp(−z·√(π/(365·α))) · cos( (2π/365)·(t − t₀ − (z/2)·√(365/(π·α))) )`

  Il donne deux choses gratuitement, et les deux sont du gameplay : l'amplitude
  saisonnière s'amortit exponentiellement avec la profondeur, et le maximum se décale dans
  le temps. Autrement dit, une cave à trois blocs de profondeur suit encore un peu les
  saisons, une cave à dix blocs est stable, et une cave intermédiaire est à son plus froid
  au début de l'été. Ça, c'est un mod qui se vend tout seul.

### 3.5 L'effet sur le joueur : le confort thermique

- **Fanger (1970)**, modèle **PMV/PPD**, normalisé dans **ISO 7730** et repris dans
  **ASHRAE Standard 55**. Le confort n'y dépend pas que de la température de l'air, mais de
  six variables : température d'air, **température radiante moyenne**, vitesse d'air,
  humidité, **isolation vestimentaire (clo)** et métabolisme (met).

  L'intérêt pour le jeu est frappant. Le « clo » correspond littéralement au système de
  vêtements que Vintage Story a déjà. La température radiante, c'est le foyer qui te chauffe
  la face même dans une pièce froide, ce que le joueur ressent intuitivement et qu'aucun mod
  ne modélise. La vitesse d'air, c'est le courant d'air de la porte ouverte. Tu remplaces un
  scalaire par un indice à six entrées, et chacune des six est un levier de construction.

- Attention à ne pas surjouer : PMV est calibré pour du confort de bureau entre 10 °C et
  30 °C. Pour l'hypothermie à −20 °C il faut autre chose. Chercher du côté des modèles de
  **bilan thermique corporel à deux nœuds** (noyau / peau), type modèle de Gagge, référence
  exacte à vérifier.

### 3.6 La conservation des aliments

- La **loi d'Arrhenius** appliquée à la cinétique de dégradation alimentaire, et sa forme
  d'ingénieur, le **coefficient Q₁₀** : la vitesse de dégradation est multipliée par environ
  2 à 3 pour chaque hausse de 10 °C. C'est un remplacement direct et physiquement fondé du
  `perishRate` actuel, avec un seul paramètre à régler par aliment.
  → Q₁₀ ≈ 2 pour commencer, ajusté ensuite au ressenti de jeu.

### 3.7 Le numérique

- Schéma explicite (Euler avant) : simple, mais la stabilité impose un pas de temps borné
  par le critère de Fourier, `Fo = α·Δt/Δx² ≤ 1/2` en 1D, plus contraignant en 3D. Avec des
  ouvertures très conductrices, ça devient vite ingérable.
- Schéma implicite (Euler arrière) : inconditionnellement stable, coûte une résolution de
  système linéaire par pas. Sur quelques dizaines de nœuds, c'est gratuit.
  → Aller directement à l'implicite. Ce n'est pas plus dur à écrire et ça supprime toute
  une classe de bugs de divergence.
- Pour les chunks déchargés : le système est linéaire, donc la relaxation vers l'équilibre
  est une exponentielle. On stocke la date du dernier tick et on extrapole analytiquement à
  la recharge, au lieu de rattraper des milliers de pas.

---

## 4. Références jeu vidéo, pour le calibrage du plaisir

- **Oxygen Not Included** simule conduction, capacité thermique et changements de phase par
  tuile, en 2D. C'est le mètre étalon de ce que la simulation thermique apporte comme
  gameplay, et aussi de ses pièges (les joueurs finissent par exploiter les artefacts du
  modèle). Regarder surtout comment le jeu *communique* la température au joueur, parce que
  c'est là que la moitié du travail se joue.
- **Dwarf Fortress** a de la température par tuile depuis longtemps, avec un coût CPU
  notoire. Un contre-exemple utile.

Sur les mods Vintage Story existants qui toucheraient à ça, je n'ai pas de liste fiable et je
ne vais pas en inventer. À chercher sur la ModDB officielle avant de démarrer, ne serait-ce
que pour ne pas refaire un travail déjà fait et pour repérer d'éventuels conflits de
patch Harmony.

---

## 5. Ce qui reste réellement risqué

Trois points, par ordre décroissant d'inquiétude.

**La détection des pièces.** Tout le modèle repose sur `RoomRegistry` ou sur un flood-fill
maison. Or les joueurs construisent n'importe quoi : volumes semi-ouverts, cavernes
aménagées, bâtiments à moitié finis, mégastructures de dix mille blocs. Il faut décider ce
qui se passe quand le flood-fill échoue ou explose, et surtout ce que devient l'extérieur.
Le plus probable est qu'il faille un nœud « extérieur » spécial dont la température est
simplement lue via `GetClimateAt`, ce qui règle le problème élégamment.

**Le multijoueur et l'autorité.** La simulation doit tourner côté serveur, et le client ne
reçoit que ce qu'il affiche. Cela veut dire un protocole de synchronisation à concevoir,
et une attention à la bande passante si l'overlay de visualisation existe.

**Les patchs Harmony.** Rebrancher la péremption et la température corporelle veut dire
patcher du code du jeu qui peut changer à chaque version, et qui est peut-être déjà patché
par d'autres mods. C'est la source la plus probable de rapports de bug incompréhensibles.

Un quatrième point, moins risqué mais coûteux : l'équilibrage. Une fois la physique juste,
il reste à choisir les valeurs de U des matériaux, les puissances des foyers et les seuils
d'inconfort pour que ce soit jouable. C'est beaucoup d'itérations en jeu.

---

## 6. Ordre de lecture proposé

1. La documentation EnergyPlus sur AirflowNetwork, pour voir un modèle multizone réel écrit
   en équations.
2. Le modèle 5R1C d'ISO 13790, pour la partie thermique pure.
3. Linden 1999 (la revue), puis Linden, Lane-Serff & Smeed 1990 si le sujet accroche.
4. Kusuda & Achenbach 1965, court et immédiatement applicable aux caves.
5. ISO 7730 / ASHRAE 55 pour le confort, en dernier, parce que c'est du réglage de surface.

Il n'y a rien dans cette liste qui demande un niveau de thermique supérieur à une bonne
licence d'ingénierie. Le seul vrai obstacle est le volume de lecture.
