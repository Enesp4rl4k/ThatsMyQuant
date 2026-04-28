using System;
using System.Collections.Generic;
using System.Linq;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Hierarchical Risk Parity (HRP) portfolio construction in the sense of Lopez de
/// Prado (2016). Three-stage algorithm that produces a diversified, well-conditioned
/// portfolio without inverting the covariance matrix:
///
///   1. <b>Tree clustering</b>: build a single-linkage hierarchical tree from the
///      correlation distance matrix d_ij = sqrt(0.5 · (1 - ρ_ij)).
///   2. <b>Quasi-diagonalization</b>: reorder assets following the tree leaves so
///      similar assets are adjacent in the resulting weight vector.
///   3. <b>Recursive bisection</b>: at each split, allocate inversely to cluster
///      variance using inverse-variance weights within each sub-cluster.
///
/// References:
///   Lopez de Prado (2016), "Building Diversified Portfolios that Outperform
///     Out-of-Sample", Journal of Portfolio Management 42(4): 59-69.
///   Lopez de Prado (2018), "Advances in Financial Machine Learning", Ch. 16.
///
/// Why HRP over Kelly / Markowitz:
///   • No matrix inversion → no condition-number explosion when N approaches T.
///   • Cluster-aware diversification: similar strategies (e.g., two highly
///     correlated pairs) share allocation rather than each getting full weight.
///   • Out-of-sample variance typically 15-25% lower than minimum-variance
///     optimization, with comparable expected return.
///   • Robust to estimation error: small covariance perturbations don't flip
///     allocations (mean-variance is famously unstable to inputs).
///
///   Used in upgrade plan: Phase B1 — replaces per-pair static Kelly weights
///   with a portfolio-aware allocation that downsizes correlated trades.
///
/// Inputs: covariance matrix Σ (preferably from <see cref="LedoitWolfShrinkage"/>
/// for stability) and asset labels. Output: weight vector w with Σ w_i = 1, w_i ≥ 0.
/// </summary>
public static class HierarchicalRiskParity
{
    /// <summary>Result bundle from an HRP allocation.</summary>
    /// <param name="Weights">Weights aligned to the original asset order (sums to 1).</param>
    /// <param name="QuasiDiagonalOrder">Reordered asset indices from the clustering pass —
    /// useful for diagnostics / visualization (assets near each other in this order
    /// are correlated).</param>
    /// <param name="ClusterMerges">Cluster-merge log from single-linkage (left, right, distance).</param>
    public sealed record HrpResult(
        double[] Weights,
        int[] QuasiDiagonalOrder,
        IReadOnlyList<(int Left, int Right, double Distance)> ClusterMerges);

