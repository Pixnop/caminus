# Atlas 0.12.0-rc.1: feedback from Caminus

What we asked for in [atlas-client-request.md](atlas-client-request.md) was, failing a real headless
client, "a client-side assertion surface without a client". That is what `ITestPlayer.Client` is, and
it covers the overlay end to end. One new scenario,
`ThermalScenarios.Overlay_reaches_the_player`, brings the suite to 36, all green.

The scenario did not pass on the very first run, but neither failure was Atlas's: a player name one
character over the engine limit, and our own wrong guess about how a command answer reaches a
player. Nothing in the new surface misbehaved.

## (a) Ergonomics

`player.Client` needs no setup: no attribute, no opt-in, no capture window to open and close. The
version bump in the csproj was the whole migration, and the 35 existing scenarios kept passing with
no edit.

`Highlights(slot)` returning the last packet's content rather than a history is the right shape.
The assertion a scenario wants to write is "what does this player see right now", which is exactly
what the client keeps, and clearing a slot falls out of it as an empty list instead of needing a
separate concept. The dedupe question we had to answer (one wall block can carry two faces, so
`OverlayServer.Highlights` keeps the loudest and sends one cube per block) was settled by comparing
the observed positions with the mod's own list, which is the comparison we wanted to make and could
not make before.

The records read well in xUnit failure messages. `HighlightedBlock(Pos, Color)` printed a position
and a colour we could act on without adding a formatter.

The raw `Color` next to the decoded `Rgba` is useful, not confusing, and it removed a real trap
rather than adding one. Caminus carries a comment saying highlights are RGBA and particles BGRA
(`ColorUtil.cs:275-281`); with `Rgba` picking the decode per packet kind, the scenario asserts
`Rgba.R == 255` on both kinds and never repeats that knowledge. Keeping the raw int alongside is
right too: a scenario that wants to compare against a colour the mod computed itself needs the int,
not the channels.

`Packets<T>(channel)` matching `T` by full name is the property that made this work at all. Our test
project references the mod project while the game's ModLoader loads its own copy of `Caminus.dll`,
and `Packets<OverlayPacket>("caminus")` came back deserialized with no ceremony.

Two smaller remarks:

- `Particles()` returns everything since the last clear while `Highlights(slot)` returns current
  state. The asymmetry is defensible (each mirrors what a client does with that packet kind) but the
  names do not hint at it. One sentence in the README would save the reader a trip to the XML doc.
- `Client` is a property, the four observations are methods. That reads fine and makes the drain
  cost visible at the call site, which is the honest way round.

## (b) Gaps

1. **No way for a test player to say something.** We wanted `ChatLines()` to carry the overlay
   toggle's own answer, which is what a real player sees when they type `/caminus overlay`. It
   cannot: the answer is delivered through the result callback that the server's chat handler
   supplies, and calling `IChatCommandApi.ExecuteUnparsed(command, args)` with no callback drops it
   (checked: `ChatLines()` stays empty). So the scenario asserts on the callback's message, the way
   it already did before 0.12, and `ChatLines()` ends up unexercised by our suite. Something like
   `player.Say("/caminus overlay")` going through the same path a real client's chat packet takes
   would close this, and it would also cover mods whose commands only ever answer through chat.
   Same family as the hotkey request in item 1 of our original ask.
2. **No GUI.** `OverlayHud` (dialog key `caminus:overlayhud`, dynamic text `text`) is still
   unassertable. Expected, it was the part of item 1 that a client-less approach cannot reach, and
   `Packets<OverlayPacket>` covers the text that feeds it, so we are not blocked.
3. **Something the surface now makes possible that we did not write.** The server has no per-player
   `SpawnParticles`, so a second player standing in the same room sees the first one's overlay
   (a known Caminus shortcut, marked in `Overlay.cs`). `Particles()` is per-connection, so a scenario
   can now prove that leak instead of reasoning about it. Noting it as a capability, not a gap.
4. `Rgba` for a packet that carried no colour is `(0, 0, 0, 0)`, indistinguishable from a real
   transparent black. The XML doc says `Color` is 0 in that case, so the information is there; only
   the decoded view is ambiguous. Minor.

## (c) Pitfalls met

- **The 16-character player name limit.** Our first name, `CaminusOverlayNet`, is 17 characters and
  the join failed. The exception named the rule, the character set, the limit and the server log
  path. Best error message we hit in this bump; nothing to fix.
- **The chunk-streaming note about particles did not bite us**, because the player stands in the room
  and its chunk is streamed long before the overlay is switched on. `Particles()` was non-empty on
  the first mod tick after the toggle. We kept the spawn-per-tick plus `World.Until` pattern anyway,
  since the overlay spawns every tick for free.
- **A read drains, so timing a read that follows a poll loop measures nothing.** Our first
  measurement of `Highlights` came out at 0.00 ms because the `World.Until` predicate had already
  drained every tick. Getting a real number meant advancing 60 ticks with nobody reading. Not a bug,
  and the contract is documented, but it is an easy way to publish a meaningless benchmark.

## (d) Bugs

None found. Every assertion behaved as documented on the first attempt:

- 54 observed positions, matching `OverlayServer.Highlights(flows)` position for position as a set
  (a 3x3 plate of wall blocks per face, no block shared between two walls).
- Colours decoding to red-dominant with `R == 255` on all 54 cubes, which is what the mod encodes
  through `ColorUtil.ColorFromRgba(255, 50, 40, alpha)`.
- `OverlayPacket` round-tripped through protobuf with its `Text` intact, last one starting with
  `Room ` and containing `outside`.
- 16 particle spawns per mod tick, which is exactly `OverlayServer.ParticleFaces`, all within 4
  blocks of the room centre, colour red-dominant through the other byte order.
- Both clearing paths: the mod's empty highlight packet emptied the slot, and `Clear()` emptied
  highlights, packets and particles at once.

## (e) Timings

Manjaro, .NET 10, Release, embedded 1.22.7 server, one scenario at a time.

| Measurement | 0.11.0 | 0.12.0-rc.1 |
| --- | --- | --- |
| Full `dotnet test` (wall clock) | 5 m 43 s | 5 m 54 s |
| `Caminus.Scenarios` (35 then 36 scenarios) | 5 m 32 s | 5 m 45 s |
| `Overlay_reaches_the_player` alone | n/a | 11 to 13 s |

The 13 s the scenario suite gained is the new scenario's own cost (a room, a firepit, a 300-tick
warm-up), so the observation tap costs the 35 scenarios that never touch `player.Client` nothing
measurable.

Read costs, from the scenario's own output:

- `Highlights(7)` draining two mod ticks of unread traffic (2 highlight packets of 54 positions
  each, 2 `OverlayPacket`s, 32 particle spawns): **0.65 ms**.
- `Packets<OverlayPacket>("caminus")` immediately after, with nothing left to drain and 3 captured
  packets to filter: **0.09 ms**.
- The same two reads with nothing pending at all: 0.00 ms and 0.05 ms.

Both numbers include the hop to the game thread. At that cost, polling a read once per tick inside a
`World.Until` is free relative to the tick itself.
