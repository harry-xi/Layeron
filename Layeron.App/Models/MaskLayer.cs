using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Layeron.Big3DArray;

namespace Layeron.App.Models;

public class MaskLayer : INotifyPropertyChanged
{
    private string _name = "Mask";
    private Color _color = Colors.Red;
    private double _opacity = 0.4;
    private bool _isVisible = true;
    private Big3DBitMap? _bitmap;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    public Color Color
    {
        get => _color;
        set => Set(ref _color, value);
    }

    public double Opacity
    {
        get => _opacity;
        set => Set(ref _opacity, Math.Clamp(value, 0, 1));
    }

    public bool IsVisible
    {
        get => _isVisible;
        set => Set(ref _isVisible, value);
    }

    public Big3DBitMap? Bitmap
    {
        get => _bitmap;
        set => Set(ref _bitmap, value);
    }

    public bool MatchesDimensions(int x, int y, int z) =>
        _bitmap != null && _bitmap.MatchesDimensions(x, y, z);

    public void SaveStream(Stream stream)
    {
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(Name);
        writer.Write((byte)Color.R);
        writer.Write((byte)Color.G);
        writer.Write((byte)Color.B);
        writer.Write(Opacity);
        writer.Write(IsVisible);
        _bitmap?.SaveStream(stream);
    }

    public static MaskLayer? LoadStream(Stream stream, int expectedX, int expectedY, int expectedZ)
    {
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        var name = reader.ReadString();
        var r = reader.ReadByte();
        var g = reader.ReadByte();
        var b = reader.ReadByte();
        var opacity = reader.ReadDouble();
        var isVisible = reader.ReadBoolean();
        var bitmap = Big3DBitMap.LoadStream(stream);

        if (!bitmap.MatchesDimensions(expectedX, expectedY, expectedZ))
            return null;

        return new MaskLayer
        {
            Name = name,
            Color = Color.FromRgb(r, g, b),
            Opacity = opacity,
            IsVisible = isVisible,
            Bitmap = bitmap
        };
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
