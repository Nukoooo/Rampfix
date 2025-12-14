using Sharp.Shared.Types;

namespace RampFix;

internal static class VectorExtension
{
    extension(Vector vec)
    {
        public Vector Normalized()
        {
            var result = vec;
        
            var lenSq = (result.X * result.X) + (result.Y * result.Y) + (result.Z * result.Z);
            if (lenSq < 1e-12f)
            {
                return new Vector();
            }
        
            var invLen = 1.0f / MathF.Sqrt(lenSq);
        
            result.X *= invLen;
            result.Y *= invLen;
            result.Z *= invLen;
        
            return result;
        }
    }
}
