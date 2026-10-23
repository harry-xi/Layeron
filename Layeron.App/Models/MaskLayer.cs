using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Layeron.Big3DArray;
using Layeron.Plugin.Abstractions;

namespace Layeron.App.Models;

public class MaskLayer : INotifyPropertyChanged, IMaskLayer
{
    private string _name = "Mask";
    private Color _color = Color.FromArgb(102, 255, 0, 0);
    private bool _isVisible = true;
    private Big3DBitMap? _bitmap;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    /// <summary>Layer colour; the alpha channel is the overlay opacity.</summary>
    public Color Color
    {
        get => _color;
        set
        {
            if (_color == value) return;
            _color = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Color)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AlphaPercent)));
        }
    }

    /// <summary>Read-only view of <see cref="Color"/>'s alpha, for the manager panel.</summary>
    public string AlphaPercent => $"{_color.A * 100 / 255.0:0}%";

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

    /// <summary>
    /// Plugin-facing colour. Implemented explicitly because the app's own <see cref="Color"/> is the
    /// Avalonia type that the XAML binds to; the plugin contract deliberately stays UI-framework free.
    /// </summary>
    MaskColor IMaskLayer.Color
    {
        get => new(_color.R, _color.G, _color.B, _color.A);
        set => Color = Avalonia.Media.Color.FromArgb(value.A, value.R, value.G, value.B);
    }

    /// <summary>Read-only bitmap view for plugins; the mutable instance is <see cref="Bitmap"/>.</summary>
    Big3DBitMap IMaskLayer.Bitmap => _bitmap!;

    public void SaveStream(Stream stream)
    {
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(Name);
        writer.Write((byte)Color.R);
        writer.Write((byte)Color.G);
        writer.Write((byte)Color.B);
        writer.Write((byte)Color.A);
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
        var a = reader.ReadByte();
        var isVisible = reader.ReadBoolean();
        var bitmap = Big3DBitMap.LoadStream(stream);

        if (!bitmap.MatchesDimensions(expectedX, expectedY, expectedZ))
            return null;

        return new MaskLayer
        {
            Name = name,
            Color = Color.FromArgb(a, r, g, b),
            IsVisible = isVisible,
            Bitmap = bitmap
        };
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}
