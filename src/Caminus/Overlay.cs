using System.Globalization;
using System.Text;
using Caminus.Core;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Caminus;

/// <summary>
/// One puff the client is asked to spawn: a wall face blowing along its normal, or a chimney block
/// letting the draft out. Everything the client needs is precomputed, so it only builds the particle.
/// </summary>
[ProtoContract]
public sealed record ParticleFace
{
    [ProtoMember(1)] public int X { get; set; }
    [ProtoMember(2)] public int Y { get; set; }
    [ProtoMember(3)] public int Z { get; set; }
    /// <summary><c>BlockFacing.Index</c> of the outward normal.</summary>
    [ProtoMember(4)] public int Facing { get; set; }
    /// <summary>What the face is doing, W, positive when heat leaves the room. 0 on a flue puff.</summary>
    [ProtoMember(5)] public double Watts { get; set; }
    /// <summary>Packed colour, already shaded by how loud the face is. Particles read it as BGRA.</summary>
    [ProtoMember(6)] public int Color { get; set; }
    /// <summary>Speed along the normal, m/s; negative flies the other way, i.e. into the room.</summary>
    [ProtoMember(7)] public float Speed { get; set; }
    /// <summary>A chimney puff rather than a wall face: same fields, another shape.</summary>
    [ProtoMember(8)] public bool Flue { get; set; }
}

/// <summary>The HUD lines and the puffs, both formatted server side. Empty text means hide the HUD.</summary>
[ProtoContract]
public class OverlayPacket
{
    [ProtoMember(1)] public string Text { get; set; } = "";
    [ProtoMember(2)] public List<ParticleFace> Faces { get; set; } = [];
}

/// <summary>
/// Draws the room a player stands in: one coloured cube per envelope block, particles along the
/// loudest flows, three lines of numbers.
/// The geometry never travels through a channel of ours: <c>IWorldAccessor.HighlightBlocks</c> is
/// already server-to-client on the server side (ServerMain.cs:3359 sends its own packet), so a
/// client without Caminus still gets the cubes. The particles do travel, in the same packet as the
/// text: <c>SpawnParticles</c> has no per-player form server side, and one player's overlay has no
/// business showing up in another player's world.
/// </summary>
public class OverlayServer : ModSystem
{
    /// <summary>Highlight slot. Vanilla holds 1, 2, 26, 27, 50, 941, 942 and 1292.</summary>
    /// <summary>Block highlight slot the overlay draws in. Public so test harnesses can read it back.</summary>
    public const int HighlightSlot = 7;
    /// <summary>How many faces get flow particles, loudest first.</summary>
    private const int ParticleFaceCount = 16;
    /// <summary>Alpha of the quietest face, so that a wall doing nothing is still visible.</summary>
    private const double MinAlpha = 0.15;

