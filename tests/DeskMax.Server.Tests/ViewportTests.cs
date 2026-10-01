using System.IO;
using DeskMax.Windows;
namespace DeskMax.Server.Tests;

[TestClass]
public class ViewportTests
{
    [TestMethod] public void LetterboxIsExcludedAndImageCoordinatesAreNormalized()
    {
        Assert.IsFalse(RemoteViewport.TryNormalize(1000, 1000, 1600, 900, 500, 100, out _, out _));
        Assert.IsTrue(RemoteViewport.TryNormalize(1000, 1000, 1600, 900, 500, 500, out var x, out var y));
        Assert.AreEqual(.5, x); Assert.AreEqual(.5, y);
        Assert.IsTrue(RemoteViewport.TryNormalize(1000, 1000, 1600, 900, 1000, 781.25, out x, out y));
        Assert.AreEqual(1, x); Assert.AreEqual(1, y);
    }
    [TestMethod] public void InvalidViewportIsRejected()
    {
        Assert.IsFalse(RemoteViewport.TryNormalize(0, 1000, 1600, 900, 0, 0, out _, out _));
        Assert.IsFalse(RemoteViewport.TryNormalize(1000, 1000, 1600, 900, double.NaN, 0, out _, out _));
    }
    [TestMethod] public void JpegDimensionsAreCheckedBeforeDecoding()
    {
        byte[] valid = [255, 216, 255, 192, 0, 8, 8, 3, 132, 6, 64, 0];
        Assert.AreEqual((1600, 900), JpegFrame.ReadSize(valid));
        valid[9] = 255; valid[10] = 255;
        Assert.ThrowsExactly<InvalidDataException>(() => JpegFrame.ReadSize(valid));
        Assert.ThrowsExactly<InvalidDataException>(() => JpegFrame.ReadSize([255, 216, 255, 192, 255, 255]));
    }
}
