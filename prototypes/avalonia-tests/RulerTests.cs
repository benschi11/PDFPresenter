using Avalonia;
using PdfPresenter;
using Xunit;

namespace PdfPresenter.Tests;

public sealed class RulerTests
{
    // 1 mm = 0,01 Seiteneinheiten; Hypotenuse waagrecht von (0,2|1) bis (1,8|1), Spitze oben
    private const double Upm = 0.01;
    private static readonly GeoPose Pose = new(new Point(1, 1), 0, 1);

    [Fact]
    public void Finds_hypotenuse_when_pen_starts_just_below_it()
    {
        var guide = GeoMath.FindGuide(Pose, Upm, new Point(1.05, 1.02), maxDistance: 0.03);
        Assert.NotNull(guide);
        Assert.Equal(0.2, guide.Value.A.X, 9);
        Assert.Equal(1.6, guide.Value.Length, 9);
        // Normale zeigt nach unten, zur Seite des Stifts
        Assert.True(guide.Value.Normal.Y > 0.99);
    }

    [Fact]
    public void Too_far_away_or_beyond_the_ends_gives_no_guide()
    {
        Assert.Null(GeoMath.FindGuide(Pose, Upm, new Point(1.0, 1.1), maxDistance: 0.03));
        Assert.Null(GeoMath.FindGuide(Pose, Upm, new Point(2.0, 1.01), maxDistance: 0.03));
    }

    [Fact]
    public void Finds_leg_near_the_slanted_edge()
    {
        // Punkt knapp außerhalb des rechten Schenkels (Mitte zwischen (1,8|1) und (1|0,2))
        var guide = GeoMath.FindGuide(Pose, Upm, new Point(1.41, 0.59), maxDistance: 0.03);
        Assert.NotNull(guide);
        Assert.Equal(1.8, guide.Value.A.X, 9);
        Assert.Equal(0.8 * Math.Sqrt(2), guide.Value.Length, 9);
    }

    [Fact]
    public void Positions_snap_to_whole_millimetres_and_stay_on_the_edge()
    {
        var guide = GeoMath.FindGuide(Pose, Upm, new Point(1.0, 1.01), 0.03)!.Value;
        Assert.Equal(0.43, guide.Along(new Point(0.6327, 1.02)), 9);   // 43,27 mm → 43 mm
        Assert.Equal(0, guide.Along(new Point(0.1, 1.0)), 9);          // vor dem Anfang → 0
        Assert.Equal(1.6, guide.Along(new Point(1.95, 1.0)), 9);       // nach dem Ende → Ende
        Assert.Equal(43, guide.Millimetres(0, 0.43), 9);
    }

    [Fact]
    public void Offset_places_the_line_beside_the_edge()
    {
        var guide = GeoMath.FindGuide(Pose, Upm, new Point(1.0, 1.01), 0.03)!.Value;
        var p = guide.At(0.8, 0.002);
        Assert.Equal(1.0, p.X, 9);
        Assert.Equal(1.002, p.Y, 9);
    }

    [Fact]
    public void Scaled_triangle_measures_in_its_own_scale()
    {
        var pose = Pose with { Scale = 2 };
        var guide = GeoMath.FindGuide(pose, Upm, new Point(1.0, 1.01), 0.03)!.Value;
        Assert.Equal(3.2, guide.Length, 9);
        Assert.Equal(10, guide.Millimetres(0, 0.2), 9); // 0,2 Einheiten = 10 mm der doppelt großen Skala
    }
}
