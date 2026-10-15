using Avalonia;
using PdfPresenter;
using Xunit;

namespace PdfPresenter.Tests;

public sealed class AngleTests
{
    private const double Upm = 0.01; // 1 mm = 0,01 Seiteneinheiten

    private static void Near(Point expected, Point actual)
    {
        Assert.Equal(expected.X, actual.X, 9);
        Assert.Equal(expected.Y, actual.Y, 9);
    }

    [Fact]
    public void Ray_snaps_to_whole_degrees_and_millimetres()
    {
        var guide = AngleGuide.From(new GeoPose(new Point(1, 1), 0, 1), Upm);
        // ca. 37,3° gegen den Uhrzeigersinn (am Bildschirm nach oben rechts), ca. 50,4 mm lang
        double a = 37.3 * Math.PI / 180;
        var (end, angle, mm) = guide.Resolve(new Point(1 + 0.504 * Math.Cos(a), 1 - 0.504 * Math.Sin(a)));

        Assert.Equal(37, angle);
        Assert.Equal(50, mm);
        double s = 37 * Math.PI / 180;
        Near(new Point(1 + 0.5 * Math.Cos(s), 1 - 0.5 * Math.Sin(s)), end);
    }

    [Fact]
    public void Angle_is_measured_relative_to_the_rotated_hypotenuse()
    {
        // Geodreieck um 30° gegen den Uhrzeigersinn gedreht (Bildschirm: −30°)
        var guide = AngleGuide.From(new GeoPose(new Point(1, 1), -30, 1), Upm);
        // Stift genau senkrecht nach oben → relativ zur Hypotenuse 60°
        var (_, angle, mm) = guide.Resolve(new Point(1, 0.7));
        Assert.Equal(60, angle);
        Assert.Equal(30, mm);
    }

    [Fact]
    public void Below_the_hypotenuse_gives_negative_angles()
    {
        var guide = AngleGuide.From(new GeoPose(new Point(1, 1), 0, 1), Upm);
        var (_, angle, _) = guide.Resolve(new Point(1.2, 1.2));
        Assert.Equal(-45, angle);
    }

    [Fact]
    public void Scaled_triangle_measures_length_in_its_scale()
    {
        var guide = AngleGuide.From(new GeoPose(new Point(1, 1), 0, 2), Upm);
        var (_, _, mm) = guide.Resolve(new Point(1.4, 1));
        Assert.Equal(20, mm);
    }
}