    private ICoreServerAPI sapi = null!;
    private RoomThermalSystem thermal = null!;
    private IServerNetworkChannel channel = null!;
    private readonly HashSet<string> enabled = [];

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        thermal = api.ModLoader.GetModSystem<RoomThermalSystem>();
        channel = api.Network.RegisterChannel("caminus").RegisterMessageType<OverlayPacket>();
        api.ChatCommands.GetOrCreate("caminus")
            .BeginSubCommand("overlay")
                .WithDescription("Show or hide the thermal overlay of the room you are in")
                .RequiresPlayer()
                .HandleWith(Toggle)
            .EndSubCommand();
        api.Event.PlayerDisconnect += player => enabled.Remove(player.PlayerUID);
        api.Event.RegisterGameTickListener(OnTick, ex => api.Logger.Error("[Caminus] overlay tick: {0}", ex), 1000);
    }

    private TextCommandResult Toggle(TextCommandCallingArgs args)
    {
        var player = (IServerPlayer)args.Caller.Player;
        if (enabled.Add(player.PlayerUID))
            return TextCommandResult.Success("Thermal overlay on. Red: heat leaving, blue: heat coming in, brown: buried, cyan: opening.");
        enabled.Remove(player.PlayerUID);
        Clear(player);
        return TextCommandResult.Success("Thermal overlay off.");
    }

    private void Clear(IServerPlayer player)
    {
        sapi.World.HighlightBlocks(player, HighlightSlot, []);
        channel.SendPacket(new OverlayPacket(), player);
    }

    private void OnTick(float dt)
    {
        foreach (string uid in enabled)
        {
            if (sapi.World.PlayerByUid(uid) is not IServerPlayer { ConnectionState: EnumClientState.Playing } player
                || player.Entity == null) continue;
            BlockPos pos = RoomThermalSystem.EyeBlockPos(player.Entity);
            if (!thermal.TryGetFaceFlows(pos, out RoomFlows? flows)) { Clear(player); continue; }

            (List<BlockPos> blocks, List<int> colors) = Highlights(flows);
            sapi.World.HighlightBlocks(player, HighlightSlot, blocks, colors);
            double? body = player.Entity.GetBehavior<EntityBehaviorCaminusBodyTemperature>()?.CurBodyTemperature;
            channel.SendPacket(new OverlayPacket { Text = Describe(flows, pos.Y, body), Faces = ParticleFaces(flows) }, player);
        }
    }

    /// <summary>
    /// One cube per envelope block, coloured by what the face is doing and shaded by how loudly.
    /// Public and pure: Atlas has no client, so this is where the scenarios look.
    /// </summary>
    public static (List<BlockPos> Blocks, List<int> Colors) Highlights(RoomFlows flows)
    {
        // An inner corner block is the neighbour of two air blocks of the same room, so one block can
        // carry two faces. The highlight is one cube per block: keep the loudest of them.
        Dictionary<BlockPos, FaceFlow> byBlock = [];
        foreach (FaceFlow f in flows.Faces)
            if (!byBlock.TryGetValue(f.Face.Pos, out FaceFlow held) || Math.Abs(f.Watts) > Math.Abs(held.Watts))
                byBlock[f.Face.Pos] = f;

        double max = 0;
        foreach (FaceFlow f in byBlock.Values) max = Math.Max(max, Math.Abs(f.Watts));
        List<BlockPos> blocks = [];
        List<int> colors = [];
        foreach (FaceFlow f in byBlock.Values)
        {
            blocks.Add(f.Face.Pos);
            (int r, int g, int b) = Rgb(f);
            colors.Add(ColorUtil.ColorFromRgba(r, g, b, (int)(255 * Alpha(f.Watts, max))));
        }
        // The stack itself, above the envelope: orange, and dim while it draws nothing.
        foreach (BlockPos p in flows.Draft?.Blocks ?? [])
        {
            blocks.Add(p);
            colors.Add(ColorUtil.ColorFromRgba(FlueColor.R, FlueColor.G, FlueColor.B,
                (int)(255 * (flows.Draft!.Flow > 0 ? 0.75 : MinAlpha))));
        }
        return (blocks, colors);
    }

    /// <summary>
    /// The face palette, RGB. Public so a scenario can name the colour it expects rather than repeat
    /// three numbers: red heat leaving, blue heat coming in, brown buried, cyan an opening, orange the stack.
    /// </summary>
    public static readonly (int R, int G, int B)
        OpeningLoss = (0, 220, 220), OpeningGain = (0, 150, 255),
        GroundLoss = (170, 110, 40), GroundGain = (120, 90, 60),
        WallLoss = (255, 50, 40), WallGain = (40, 90, 255),
        FlueColor = (255, 150, 30);

    /// <summary>Red when heat leaves the room, blue when it comes in; brown when buried, cyan for an opening.</summary>
    private static (int R, int G, int B) Rgb(in FaceFlow f)
    {
        bool loss = f.Watts >= 0;
        if (f.Face.Opening) return loss ? OpeningLoss : OpeningGain;
        if (f.Face.Ground) return loss ? GroundLoss : GroundGain;
        return loss ? WallLoss : WallGain;
    }

    /// <summary>Share of the room's loudest face, floored so that every face stays on screen.</summary>
    private static double Alpha(double watts, double max) =>
        max <= 0 ? MinAlpha : Math.Clamp(Math.Abs(watts) / max, MinAlpha, 1);

    /// <summary>
    /// What the client is asked to spawn: a puff over the middle of each of the loudest faces moving
    /// along the face normal (out of the room where heat leaves it, in where it comes back), then one
    /// per chimney block, climbing as fast as the draft is strong.
    /// Public and pure, same reason as <see cref="Highlights"/>.
    /// </summary>
    public static List<ParticleFace> ParticleFaces(RoomFlows flows)
    {
        List<ParticleFace> puffs = [];
        double max = 0;
        foreach (FaceFlow f in flows.Faces) max = Math.Max(max, Math.Abs(f.Watts));
        if (max > 0)
            foreach (FaceFlow f in flows.Faces.OrderByDescending(x => Math.Abs(x.Watts)).Take(ParticleFaceCount))
            {
                (int r, int g, int b) = Rgb(f);
                puffs.Add(new ParticleFace
                {
                    X = f.Face.Pos.X, Y = f.Face.Pos.Y, Z = f.Face.Pos.Z,
                    Facing = f.Face.Facing.Index,
                    Watts = f.Watts,
                    // Particles read the colour as BGRA, walls as RGBA (ColorUtil.cs:275-281).
                    Color = ColorUtil.ToRgba((int)(255 * Alpha(f.Watts, max)), r, g, b),
                    Speed = (float)(0.15 + 0.45 * Math.Abs(f.Watts) / max) * (f.Watts >= 0 ? 1 : -1),
                });
            }
        if (flows.Draft is not { Flow: > 0 } draft) return puffs;
        // A m³/s through a one-block flue IS a metre per second, so the flow per column is the speed.
        var up = (float)Math.Min(3, draft.Flow / Math.Max(1, draft.Columns));
        foreach (BlockPos p in draft.Blocks)
            puffs.Add(new ParticleFace
            {
                X = p.X, Y = p.Y, Z = p.Z,
                Facing = BlockFacing.UP.Index,
                Color = ColorUtil.ToRgba(180, FlueColor.R, FlueColor.G, FlueColor.B),
                Speed = up,
                Flue = true,
            });
        return puffs;
    }

    /// <summary>
    /// The three HUD lines. <paramref name="eyeY"/> is the block the player is looking out of.
    /// Public and pure, same reason as <see cref="Highlights"/>.
    /// </summary>
    /// <param name="body">Player's body temperature, when the caller has one to show.</param>
    public static string Describe(RoomFlows flows, int eyeY, double? body = null)
    {
        int floorY = int.MaxValue, ceilingY = int.MinValue;
        double watts = 0;
        foreach (FaceFlow f in flows.Faces)
        {
            floorY = Math.Min(floorY, f.Face.Y);
            ceilingY = Math.Max(ceilingY, f.Face.Y);
            watts += f.Watts;
        }
        double wind = Math.Sqrt(flows.Wind.X * flows.Wind.X + flows.Wind.Z * flows.Wind.Z);
        CultureInfo c = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append(c, $"Room {flows.Temperature:0.0} °C   outside {flows.OutsideTemperature:0.0} °C");
        if (!double.IsNaN(flows.GroundTemperature)) sb.Append(c, $"   ground {flows.GroundTemperature:0.0} °C");
        if (flows.GeologicActivity > 0) sb.Append(c, $"   geology {flows.GeologicActivity:0.00}");
        sb.AppendLine();
        sb.Append(c, $"Wind {wind:0.00} from the {RoomThermalSystem.ComesFrom(flows.Wind)} at {flows.WindTemperature:0.0} °C");
        if (flows.ForestDensity > 0) sb.Append(c, $"   forest {flows.ForestDensity:0.00}");
        sb.Append(c, $"   sun {flows.SolarWatts:+0;-0;0} W");
        if (flows.Draft is { Blocked: false } drawing) sb.Append(c, $"   flue {drawing.Height} m");
        else if (flows.Draft != null) sb.Append("   flue blocked");
        if (flows.Smoke > 0.1 || flows.SmokeSources > 0) sb.Append(c, $"   smoke {Chimney.Level(flows.Smoke)}");
        sb.AppendLine();
        // Faces count heat leaving as positive; the player reads a room that loses heat as negative.
        sb.Append(c, $"Floor {Local(flows, floorY):0.0} °C   eyes {Local(flows, eyeY):0.0} °C   " +
                     $"ceiling {Local(flows, ceilingY):0.0} °C   net {-watts:0} W");
        if (body != null) sb.Append(c, $"   body {body:0.0} °C");
        return sb.ToString();
    }

    private static double Local(RoomFlows flows, int y) =>
        Stratification.At(flows.Temperature, flows.Gradient, y, flows.YMid);
}

