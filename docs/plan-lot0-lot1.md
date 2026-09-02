# Implementation plan: milestones 0 and 1

## Milestone 0: skeleton

- `Caminus.sln`, `Directory.Build.props` (net10.0, version 0.1.0, `VintageStoryDir` = `$VINTAGE_STORY`).
- `src/Caminus.Core`: pure engine, no reference to the game. Contract `ThermalNetwork` (nodes, fixed nodes, edges, sources, `Step`).
- `src/Caminus`: the mod. `Private=false` references to VintagestoryAPI, VintagestoryLib, VSEssentials, VSSurvivalMod, protobuf-net, Newtonsoft, 0Harmony. `PackageMod` target in Release: `dist/caminus_<version>.zip` (Caminus.dll, Caminus.Core.dll, modinfo.json, assets/).
- `tests/Caminus.Core.Tests`: xunit 2.9.3, runner 2.8.2, Test.Sdk 17.14.1, coverlet.
- CI GitHub Actions "Build & tests": downloads the 1.22.7 server from the CDN (cached), builds, runs tests with opencover coverage, Sonar `Pixnop_caminus` (org `leon-fievet-pixnop`) if the `SONAR_TOKEN` secret is present, zips as an artifact.
- `tools/deploy.sh`: builds and copies the zip into the `Mods` folder of a test VSL profile.

## Milestone 1: the honest thermometer

### Engine (`Caminus.Core`)
`ThermalNetwork`: implicit Euler, `(C/dt + L) T⁺ = (C/dt) T + Q`, fixed nodes as Dirichlet
conditions, dense solver (Gaussian elimination or Cholesky, n ≤ 100). Tests: analytic equilibrium,
time constant, stability at a large time step, energy conservation in a closed system, sign of the
flows, fixed node stays immutable.

### Adapter (`Caminus`)
- `RoomThermalSystem` (server): 1 s tick. For each player, `RoomRegistry.GetRoomForPosition` (main
  thread only). One room = one node, identified by its `Room` object (the registry returns the same
  instance until invalidated by `ChunkDirty`); if the instance changes, the geometry is recomputed.
- Geometry: room positions via `Room.Contains`. Volume = number of air blocks. For each air-block
  face facing outward from the room: wall (solid block on that face) → U from `EnumBlockMaterial` via
  `assets/caminus/config/thermal.json`; otherwise opening → opening conductance. Area = 1 m² per face.
- Sources: `Block.GetInterface<IHeatSource>` over a bbox expanded by one block, `GetHeatStrength` × W per unit.
- Fixed outdoor node: `GetClimateAt(center, NowValues)` every tick, `null` tolerated.
- Rooms are forgotten after 5 min with no player. No persistence (milestone 2), no networking (milestone 5).
- Commands: `/caminus version`, `/caminus temp` (room temperature, outdoor temperature, volume, losses
  by material in W, sources in W).

### Verification
Engine unit tests in CI. Dedicated 1.22.7 server run headless with the zip: loads without error.
In-game validation by Pixnop: cabin with a firepit, open door, stone against wood.
