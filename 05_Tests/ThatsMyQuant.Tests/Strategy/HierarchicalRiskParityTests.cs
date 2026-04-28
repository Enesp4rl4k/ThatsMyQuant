using System;
using System.Linq;
using FluentAssertions;
using ThatsMyQuant.Application.Strategy;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

public class HierarchicalRiskParityTests
{
    [Fact]
    public void Allocate_NullCovariance_Throws()
    {
        Action act = () => HierarchicalRiskParity.Allocate(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Allocate_NonSquare_Throws()
    {
        var cov = new double[2, 3];
        Action act = () => HierarchicalRiskParity.Allocate(cov);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Allocate_SingleAsset_AllocatesAllToIt()
    {
        var cov = new double[,] { { 0.04 } };
        var result = HierarchicalRiskParity.Allocate(cov);
        result.Weights.Should().HaveCount(1);
        result.Weights[0].Should().Be(1.0);
    }

    [Fact]
    public void Allocate_WeightsSumToOne()
    {
        var returns = GenerateRandomReturns(seed: 5, t: 200, n: 8, vol: 0.02);
        var lw = LedoitWolfShrinkage.Estimate(returns);
        var hrp = HierarchicalRiskParity.Allocate(lw.Covariance);

        double sum = hrp.Weights.Sum();
        sum.Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public void Allocate_AllWeightsNonNegative()
    {
        var returns = GenerateRandomReturns(seed: 11, t: 200, n: 6, vol: 0.02);
        var lw = LedoitWolfShrinkage.Estimate(returns);
        var hrp = HierarchicalRiskParity.Allocate(lw.Covariance);

        foreach (var w in hrp.Weights)
        {
            w.Should().BeGreaterThanOrEqualTo(0.0);
        }
    }

    [Fact]
    public void Allocate_HighVarianceAsset_ReceivesLowerWeight()
    {
        // Construct: 3 assets with vols 0.01, 0.01, 0.05 — uncorrelated.
        // HRP should give the high-vol asset a smaller weight.
        var cov = new double[,]
        {
            { 0.01 * 0.01, 0.0,        0.0        },
            { 0.0,        0.01 * 0.01, 0.0        },
            { 0.0,        0.0,         0.05 * 0.05 },
        };
        var hrp = HierarchicalRiskParity.Allocate(cov);

        hrp.Weights[2].Should().BeLessThan(hrp.Weights[0],
            "high-variance asset must receive less weight");
        hrp.Weights[2].Should().BeLessThan(hrp.Weights[1],
            "high-variance asset must receive less weight");
    }

    [Fact]
    public void Allocate_HighlyCorrelatedAssets_DownsizedAsCluster()
    {
        // Build 4 assets: pair (0,1) highly correlated, pair (2,3) highly correlated,
        // negligible correlation across pairs. HRP clusters (0,1) and (2,3) and
        // splits ~half/half between them — NOT four equal weights.
        var cov = new double[,]
        {
            { 0.04, 0.038, 0.001, 0.001 },
            { 0.038, 0.04, 0.001, 0.001 },
            { 0.001, 0.001, 0.04, 0.038 },
            { 0.001, 0.001, 0.038, 0.04 },
        };
        var hrp = HierarchicalRiskParity.Allocate(cov);

        // Cluster sums: (w0 + w1) ≈ (w2 + w3) ≈ 0.5
        double cluster1 = hrp.Weights[0] + hrp.Weights[1];
        double cluster2 = hrp.Weights[2] + hrp.Weights[3];
        cluster1.Should().BeApproximately(0.5, 0.05);
        cluster2.Should().BeApproximately(0.5, 0.05);
    }

    [Fact]
    public void Allocate_QuasiDiagonalOrder_GroupsCorrelatedAssets()
    {
        // Same setup as above. Quasi-diagonal order should put correlated assets
        // adjacent: either [0,1,2,3] or [2,3,0,1] but never [0,2,1,3].
        var cov = new double[,]
        {
            { 0.04, 0.038, 0.001, 0.001 },
            { 0.038, 0.04, 0.001, 0.001 },
            { 0.001, 0.001, 0.04, 0.038 },
            { 0.001, 0.001, 0.038, 0.04 },
        };
        var hrp = HierarchicalRiskParity.Allocate(cov);
        var order = hrp.QuasiDiagonalOrder;

        // 0 and 1 should be in adjacent positions; 2 and 3 should be in adjacent positions.
        int pos0 = Array.IndexOf(order, 0);
        int pos1 = Array.IndexOf(order, 1);
        int pos2 = Array.IndexOf(order, 2);
        int pos3 = Array.IndexOf(order, 3);

        Math.Abs(pos0 - pos1).Should().Be(1, "correlated pair (0,1) must be adjacent in quasi-diagonal order");
        Math.Abs(pos2 - pos3).Should().Be(1, "correlated pair (2,3) must be adjacent in quasi-diagonal order");
    }

    [Fact]
    public void Allocate_ClusterMerges_CountIsNMinusOne()
    {
        var returns = GenerateRandomReturns(seed: 21, t: 150, n: 5, vol: 0.02);
        var lw = LedoitWolfShrinkage.Estimate(returns);
        var hrp = HierarchicalRiskParity.Allocate(lw.Covariance);

        // Single-linkage agglomerative tree on N leaves has exactly N-1 merges.
        hrp.ClusterMerges.Count.Should().Be(4);
    }

    [Fact]
    public void Allocate_MoreVolatileCluster_GetsLessAllocation()
    {
        // 4 assets in two clusters: cluster A is low-vol (0.01), cluster B is high-vol (0.05).
        // HRP should allocate MORE to the low-vol cluster (inverse to cluster variance).
        double lowVol = 0.01;
        double highVol = 0.05;
        var cov = new double[,]
        {
            { lowVol * lowVol,  0.95 * lowVol * lowVol, 0.0,                  0.0                   },
            { 0.95 * lowVol * lowVol, lowVol * lowVol,  0.0,                  0.0                   },
            { 0.0,              0.0,                    highVol * highVol,    0.95 * highVol * highVol },
            { 0.0,              0.0,                    0.95 * highVol * highVol, highVol * highVol },
        };
        var hrp = HierarchicalRiskParity.Allocate(cov);

        double clusterLow = hrp.Weights[0] + hrp.Weights[1];
        double clusterHigh = hrp.Weights[2] + hrp.Weights[3];

        clusterLow.Should().BeGreaterThan(clusterHigh,
            "HRP should overweight the low-vol cluster (inverse-variance allocation)");
    }

    private static double[,] GenerateRandomReturns(int seed, int t, int n, double vol)
    {
        var rng = new Random(seed);
        var returns = new double[t, n];
        for (int i = 0; i < t; i++)
        {
            for (int j = 0; j < n; j++)
            {
                double u1 = 1.0 - rng.NextDouble();
                double u2 = 1.0 - rng.NextDouble();
                returns[i, j] = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2) * vol;
            }
        }
        return returns;
    }
}
