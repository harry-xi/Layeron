namespace Layeron.Big3DArray;

/// <summary>
/// The scalar element type stored in a 3D volume, and thus the physical bit depth
/// of each voxel. Supported storage depths are 8-bit (byte) and 16-bit (ushort).
/// </summary>
public enum PixelElementType : byte
{
    /// <summary>8-bit grayscale voxels, range [0, 255].</summary>
    Byte = 0,

    /// <summary>16-bit grayscale voxels, range [0, 65535].</summary>
    UInt16 = 1
}