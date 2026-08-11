using System.Windows;
using GeminiTranslate.Core.Contracts;
using GeminiTranslate.Core.Diagnostics;

namespace GeminiTranslate.App.Ui;

/// <summary>
/// Dono da única janela de sugestão: reaproveita-a a cada pergunta e cancela a consulta anterior.
/// </summary>
/// <remarks>
/// Uma janela por pergunta era o desenho anterior, e ele tinha três problemas. As janelas se
/// empilhavam na mesma posição, dando a impressão de que fechar não funcionava. A resposta de uma
/// consulta antiga chegava depois e escrevia numa janela já fechada. E reabrir a mesma pergunta
/// gastava outra requisição da cota gratuita, que é por requisição e não por token.
/// </remarks>
public sealed class SuggestionPresenter(Window owner)
{
    private SuggestionWindow? _window;
    private CancellationTokenSource? _pending;
    private string? _currentQuestion;

    /// <summary>
    /// Mostra a sugestão para <paramref name="question"/>, consultando a IA.
    /// </summary>
    /// <remarks>
    /// Clicar de novo na pergunta que já está na tela apenas traz a janela para frente: repetir a
    /// consulta não traria informação nova e consumiria cota à toa.
    /// </remarks>
    /// <param name="assistant">Assistente a consultar.</param>
    /// <param name="question">A pergunta a responder.</param>
    /// <param name="context">Transcrição que ajuda a desambiguar a pergunta.</param>
    public async Task ShowAsync(IAssistant assistant, string question, string context)
    {
        if (_window is not null && _currentQuestion == question)
        {
            Log.Write("Sugestão", "mesma pergunta já na tela — trazendo para frente.");
            _window.Activate();
            return;
        }

        var (window, token) = BeginQuestion(question);

        try
        {
            var answer = await assistant.SuggestAnswerAsync(question, context, token);
            if (IsStillCurrent(window, token)) window.SetAnswer(answer);
        }
        catch (OperationCanceledException)
        {
            Log.Write("Sugestão", "consulta cancelada — outra pergunta tomou a janela.");
        }
        catch (Exception ex)
        {
            if (IsStillCurrent(window, token)) window.SetError(ex.Message);
            Log.Write("Sugestão", "falha ao sugerir resposta: " + ex);
        }
    }

    /// <summary>Fecha a janela, se houver. Chamado ao parar a tradução.</summary>
    public void Close()
    {
        _pending?.Cancel();
        try { _window?.Close(); } catch { }
    }

    /// <summary>Descarta a consulta em voo e prepara a janela para a pergunta nova.</summary>
    private (SuggestionWindow Window, CancellationToken Token) BeginQuestion(string question)
    {
        _pending?.Cancel();
        _pending?.Dispose();
        _pending = new CancellationTokenSource();

        _currentQuestion = question;
        Log.Write("Sugestão", $"consultando a IA para: '{question}'");

        var window = EnsureWindow();
        window.BeginQuestion(question);
        return (window, _pending.Token);
    }

    /// <summary>
    /// Se a resposta que acabou de chegar ainda é a que a janela espera. Uma pergunta nova, ou o
    /// fechamento da janela, tornam a anterior irrelevante.
    /// </summary>
    private bool IsStillCurrent(SuggestionWindow window, CancellationToken token) =>
        !token.IsCancellationRequested && ReferenceEquals(_window, window);

    private SuggestionWindow EnsureWindow()
    {
        if (_window is not null) return _window;

        var window = new SuggestionWindow { Owner = owner };
        window.Closed += (_, _) =>
        {
            _window = null;
            _currentQuestion = null;
            _pending?.Cancel();
            Log.Write("Sugestão", "janela fechada.");
        };

        _window = window;
        window.Show();
        return window;
    }
}
