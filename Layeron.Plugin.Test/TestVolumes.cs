using Layeron.Big3DArray;

namespace Layeron.Plugin.Test;

/// <summary>Helpers for building volumes the plugin tests can run against.</summary>
internal static class TestVolumes
{
    /// <summary>A small 8-bit volume with a deterministic ramp, used where only shape matters.</summary>
    public static Big3DArray<byte> Small(int size = 40)
    {
        var volume = new Big3DArray<byte>(size, size, size);
        volume.Fill((x, y, z) => (byte)((x + y + z) % 251));
        return volume;
    }

    /// <summary>An 8-bit volume that crosses block boundaries, so block-edge paths are exercised.</summary>
    public static Big3DArray<byte> Boundary()
    {
        var volume = new Big3DArray<byte>(70, 68, 66);
        volume.Fill((x, y, z) => (byte)(x + y + z));
        return volume;
    }

    /// <summary>A 16-bit volume, to prove the same code path serves both element types.</summary>
    public static Big3DArray<ushort> Small16(int size = 20)
    {
        var volume = new Big3DArray<ushort>(size, size, size);
        volume.Fill((x, y, z) => (ushort)((x * 1000 + y * 100 + z) % 60000));
        return volume;
    }
}
