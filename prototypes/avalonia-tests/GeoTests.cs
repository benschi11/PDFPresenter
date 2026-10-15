using Avalonia;
using PdfPresenter;
using Xunit;

namespace PdfPresenter.Tests;

public sealed class GeoTests
{
    private static GeoPose Start => new(new Point(1, 0.5), 0, 1);

    private static void Near(Point expected, Point actual, double tol = 1e-9)
    {
        Assert.Equal(expected.X, actual.X, tol);
        Assert.Equal(expected.Y, actual.Y, tol);
    }

    [Fact]
    public void One_finger_moves_without_rotating()
    {
        var p = GeoMath.Translate(Start, new Point(0.9, 0.4), new Point(1.0, 0.45));
        Near(new Point(1.1, 0.55), p.Center);
        Assert.Equal(0, p.AngleDeg);
        Assert.Equal(1, p.Scale);
    }

    [Fact]
    public void Two_fingers_rotate_around_their_midpoint()
    {
        double raw = 0;
        // Finger symmetrisch um den Mittelpunkt des Dreiecks, Drehung um 90° (im Uhrzeigersinn am Bildschirm)
        var p = GeoMath.TwoFinger(Start, ref raw,
            new Point(0.9, 0.5), new Point(1.1, 0.5),
            new Point(1.0, 0.4), new Point(1.0, 0.6), allowScale: false);
        Near(new Point(1, 0.5), p.Center);
        Assert.Equal(90, p.AngleDeg, 9);
        Assert.Equal(90, raw, 9);
        Assert.Equal(-90, p.MathAngle, 9);
    }

    [Fact]
    public void Point_under_a_finger_stays_under_it()
    {
        // Ein Punkt des Dreiecks, der unter Finger A lag, liegt nach der Geste unter dem neuen A.
        var pose = new GeoPose(new Point(0.7, 0.6), 20, 1.3);
        Point a0 = new(0.5, 0.5), b0 = new(0.8, 0.7), a1 = new(0.6, 0.3), b1 = new(0.75, 0.65);
        double raw = pose.AngleDeg;
        var after = GeoMath.TwoFinger(pose, ref raw, a0, b0, a1, b1, allowScale: true);

        const double upm = 0.01;
        var local = a0.Transform(Invert(GeoMath.LocalToPage(pose, upm)));
        Near(a1, local.Transform(GeoMath.LocalToPage(after, upm)), 1e-9);
    }

    [Fact]
    public void Locked_scale_ignores_pinch()
    {
        double raw = 0;
        var p = GeoMath.TwoFinger(Start, ref raw,
            new Point(0.9, 0.5), new Point(1.1, 0.5),
            new Point(0.8, 0.5), new Point(1.2, 0.5), allowScale: false);
        Assert.Equal(1, p.Scale);
        var q = GeoMath.TwoFinger(Start, ref raw,
            new Point(0.9, 0.5), new Point(1.1, 0.5),
            new Point(0.8, 0.5), new Point(1.2, 0.5), allowScale: true);
        Assert.Equal(2, q.Scale, 9);
    }

    [Fact]
    public void Scale_is_clamped()
    {
        double raw = 0;
        var p = GeoMath.TwoFinger(Start, ref raw,
            new Point(0.99, 0.5), new Point(1.01, 0.5),
            new Point(0, 0.5), new Point(2, 0.5), allowScale: true);
        Assert.Equal(GeoPose.MaxScale, p.Scale);
    }

    [Theory]
    [InlineData(1.5, 0)]
    [InlineData(44, 45)]
    [InlineData(-91.9, -90)]
    [InlineData(42.5, 42.5)]
    [InlineData(30, 30)]
    public void Snaps_near_multiples_of_45(double input, double expected) =>
        Assert.Equal(expected, GeoMath.Snap(input), 9);

    [Theory]
    [InlineData(0, -1, true)]       // knapp über der Hypotenuse
    [InlineData(0, -79, true)]      // unter der Spitze
    [InlineData(70, -5, true)]      // nahe der rechten Ecke
    [InlineData(70, -20, false)]    // außerhalb des Schenkels
    [InlineData(0, 2, false)]       // unter der Hypotenuse
    public void Contains_triangle(double x, double y, bool inside) =>
        Assert.Equal(inside, GeoMath.Contains(new Point(x, y)));

    [Fact]
    public void Math_angle_is_counterclockwise_and_normalized()
    {
        Assert.Equal(30, new GeoPose(default, -30, 1).MathAngle, 9);
        Assert.Equal(-170, new GeoPose(default, -190, 1).MathAngle, 9);
    }

    private static Matrix Invert(Matrix m) => m.TryInvert(out var inv) ? inv : throw new InvalidOperationException();
}
