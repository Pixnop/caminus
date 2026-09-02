using Caminus.Core;

namespace Caminus.Core.Tests;

public class ThermalNetworkTests
{
    // 1. Analytical equilibrium: one free node between two fixed nodes, with a source.
    [Fact]
    public void Equilibrium_ConvergesToWeightedMeanWithSource()
    {
        const double t1 = -5, t2 = 20, g1 = 3, g2 = 7, q = 40;
        var net = new ThermalNetwork();
        int f1 = net.AddFixedNode(t1);
        int f2 = net.AddFixedNode(t2);
        int n = net.AddNode(5000, 100);
        net.AddEdge(n, f1, g1);
        net.AddEdge(n, f2, g2);
        net.SetSourcePower(n, q);

        for (int i = 0; i < 2000; i++) net.Step(60);

        Assert.Equal((g1 * t1 + g2 * t2 + q) / (g1 + g2), net.GetTemperature(n), 1e-6);
    }

    // 2. Time constant: after t = τ, the gap is reduced by a factor of e.
    [Fact]
    public void TimeConstant_AfterTau_GapDividedByE()
    {
        const double c = 2000, g = 4, t0 = 30, tf = 10;
        double tau = c / g;
        var net = new ThermalNetwork();
        int f = net.AddFixedNode(tf);
        int n = net.AddNode(c, t0);
        net.AddEdge(n, f, g);

        for (int i = 0; i < 1000; i++) net.Step(tau / 1000);

        double attendu = tf + (t0 - tf) * Math.Exp(-1);
        Assert.Equal(attendu, net.GetTemperature(n), 0.01 * Math.Abs(attendu));
    }

    // 3. Stability: a huge step stays bounded and does not diverge.
    [Fact]
    public void HugeStep_SingleStep_StaysBoundedAndCloseToFixed()
    {
        const double c = 2000, g = 4, t0 = 30, tf = 10;
        double tau = c / g;
        var net = new ThermalNetwork();
        int f = net.AddFixedNode(tf);
        int n = net.AddNode(c, t0);
        net.AddEdge(n, f, g);

        net.Step(1e6 * tau);

        double t = net.GetTemperature(n);
        Assert.True(double.IsFinite(t));
        Assert.InRange(t, tf, t0);
        Assert.Equal(tf, t, 1e-3);
    }

    // 4. Conservation: closed system, Σ C·T invariant, converges to the weighted mean.
    [Fact]
    public void ClosedSystem_ConservesEnergyAndConvergesToTheMean()
    {
        const double ca = 1000, cb = 3000, ta = 50, tb = 10;
        var net = new ThermalNetwork();
        int a = net.AddNode(ca, ta);
        int b = net.AddNode(cb, tb);
        net.AddEdge(a, b, 20);

        double e0 = ca * ta + cb * tb;
        for (int i = 0; i < 1000; i++)
        {
            net.Step(1);
            double e = ca * net.GetTemperature(a) + cb * net.GetTemperature(b);
            Assert.Equal(e0, e, 1e-9 * Math.Abs(e0));
        }

        double moyenne = e0 / (ca + cb);
        Assert.Equal(moyenne, net.GetTemperature(a), 1e-6);
        Assert.Equal(moyenne, net.GetTemperature(b), 1e-6);
    }

    // 5. Fixed node: immutable, and unaffected by SetSourcePower.
    [Fact]
    public void FixedNode_DoesNotMoveAndIgnoresSources()
    {
        var net = new ThermalNetwork();
        int f = net.AddFixedNode(-10);
        int n = net.AddNode(1000, 20);
        net.AddEdge(n, f, 5);

        net.SetSourcePower(f, 1e9);
        Assert.Equal(0.0, net.GetSourcePower(f));

        for (int i = 0; i < 100; i++) net.Step(1);
        Assert.Equal(-10.0, net.GetTemperature(f));

        // Reference without the parasitic source: the rest of the network is identical.
        var temoin = new ThermalNetwork();
        int ft = temoin.AddFixedNode(-10);
        int nt = temoin.AddNode(1000, 20);
        temoin.AddEdge(nt, ft, 5);
        for (int i = 0; i < 100; i++) temoin.Step(1);
        Assert.Equal(temoin.GetTemperature(nt), net.GetTemperature(n), 1e-12);
    }

