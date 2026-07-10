; Instalador do Tradutor de Reuniões (GeminiTranslateLite).
;
; O VB-CABLE e o Hi-Fi Cable ASIO Bridge sao redistribuidos AQUI SEM MODIFICACAO,
; conforme a licenca do VB-CABLE (readme.txt em vendor\VBCABLE) permite diffusion
; "AS IS". A licenca proibe integrar o instalador deles em outro procedimento de
; instalacao sem autorizacao do autor -- por isso este setup NAO os executa
; silenciosamente: ele copia os pacotes originais para o disco e cria atalhos
; para o usuario rodar cada um manualmente (cada um abre a propria tela/UAC).
;
; Credito exigido pela licenca: origem em https://www.vb-cable.com -- VB-CABLE
; e um donationware.

#define MyAppName "Tradutor de Reunioes (Gemini Live)"
#define MyAppExeName "GeminiTranslateLite.exe"
#define MyAppPublisher "Projeto Gemini Live Translate"
#define MyAppVersion "1.0"

[Setup]
AppId={{B1B6C9B0-6E7B-4B8B-9B7B-6C6E6B6B6B6B}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\GeminiTranslateLite
DefaultGroupName=Tradutor de Reunioes
DisableProgramGroupPage=yes
OutputDir=output
OutputBaseFilename=TradutorReunioes-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Files]
Source: "..\publish-lite\GeminiTranslateLite.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "vendor\VBCABLE\*"; DestDir: "{app}\Drivers\VB-CABLE"; Flags: ignoreversion recursesubdirs
Source: "vendor\HiFiCable\*"; DestDir: "{app}\Drivers\HiFiCable"; Flags: ignoreversion recursesubdirs
Source: "LEIA-ME.txt"; DestDir: "{app}\Drivers"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\1 - Instalar VB-CABLE (audio virtual)"; Filename: "{app}\Drivers\VB-CABLE\VBCABLE_Setup_x64.exe"
Name: "{group}\2 - Instalar Hi-Fi Cable ASIO Bridge"; Filename: "{app}\Drivers\HiFiCable\HiFiCableAsioBridgeSetup.exe"
Name: "{group}\Pasta dos instaladores de audio"; Filename: "{app}\Drivers"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{group}\Desinstalar"; Filename: "{uninstallexe}"

[Tasks]
Name: "desktopicon"; Description: "Criar atalho na area de trabalho"; GroupDescription: "Atalhos adicionais:"

[Run]
Filename: "{app}\Drivers"; Description: "Abrir a pasta dos instaladores de audio (VB-CABLE / Hi-Fi Cable)"; Flags: postinstall shellexec skipifsilent unchecked
Filename: "{app}\{#MyAppExeName}"; Description: "Abrir o Tradutor de Reunioes agora"; Flags: postinstall nowait skipifsilent unchecked

[Code]
procedure InitializeWizard();
begin
  WizardForm.FinishedLabel.Caption :=
    'Instalacao concluida!' + #13#10 + #13#10 +
    'Falta so mais um passo, feito uma unica vez, para a outra pessoa te ouvir traduzido:' + #13#10 + #13#10 +
    '1) No menu Iniciar, abra "1 - Instalar VB-CABLE" e clique em Install (aceite o aviso do Windows).' + #13#10 +
    '2) Abra "2 - Instalar Hi-Fi Cable ASIO Bridge" e siga o assistente (Next, Next, Install).' + #13#10 +
    '3) REINICIE o computador (os dois pedem isso para funcionar).' + #13#10 +
    '4) Abra o Tradutor de Reunioes, cole sua API key e escolha os dispositivos de audio.' + #13#10 + #13#10 +
    'Esses dois atalhos continuam disponiveis no menu Iniciar a qualquer momento.';
end;
