using System.Numerics;

namespace Layeron.Plugins.Model3D;

/// <summary>
/// Where a working grid sits inside the full downsample lattice.
/// <para>
/// <see cref="OriginX"/> / <see cref="OriginY"/> / <see cref="OriginZ"/> are coarse-cell coordinates:
/// working cell (0, 0, 0) is the full-volume cell at that offset. <see cref="VolumeX"/> /
/// <see cref="VolumeY"/> / <see cref="VolumeZ"/> are the cell counts of the whole volume, not of the
/// working grid. They are what camera framing uses, so cropping never moves the bounding sphere.
/// </para>
/// </summary>
internal readonly record struct GridFrame(
    int OriginX,
    int OriginY,
    int OriginZ,
    int VolumeX,
    int VolumeY,
    int VolumeZ)
{
    /// <summary>A frame for a grid that already covers the whole volume.</summary>
    public static GridFrame Full(int sizeX, int sizeY, int sizeZ)
        => new(0, 0, 0, sizeX, sizeY, sizeZ);

    /// <summary>Centre of the sampled box, in world units, from the full volume.</summary>
    public Vector3 Center(int voxelSize)
        => new Vector3(VolumeX, VolumeY, VolumeZ) * (voxelSize * 0.5f);

    /// <summary>Radius of that box's bounding sphere, in world units.</summary>
    public float Radius(int voxelSize)
        => 0.5f * new Vector3(VolumeX, VolumeY, VolumeZ).Length() * voxelSize;
}

/// <summary>
/// The downsampled working grid plus the frame that maps it back into the full volume.
/// <see cref="HasSolid"/> is false for an entirely empty mask; in that case an extractor can return
/// an empty surface without allocating a distance field or walking the grid.
/// </summary>
internal sealed record DownsampledGrid(
    bool[,,] Grid,
    GridFrame Frame,
    bool HasSolid,
    DownsampleStats Stats);

/// <summary>Block and cell counts from a downsample, for diagnostics and benchmark output.</summary>
internal readonly record struct DownsampleStats(
    int TotalBlocks,
    int OccupiedBlocks,
    int FullBlocks,
    int GridCells,
    int VolumeCells)
{
    /// <summary>Fraction of the full coarse volume the working grid covers, 0..1.</summary>
    public double GridFraction => VolumeCells == 0 ? 0 : (double)GridCells / VolumeCells;

    /// <summary>Fraction of the storage blocks that hold any data, 0..1.</summary>
    public double OccupiedFraction => TotalBlocks == 0 ? 0 : (double)OccupiedBlocks / TotalBlocks;
}
