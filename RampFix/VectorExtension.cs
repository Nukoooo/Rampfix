using System.Numerics;
using System.Runtime.CompilerServices;
using Vector = Sharp.Shared.Types.Vector;

namespace RampFix;

internal static class VectorExtension
{
    extension(Vector vec)
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector Normalized()
        {
            var v     = Unsafe.BitCast<Vector, Vector3>(vec);
            var lenSq = v.LengthSquared();

            if (lenSq < 1e-12f)
                return default;

            var inv = MathF.ReciprocalSqrtEstimate(lenSq);
            inv *= 1.5f - 0.5f * lenSq * inv * inv;

            return Unsafe.BitCast<Vector3, Vector>(v * inv);
        }
    }
}
