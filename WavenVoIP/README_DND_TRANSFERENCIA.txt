ATUALIZAÇÃO APLICADA
- botão DND automático na tela principal
  - ativa com *78
  - desativa com *79
- transferência cega automática no popup de chamada
  - envia ## + ramal + #
- transferência assistida automática no popup de chamada
  - envia *2 + ramal + #
- nova janela simples para digitar o ramal de destino da transferência

Substituições principais:
- src/WavenVoIP/SipService.cs
- src/WavenVoIP/Views/DialerShellWindow.xaml
- src/WavenVoIP/Views/DialerShellWindow.xaml.cs
- src/WavenVoIP/Views/InputPromptWindow.xaml
- src/WavenVoIP/Views/InputPromptWindow.xaml.cs
