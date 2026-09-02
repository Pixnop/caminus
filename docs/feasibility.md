# Thermal simulation for Vintage Story (Caminus mod): feasibility note

Goal: a single thermal engine that feeds player comfort, food spoilage, and reading tools for the
builder. Chimneys, insulation, cellars.

Status: preparatory note. No architecture decision is locked in.

---

## 1. What the game already does, and why it falls short

The game has no temperature field. It has three separate mechanisms that give the illusion of one.

`GetClimateAt(pos, mode)` samples an analytic climate field generated at worldgen (temperature,
rainfall, fertility). It varies with latitude, altitude, season, and time of day. It's a read, not a
state: nothing is stored, nothing evolves.

`RoomRegistry` does a flood-fill to detect an enclosed volume and returns a `Room` carrying counters
(exits, ceiling blocks exposed to sky, "cold" blocks). These counters are used to dampen climate
variation in the spoilage calculation (`BlockEntityContainer.GetPerishRate()`) and to detect
greenhouses. The physical intuition is right, it's thermal inertia, but it's hardcoded instead of
integrated.

`IHeatSource.GetHeatStrength(world, posSource, posReceiver)` is implemented by the firepit, the
forge, the oven. The body temperature behavior sweeps a sphere around the player and sums
distance-decayed contributions. No obstacle is considered: heat passes straight through a floor.

Direct consequence: there's nothing to "improve" in the existing system. A persistent thermal state
needs to be added, and the three consumers rewired onto it.

*Warning: the member names above are written from memory. They need to be re-verified in
`VintagestoryAPI.dll` with a decompiler before writing any code.*

---

## 2. The feasibility verdict, one line per option

### Option A: 3D voxel field, one temperature per block

Not viable. A server typically loads several hundred 32³ chunks around a player, which puts the
order of magnitude well above a hundred million cells. Even simulating building interiors only, a
decent player base already runs several tens of thousands of air blocks, needing to diffuse several
times per second, in managed C#, on the server thread. Oxygen Not Included does exactly this and
pulls it off, but in 2D on a grid of roughly 10⁴ to 10⁵ tiles. Going 3D costs two to three orders of
magnitude.

### Option B: nodal graph of rooms

Viable, and by far the best result-to-cost ratio. A detected room becomes a node with a thermal
capacity and a state temperature. Walls become conductances, openings high conductances, the floor a
conductance toward the deep ground. An ambitious player base runs twenty to forty nodes. CPU cost
becomes negligible and the budget goes entirely into room detection and exchange-surface
computation, which only needs to be redone on a block change.

This is also, literally, what building thermal simulation engines have been doing for forty years.
No need to improvise, just apply it.

### Option C: nodal hybrid + coarse grid

Viable as a second step. The nodal graph gives the average temperature of each room, and a coarse
grid (one cell per 4³ or 8³ blocks, only inside detected volumes) carries vertical stratification and
local gradients near sources. Keep in reserve: stratification can first be approximated analytically,
with no grid at all, via a simple vertical gradient within the room.

**Recommendation: B for v1, C worth considering for v2 if the rendering lacks nuance.**

---

## 3. What to build on

### 3.1 The nodal RC model: the core

This is the best-standardized area of the whole subject.

- **ISO 13790** defines a simplified hourly model called **5R1C**: five thermal resistances and one
  capacitance, for a building zone. This is exactly the level of abstraction needed here, and it's
  written in black and white in a standard. Since superseded by the **ISO 52016-1:2017** series,
  which reuses the principle with a finer nodal breakdown (several nodes per wall).
  → Read the 5R1C from ISO 13790 first. It's the simplest and closest to the need.

- **Bacher & Madsen, "Identifying suitable models for the heat dynamics of buildings",
  Energy and Buildings, 2011** (DTU). Widely cited paper that builds a hierarchy of RC models, from
  coarsest to most detailed, and shows which one is justified given available data. Direct value:
  it tells you at what complexity level you stop gaining realism. For a game, that's exactly the
  question.

- Also worth searching: reviews on **"grey-box" models / RC networks** in building physics. It's a
  dense field, several state-of-the-art surveys exist.

### 3.2 Airflow between rooms: stack effect

What you'd call "the airflow" has a precise name in building physics: the
**stack effect** (thermal draft), and it's modeled by a **multizone airflow network**.

- **CONTAM** (NIST) and the **AirflowNetwork** module of EnergyPlus are free, documented
  implementations of the multizone model: zones at uniform pressure connected by leakage paths, with
  pressure-balance resolution. EnergyPlus's AirflowNetwork documentation is freely available and very
  explicit about the equations.
  → This is where you'll find the exact form of the model to implement.
- The ancestor is **AIRNET** by G.N. Walton (NIST, late 1980s). Exact reference to verify.
- The **ASHRAE Handbook, Fundamentals**, chapter on ventilation and infiltration, gives the usual
  stack-effect flow formula,
  `Q = Cd·A·√(2·g·ΔH·ΔT/T)`, along with discharge coefficient values.
  This is the bare minimum and probably enough for v1.
- **EN 13384** standardizes the thermo-fluid dynamic calculation of flue ducts. Useful if you want
  the chimney's draft to genuinely depend on its height and diameter, which would make an excellent
  gameplay lever.

### 3.3 Natural ventilation and plumes: for firepit realism

- **Linden, Lane-Serff & Smeed (1990), "Emptying filling boxes: the fluid mechanics of
  natural ventilation," Journal of Fluid Mechanics.** The foundational paper on displacement
  ventilation. It describes the regime where a floor-level heat source creates a two-layer
  stratification in a room vented at top and bottom, and gives the interface height between the hot
  layer and the cold layer as a function of power and opening area. This is *exactly* the physics of
  a room with a firepit and a chimney.
