using System.Globalization;
using System.Text;
using Caminus.Core;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Caminus;

/// <summary>The HUD lines, already formatted server side. Empty text means hide the HUD.</summary>
[ProtoContract]
public class OverlayPacket
{
    [ProtoMember(1)] public string Text { get; set; } = "";
}

/// <summary>
/// Draws the room a player stands in: one coloured cube per envelope block, particles along the
/// loudest flows, three lines of numbers.
/// The geometry never travels through a channel of ours. <c>IWorldAccessor.HighlightBlocks</c> and
/// <c>SpawnParticles</c> are already server-to-client on the server side (ServerMain.cs:3359 and
/// 3070 send their own packets), so the only thing left to send is the text, and a client that does
/// not have Caminus installed still gets the colours and the flow.
/// </summary>
public class OverlayServer : ModSystem
{
    /// <summary>Highlight slot. Vanilla holds 1, 2, 26, 27, 50, 941, 942 and 1292.</summary>
    private const int Slot = 7;
    /// <summary>How many faces get flow particles, loudest first.</summary>
    private const int ParticleFaces = 16;
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
        sapi.World.HighlightBlocks(player, Slot, []);
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
            sapi.World.HighlightBlocks(player, Slot, blocks, colors);
            SpawnFlowParticles(flows);
            channel.SendPacket(new OverlayPacket { Text = Describe(flows, pos.Y) }, player);
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
        return (blocks, colors);
    }

    /// <summary>Red when heat leaves the room, blue when it comes in; brown when buried, cyan for an opening.</summary>
    private static (int R, int G, int B) Rgb(in FaceFlow f)
    {
        bool loss = f.Watts >= 0;
        if (f.Face.Opening) return loss ? (0, 220, 220) : (0, 150, 255);
        if (f.Face.Ground) return loss ? (170, 110, 40) : (120, 90, 60);
        return loss ? (255, 50, 40) : (40, 90, 255);
    }

    /// <summary>Share of the room's loudest face, floored so that every face stays on screen.</summary>
    private static double Alpha(double watts, double max) =>
        max <= 0 ? MinAlpha : Math.Clamp(Math.Abs(watts) / max, MinAlpha, 1);

    /// <summary>
    /// A puff of quads over the middle of each of the loudest faces, moving along the face normal:
    /// out of the room where heat leaves it, in where it comes back.
    /// </summary>
    // ponytail: the server has no per-player SpawnParticles, so a second player standing in the same
    // room sees someone else's overlay. Put the top faces in OverlayPacket and spawn them client side
    // if that ever bothers anyone.
    private void SpawnFlowParticles(RoomFlows flows)
    {
        double max = 0;
        foreach (FaceFlow f in flows.Faces) max = Math.Max(max, Math.Abs(f.Watts));
        if (max <= 0) return;

        foreach (FaceFlow f in flows.Faces.OrderByDescending(x => Math.Abs(x.Watts)).Take(ParticleFaces))
        {
            Vec3i n = f.Face.Facing.Normali;
            // Face.Pos is the wall block and the air block is one step back along the normal, so the
            // face plane sits at Pos + 0.5 - 0.5n; another 0.15 back puts the particles in the room.
            var mid = new Vec3d(f.Face.Pos.X + 0.5 - 0.65 * n.X, f.Face.Pos.Y + 0.5 - 0.65 * n.Y, f.Face.Pos.Z + 0.5 - 0.65 * n.Z);
            var spread = new Vec3d(0.3 * (1 - Math.Abs(n.X)), 0.3 * (1 - Math.Abs(n.Y)), 0.3 * (1 - Math.Abs(n.Z)));
            float speed = (float)(0.15 + 0.45 * Math.Abs(f.Watts) / max) * (f.Watts >= 0 ? 1 : -1);
            var velocity = new Vec3f(n.X * speed, n.Y * speed, n.Z * speed);
            (int r, int g, int b) = Rgb(f);

            sapi.World.SpawnParticles(new SimpleParticleProperties(2, 4,
                // Particles read the colour as BGRA, walls as RGBA (ColorUtil.cs:275-281).
                ColorUtil.ToRgba((int)(255 * Alpha(f.Watts, max)), r, g, b),
                new Vec3d(mid.X - spread.X, mid.Y - spread.Y, mid.Z - spread.Z),
                new Vec3d(mid.X + spread.X, mid.Y + spread.Y, mid.Z + spread.Z),
                velocity, velocity, 1.2f, 0f, 0.15f, 0.3f, EnumParticleModel.Quad)
            {
                // A face particle starts a hand's width from a solid block and flies at it.
                WithTerrainCollision = false,
            });
        }
    }

    /// <summary>
    /// The three HUD lines. <paramref name="eyeY"/> is the block the player is looking out of.
    /// Public and pure, same reason as <see cref="Highlights"/>.
    /// </summary>
    public static string Describe(RoomFlows flows, int eyeY)
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
        sb.Append(c, $"   sun {flows.SolarWatts:+0;-0;0} W").AppendLine();
        // Faces count heat leaving as positive; the player reads a room that loses heat as negative.
        sb.Append(c, $"Floor {Local(flows, floorY):0.0} °C   eyes {Local(flows, eyeY):0.0} °C   " +
                     $"ceiling {Local(flows, ceilingY):0.0} °C   net {-watts:0} W");
        return sb.ToString();
    }

    private static double Local(RoomFlows flows, int y) =>
        Stratification.At(flows.Temperature, flows.Gradient, y, flows.YMid);
}

/// <summary>The hotkey and the text box. Everything else about the overlay is server side.</summary>
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
            .SetMessageHandler<OverlayPacket>(packet => hud.Set(packet.Text));
        // K is free in 1.22.7: no vanilla hotkey uses it, with or without a modifier.
        api.Input.RegisterHotKey("caminusoverlay", "Caminus thermal overlay", GlKeys.K, HotkeyType.HelpAndOverlays);
        // The rooms live on the server and it already has the command, so the key just types it.
        // That saves a second message type and lets /caminus overlay work from a vanilla client too.
        api.Input.SetHotKeyHandler("caminusoverlay", _ => { api.SendChatMessage("/caminus overlay"); return true; });
    }
}

/// <summary>Three lines in the top left corner, hidden while the server sends nothing.</summary>
public class OverlayHud : HudElement
{
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
            ElementBounds text3Lines = ElementBounds.Fixed(0, 0, 420, 66);
            SingleComposer = capi.Gui
                .CreateCompo("caminusoverlay", ElementStdBounds.AutosizedMainDialog
                    .WithAlignment(EnumDialogArea.LeftTop)
                    .WithFixedAlignmentOffset(GuiStyle.DialogToScreenPadding, GuiStyle.DialogToScreenPadding))
                .AddGameOverlay(text3Lines.ForkBoundingParent(5, 5, 5, 5))
                .AddDynamicText("", CairoFont.WhiteSmallishText(), text3Lines, "text")
                .Compose();
        }
        SingleComposer.GetDynamicText("text").SetNewText(text);
        if (!IsOpened()) TryOpen();
    }
}
