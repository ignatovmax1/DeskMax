namespace DeskMax.Windows;

public static class RemoteViewport
{
    public static bool TryNormalize(double viewWidth, double viewHeight, double pixelWidth, double pixelHeight, double x, double y, out double normalizedX, out double normalizedY)
    {
        normalizedX = normalizedY = 0;
        if (!double.IsFinite(viewWidth + viewHeight + pixelWidth + pixelHeight + x + y) || viewWidth <= 0 || viewHeight <= 0 || pixelWidth <= 0 || pixelHeight <= 0) return false;
        var scale = Math.Min(viewWidth / pixelWidth, viewHeight / pixelHeight);
        var width = pixelWidth * scale; var height = pixelHeight * scale;
        normalizedX = (x - (viewWidth - width) / 2) / width;
        normalizedY = (y - (viewHeight - height) / 2) / height;
        return normalizedX is >= 0 and <= 1 && normalizedY is >= 0 and <= 1;
    }
}
