using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace GeminiTranslateV2;

/// <summary>Captura a tela (todos os monitores) ou uma região, retornando PNG em bytes.</summary>
public static class ScreenCapture
{
    /// <summary>Área que cobre todos os monitores, em pixels físicos.</summary>
    public static Rectangle VirtualScreen => SystemInformation.VirtualScreen;

    public static byte[] CaptureFull()
    {
        var r = VirtualScreen;
        return Capture(r.Left, r.Top, r.Width, r.Height);
    }

    public static byte[] Capture(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentException("região inválida.");
        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(x, y, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }
}
