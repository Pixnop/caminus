# Caminus

Building thermal simulation for Vintage Story. Each room has a temperature, the firepit heats it,
walls leak according to their material, the chimney draws. Food spoilage and player comfort follow
the actual temperature.

Status: milestone 1 in progress (see `docs/roadmap.md`). Target: Vintage Story 1.22.7, .NET 10.

## Build

```bash
export VINTAGE_STORY=/path/to/vintagestory   # folder containing VintagestoryAPI.dll
dotnet build -c Release                            # produces dist/caminus_<version>.zip
dotnet test
```

`tools/deploy.sh` builds and copies the zip into the `Mods` folder of a test profile.

## Structure

- `src/Caminus.Core`: pure thermal engine (nodal RC network, implicit Euler), no dependency on the game.
- `src/Caminus`: the mod, the adapter between the game and the engine.
- `tests/Caminus.Core.Tests`: engine tests.
- `docs/`: feasibility study, 1.22.7 API verification, ModDB survey, milestone roadmap.