/// <summary>The hotkey, the text box and the puffs. The room itself is measured server side.</summary>
public class OverlayClient : ModSystem
{
    private OverlayHud hud = null!;

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

    public override void StartClientSide(ICoreClientAPI api)
    {
        hud = new OverlayHud(api);
        api.Gui.RegisterDialog(hud);
        api.Network.RegisterChannel("caminus")
            .RegisterMessageType<OverlayPacket>()
            .SetMessageHandler<OverlayPacket>(packet =>
            {
                hud.Set(packet.Text);
                foreach (ParticleFace f in packet.Faces) Spawn(api, f);
            });
        // K is free in 1.22.7: no vanilla hotkey uses it, with or without a modifier.
        api.Input.RegisterHotKey("caminusoverlay", "Caminus thermal overlay", GlKeys.K, HotkeyType.HelpAndOverlays);
        // The rooms live on the server and it already has the command, so the key just types it.
        // That saves a second message type and lets /caminus overlay work from a vanilla client too.
        api.Input.SetHotKeyHandler("caminusoverlay", _ => { api.SendChatMessage("/caminus overlay"); return true; });
    }

    /// <summary>One puff, in this client's world only. Everything but the shape is already decided.</summary>
    private static void Spawn(ICoreClientAPI api, ParticleFace f)
    {
        if (f.Flue)
        {
            var up = new Vec3f(0, f.Speed, 0);
            api.World.SpawnParticles(new SimpleParticleProperties(1, 2, f.Color,
                new Vec3d(f.X + 0.35, f.Y + 0.2, f.Z + 0.35), new Vec3d(f.X + 0.65, f.Y + 0.4, f.Z + 0.65),
                up, up, 1.5f, 0f, 0.2f, 0.4f, EnumParticleModel.Quad)
            {
                WithTerrainCollision = false,
            });
            return;
        }
        Vec3i n = BlockFacing.ALLFACES[f.Facing].Normali;
        // The wall block is at (X, Y, Z) and the air block is one step back along the normal, so the
        // face plane sits at pos + 0.5 - 0.5n; another 0.15 back puts the particles in the room.
        var mid = new Vec3d(f.X + 0.5 - 0.65 * n.X, f.Y + 0.5 - 0.65 * n.Y, f.Z + 0.5 - 0.65 * n.Z);
        var spread = new Vec3d(0.3 * (1 - Math.Abs(n.X)), 0.3 * (1 - Math.Abs(n.Y)), 0.3 * (1 - Math.Abs(n.Z)));
        var velocity = new Vec3f(n.X * f.Speed, n.Y * f.Speed, n.Z * f.Speed);
        api.World.SpawnParticles(new SimpleParticleProperties(2, 4, f.Color,
            new Vec3d(mid.X - spread.X, mid.Y - spread.Y, mid.Z - spread.Z),
            new Vec3d(mid.X + spread.X, mid.Y + spread.Y, mid.Z + spread.Z),
            velocity, velocity, 1.2f, 0f, 0.15f, 0.3f, EnumParticleModel.Quad)
        {
            // A face particle starts a hand's width from a solid block and flies at it.
            WithTerrainCollision = false,
        });
    }
}

