# Caminus

Building thermal simulation for Vintage Story. Each room has a temperature, the firepit heats it,
walls leak according to their material, the chimney draws. Food spoilage and player comfort follow
the actual temperature.

Status: milestone 1 in progress (see `docs/roadmap.md`). Target: Vintage Story 1.22.7, .NET 10.

## Overlay

Press `K` (or type `/caminus overlay`) while standing in an enclosed room and every block of its
envelope lights up, refreshed once a second. Press it again to turn it off.

Each block is coloured by what the face behind it is doing, and how brightly by how much of the
room's largest flow it carries:

| Colour | Meaning |
| --- | --- |
| red | heat leaving the room through a wall |
| blue | heat coming in through a wall |
| brown | a face buried in the ground |
| cyan | an opening: a doorway, a hole, a window |

Particles drift off the sixteen loudest faces, out of the room where it loses heat and into it where
it gains, and a small box in the top left corner gives the room and outside temperatures, the wind,
and the floor/eyes/ceiling spread with the net power.

Highlights and particles are drawn by the server, so a client without Caminus installed sees them
too. The hotkey and the text box need the mod on the client.

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
