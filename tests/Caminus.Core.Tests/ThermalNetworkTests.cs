using Caminus.Core;

namespace Caminus.Core.Tests;

public class ThermalNetworkTests
{
    // 1. Équilibre analytique : un nœud libre entre deux fixes, avec source.
    [Fact]
    public void Equilibre_ConvergeVersLaMoyennePondereeAvecSource()
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

    // 2. Constante de temps : après t = τ, l'écart est réduit d'un facteur e.
    [Fact]
    public void ConstanteDeTemps_ApresTau_EcartDiviseParE()
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

    // 3. Stabilité : un pas énorme reste borné et ne diverge pas.
    [Fact]
    public void GrandPas_UnSeulStep_ResteBorneEtProcheDuFixe()
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

    // 4. Conservation : système fermé, Σ C·T invariant, convergence vers la moyenne pondérée.
    [Fact]
    public void SystemeFerme_ConserveLEnergieEtConvergeVersLaMoyenne()
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

    // 5. Nœud fixe : immuable, et insensible à SetSourcePower.
    [Fact]
    public void NoeudFixe_NeBougePasEtIgnoreLesSources()
    {
        var net = new ThermalNetwork();
        int f = net.AddFixedNode(-10);
        int n = net.AddNode(1000, 20);
        net.AddEdge(n, f, 5);

        net.SetSourcePower(f, 1e9);
        Assert.Equal(0.0, net.GetSourcePower(f));

        for (int i = 0; i < 100; i++) net.Step(1);
        Assert.Equal(-10.0, net.GetTemperature(f));

        // Référence sans la source parasite : le reste du réseau est identique.
        var temoin = new ThermalNetwork();
        int ft = temoin.AddFixedNode(-10);
        int nt = temoin.AddNode(1000, 20);
        temoin.AddEdge(nt, ft, 5);
        for (int i = 0; i < 100; i++) temoin.Step(1);
        Assert.Equal(temoin.GetTemperature(nt), net.GetTemperature(n), 1e-12);
    }

    // 6. Flux : signe, valeur, et cohérence avec la conductance courante.
    [Fact]
    public void FluxArete_SigneEtValeur()
    {
        var net = new ThermalNetwork();
        int chaud = net.AddNode(1000, 30);
        int froid = net.AddNode(1000, 10);
        int e = net.AddEdge(chaud, froid, 2.5);

        Assert.Equal((chaud, froid), net.GetEdgeNodes(e));
        Assert.Equal(2.5 * 20, net.GetEdgeHeatFlow(e), 1e-12); // A plus chaud → flux positif

        net.SetEdgeConductance(e, 5);
        Assert.Equal(5.0, net.GetEdgeConductance(e));
        Assert.Equal(5 * 20, net.GetEdgeHeatFlow(e), 1e-12);

        // Sens inverse : flux négatif.
        var inverse = new ThermalNetwork();
        int a = inverse.AddNode(1000, 10);
        int b = inverse.AddNode(1000, 30);
        int e2 = inverse.AddEdge(a, b, 2.5);
        Assert.Equal(-2.5 * 20, inverse.GetEdgeHeatFlow(e2), 1e-12);

        // Conductance nulle : pas de flux.
        inverse.SetEdgeConductance(e2, 0);
        Assert.Equal(0.0, inverse.GetEdgeHeatFlow(e2));
    }

    // 7. Indépendance au pas : 100 s en pas de 1 s ou de 0,1 s, à 2 % près.
    [Fact]
    public void ResultatQuasiIndependantDuPas()
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

    // 8. Arguments invalides.
    [Fact]
    public void ArgumentsInvalides_Levent()
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

    // Une arête entre deux nœuds fixes est acceptée et sans effet.
    [Fact]
    public void AreteEntreDeuxFixes_AccepteeEtSansEffet()
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
        Assert.Equal(0, net.GetTemperature(n), 1e-6); // n ne voit que f1
    }
}
