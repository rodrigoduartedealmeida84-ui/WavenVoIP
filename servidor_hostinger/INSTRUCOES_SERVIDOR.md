# Instruções — Servidor de Atualizações (Hostinger)

## Estrutura esperada em https://almeidagas.com/waven/

```
waven/
├── version.json        ← metadados da versão atual
└── WavenVoIP.zip       ← pacote do aplicativo
```

---

## version.json — campos obrigatórios

| Campo    | Descrição                                        |
|----------|--------------------------------------------------|
| versao   | Versão nova (ex: "1.2.0") — deve ser maior que a local para disparar update |
| zip      | URL completa do ZIP                              |
| sha256   | Hash SHA-256 do ZIP em hexadecimal (64 chars)   |
| data     | Data da build (informativo)                      |
| notas    | Descrição opcional das mudanças                  |

---

## Como gerar o SHA-256 do ZIP no PowerShell

```powershell
(Get-FileHash .\WavenVoIP.zip -Algorithm SHA256).Hash.ToLower()
```

Cole o resultado no campo `sha256` do version.json.

---

## Estrutura interna do WavenVoIP.zip

O ZIP deve conter os arquivos do aplicativo na raiz (sem subpasta envolvendo tudo):

```
WavenVoIP.zip
├── WavenVoIP.exe
├── WavenUpdater.exe        ← incluir sempre!
├── *.dll                   ← todas as DLLs do publish
├── Assets\
│   ├── wavenvoip.ico
│   ├── toque_padrao.mp3
│   ├── ringback_tuuu.wav
│   └── ...
└── Config\
    └── google_credentials.json
```

IMPORTANTE: NÃO incluir no ZIP:
- sipconfig.json            (fica em %APPDATA%\WavenVoIP — não é tocado)
- sipconfig.backup.json
- Qualquer arquivo de configuração do usuário

---

## Diretórios usados pelo sistema

| Pasta                              | Conteúdo                         |
|------------------------------------|----------------------------------|
| %LOCALAPPDATA%\WavenVoIP\App\      | Arquivos do aplicativo (atualizados pelo updater) |
| %APPDATA%\WavenVoIP\               | sipconfig.json e configs do usuário (NUNCA tocado pelo updater) |
| %LOCALAPPDATA%\WavenVoIP\Logs\     | Logs (ui_flow, sip_signal, update, etc.) |
| %LOCALAPPDATA%\WavenVoIP\Backup\   | Backups automáticos (últimos 3) |

---

## Fluxo completo de publicação de nova versão

1. Compilar Release:
   ```
   dotnet publish WavenVoIP\WavenVoIP.csproj -c Release -r win-x64 --self-contained false
   ```

2. Criar ZIP com todo o conteúdo da pasta publish (sem subpasta raiz):
   ```powershell
   Compress-Archive -Path ".\publish\*" -DestinationPath ".\WavenVoIP.zip" -Force
   ```

3. Gerar SHA-256:
   ```powershell
   (Get-FileHash .\WavenVoIP.zip -Algorithm SHA256).Hash.ToLower()
   ```

4. Atualizar version.json com a nova versão e o SHA-256.

5. Fazer upload via FTP/Hostinger:
   - WavenVoIP.zip → /waven/WavenVoIP.zip
   - version.json  → /waven/version.json

6. Os clientes detectarão a atualização automaticamente em até 8 segundos após abrir o WavenVoIP.

---

## Logs de atualização

Cada cliente grava em:
`%LOCALAPPDATA%\WavenVoIP\Logs\update.log`

Tags dos logs:
- `[CHECK]` — verificação de versão
- `[DISPONIVEL]` — update detectado
- `[UPDATER_START]` — WavenUpdater iniciou
- `[UPDATER_DOWNLOAD_OK]` — download concluído
- `[UPDATER_INTEGRITY_OK]` — SHA-256 verificado
- `[UPDATER_BACKUP_OK]` — backup criado
- `[UPDATER_SUCCESS]` — update aplicado com sucesso
- `[UPDATER_ERROR]` — erro (com rollback automático)
- `[UPDATER_ROLLBACK_OK]` — versão anterior restaurada
