; WavenVoIP Setup — Inno Setup 6
; Instala em %LOCALAPPDATA%\WavenVoIP\App\ sem privilégios de administrador
; NUNCA toca em %APPDATA%\WavenVoIP\ (configs do usuário)

#define AppName        "WavenVoIP"
#define AppVersion     "2.3.7"
#define AppPublisher   "Almeida Gás"
#define AppURL         "https://almeidagas.com/waven/"
#define AppExeName     "WavenVoIP.exe"
#define PublishDir     "publish"
#define AppCopyright   "Copyright © 2026 Almeida Gás"

[Setup]
AppId={{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} v{#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
AppUpdatesURL={#AppURL}
DefaultDirName={localappdata}\WavenVoIP\App
DefaultGroupName={#AppName}
DisableDirPage=yes
DisableProgramGroupPage=yes
OutputDir=.
OutputBaseFilename=WavenVoIP_Setup
SetupIconFile={#PublishDir}\Assets\wavenvoip.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
MinVersion=10.0.17763
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
CloseApplicationsFilter=WavenVoIP.exe,WavenUpdater.exe
RestartApplications=no
CreateUninstallRegKey=yes

; Informações de versão visíveis no Explorer / Painel de Controle
VersionInfoVersion={#AppVersion}.0
VersionInfoCompany={#AppPublisher}
VersionInfoDescription=Instalador do {#AppName}
VersionInfoTextVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoProductTextVersion={#AppVersion}
VersionInfoCopyright={#AppCopyright}

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[CustomMessages]
brazilianportuguese.InstallingDotNet=Instalando .NET 8 Desktop Runtime, aguarde...
brazilianportuguese.DownloadingDotNet=Baixando .NET 8 Desktop Runtime (~55 MB)...

[Tasks]
Name: "desktopicon";    Description: "Criar atalho na Área de Trabalho"; GroupDescription: "Atalhos:"
Name: "startmenuicon";  Description: "Criar atalho no Menu Iniciar";     GroupDescription: "Atalhos:"
Name: "preserveconfig"; Description: "Preservar configurações de usuário existentes (manter login, ramal e senha atuais)"; GroupDescription: "Opções de instalação:"; Flags: unchecked

[Files]
; Executáveis
Source: "{#PublishDir}\WavenVoIP.exe";      DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\WavenUpdater.exe";   DestDir: "{app}"; Flags: ignoreversion

; DLLs e manifests (excluindo dados do usuário)
Source: "{#PublishDir}\*.dll";              DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\*.json";             DestDir: "{app}"; Flags: ignoreversion; Excludes: "sipconfig.json,sipconfig.backup.json,sipconfig.backup1.json,sipconfig.backup2.json,sipconfig.backup3.json,contatos.json,historico.json,audio-config.json,whatsapp_config.json,whatsapp_envios.json,google_contacts_cache.json,user.config,settings.json"
Source: "{#PublishDir}\*.pdb";             DestDir: "{app}"; Flags: ignoreversion

; Assets (áudio + ícone root)
Source: "{#PublishDir}\Assets\*";           DestDir: "{app}\Assets"; Flags: ignoreversion
; Ícone — raiz Assets\ (usado pelos atalhos e WPF runtime)
Source: "{#PublishDir}\Assets\wavenvoip.ico"; DestDir: "{app}\Assets"; Flags: ignoreversion

; Google OAuth credentials (credencial do app, não dado do usuário)
Source: "{#PublishDir}\Config\google_credentials.json"; DestDir: "{app}\Config"; Flags: ignoreversion
; Contatos da empresa (distribuído para todos os ramais, atualizado via sync remoto)
Source: "{#PublishDir}\Config\company-contacts.json";   DestDir: "{app}\Config"; Flags: ignoreversion

[Icons]
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\Assets\wavenvoip.ico"; Tasks: desktopicon
Name: "{userprograms}\{#AppName}\{#AppName}"; Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\Assets\wavenvoip.ico"; Tasks: startmenuicon
Name: "{userprograms}\{#AppName}\Desinstalar {#AppName}"; Filename: "{uninstallexe}"; Tasks: startmenuicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Iniciar {#AppName} agora"; Flags: nowait postinstall skipifsilent

[Code]

// ── Detecção de .NET 8 Desktop Runtime ──────────────────────────────────────
// Usa FindFirst com wildcard para escanear o diretório de runtimes instalados.
// Mais confiável que registry (que fica em 64-bit hive, invisível para IS 32-bit).

function IsDotNet8DesktopInstalled(): Boolean;
var
  DotNetDir: String;
  FindRec: TFindRec;
begin
  Result := False;
  // .NET Desktop Runtime instala em: C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App\{ver}\
  DotNetDir := ExpandConstant('{pf64}') + '\dotnet\shared\Microsoft.WindowsDesktop.App\';
  if FindFirst(DotNetDir + '8.*', FindRec) then
  begin
    try
      repeat
        // Qualquer subdiretório "8.*" indica que .NET 8 está instalado
        if FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0 then
        begin
          Result := True;
          Exit;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

// ── PrepareToInstall: baixa e instala .NET 8 silenciosamente se necessário ───
// Usa System.Net.WebClient via PowerShell — sem plugin externo, funciona em
// Win10/11 com qualquer versão do PS disponível.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  TempExe, PSExe, PSArgs: String;
  ResultCode: Integer;
begin
  Result := '';
  NeedsRestart := False;

  if IsDotNet8DesktopInstalled() then
    Exit; // .NET 8 presente — prosseguir normalmente

  // .NET 8 ausente — baixar silenciosamente via PowerShell/WebClient
  WizardForm.StatusLabel.Caption := ExpandConstant('{cm:DownloadingDotNet}');

  TempExe := ExpandConstant('{tmp}') + '\dotnet8-runtime.exe';
  PSExe   := ExpandConstant('{sys}') + '\WindowsPowerShell\v1.0\powershell.exe';

  // WebClient.DownloadFile é síncrono e funciona em PS 2.0+
  PSArgs := '-NoProfile -NonInteractive -WindowStyle Hidden -Command ' +
            '"[Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12;' +
            '[System.Net.WebClient]::new().DownloadFile(' +
            '''https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe'',' +
            '''' + TempExe + ''')"';

  Exec(PSExe, PSArgs, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  if not FileExists(TempExe) then
  begin
    Result := 'Não foi possível baixar o .NET 8 Desktop Runtime.' + #13#10 +
              'Instale manualmente: https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe';
    Exit;
  end;

  WizardForm.StatusLabel.Caption := ExpandConstant('{cm:InstallingDotNet}');
  Exec(TempExe, '/quiet /norestart', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if ResultCode <> 0 then
    Result := 'Falha ao instalar .NET 8 Desktop Runtime. Código: ' + IntToStr(ResultCode);
end;

// ── fresh_install.flag: criado quando "Preservar configurações" NÃO está marcado ──
// O app lê esse arquivo no startup, reseta a identidade do usuário e abre SetupWindow.
// Configurações avançadas (AMI, CDR, WhatsApp, servidor SIP) são PRESERVADAS.
// O flag é apagado pelo app após o usuário cadastrar um novo usuário com sucesso.

procedure CurStepChanged(CurStep: TSetupStep);
var
  FlagDir, FlagFile: String;
begin
  if CurStep = ssPostInstall then
  begin
    FlagDir  := ExpandConstant('{localappdata}') + '\WavenVoIP';
    FlagFile := FlagDir + '\fresh_install.flag';

    if WizardIsTaskSelected('preserveconfig') then
    begin
      // Usuário escolheu preservar — garantir que flag antiga não exista
      if FileExists(FlagFile) then
        DeleteFile(FlagFile);
    end
    else
    begin
      // Instalação limpa — criar flag para forçar SetupWindow
      ForceDirectories(FlagDir);
      SaveStringToFile(FlagFile, 'FRESH_INSTALL', False);
    end;
  end;
end;

// ── Garantia: %APPDATA%\WavenVoIP\ nunca é tocado pelo instalador ────────────
// (proteção por omissão — esse diretório simplesmente não aparece em [Files])
