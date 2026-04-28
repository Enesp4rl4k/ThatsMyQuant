using System;
using FluentAssertions;
using ThatsMyQuant.Application.Strategy;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

public class MatrixOpsTests
{
    [Fact]
    public void Multiply_KnownInput_MatchesExpected()
    {
        var a = new double[,] { { 1, 2 }, { 3, 4 } };
        var b = new double[,] { { 5, 6 }, { 7, 8 } };
        var c = MatrixOps.Multiply(a, b);

        c[0, 0].Should().Be(19); c[0, 1].Should().Be(22);
        c[1, 0].Should().Be(43); c[1, 1].Should().Be(50);
    }

    [Fact]
    public void Multiply_DimensionMismatch_Throws()
    {
        var a = new double[2, 3];
        var b = new double[4, 2];
        Action act = () => MatrixOps.Multiply(a, b);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Transpose_PreservesElementsByPosition()
    {
        var a = new double[,] { { 1, 2, 3 }, { 4, 5, 6 } };
        var t = MatrixOps.Transpose(a);

        t.GetLength(0).Should().Be(3);
        t.GetLength(1).Should().Be(2);
        t[0, 0].Should().Be(1); t[2, 1].Should().Be(6);
    }

    [Fact]
    public void Add_ElementWise()
    {
        var a = new double[,] { { 1, 2 }, { 3, 4 } };
        var b = new double[,] { { 10, 20 }, { 30, 40 } };
        var c = MatrixOps.Add(a, b);
        c[0, 0].Should().Be(11);
        c[1, 1].Should().Be(44);
    }

    [Fact]
    public void Identity_HasOnesOnDiagonal()
    {
        var i = MatrixOps.Identity(4);
        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 4; c++)
                i[r, c].Should().Be(r == c ? 1.0 : 0.0);
    }

    [Fact]
    public void Invert_2x2_KnownInverse()
    {
        // | 4 7 |        |  0.6 -0.7 |
        // | 2 6 |  inv = | -0.2  0.4 |
        var a = new double[,] { { 4, 7 }, { 2, 6 } };
        var inv = MatrixOps.Invert(a);
        inv[0, 0].Should().BeApproximately(0.6, 1e-12);
        inv[0, 1].Should().BeApproximately(-0.7, 1e-12);
        inv[1, 0].Should().BeApproximately(-0.2, 1e-12);
        inv[1, 1].Should().BeApproximately(0.4, 1e-12);
    }

    [Fact]
    public void Invert_TimesOriginal_GivesIdentity()
    {
        var rng = new Random(7);
        int n = 5;
        var a = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) a[i, j] = rng.NextDouble() * 2 - 1;
        // Make diagonally dominant for invertibility.
        for (int i = 0; i < n; i++) a[i, i] += n;

        var inv = MatrixOps.Invert(a);
        var product = MatrixOps.Multiply(a, inv);

        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                product[i, j].Should().BeApproximately(i == j ? 1.0 : 0.0, 1e-9);
    }

    [Fact]
    public void Invert_SingularMatrix_Throws()
    {
        var singular = new double[,] { { 1, 2 }, { 2, 4 } };  // row 2 = 2 × row 1
        Action act = () => MatrixOps.Invert(singular);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Solve_Recovers_Ax_Equals_b()
    {
        // A = [[3, 2], [1, 4]], x = [1, 2] → b = [7, 9]
        var a = new double[,] { { 3, 2 }, { 1, 4 } };
        var b = new double[] { 7, 9 };

        var x = MatrixOps.Solve(a, b);

        x[0].Should().BeApproximately(1.0, 1e-12);
        x[1].Should().BeApproximately(2.0, 1e-12);
    }

    [Fact]
    public void MultiplyVector_KnownInput()
    {
        var a = new double[,] { { 1, 2 }, { 3, 4 } };
        var x = new double[] { 5, 6 };
        var y = MatrixOps.MultiplyVector(a, x);

        y[0].Should().Be(17);  // 1·5 + 2·6
        y[1].Should().Be(39);  // 3·5 + 4·6
    }

    [Fact]
    public void Scale_MultipliesAllElements()
    {
        var a = new double[,] { { 1, 2 }, { 3, 4 } };
        var c = MatrixOps.Scale(a, 2.5);
        c[0, 0].Should().Be(2.5);
        c[1, 1].Should().Be(10.0);
    }
}
