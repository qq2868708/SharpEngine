using System.Numerics;

namespace SharpEngine.Spatial;

// GJK distance with an exhaustive closest-simplex reduction. This also supports
// affine-transformed primitives and swept convex volumes without AABB false hits.
internal static class ConvexDistance
{
    internal const float ContactTolerance = 0.0001f;

    internal static bool Intersects(Func<Vector3, Vector3> First, Func<Vector3, Vector3> Second)
    {
        Vector3 Support(Vector3 Direction) { return First(Direction) - Second(-Direction); }
        var Simplex = new List<Vector3>(4);
        var Closest = Support(Vector3.UnitX);
        Simplex.Add(Closest);
        for (var Iteration = 0; Iteration < 96; Iteration++)
        {
            var Squared = Closest.LengthSquared();
            if (Squared <= ContactTolerance * ContactTolerance)
            {
                return true;
            }
            var Point = Support(-Closest);
            // A support plane separating the complete Minkowski difference from
            // the origin proves separation; distance convergence is unnecessary.
            if (Vector3.Dot(Closest, Point) > ContactTolerance * MathF.Sqrt(Squared))
            {
                return false;
            }
            if (Simplex.Contains(Point))
            {
                return false;
            }
            Simplex.Add(Point);
            Closest = Reduce(Simplex);
        }
        // A failed numerical convergence must not allow navigation through a shape.
        throw new InvalidOperationException("COLLISION_CONVERGENCE: convex query exceeded its iteration budget.");
    }

    private static Vector3 Reduce(List<Vector3> Simplex)
    {
        var BestDistance = double.PositiveInfinity;
        var Best = Vector3.Zero;
        var BestMask = 0;
        for (var Mask = 1; Mask < 1 << Simplex.Count; Mask++)
        {
            var Indices = Enumerable.Range(0, Simplex.Count).Where(Index => (Mask & (1 << Index)) != 0).ToArray();
            if (Indices.Length > 4)
            {
                continue;
            }
            var Origin = Simplex[Indices[0]];
            var Count = Indices.Length - 1;
            var Edges = Indices.Skip(1).Select(Index => Simplex[Index] - Origin).ToArray();
            var System = new double[Count, Count + 1];
            for (var Row = 0; Row < Count; Row++)
            {
                for (var Column = 0; Column < Count; Column++)
                {
                    System[Row, Column] = Dot(Edges[Row], Edges[Column]);
                }
                System[Row, Count] = -Dot(Edges[Row], Origin);
            }
            var Weights = Solve(System, Count);
            if (Weights is null || Weights.Any(Weight => Weight < -1e-10) || Weights.Sum() > 1 + 1e-10)
            {
                continue;
            }
            var Point = Origin;
            for (var Index = 0; Index < Count; Index++)
            {
                Point += Edges[Index] * (float)Weights[Index];
            }
            var Distance = Dot(Point, Point);
            if (Distance < BestDistance)
            {
                BestDistance = Distance;
                Best = Point;
                BestMask = Mask;
            }
        }
        for (var Index = Simplex.Count - 1; Index >= 0; Index--)
        {
            if ((BestMask & (1 << Index)) == 0)
            {
                Simplex.RemoveAt(Index);
            }
        }
        return Best;
    }

    private static double Dot(Vector3 First, Vector3 Second)
    {
        return (double)First.X * Second.X + (double)First.Y * Second.Y + (double)First.Z * Second.Z;
    }

    private static double[]? Solve(double[,] System, int Count)
    {
        for (var Column = 0; Column < Count; Column++)
        {
            var Pivot = Column;
            for (var Row = Column + 1; Row < Count; Row++)
            {
                if (Math.Abs(System[Row, Column]) > Math.Abs(System[Pivot, Column]))
                {
                    Pivot = Row;
                }
            }
            if (Math.Abs(System[Pivot, Column]) < 1e-18)
            {
                return null;
            }
            for (var Index = Column; Index <= Count; Index++)
            {
                (System[Column, Index], System[Pivot, Index]) = (System[Pivot, Index], System[Column, Index]);
            }
            var Divisor = System[Column, Column];
            for (var Index = Column; Index <= Count; Index++)
            {
                System[Column, Index] /= Divisor;
            }
            for (var Row = 0; Row < Count; Row++)
            {
                if (Row == Column)
                {
                    continue;
                }
                var Factor = System[Row, Column];
                for (var Index = Column; Index <= Count; Index++)
                {
                    System[Row, Index] -= Factor * System[Column, Index];
                }
            }
        }
        return Enumerable.Range(0, Count).Select(Index => System[Index, Count]).ToArray();
    }
}
