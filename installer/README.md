# Instalador (TradutorReunioes-Setup.exe)

Gera um único `.exe` que instala o `GeminiTranslateLite` e deixa os drivers de
áudio virtual (VB-CABLE e Hi-Fi Cable ASIO Bridge) prontos para instalação
manual — a licença do VB-CABLE proíbe rodar o instalador dele silenciosamente
dentro de outro instalador, então o setup copia os pacotes originais (sem
modificação) e cria atalhos no menu Iniciar para o usuário rodar cada um.

## Pré-requisitos para gerar o instalador

- [Inno Setup 6](https://jrsoftware.org/isdl.php) (`ISCC.exe` no PATH ou em
  `C:\Program Files (x86)\Inno Setup 6`).
- Os pacotes originais dos drivers, baixados de vb-audio.com, colocados em:
  - `installer/vendor/VBCABLE/` — conteúdo do `VBCABLE_Driver_Pack` (zip de
    https://vb-audio.com/Cable/, extraído direto, sem modificar nada).
  - `installer/vendor/HiFiCable/` — `HiFiCableAsioBridgeSetup.exe` extraído do
    zip de https://vb-audio.com/Cable/ (seção Hi-Fi Cable).

  Essa pasta é ignorada pelo git (binários de terceiros, redistribuição
  "as is" conforme a licença deles — não fazem parte do código do projeto).

## Build

```powershell
dotnet publish src\GeminiTranslateLite\GeminiTranslateLite.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish-lite

cd installer
& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" setup.iss
```

Resultado: `installer/output/TradutorReunioes-Setup.exe`.
