using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace GeminiTranslate.Infrastructure.Windows;

/// <summary>Captura a tela inteira, ou uma região dela, devolvendo PNG em bytes.</summary>
public static class ScreenCapture
{
    /// <summary>Área que cobre todos os monitores, em pixels físicos.</summary>
    public static Rectangle VirtualScreen => SystemInformation.VirtualScreen;

    /// <summary>Captura todos os monitores.</summary>
    public static byte[] CaptureFull()
    {
        var area = VirtualScreen;
        return Capture(area.Left, area.Top, area.Width, area.Height);
    }

    /// <summary>Captura um retângulo em coordenadas de tela.</summary>
    public static byte[] Capture(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentException("região inválida.");

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
            graphics.CopyFromScreen(x, y, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }
}

/// <summary>Captura de tela do Windows, como porta consumível pelo núcleo.</summary>
public sealed class WindowsScreenCapture : GeminiTranslate.Core.Contracts.IScreenCapture
{
    /// <inheritdoc />
    public byte[] CaptureFull() => ScreenCapture.CaptureFull();

    /// <inheritdoc />
    public byte[] Capture(int x, int y, int width, int height) => ScreenCapture.Capture(x, y, width, height);
}
