using System.Windows;
using System.Windows.Input;
using GeminiTranslate.Core.Diagnostics;
using GeminiTranslate.Core.Session;

namespace GeminiTranslate.App.Ui;

/// <summary>
/// Parte da janela principal que hospeda o assistente: a aba onde a conversa com a IA acontece.
/// </summary>
/// <remarks>
/// A conversa vive AQUI, num lugar só. A barra flutuante sobre a reunião é um controle remoto
/// desta aba: ela dispara a ação e traz esta janela à frente com o resultado. Antes as duas
/// tinham chat próprio e históricos separados para a mesma função.
/// </remarks>
public partial class MainWindow
{
    /// <summary>Tempo para a interface sumir da tela antes de uma captura.</summary>
    private const int HideBeforeCaptureMs = 120;

    /// <summary>Liga o controlador da sessão à aba e à barra flutuante.</summary>
    private AssistantController BuildAssistant(TranslationSession session)
    {
        var assistant = new AssistantController(session.Assistant!, session.Context, _services.ScreenCapture)
        {
            CaptureWithUiHidden = CaptureHidingAppAsync,
            SelectRegion = SelectRegionAsync
        };

        assistant.StatusChanged += status =>
            Dispatcher.BeginInvoke(() => AssistantStatus.Text = status);
        assistant.ChatChanged += chat =>
            Dispatcher.BeginInvoke(() =>
            {
                AssistantChatBox.Text = chat;
                AssistantChatBox.ScrollToEnd();
            });
        assistant.ResultReady += () => Dispatcher.BeginInvoke(BringAssistantToFront);

        return assistant;
    }

    /// <summary>
    /// Traz a janela à frente, na aba do assistente, porque é onde a resposta vai aparecer.
    /// </summary>
    /// <remarks>
    /// Necessário porque a ação costuma vir da barra flutuante, com esta janela minimizada ou
    /// escondida atrás do app de reunião — sem isso a resposta chegaria fora de vista.
    /// </remarks>
    private void BringAssistantToFront()
    {
        ShowView(Section.Assistant);

        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>Habilita ou desabilita os controles da aba conforme o assistente existir.</summary>
    private void SetAssistantAvailable(bool available)
    {
        foreach (var control in AssistantControls()) control.IsEnabled = available;

        if (available) return;

        AssistantChatBox.Text = "";
        AssistantStatus.Text = "ligue o assistente em Configurar e inicie a tradução";
    }

    private System.Windows.Controls.Control[] AssistantControls() =>
    [
        AssistantScreenButton, AssistantRegionButton, AssistantSuggestButton,
        AssistantSendButton, AssistantInputBox, AssistantCopyButton, AssistantClearButton
    ];

    /// <summary>Esconde a janela e a barra flutuante, captura, e devolve as duas.</summary>
    private async Task<byte[]> CaptureHidingAppAsync(Func<byte[]> capture)
    {
        bool wasVisible = IsVisible;
        if (wasVisible) Hide();
        if (_overlay is not null) _overlay.Visibility = Visibility.Hidden;

        await Task.Delay(HideBeforeCaptureMs);

        try
        {
            return capture();
        }
        finally
        {
            if (_overlay is not null) _overlay.Visibility = Visibility.Visible;
            if (wasVisible) Show();
        }
    }

    /// <summary>Esconde a interface, deixa o usuário arrastar a região, e a devolve.</summary>
    private async Task<System.Drawing.Rectangle> SelectRegionAsync()
    {
        bool wasVisible = IsVisible;
        if (wasVisible) Hide();
        if (_overlay is not null) _overlay.Visibility = Visibility.Hidden;

        await Task.Delay(HideBeforeCaptureMs);

        try
        {
            using var form = new RegionSelectForm();
            return form.ShowDialog() == System.Windows.Forms.DialogResult.OK
                ? form.SelectedRegion
                : default;
        }
        finally
        {
            if (_overlay is not null) _overlay.Visibility = Visibility.Visible;
            if (wasVisible) Show();
        }
    }

    private void OnAssistantScreen(object sender, RoutedEventArgs e) =>
        _ = _assistant?.CaptureScreenAsync();

    private void OnAssistantRegion(object sender, RoutedEventArgs e) =>
        _ = _assistant?.CaptureRegionAsync();

    private void OnAssistantSuggest(object sender, RoutedEventArgs e) =>
        _ = _assistant?.SuggestAsync();

    private void OnAssistantSend(object sender, RoutedEventArgs e) => SendAssistantInput();

    /// <summary>Enter envia; Shift+Enter continua na linha de baixo.</summary>
    private void OnAssistantInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || (Keyboard.Modifiers & ModifierKeys.Shift) != 0) return;

        e.Handled = true;
        SendAssistantInput();
    }

    /// <summary>
    /// Manda a pergunta digitada e limpa o campo já.
    /// </summary>
    /// <remarks>
    /// Limpar antes da resposta é proposital: se a chamada demorar, o usuário não fica olhando a
    /// própria pergunta ainda no campo sem saber se ela foi enviada.
    /// </remarks>
    private void SendAssistantInput()
    {
        if (_assistant is null) return;

        var question = AssistantInputBox.Text.Trim();
        if (question.Length == 0) return;

        AssistantInputBox.Text = "";
        _ = _assistant.AskAsync(question);
    }

    private void OnAssistantCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(AssistantChatBox.Text);
            Log.Write("Assistente", "chat copiado.");
        }
        catch (Exception ex)
        {
            Log.Write("Assistente", "falha ao copiar: " + ex.Message);
        }
    }

    private void OnAssistantClear(object sender, RoutedEventArgs e) => _assistant?.ClearChat();

    private void OnAssistantSetup(object sender, RoutedEventArgs e) => ShowView(Section.Setup);
}