/// <summary>Three lines in the top left corner, hidden while the server sends nothing.</summary>
public class OverlayHud : HudElement
{
    /// <summary>Stable identifier of this dialog for test harnesses. The dynamic text component is named "text".</summary>
    public const string DialogKey = "caminus:overlayhud";

    /// <summary>
    /// Deliberately null: the K hotkey is handled by OverlayClient, which asks the server to toggle
    /// the overlay; letting the dialog toggle itself on the same key would open an empty HUD.
    /// </summary>
    public override string ToggleKeyCombinationCode => null!;

    public OverlayHud(ICoreClientAPI capi) : base(capi) { }

    public void Set(string text)
    {
        if (text.Length == 0)
        {
            if (IsOpened()) TryClose();
            return;
        }
        // Composed on the first packet rather than at startup: the player is in the world by then,
        // which is what HudElementCoordinates waits for with OnOwnPlayerDataReceived.
        if (SingleComposer == null)
        {
            // Wide enough for the longest line in medium text, tall enough for a wrapped one.
            ElementBounds text3Lines = ElementBounds.Fixed(0, 0, 760, 110);
            SingleComposer = capi.Gui
                .CreateCompo("caminusoverlay", ElementStdBounds.AutosizedMainDialog
                    .WithAlignment(EnumDialogArea.LeftTop)
                    .WithFixedAlignmentOffset(GuiStyle.DialogToScreenPadding, GuiStyle.DialogToScreenPadding))
                .AddGameOverlay(text3Lines.ForkBoundingParent(5, 5, 5, 5))
                .AddDynamicText("", CairoFont.WhiteMediumText(), text3Lines, "text")
                .Compose();
        }
        SingleComposer.GetDynamicText("text").SetNewText(text);
        if (!IsOpened()) TryOpen();
    }
}
