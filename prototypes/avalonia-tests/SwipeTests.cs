using Avalonia;
using PdfPresenter;
using Xunit;

namespace PdfPresenter.Tests;

public sealed class SwipeTests
{
    [Theory]
    [InlineData(500, 300, 300, 310, 200, 1)]    // schnell nach links → nächste Seite
    [InlineData(300, 300, 500, 290, 200, -1)]   // nach rechts → vorige Seite
    [InlineData(500, 300, 420, 300, 200, 0)]    // zu kurz
    [InlineData(500, 300, 300, 450, 200, 0)]    // zu steil (eher senkrecht)
    [InlineData(500, 300, 300, 300, 900, 0)]    // zu langsam
    public void Detects_horizontal_swipes(double x0, double y0, double x1, double y1, double ms, int expected) =>
        Assert.Equal(expected, Swipe.Detect(new Point(x0, y0), new Point(x1, y1), ms));
}
