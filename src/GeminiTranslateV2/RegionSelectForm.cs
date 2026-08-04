using System.Drawing;
using System.Windows.Forms;

namespace GeminiTranslateV2;

/// <summary>
/// Overlay em tela cheia (todos os monitores) para o usuário arrastar e selecionar uma região.
/// Retorna o retângulo em pixels de tela. Enter/soltar confirma; Esc cancela.
/// Coordenadas em pixels físicos, compatíveis com ScreenCapture (caso simples de 1 monitor).
/// </summary>
public sealed class RegionSelectForm : Form
{
    private Point _start;
    private Rectangle _rect;
    private bool _dragging;

    public Rectangle SelectedRegion { get; private set; }

    public RegionSelectForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = ScreenCapture.VirtualScreen;
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = Color.Black;
        Opacity = 0.35;
        Cursor = Cursors.Cross;
        DoubleBuffered = true;
        KeyPreview = true;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // O retângulo de seleção também não deve vazar para quem vê sua tela.
        Stealth.ApplyToHandle(Handle);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        _start = e.Location;
        _dragging = true;
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging)
        {
            _rect = Rectangle.FromLTRB(
                Math.Min(_start.X, e.X), Math.Min(_start.Y, e.Y),
                Math.Max(_start.X, e.X), Math.Max(_start.Y, e.Y));
            Invalidate();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _dragging = false;
        if (_rect.Width > 3 && _rect.Height > 3)
        {
            // Converte de coordenadas do form (cliente) para pixels de tela.
            SelectedRegion = new Rectangle(_rect.X + Bounds.X, _rect.Y + Bounds.Y, _rect.Width, _rect.Height);
            DialogResult = DialogResult.OK;
        }
        else
        {
            DialogResult = DialogResult.Cancel;
        }
        Close();
        base.OnMouseUp(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_rect.Width > 0 && _rect.Height > 0)
        {
            // "Recorta" a área selecionada (mais clara) e desenha a borda.
            using var clear = new SolidBrush(Color.FromArgb(60, 30, 120, 220));
            e.Graphics.FillRectangle(clear, _rect);
            using var pen = new Pen(Color.FromArgb(230, 77, 171, 247), 2);
            e.Graphics.DrawRectangle(pen, _rect);
        }
    }
}
