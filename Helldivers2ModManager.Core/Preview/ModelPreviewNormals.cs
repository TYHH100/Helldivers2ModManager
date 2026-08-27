namespace Helldivers2ModManager.Core.Preview;

public static class ModelPreviewNormals
{
    public static float[] BuildSmoothedNormals(float[] positions, int[] triangleIndices)
    {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(triangleIndices);
        var normals = new float[positions.Length];
        for (var index = 0; index + 2 < triangleIndices.Length; index += 3)
        {
            var first = triangleIndices[index] * 3;
            var second = triangleIndices[index + 1] * 3;
            var third = triangleIndices[index + 2] * 3;
            if (first + 2 >= positions.Length || second + 2 >= positions.Length || third + 2 >= positions.Length) continue;
            var abX = positions[second] - positions[first];
            var abY = positions[second + 1] - positions[first + 1];
            var abZ = positions[second + 2] - positions[first + 2];
            var acX = positions[third] - positions[first];
            var acY = positions[third + 1] - positions[first + 1];
            var acZ = positions[third + 2] - positions[first + 2];
            var normalX = abY * acZ - abZ * acY;
            var normalY = abZ * acX - abX * acZ;
            var normalZ = abX * acY - abY * acX;
            if (!float.IsFinite(normalX) || !float.IsFinite(normalY) || !float.IsFinite(normalZ)) continue;
            AddNormal(normals, first, normalX, normalY, normalZ);
            AddNormal(normals, second, normalX, normalY, normalZ);
            AddNormal(normals, third, normalX, normalY, normalZ);
        }

        for (var index = 0; index + 2 < normals.Length; index += 3)
        {
            var x = normals[index];
            var y = normals[index + 1];
            var z = normals[index + 2];
            var length = MathF.Sqrt(x * x + y * y + z * z);
            if (length > 0 && float.IsFinite(length))
            {
                normals[index] = x / length;
                normals[index + 1] = y / length;
                normals[index + 2] = z / length;
            }
            else normals[index + 1] = 1;
        }
        return normals;
    }

    private static void AddNormal(float[] normals, int offset, float x, float y, float z)
    {
        normals[offset] += x;
        normals[offset + 1] += y;
        normals[offset + 2] += z;
    }
}
