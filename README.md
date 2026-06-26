# Tradutor de Reuniões — Gemini 3.5 Live

App de desktop (Windows / WPF) que traduz **voz para voz em tempo real** nas suas
reuniões (Teams, Google Meet, WhatsApp, Zoom, etc.) usando o modelo
`gemini-3.5-live-translate-preview` da Google.

- A **outra pessoa fala em inglês** → você ouve em **português** no seu fone.
- **Você fala em português** → ela ouve em **inglês** na reunião.

A tradução é nativa voz‑para‑voz (não transcreve → traduz → sintetiza), então a
latência é baixa e a entonação é preservada.

---

## Como funciona

São **duas sessões** independentes da Live API rodando ao mesmo tempo (cada sessão
traduz para **um** idioma só):

```
ENTRADA  (entender o outro)
  Áudio da reunião  ──loopback──►  Gemini (alvo: PT)  ──►  seu fone

SAÍDA    (eles te entenderem)
  Seu microfone     ───────────►  Gemini (alvo: EN)  ──►  microfone virtual ──► Teams/Meet
```

Formato de áudio (cuidado pelo app automaticamente): entrada PCM 16‑bit **16 kHz**
mono, saída PCM 16‑bit **24 kHz** mono, enviada em blocos de ~100 ms.

---

## Pré‑requisitos

1. **Windows 10/11** e **.NET 9 SDK** (`dotnet --version`).
2. **API key** do Google AI Studio com acesso ao modelo
   `gemini-3.5-live-translate-preview` (é preview — pode exigir habilitação na conta).
3. **VB‑CABLE** (cabo de áudio virtual, grátis) — necessário para a direção de **saída**:
   https://vb-audio.com/Cable/ — instale e reinicie.

> Sem o VB‑CABLE você ainda usa a direção de **entrada** (entender o outro).
> Para falar de volta traduzido, o cabo virtual é obrigatório: nenhum app injeta
> áudio no microfone de outro programa sem esse intermediário.

---

## Roteamento de áudio (a parte importante)

O segredo é **não capturar a sua própria tradução de volta**. Configure assim:

### Direção de SAÍDA (você → eles, em inglês)
1. No app, **Meu microfone** = seu microfone real.
2. No app, **Microfone virtual** = `CABLE Input (VB-Audio Virtual Cable)`.
3. No **Teams/Meet/WhatsApp**, defina o **microfone** como
   `CABLE Output (VB-Audio Virtual Cable)`.

Resultado: você fala → app traduz para inglês → joga no CABLE Input → a reunião
escuta pelo CABLE Output como se fosse seu mic.

### Direção de ENTRADA (eles → você, em português)
Você precisa capturar **só a voz da outra pessoa**, sem misturar a tradução que
toca no seu fone. Duas opções:

- **Simples (1 fone, pode ter eco leve):** no app, **Áudio da reunião** = o
  dispositivo onde a reunião toca (seu fone) e **Ouvir tradução** = o mesmo fone.
  O app avisa que pode haver eco. Funciona, mas não é o ideal.

- **Recomendado (sem eco):** instale um **segundo** cabo virtual (ex.: VB‑CABLE A+B
  ou VoiceMeeter). Aponte a **saída de som** do Teams/Meet para esse segundo cabo,
  configure no app **Áudio da reunião** = a saída desse cabo e **Ouvir tradução** =
  seu fone real. Assim a captura pega só o inglês limpo da outra pessoa.

O app mostra um ⚠ aviso quando "Áudio da reunião" e "fone" são o mesmo dispositivo.

---

## Rodar

```powershell
cd src\GeminiLiveTranslate
dotnet run -c Release
```

Ou compile e execute o `.exe`:

```powershell
dotnet build -c Release
.\bin\Release\net9.0-windows\GeminiLiveTranslate.exe
```

No app:
1. Cole sua **API key**.
2. Escolha os dispositivos conforme o roteamento acima.
3. Escolha os idiomas (entrada: o que **você** entende; saída: o que **eles** ouvem).
4. **Iniciar**. Os painéis mostram a transcrição ao vivo dos dois lados.

As configurações (incluindo a API key) ficam em
`%AppData%\GeminiLiveTranslate\settings.json`.

---

## Estrutura do código

```
src/GeminiLiveTranslate/
  Audio/
    AudioDeviceService.cs      Enumera/resolve dispositivos WASAPI
    AudioCaptureSource.cs      Captura mic/loopback → 16 kHz mono PCM (com gate de silêncio)
    PcmPlayer.cs               Toca 24 kHz mono → dispositivo escolhido (resampla p/ mix format)
    ChannelSampleProviders.cs  Conversão de canais (down/up-mix)
  Gemini/
    GeminiLiveClient.cs        WebSocket BidiGenerateContent + translationConfig
  Translation/
    TranslationDirection.cs    Uma direção: captura → Gemini → playback
    TranslationEngine.cs       Orquestra as duas direções
  Config/
    AppSettings.cs             Persistência em %AppData%
    Languages.cs               Lista de idiomas
  MainWindow.xaml(.cs)         UI estilo Teams/Meet
```

---

## Limitações conhecidas (do modelo, segundo a Google)

- A "clonagem" da voz pode oscilar após pausas longas ou em troca rápida de falantes.
- Detecção de idioma pode falhar com sotaques fortes ou troca brusca de língua.
- Filtragem de ruído de fundo não é perfeita.
- O modelo é **preview** — o nome do modelo e campos da API podem mudar.

## Logs (diagnóstico)

O app registra **tudo** para facilitar a depuração:

- **Painel "Log ao vivo"** na parte de baixo da janela (com **Copiar** e **Abrir pasta**).
- **Arquivo por sessão** em `%AppData%\GeminiLiveTranslate\logs\session-AAAAMMDD-HHMMSS.log`.

O que é logado: dispositivos enumerados e seus formatos, o JSON de `setup`
enviado, **cada mensagem recebida** do servidor (áudio base64 é encurtado para
`<N chars>`), `setupComplete`, transcrições original/traduzida, contadores de
chunks enviados/recebidos, estatísticas de captura a cada 5 s (nível, silêncio,
gate), e qualquer exceção com stack trace. A API key aparece mascarada.

**Para eu (Claude) te ajudar a entender um problema:** reproduza, clique em
**Copiar** no painel de log (ou pegue o arquivo `.log` mais recente) e me cole o
conteúdo.

## Segurança

A API key é gravada em texto puro em `%AppData%`. Para produção, a Google
recomenda **ephemeral tokens** em vez da API key direta. Trate este projeto como
ferramenta pessoal/local.

## Fontes da documentação

- Live API (visão geral): https://ai.google.dev/gemini-api/docs/live-api
- Tradução ao vivo: https://ai.google.dev/gemini-api/docs/live-api/live-translate
- Modelo: https://ai.google.dev/gemini-api/docs/models/gemini-3.5-live-translate-preview
- Anúncio: https://blog.google/innovation-and-ai/models-and-research/gemini-models/gemini-live-3-5-translate/
