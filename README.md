# Caminus

Simulation thermique du bâtiment pour Vintage Story. Chaque pièce a une température, le foyer la
chauffe, les murs fuient selon leur matériau, la cheminée tire. La conservation des aliments et le
confort du joueur suivent la température réelle.

Statut : lot 1 en cours (voir `docs/decoupage-lots.md`). Cible : Vintage Story 1.22.7, .NET 10.

## Compiler

```bash
export VINTAGE_STORY=/chemin/vers/vintagestory   # dossier contenant VintagestoryAPI.dll
dotnet build -c Release                            # produit dist/caminus_<version>.zip
dotnet test
```

`tools/deploy.sh` compile et copie le zip dans le dossier `Mods` d'un profil de test.

## Structure

- `src/Caminus.Core` : moteur thermique pur (réseau nodal RC, Euler implicite), sans dépendance au jeu.
- `src/Caminus` : le mod, adaptateur entre le jeu et le moteur.
- `tests/Caminus.Core.Tests` : tests du moteur.
- `docs/` : faisabilité, vérification de l'API 1.22.7, recherche ModDB, découpage en lots.