    // 6. Flow: sign, value, and consistency with the current conductance.
    [Fact]
    public void EdgeFlow_SignAndValue()
    {
        var net = new ThermalNetwork();
        int chaud = net.AddNode(1000, 30);
        int froid = net.AddNode(1000, 10);
        int e = net.AddEdge(chaud, froid, 2.5);

        Assert.Equal((chaud, froid), net.GetEdgeNodes(e));
        Assert.Equal(2.5 * 20, net.GetEdgeHeatFlow(e), 1e-12); // A hotter → positive flow

        net.SetEdgeConductance(e, 5);
        Assert.Equal(5.0, net.GetEdgeConductance(e));
        Assert.Equal(5 * 20, net.GetEdgeHeatFlow(e), 1e-12);

        // Reverse direction: negative flow.
        var inverse = new ThermalNetwork();
        int a = inverse.AddNode(1000, 10);
        int b = inverse.AddNode(1000, 30);
        int e2 = inverse.AddEdge(a, b, 2.5);
        Assert.Equal(-2.5 * 20, inverse.GetEdgeHeatFlow(e2), 1e-12);

        // Zero conductance: no flow.
        inverse.SetEdgeConductance(e2, 0);
        Assert.Equal(0.0, inverse.GetEdgeHeatFlow(e2));
    }

    // 7. Step independence: 100 s in steps of 1 s or 0.1 s, within 2%.
    [Fact]
    public void ResultAlmostIndependentOfStepSize()
    {
        static double Simule(double dt)
        {
            var net = new ThermalNetwork();
            int f = net.AddFixedNode(20);
            int n = net.AddNode(1000, 0);
            net.AddEdge(n, f, 10);
            net.SetSourcePower(n, 50);
            for (int i = 0; i < (int)Math.Round(100 / dt); i++) net.Step(dt);
            return net.GetTemperature(n);
        }

        double gros = Simule(1), fin = Simule(0.1);
        Assert.Equal(fin, gros, 0.02 * Math.Abs(fin));
    }

    // 8. Invalid arguments.
    [Fact]
    public void InvalidArguments_Throw()
    {
        var net = new ThermalNetwork();
        int f = net.AddFixedNode(0);
        int n = net.AddNode(100, 0);

        Assert.Throws<ArgumentOutOfRangeException>(() => net.AddNode(0, 20));
        Assert.Throws<ArgumentOutOfRangeException>(() => net.AddNode(-1, 20));
        Assert.Throws<ArgumentOutOfRangeException>(() => net.SetHeatCapacity(n, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => net.AddEdge(n, f, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => net.SetEdgeConductance(net.AddEdge(n, f, 1), -0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => net.Step(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => net.Step(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => net.GetTemperature(42));
        Assert.Throws<ArgumentOutOfRangeException>(() => net.AddEdge(n, 42, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => net.GetEdgeConductance(99));
    }

    // An edge between two fixed nodes is accepted and has no effect.
    [Fact]
    public void EdgeBetweenTwoFixedNodes_AcceptedAndHasNoEffect()
    {
        var net = new ThermalNetwork();
        int f1 = net.AddFixedNode(0);
        int f2 = net.AddFixedNode(100);
        int n = net.AddNode(1000, 50);
        net.AddEdge(f1, f2, 1e6);
        net.AddEdge(n, f1, 5);

        for (int i = 0; i < 500; i++) net.Step(10);

        Assert.Equal(0.0, net.GetTemperature(f1));
        Assert.Equal(100.0, net.GetTemperature(f2));
        Assert.Equal(0, net.GetTemperature(n), 1e-6); // n only sees f1
    }

    // Offline relaxation: skipping 1000 steps analytically lands where integrating them does.
    // This is the round trip a room takes when its chunk unloads and comes back.
    [Fact]
    public void Relax_MatchesIntegratingTheSameSpan()
    {
        const double c = 150_000, gOut = 20, gGround = 34, tOut = -4, tGround = 9, q = 800, t0 = 21;
        var net = new ThermalNetwork();
        int outside = net.AddFixedNode(tOut);
        int ground = net.AddFixedNode(tGround);
        int room = net.AddNode(c, t0);
        net.AddEdge(room, outside, gOut);
        net.AddEdge(room, ground, gGround);
        net.SetSourcePower(room, q);

        const double dt = 5;
        for (int i = 0; i < 1000; i++) net.Step(dt);

        double g = gOut + gGround;
        double teq = (gOut * tOut + gGround * tGround + q) / g;
        double relaxed = ThermalNetwork.Relax(t0, teq, 1000 * dt, c / g);
        // Implicit Euler is first order, so the two only agree to within the step's own error.
        Assert.Equal(relaxed, net.GetTemperature(room), 0.01);
    }

    // Degenerate spans: no elapsed time changes nothing, a very long one lands on equilibrium.
    [Fact]
    public void Relax_ZeroSpanKeepsState_LongSpanReachesEquilibrium()
    {
        Assert.Equal(21.0, ThermalNetwork.Relax(21, 5, 0, 3000));
        Assert.Equal(5.0, ThermalNetwork.Relax(21, 5, 1e9, 3000), 1e-9);
    }
}