- **Linden (1999), "The fluid mechanics of natural ventilation," Annual Review of Fluid
  Mechanics, vol. 31.** The review by the same author. More readable entry point.
- **Morton, Taylor & Turner (1956), "Turbulent gravitational convection from maintained
  and instantaneous sources," Proceedings of the Royal Society A.** The "MTT" plume model: how a
  thermal plume widens and dilutes as it rises. If you want the firepit to heat the ceiling before
  the walls, this is the law to apply.

These three are the real treasure of this note. They give a two-layer-per-room model, analytical,
without a grid and without CFD, that produces exactly the behavior "hot air rises, the chimney
vents it, cold air comes in from below."

### 3.4 Ground temperature: for cellars

- **Kusuda & Achenbach (1965), "Earth temperature and thermal diffusivity at selected
  stations in the United States," ASHRAE Transactions.** The standard analytic model for ground
  temperature:

  `T(z, t) = T_avg − A·exp(−z·√(π/(365·α))) · cos( (2π/365)·(t − t₀ − (z/2)·√(365/(π·α))) )`

  It gives two things for free, and both are gameplay: the seasonal amplitude decays exponentially
  with depth, and the peak shifts in time. In other words, a cellar three blocks deep still tracks
  the seasons somewhat, a cellar ten blocks deep is stable, and an intermediate cellar is coldest at
  the start of summer. That's a mod that sells itself.

### 3.5 Effect on the player: thermal comfort

- **Fanger (1970)**, the **PMV/PPD** model, standardized in **ISO 7730** and adopted by
  **ASHRAE Standard 55**. Comfort there doesn't depend only on air temperature, but on six
  variables: air temperature, **mean radiant temperature**, air velocity, humidity, **clothing
  insulation (clo)**, and metabolic rate (met).

  The appeal for the game is striking. "Clo" maps literally onto the clothing system Vintage Story
  already has. Radiant temperature is the firepit warming your face even in a cold room, something
  the player feels intuitively and no mod currently models. Air velocity is the draft from an open
  door. You swap a scalar for a six-input index, and each of the six is a building-design lever.

- Careful not to overreach: PMV is calibrated for office comfort between 10 °C and 30 °C. For
  hypothermia at −20 °C something else is needed. Look toward **two-node body heat balance**
  models (core / skin), such as the Gagge model; exact reference to verify.

### 3.6 Food spoilage

- **Arrhenius's law** applied to food degradation kinetics, and its engineering form, the
  **Q₁₀ coefficient**: degradation rate roughly multiplies by 2 to 3 for every 10 °C rise. This is a
  direct, physically grounded replacement for the current `perishRate`, with a single per-food
  parameter to tune.
  → Q₁₀ ≈ 2 as a starting point, tuned afterward to gameplay feel.

### 3.7 The numerics

- Explicit scheme (forward Euler): simple, but stability requires a bounded time step per the
  Fourier criterion, `Fo = α·Δt/Δx² ≤ 1/2` in 1D, more restrictive in 3D. With highly conductive
  openings, this quickly becomes unmanageable.
- Implicit scheme (backward Euler): unconditionally stable, costs one linear system solve per step.
  Over a few dozen nodes, that's free.
  → Go straight to implicit. It's not much harder to write, and it removes an entire class of
  divergence bugs.
- For unloaded chunks: the system is linear, so the relaxation toward equilibrium is an exponential.
  Store the timestamp of the last tick and extrapolate analytically on reload, instead of catching up
  thousands of steps.

---

## 4. Video game references, for calibrating fun

- **Oxygen Not Included** simulates conduction, thermal capacity, and phase changes per tile, in 2D.
  It's the benchmark for what thermal simulation brings as gameplay, and also for its pitfalls
  (players end up exploiting the model's artifacts). Pay special attention to how the game
  *communicates* temperature to the player, because that's where half the work lies.
- **Dwarf Fortress** has had per-tile temperature for a long time, with a notorious CPU cost. A
  useful counterexample.

On existing Vintage Story mods that touch this space, I don't have a reliable list and won't
invent one. Worth searching the official ModDB before starting, if only to avoid redoing work
already done and to spot potential Harmony patch conflicts.

---

## 5. What's genuinely still risky

Three points, in decreasing order of concern.

**Room detection.** The whole model rests on `RoomRegistry` or a homemade flood-fill. Players build
anything: semi-open volumes, furnished caverns, half-finished buildings, ten-thousand-block
megastructures. A decision is needed for what happens when the flood-fill fails or blows up, and
above all what the "outside" becomes. Most likely a special "outdoor" node is needed, whose
temperature is simply read via `GetClimateAt`, which settles the problem elegantly.

**Multiplayer and authority.** The simulation must run server-side, and the client only receives
what it displays. That means a sync protocol to design, and attention to bandwidth if the
visualization overlay exists.

**Harmony patches.** Rewiring spoilage and body temperature means patching game code that can change
with every version, and that may already be patched by other mods. This is the most likely source of
baffling bug reports.

A fourth point, less risky but costly: balancing. Once the physics is right, there's still the
matter of choosing material U-values, firepit power, and discomfort thresholds to keep it
playable. That's a lot of in-game iteration.

---

## 6. Suggested reading order

1. The EnergyPlus documentation on AirflowNetwork, to see a real multizone model written out in
   equations.
2. The 5R1C model from ISO 13790, for the pure thermal part.
3. Linden 1999 (the review), then Linden, Lane-Serff & Smeed 1990 if the topic catches interest.
4. Kusuda & Achenbach 1965, short and immediately applicable to cellars.
5. ISO 7730 / ASHRAE 55 for comfort, last, because it's surface-level tuning.

Nothing on this list requires a thermal background beyond a good engineering degree. The only real
obstacle is the reading volume.
