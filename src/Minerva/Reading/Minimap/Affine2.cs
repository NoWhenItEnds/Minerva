using System.Numerics;

namespace Minerva.Reading.Minimap;

/// <summary>A 2D affine transform: <c>x' = A·x + B·y + C</c>, <c>y' = D·x + E·y + F</c>.</summary>
public readonly record struct Affine2(double A, double B, double C, double D, double E, double F)
{
    public Vector2 Apply(Vector2 p) => new((float)(A * p.X + B * p.Y + C), (float)(D * p.X + E * p.Y + F));

    public Affine2 Inverse()
    {
        var det = A * E - B * D;
        if (Math.Abs(det) < 1e-12) throw new InvalidOperationException("Transform is not invertible.");
        var ia = E / det;
        var ib = -B / det;
        var id = -D / det;
        var ie = A / det;
        return new Affine2(ia, ib, -(ia * C + ib * F), id, ie, -(id * C + ie * F));
    }

    /// <summary>Average scale (output units per input unit).</summary>
    public double Scale => Math.Sqrt(Math.Abs(A * E - B * D));

    /// <summary>Least-squares fit mapping <paramref name="from"/> onto <paramref name="to"/>; null if degenerate.</summary>
    public static Affine2? Fit(IReadOnlyList<Vector2> from, IReadOnlyList<Vector2> to)
    {
        if (from.Count != to.Count || from.Count < 3) return null;
        // Normal equations for [x y 1]·[a b c]ᵀ = x' (and the same matrix for y').
        double sxx = 0, sxy = 0, syy = 0, sx = 0, sy = 0, n = from.Count;
        double tx = 0, txx = 0, txy = 0, ty = 0, tyx = 0, tyy = 0;
        for (var i = 0; i < from.Count; i++)
        {
            double x = from[i].X, y = from[i].Y, u = to[i].X, v = to[i].Y;
            sxx += x * x; sxy += x * y; syy += y * y; sx += x; sy += y;
            txx += x * u; txy += y * u; tx += u;
            tyx += x * v; tyy += y * v; ty += v;
        }
        double[,] m = { { sxx, sxy, sx }, { sxy, syy, sy }, { sx, sy, n } };
        if (Solve(m, [txx, txy, tx]) is not { } abc || Solve(m, [tyx, tyy, ty]) is not { } def) return null;
        return new Affine2(abc[0], abc[1], abc[2], def[0], def[1], def[2]);
    }

    private static double[]? Solve(double[,] matrix, double[] rhs)
    {
        var m = (double[,])matrix.Clone();
        var b = (double[])rhs.Clone();
        const int n = 3;
        for (var col = 0; col < n; col++)
        {
            var pivot = col;
            for (var r = col + 1; r < n; r++) if (Math.Abs(m[r, col]) > Math.Abs(m[pivot, col])) pivot = r;
            if (Math.Abs(m[pivot, col]) < 1e-9) return null; // collinear landmarks
            if (pivot != col)
            {
                for (var c = 0; c < n; c++) (m[col, c], m[pivot, c]) = (m[pivot, c], m[col, c]);
                (b[col], b[pivot]) = (b[pivot], b[col]);
            }
            for (var r = 0; r < n; r++)
            {
                if (r == col) continue;
                var f = m[r, col] / m[col, col];
                for (var c = 0; c < n; c++) m[r, c] -= f * m[col, c];
                b[r] -= f * b[col];
            }
        }
        return [b[0] / m[0, 0], b[1] / m[1, 1], b[2] / m[2, 2]];
    }
}
