using GeminiTranslate.Core.Session;
using Xunit;

namespace GeminiTranslate.Core.Tests;

/// <summary>Comportamento do contexto que alimenta as ações de IA.</summary>
public class ConversationContextTests
{
    [Fact]
    public void ComecaVazio()
    {
        Assert.True(new ConversationContext().IsEmpty);
    }

    [Fact]
    public void AgrupaFragmentosDoMesmoFalanteNaMesmaLinha()
    {
        var context = new ConversationContext();

        context.Add("Eles", "bom ");
        context.Add("Eles", "dia");

        Assert.Equal("Eles: bom dia", context.GetRecent());
    }

    [Fact]
    public void AbreLinhaNovaQuandoOFalanteMuda()
    {
        var context = new ConversationContext();

        context.Add("Eles", "tudo bem?");
        context.Add("Você", "tudo");

        Assert.Equal("Eles: tudo bem?\nVocê: tudo", context.GetRecent());
    }

    [Fact]
    public void FragmentoVazioNaoAbreFalante()
    {
        var context = new ConversationContext();

        context.Add("Eles", "");

        Assert.True(context.IsEmpty);
    }

    [Fact]
    public void LimitaAoTamanhoPedido()
    {
        var context = new ConversationContext();
        context.Add("Eles", new string('x', 500));

        Assert.Equal(100, context.GetRecent(100).Length);
    }

    [Fact]
    public void PerguntaRecemDetectadaEstaDisponivel()
    {
        var context = new ConversationContext();

        context.NoteQuestion("  quanto tempo leva?  ");

        Assert.Equal("quanto tempo leva?", context.RecentQuestion);
    }

    [Fact]
    public void PerguntaEmBrancoEIgnorada()
    {
        var context = new ConversationContext();

        context.NoteQuestion("   ");

        Assert.Null(context.RecentQuestion);
    }

    [Fact]
    public void LimparApagaConversaEPergunta()
    {
        var context = new ConversationContext();
        context.Add("Eles", "oi");
        context.NoteQuestion("tudo bem?");

        context.Clear();

        Assert.True(context.IsEmpty);
        Assert.Null(context.RecentQuestion);
    }
}
