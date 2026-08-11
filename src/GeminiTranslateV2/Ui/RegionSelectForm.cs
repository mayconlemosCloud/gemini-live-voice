using System.Drawing;
using System.Windows.Forms;
using GeminiTranslate.App.Platform;
using GeminiTranslate.Infrastructure.Windows;

namespace GeminiTranslate.App.Ui;

/// <summary>
/// Overlay em tela cheia, cobrindo todos os monitores, para o usuário arrastar e selecionar uma
/// região. Soltar o botão confirma; Esc cancela.
/// </summary>
/// <remarks>
/// Devolve o retângulo em pixels físicos de tela, compatível com <see cref="ScreenCapture"/>.
/// </remarks>
public sealed class RegionSelectForm : Form
{
    private const int MinDragPixels = 3;

    private Point _start;
    private Rectangle _selection;
    private bool _dragging;

    /// <summary>Região escolhida, em pixels de tela. Vazia quando cancelado.</summary>
    public Rectangle SelectedRegion { get; private set; }

    /// <summary>Cria o overlay escurecido cobrindo a área virtual da tela.</summary>
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

    /// <summary>O retângulo de seleção também não deve vazar para quem vê sua tela.</summary>
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Stealth.ApplyToHandle(Handle);
    }

    /// <inheritdoc />
    protected override void OnMouseDown(MouseEventArgs e)
    {
        _start = e.Location;
        _dragging = true;
        base.OnMouseDown(e);
    }

    /// <inheritdoc />
    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging)
        {
            _selection = Rectangle.FromLTRB(
                Math.Min(_start.X, e.X), Math.Min(_start.Y, e.Y),
                Math.Max(_start.X, e.X), Math.Max(_start.Y, e.Y));
            Invalidate();
        }
        base.OnMouseMove(e);
    }

    /// <summary>Confirma a seleção, convertendo de coordenadas do formulário para pixels de tela.</summary>
    protected override void OnMouseUp(MouseEventArgs e)
    {
        _dragging = false;

        bool valid = _selection.Width > MinDragPixels && _selection.Height > MinDragPixels;
        if (valid)
        {
            SelectedRegion = new Rectangle(
                _selection.X + Bounds.X, _selection.Y + Bounds.Y,
                _selection.Width, _selection.Height);
        }

        DialogResult = valid ? DialogResult.OK : DialogResult.Cancel;
        Close();
        base.OnMouseUp(e);
    }

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            DialogResult = DialogResult.Cancel;
            Close();
        }
        base.OnKeyDown(e);
    }

    /// <summary>Clareia a área selecionada e desenha a borda.</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_selection.Width <= 0 || _selection.Height <= 0) return;

        using var fill = new SolidBrush(Color.FromArgb(60, 30, 120, 220));
        e.Graphics.FillRectangle(fill, _selection);

        using var border = new Pen(Color.FromArgb(230, 77, 171, 247), 2);
        e.Graphics.DrawRectangle(border, _selection);
    }
}
