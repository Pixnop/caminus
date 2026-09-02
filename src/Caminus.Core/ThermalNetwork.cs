namespace Caminus.Core;

/// <summary>
/// Réseau thermique nodal RC. Unités SI : capacité en J/K, conductance en W/K, puissance en W,
/// température en °C, temps en s. Les nœuds fixes (extérieur, terre profonde) imposent leur
/// température (condition de Dirichlet). L'intégration est en Euler implicite : inconditionnellement
/// stable quel que soit le pas ou la raideur des conductances.
/// </summary>
public sealed class ThermalNetwork
{
    public int NodeCount => throw new NotImplementedException();
    public int EdgeCount => throw new NotImplementedException();

    /// <summary>Ajoute un nœud à capacité finie. Retourne son index.</summary>
    public int AddNode(double heatCapacity, double temperature) => throw new NotImplementedException();

    /// <summary>Ajoute un nœud à température imposée. Retourne son index.</summary>
    public int AddFixedNode(double temperature) => throw new NotImplementedException();

    public bool IsFixed(int node) => throw new NotImplementedException();
    public double GetTemperature(int node) => throw new NotImplementedException();
    /// <summary>Nœud fixe : nouvelle valeur imposée. Nœud libre : réinitialise l'état.</summary>
    public void SetTemperature(int node, double temperature) => throw new NotImplementedException();
    public double GetHeatCapacity(int node) => throw new NotImplementedException();
    public void SetHeatCapacity(int node, double heatCapacity) => throw new NotImplementedException();
    /// <summary>Puissance injectée dans le nœud (positive = chauffage). Sans effet sur un nœud fixe.</summary>
    public void SetSourcePower(int node, double watts) => throw new NotImplementedException();
    public double GetSourcePower(int node) => throw new NotImplementedException();

    /// <summary>Ajoute une conductance entre deux nœuds. Retourne l'index de l'arête.</summary>
    public int AddEdge(int a, int b, double conductance) => throw new NotImplementedException();
    public void SetEdgeConductance(int edge, double conductance) => throw new NotImplementedException();
    public double GetEdgeConductance(int edge) => throw new NotImplementedException();
    public (int A, int B) GetEdgeNodes(int edge) => throw new NotImplementedException();
    /// <summary>Flux à l'état courant, en W, positif de A vers B.</summary>
    public double GetEdgeHeatFlow(int edge) => throw new NotImplementedException();

    /// <summary>Avance l'état de dt secondes (Euler implicite, résolution d'un système linéaire dense).</summary>
    public void Step(double dtSeconds) => throw new NotImplementedException();
}
