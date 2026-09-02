namespace Caminus.Core;

/// <summary>
/// Réseau thermique nodal RC. Unités SI : capacité en J/K, conductance en W/K, puissance en W,
/// température en °C, temps en s. Les nœuds fixes (extérieur, terre profonde) imposent leur
/// température (condition de Dirichlet). L'intégration est en Euler implicite : inconditionnellement
/// stable quel que soit le pas ou la raideur des conductances.
/// </summary>
public sealed class ThermalNetwork
{
    private readonly List<double> _capacity = [];
    private readonly List<double> _temperature = [];
    private readonly List<double> _source = [];
    private readonly List<bool> _fixed = [];
    private readonly List<int> _edgeA = [];
    private readonly List<int> _edgeB = [];
    private readonly List<double> _conductance = [];

    // Système (C/dt + L) T⁺ = (C/dt) T + Q, alloué une fois puis réutilisé entre les Step.
    private double[,] _m = new double[0, 0];
    private double[] _rhs = [];

    public int NodeCount => _capacity.Count;
    public int EdgeCount => _conductance.Count;

    /// <summary>Ajoute un nœud à capacité finie. Retourne son index.</summary>
    public int AddNode(double heatCapacity, double temperature)
    {
        RequirePositive(heatCapacity, nameof(heatCapacity));
        _capacity.Add(heatCapacity);
        _temperature.Add(temperature);
        _source.Add(0);
        _fixed.Add(false);
        return _capacity.Count - 1;
    }

    /// <summary>Ajoute un nœud à température imposée. Retourne son index.</summary>
    public int AddFixedNode(double temperature)
    {
        _capacity.Add(0);
        _temperature.Add(temperature);
        _source.Add(0);
        _fixed.Add(true);
        return _capacity.Count - 1;
    }

    public bool IsFixed(int node) => _fixed[Check(node, NodeCount, nameof(node))];
    public double GetTemperature(int node) => _temperature[Check(node, NodeCount, nameof(node))];

    /// <summary>Nœud fixe : nouvelle valeur imposée. Nœud libre : réinitialise l'état.</summary>
    public void SetTemperature(int node, double temperature) => _temperature[Check(node, NodeCount, nameof(node))] = temperature;

    public double GetHeatCapacity(int node) => _capacity[Check(node, NodeCount, nameof(node))];

    public void SetHeatCapacity(int node, double heatCapacity)
    {
        Check(node, NodeCount, nameof(node));
        if (_fixed[node]) return;
        RequirePositive(heatCapacity, nameof(heatCapacity));
        _capacity[node] = heatCapacity;
    }

    /// <summary>Puissance injectée dans le nœud (positive = chauffage). Sans effet sur un nœud fixe.</summary>
    public void SetSourcePower(int node, double watts)
    {
        Check(node, NodeCount, nameof(node));
        if (!_fixed[node]) _source[node] = watts;
    }

    public double GetSourcePower(int node) => _source[Check(node, NodeCount, nameof(node))];

    /// <summary>Ajoute une conductance entre deux nœuds. Retourne l'index de l'arête.</summary>
    public int AddEdge(int a, int b, double conductance)
    {
        Check(a, NodeCount, nameof(a));
        Check(b, NodeCount, nameof(b));
        RequireNonNegative(conductance, nameof(conductance));
        _edgeA.Add(a);
        _edgeB.Add(b);
        _conductance.Add(conductance);
        return _conductance.Count - 1;
    }

    public void SetEdgeConductance(int edge, double conductance)
    {
        Check(edge, EdgeCount, nameof(edge));
        RequireNonNegative(conductance, nameof(conductance));
        _conductance[edge] = conductance;
    }

    public double GetEdgeConductance(int edge) => _conductance[Check(edge, EdgeCount, nameof(edge))];

    public (int A, int B) GetEdgeNodes(int edge) => (_edgeA[Check(edge, EdgeCount, nameof(edge))], _edgeB[edge]);

    /// <summary>Flux à l'état courant, en W, positif de A vers B.</summary>
    public double GetEdgeHeatFlow(int edge)
    {
        Check(edge, EdgeCount, nameof(edge));
        return _conductance[edge] * (_temperature[_edgeA[edge]] - _temperature[_edgeB[edge]]);
    }

    /// <summary>Avance l'état de dt secondes (Euler implicite, résolution d'un système linéaire dense).</summary>
    public void Step(double dtSeconds)
    {
        RequirePositive(dtSeconds, nameof(dtSeconds));
        int n = NodeCount;
        if (n == 0) return;
        if (_rhs.Length != n) { _m = new double[n, n]; _rhs = new double[n]; }
        Array.Clear(_m);

        for (int i = 0; i < n; i++)
        {
            if (_fixed[i]) { _m[i, i] = 1; _rhs[i] = _temperature[i]; }
            else { _m[i, i] = _capacity[i] / dtSeconds; _rhs[i] = _capacity[i] / dtSeconds * _temperature[i] + _source[i]; }
        }
        for (int e = 0; e < EdgeCount; e++)
        {
            int a = _edgeA[e], b = _edgeB[e];
            double g = _conductance[e];
            if (!_fixed[a]) { _m[a, a] += g; _m[a, b] -= g; }
            if (!_fixed[b]) { _m[b, b] += g; _m[b, a] -= g; }
        }

        SolveInPlace(n);
        // Les nœuds fixes gardent leur valeur exacte : pas de bruit d'arrondi du solveur.
        for (int i = 0; i < n; i++) if (!_fixed[i]) _temperature[i] = _rhs[i];
    }

    /// <summary>Élimination de Gauss avec pivot partiel sur (_m, _rhs) ; la solution atterrit dans _rhs.</summary>
    private void SolveInPlace(int n)
    {
        for (int k = 0; k < n; k++)
        {
            int p = k;
            for (int i = k + 1; i < n; i++) if (Math.Abs(_m[i, k]) > Math.Abs(_m[p, k])) p = i;
            if (p != k)
            {
                for (int j = k; j < n; j++) (_m[k, j], _m[p, j]) = (_m[p, j], _m[k, j]);
                (_rhs[k], _rhs[p]) = (_rhs[p], _rhs[k]);
            }
            double pivot = _m[k, k];
            if (pivot == 0) throw new InvalidOperationException("Réseau thermique singulier : un nœud libre sans capacité ni conductance ?");
            for (int i = k + 1; i < n; i++)
            {
                double f = _m[i, k] / pivot;
                if (f == 0) continue;
                for (int j = k; j < n; j++) _m[i, j] -= f * _m[k, j];
                _rhs[i] -= f * _rhs[k];
            }
        }
        for (int i = n - 1; i >= 0; i--)
        {
            double s = _rhs[i];
            for (int j = i + 1; j < n; j++) s -= _m[i, j] * _rhs[j];
            _rhs[i] = s / _m[i, i];
        }
    }

    private static int Check(int index, int count, string name) =>
        index >= 0 && index < count ? index : throw new ArgumentOutOfRangeException(name);

    private static void RequirePositive(double v, string name)
    {
        if (!(v > 0)) throw new ArgumentOutOfRangeException(name, v, "doit être strictement positif");
    }

    private static void RequireNonNegative(double v, string name)
    {
        if (!(v >= 0)) throw new ArgumentOutOfRangeException(name, v, "doit être positif ou nul");
    }
}
