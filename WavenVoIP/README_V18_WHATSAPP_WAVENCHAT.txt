Versão V18 - Integração WhatsApp ZPRO/Waven Chat

Implementado:
- Tela de configuração com URL da API, Bearer Token, número de teste e botão Enviar teste.
- Envio via GET /params/ com query body, number, externalKey e bearertoken.
- Header Authorization: Bearer TOKEN.
- Normalização de telefone para 55 + DDD + número.
- Remove automaticamente o primeiro dígito de rota antes do envio por WhatsApp:
  1 = Operadora, 2 = WhatsApp TIM, 3 = WhatsApp Vivo.
  Exemplo: 266984671226 -> 66984671226 -> 5566984671226.
- Botão Enviar WhatsApp na janela de chamada.
- Botão WhatsApp na tela de Contatos.
- Botão WhatsApp na tela de Histórico.
- Log local dos envios em AppData/Local/WavenVoIP/whatsapp_envios.json.
- ExternalKey único para evitar duplicidade.

Arquivos principais:
- Services/WhatsAppService.cs
- Services/WhatsAppConfigService.cs
- Services/WhatsAppLogService.cs
- Models/WhatsAppConfig.cs
- Models/WhatsAppEnvioLog.cs
- Models/WhatsAppResultado.cs
- Views/WhatsAppMessageWindow.xaml
- Views/WhatsAppMessageWindow.xaml.cs

Observação:
Não foi alterado o núcleo SIP de ligação/recebimento nesta versão; a integração WhatsApp foi adicionada de forma isolada para não atrapalhar o funcionamento das chamadas.