    /// <summary>
    /// Compute HRP weights from a covariance matrix.
    /// </summary>
    /// <param name="covariance">[N×N] covariance matrix, symmetric, positive semi-definite.
    /// Pass the shrunk covariance from <see cref="LedoitWolfShrinkage"/> for best results.</param>
    public static HrpResult Allocate(double[,] covariance)
    {
        ArgumentNullException.ThrowIfNull(covariance);
        int n = covariance.GetLength(0);
        if (n == 0 || covariance.GetLength(1) != n)
            throw new ArgumentException("Covariance matrix must be square and non-empty.", nameof(covariance));

        if (n == 1)
        {
            return new HrpResult(new double[] { 1.0 }, new int[] { 0 },
                Array.Empty<(int, int, double)>());
        }

        // 1. Build correlation matrix and distance matrix.
        var corr = new double[n, n];
        var sd = new double[n];
        for (int i = 0; i < n; i++) sd[i] = Math.Sqrt(Math.Max(covariance[i, i], 0.0));
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                double denom = sd[i] * sd[j];
                corr[i, j] = denom > 0 ? Math.Max(-1.0, Math.Min(1.0, covariance[i, j] / denom)) : 0.0;
            }
        }

        var distance = new double[n, n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                // Mantegna distance: d_ij = sqrt(0.5 · (1 − ρ)).
                distance[i, j] = Math.Sqrt(Math.Max(0.0, 0.5 * (1.0 - corr[i, j])));
            }
        }

        // 2. Hierarchical clustering (single linkage). Produces a merge tree.
        var (merges, leafOrder) = SingleLinkageClustering(distance);

        // 3. Recursive bisection in the quasi-diagonal order.
        var weights = new double[n];
        Array.Fill(weights, 1.0);
        var clusters = new List<int[]> { leafOrder.ToArray() };

        while (clusters.Count > 0)
        {
            var nextRound = new List<int[]>();
            foreach (var cluster in clusters)
            {
                if (cluster.Length <= 1) continue;
                int half = cluster.Length / 2;
                var left = cluster.Take(half).ToArray();
                var right = cluster.Skip(half).ToArray();

                double leftVar = ClusterVariance(covariance, left);
                double rightVar = ClusterVariance(covariance, right);
                double total = leftVar + rightVar;
                double alpha = total > 0 ? 1.0 - leftVar / total : 0.5;

                foreach (var idx in left) weights[idx] *= alpha;
                foreach (var idx in right) weights[idx] *= (1.0 - alpha);

                nextRound.Add(left);
                nextRound.Add(right);
            }
            clusters = nextRound;
        }

        // Numerical normalization (should already sum to 1 within float precision).
        double sum = 0.0;
        for (int i = 0; i < n; i++) sum += weights[i];
        if (sum > 0)
        {
            double inv = 1.0 / sum;
            for (int i = 0; i < n; i++) weights[i] *= inv;
        }

        return new HrpResult(weights, leafOrder, merges);
    }

    /// <summary>
    /// Single-linkage agglomerative clustering. At each step, merges the two
    /// clusters with smallest pairwise distance (using min-distance among members).
    /// Returns the merge log and the leaf order resulting from a depth-first traversal.
    /// </summary>
    private static (List<(int Left, int Right, double Distance)> merges, int[] leafOrder)
        SingleLinkageClustering(double[,] distance)
    {
        int n = distance.GetLength(0);
        // Each cluster is represented by an id ≥ 0 (original asset) or ≥ n (synthetic).
        // members[id] gives the leaves under that cluster.
        var members = new Dictionary<int, List<int>>();
        for (int i = 0; i < n; i++) members[i] = new List<int> { i };

        // Active cluster ids and pairwise distances (Lance-Williams update).
        var active = new HashSet<int>(Enumerable.Range(0, n));
        var dist = new Dictionary<(int, int), double>();
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
                dist[(i, j)] = distance[i, j];

        var merges = new List<(int Left, int Right, double Distance)>();
        int nextId = n;

        while (active.Count > 1)
        {
            // Find the closest pair among active clusters.
            double minD = double.PositiveInfinity;
            int a = -1, b = -1;
            foreach (var kvp in dist)
            {
                if (!active.Contains(kvp.Key.Item1) || !active.Contains(kvp.Key.Item2)) continue;
                if (kvp.Value < minD) { minD = kvp.Value; a = kvp.Key.Item1; b = kvp.Key.Item2; }
            }
            if (a < 0) break;

            // Merge a, b into nextId.
            int newId = nextId++;
            var merged = new List<int>(members[a]);
            merged.AddRange(members[b]);
            members[newId] = merged;
            merges.Add((a, b, minD));

            active.Remove(a);
            active.Remove(b);

            // Single-linkage distance to other active clusters: min(d(a,k), d(b,k)).
            foreach (var k in active)
            {
                double dAk = LookupDistance(dist, a, k);
                double dBk = LookupDistance(dist, b, k);
                double dNew = Math.Min(dAk, dBk);
                int lo = Math.Min(newId, k);
                int hi = Math.Max(newId, k);
                dist[(lo, hi)] = dNew;
            }
            active.Add(newId);
        }

        // Build leaf order via DFS over the merge tree.
        var leafOrder = new List<int>();
        if (active.Count == 1)
        {
            int root = active.First();
            DfsLeaves(merges, members, root, n, leafOrder);
        }
        else
        {
            // Degenerate (n == 0 path); shouldn't happen after the early-exit above.
            for (int i = 0; i < n; i++) leafOrder.Add(i);
        }

        return (merges, leafOrder.ToArray());
    }

    private static double LookupDistance(Dictionary<(int, int), double> dist, int a, int b)
    {
        int lo = Math.Min(a, b);
        int hi = Math.Max(a, b);
        return dist.TryGetValue((lo, hi), out var d) ? d : double.PositiveInfinity;
    }

    private static void DfsLeaves(
        List<(int Left, int Right, double Distance)> merges,
        Dictionary<int, List<int>> members,
        int cluster,
        int leafThreshold,
        List<int> output)
    {
        if (cluster < leafThreshold)
        {
            output.Add(cluster);
            return;
        }
        // Find the merge that produced 'cluster' (synthetic id).
        int mergeIdx = cluster - leafThreshold;
        if (mergeIdx < 0 || mergeIdx >= merges.Count)
        {
            output.AddRange(members[cluster]);
            return;
        }
        var (left, right, _) = merges[mergeIdx];
        DfsLeaves(merges, members, left, leafThreshold, output);
        DfsLeaves(merges, members, right, leafThreshold, output);
    }

    /// <summary>
    /// Cluster variance under inverse-variance weighting:
    ///   w_i = (1/σ_ii) / Σ (1/σ_kk),   Var(cluster) = w' Σ w.
    /// This is the canonical HRP intra-cluster weighting that avoids matrix inversion.
    /// </summary>
    private static double ClusterVariance(double[,] cov, int[] indices)
    {
        int m = indices.Length;
        if (m == 0) return 0.0;

        // Inverse-variance weights.
        var w = new double[m];
        double sum = 0.0;
        for (int i = 0; i < m; i++)
        {
            double diag = cov[indices[i], indices[i]];
            w[i] = diag > 1e-15 ? 1.0 / diag : 0.0;
            sum += w[i];
        }
        if (sum <= 0) return 0.0;
        for (int i = 0; i < m; i++) w[i] /= sum;

        // w' Σ w
        double v = 0.0;
        for (int i = 0; i < m; i++)
        {
            for (int j = 0; j < m; j++)
            {
                v += w[i] * w[j] * cov[indices[i], indices[j]];
            }
        }
        return v;
    }
}
