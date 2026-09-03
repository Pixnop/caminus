# Request to Atlas: client-side testing for Caminus

Caminus (Vintage Story 1.22.7, Pixnop.Atlas.XUnit 0.11.0, 35 server-side scenarios green) has a
client overlay that no scenario can exercise today, because Atlas embeds a server only.

## What the mod does on the client

- Receives packets on a mod network channel `caminus` (protobuf-net), one message type
  `OverlayPacket { string Text }` carrying the HUD text.
- Shows a `HudElement` (GuiComposer, `AddGameOverlay` + `AddDynamicText("text")`) with three lines.
- Registers a hotkey `caminusoverlay` (K, `HotkeyType.HelpAndOverlays`) whose handler sends the chat
  command `/caminus overlay`.
- The server side calls `sapi.World.HighlightBlocks(player, slot 7, positions, colours,
  Absolute, Arbitrary)` and `sapi.World.SpawnParticles(...)`, which travel as vanilla packets.

## What Caminus needs, by value

1. **A test player with a client.** A headless real client (no window, ideally no GPU: offscreen
   OpenTK or rendering disabled) joined to the embedded server, on which a scenario can: fire a
   hotkey by its code, send a client chat message or command, read the open GUI dialogs and their
   text (HudElement, `GetDynamicText`), read the highlighted blocks received (positions and colours
   per highlight slot) and the particles spawned (position, colour, velocity), and read the packets
   received on a mod channel, by type.
2. **Failing that, a client-side assertion surface without a client**: capture what the server sends
   to a given player (block highlight packets, particle packets, mod channel packets) and expose it
   on `ITestPlayer`, for example `player.Client.Highlights(slot)`, `player.Client.Particles()`,
   `player.Client.Packets<T>("caminus")`. That alone covers most of the need.
3. **Bonus**: a PNG screenshot of the headless client, for visual review by an agent.

## Answer wanted

Feasibility, chosen approach, target Atlas version, and what Caminus must prepare on the mod side to
be testable (dialog naming, highlight slot ids, packet visibility).
