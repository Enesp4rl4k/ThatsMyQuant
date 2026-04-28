using System;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Minimal linear-algebra utilities for portfolio construction algorithms that
/// need matrix operations: multiply, transpose, invert, solve. Designed for
/// small-to-medium matrices (N ≤ ~50) typical of N-asset portfolios; not a
/// general-purpose linear algebra library.
///
/// Implementations:
///   • Gauss-Jordan elimination with partial pivoting for inversion (works on
///     non-symmetric matrices).
///   • LU-style forward/back substitution via the same elimination tableau.
///
/// Used in upgrade plan:
///   Phase B4 — Black-Litterman posterior return computation.
///   Phase J4 — PCA factor residuals (regression of returns on factors).
///   Phase K3 — DCC-GARCH dynamic correlation matrix updates (future).
/// </summary>
public static class MatrixOps
{
    /// <summary>C = A · B for compatible dimensions [m×k] · [k×n] = [m×n].</summary>
    public static double[,] Multiply(double[,] a, double[,] b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        int m = a.GetLength(0);
        int k = a.GetLength(1);
        int kb = b.GetLength(0);
        int n = b.GetLength(1);
        if (k != kb)
            throw new ArgumentException($"Inner dimensions disagree: {k} vs {kb}.");

        var c = new double[m, n];
        for (int i = 0; i < m; i++)
        {
            for (int j = 0; j < n; j++)
            {
                double sum = 0.0;
                for (int p = 0; p < k; p++) sum += a[i, p] * b[p, j];
                c[i, j] = sum;
            }
        }
        return c;
    }

    /// <summary>y = A · x for A:[m×n], x:[n], result:[m].</summary>
    public static double[] MultiplyVector(double[,] a, double[] x)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(x);
        int m = a.GetLength(0);
        int n = a.GetLength(1);
        if (n != x.Length)
            throw new ArgumentException($"Inner dimensions disagree: {n} vs {x.Length}.");

        var y = new double[m];
        for (int i = 0; i < m; i++)
        {
            double s = 0.0;
            for (int j = 0; j < n; j++) s += a[i, j] * x[j];
            y[i] = s;
        }
        return y;
    }

    /// <summary>A_T (transpose).</summary>
    public static double[,] Transpose(double[,] a)
    {
        ArgumentNullException.ThrowIfNull(a);
        int m = a.GetLength(0);
        int n = a.GetLength(1);
        var t = new double[n, m];
        for (int i = 0; i < m; i++)
            for (int j = 0; j < n; j++) t[j, i] = a[i, j];
        return t;
    }

    /// <summary>C = A + B element-wise (matrices must be same shape).</summary>
    public static double[,] Add(double[,] a, double[,] b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        int m = a.GetLength(0);
        int n = a.GetLength(1);
        if (b.GetLength(0) != m || b.GetLength(1) != n)
            throw new ArgumentException("Matrix dimensions disagree.");
        var c = new double[m, n];
        for (int i = 0; i < m; i++)
            for (int j = 0; j < n; j++) c[i, j] = a[i, j] + b[i, j];
        return c;
    }

    /// <summary>I_n (n × n identity matrix).</summary>
    public static double[,] Identity(int n)
    {
        if (n < 0) throw new ArgumentOutOfRangeException(nameof(n));
        var i = new double[n, n];
        for (int k = 0; k < n; k++) i[k, k] = 1.0;
        return i;
    }

    /// <summary>
    /// A^{-1} via Gauss-Jordan elimination with partial pivoting.
    /// Throws on singular matrices.
    /// </summary>
    public static double[,] Invert(double[,] a)
    {
        ArgumentNullException.ThrowIfNull(a);
        int n = a.GetLength(0);
        if (a.GetLength(1) != n)
            throw new ArgumentException("Matrix must be square.", nameof(a));

        // Augmented [A | I]
        var aug = new double[n, 2 * n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++) aug[i, j] = a[i, j];
            aug[i, n + i] = 1.0;
        }

        for (int col = 0; col < n; col++)
        {
            // Partial pivot.
            int pivotRow = col;
            double pivotMag = Math.Abs(aug[col, col]);
            for (int r = col + 1; r < n; r++)
            {
                double mag = Math.Abs(aug[r, col]);
                if (mag > pivotMag) { pivotMag = mag; pivotRow = r; }
            }
            if (pivotMag < 1e-15)
                throw new InvalidOperationException("Matrix is singular or near-singular.");

            if (pivotRow != col)
            {
                for (int j = 0; j < 2 * n; j++)
                    (aug[col, j], aug[pivotRow, j]) = (aug[pivotRow, j], aug[col, j]);
            }

            // Normalize pivot row.
            double pivot = aug[col, col];
            for (int j = 0; j < 2 * n; j++) aug[col, j] /= pivot;

            // Eliminate other rows.
            for (int r = 0; r < n; r++)
            {
                if (r == col) continue;
                double factor = aug[r, col];
                if (factor == 0.0) continue;
                for (int j = 0; j < 2 * n; j++)
                    aug[r, j] -= factor * aug[col, j];
            }
        }

        var inv = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) inv[i, j] = aug[i, n + j];
        return inv;
    }

    /// <summary>
    /// Solve A · x = b for x via LU-style elimination (more numerically stable
    /// than computing A^{-1} explicitly when only a single solve is needed).
    /// </summary>
    public static double[] Solve(double[,] a, double[] b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        int n = a.GetLength(0);
        if (a.GetLength(1) != n)
            throw new ArgumentException("Matrix must be square.", nameof(a));
        if (b.Length != n)
            throw new ArgumentException("RHS length must match matrix size.", nameof(b));

        var aug = new double[n, n + 1];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++) aug[i, j] = a[i, j];
            aug[i, n] = b[i];
        }

        for (int col = 0; col < n; col++)
        {
            int pivotRow = col;
            double pivotMag = Math.Abs(aug[col, col]);
            for (int r = col + 1; r < n; r++)
            {
                double mag = Math.Abs(aug[r, col]);
                if (mag > pivotMag) { pivotMag = mag; pivotRow = r; }
            }
            if (pivotMag < 1e-15)
                throw new InvalidOperationException("Matrix is singular or near-singular.");

            if (pivotRow != col)
            {
                for (int j = 0; j <= n; j++)
                    (aug[col, j], aug[pivotRow, j]) = (aug[pivotRow, j], aug[col, j]);
            }

            for (int r = col + 1; r < n; r++)
            {
                double factor = aug[r, col] / aug[col, col];
                for (int j = col; j <= n; j++) aug[r, j] -= factor * aug[col, j];
            }
        }

        // Back substitution.
        var x = new double[n];
        for (int i = n - 1; i >= 0; i--)
        {
            double s = aug[i, n];
            for (int j = i + 1; j < n; j++) s -= aug[i, j] * x[j];
            x[i] = s / aug[i, i];
        }
        return x;
    }

    /// <summary>Element-wise scaling: C = α · A.</summary>
    public static double[,] Scale(double[,] a, double alpha)
    {
        ArgumentNullException.ThrowIfNull(a);
        int m = a.GetLength(0);
        int n = a.GetLength(1);
        var c = new double[m, n];
        for (int i = 0; i < m; i++)
            for (int j = 0; j < n; j++) c[i, j] = a[i, j] * alpha;
        return c;
    }
}
