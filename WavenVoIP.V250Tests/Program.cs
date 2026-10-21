// Testes da v2.5.0 — tudo offline: NÃO grava histórico nem config e NÃO consulta o servidor.
// Uso: V250Tests <cdr.json> [saida.json]
//   cdr.json   resposta salva de /api/cdr/calls (servida por um servidor local)
//   saida.json onde gravar o histórico calculado, para comparar duas versões do app
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using WavenVoIP;
using WavenVoIP.Models;
using WavenVoIP.Services;

int total = 0, falhas = 0;
void Check(string nome, bool ok, string detalhe = "")
{
    total++; if (!ok) falhas++;
    Console.WriteLine($"[{(ok ? "OK  " : "FAIL")}] {nome} {detalhe}");
}

const BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Static;

// ── Modo: fotografar as telas do "Puxar ligação" (conferência visual sem chamada real) ──
// Uso: V250Tests --telas <pasta>. Monta a janela e o popup com chamadas de exemplo e grava PNGs
// em várias larguras e escalas do Windows (100%, 125%, 150%). Nada é exibido nem consultado.
// Uso: V250Tests --api-contato <id> <nome> <observacao>
// Envia a edicao de um contato da empresa pela MESMA chamada que a tela "Editar contato" usa
// (WavenApiService.AtualizarContatoAsync, com a configuracao real deste posto).
if (args.Length >= 4 && args[0] == "--api-contato")
{
    var cfgC = SipConfig.CarregarSalva();
    var okC = WavenVoIP.Services.WavenApiService.AtualizarContatoAsync(args[1], args[2], empresa: null, observacao: args[3],
        ramal: cfgC?.Ramal ?? "100", atualizadoEm: DateTime.UtcNow).GetAwaiter().GetResult();
    Console.WriteLine($"AtualizarContatoAsync -> {(okC ? "OK" : "FALHOU")}");
    return okC ? 0 : 1;
}

// Uso: V250Tests --api-link-gravacao <arquivo> <yyyy-MM-dd> <ramal> [urlApiTeste] [salvarEm]
// Pede o link temporário de gravação pela MESMA chamada do Histórico (token real deste posto; a URL
// pode apontar para uma API de teste) e, com salvarEm, baixa o áudio pelo link como o player faz.
// Mostra só o status, sem imprimir o link.
if (args.Length >= 4 && args[0] == "--api-link-gravacao")
{
    var (st, link, erro) = WavenVoIP.Services.WavenApiService.GetLinkGravacaoAsync(args[1], DateTime.Parse(args[2]), args[3],
        args.Length >= 5 ? args[4] : null).GetAwaiter().GetResult();
    Console.WriteLine($"status={st} link={(link.Length > 0 ? "recebido" : "-")} erro={erro}");
    if (st == 200 && args.Length >= 6)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var bytes = http.GetByteArrayAsync(link).GetAwaiter().GetResult();
        File.WriteAllBytes(args[5], bytes);
        Console.WriteLine($"baixado bytes={bytes.Length} sha256={Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant()}");
    }
    return st == 200 ? 0 : st;
}

if (args.Length >= 2 && args[0] == "--fonte")
{
    var thf = new Thread(() =>
    {
        var sp = new System.Windows.Controls.StackPanel { Background = System.Windows.Media.Brushes.White, Margin = new System.Windows.Thickness(6) };
        foreach (var fam in new[] { "Segoe UI Variable Display", "Segoe UI Variable Text", "Segoe UI" })
            foreach (var peso in new[] { System.Windows.FontWeights.Normal, System.Windows.FontWeights.SemiBold, System.Windows.FontWeights.Bold })
            {
                var tb = new System.Windows.Controls.TextBlock { Text = $"{fam} {peso}: Histórico Configurações 66984671226 Transferência", FontFamily = new System.Windows.Media.FontFamily(fam), FontWeight = peso, FontSize = 13 };
                System.Windows.Media.TextOptions.SetTextFormattingMode(tb, System.Windows.Media.TextFormattingMode.Display);
                sp.Children.Add(tb);
                var tf = new System.Windows.Media.Typeface(new System.Windows.Media.FontFamily(fam), System.Windows.FontStyles.Normal, peso, System.Windows.FontStretches.Normal);
                tf.TryGetGlyphTypeface(out var gt);
                Console.WriteLine($"{fam} | {peso} | face={gt?.FaceNames.Values.FirstOrDefault()} peso_real={gt?.Weight} simulacao={gt?.StyleSimulations} arquivo={System.IO.Path.GetFileName(gt?.FontUri.LocalPath ?? "")}");
            }
        sp.Measure(new System.Windows.Size(700, 400)); sp.Arrange(new System.Windows.Rect(0, 0, 700, sp.DesiredSize.Height)); sp.UpdateLayout();
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(875, (int)(sp.DesiredSize.Height * 1.25), 120, 120, System.Windows.Media.PixelFormats.Pbgra32);
        bmp.Render(sp);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder(); enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = File.Create(args[1]); enc.Save(fs);
    });
    thf.SetApartmentState(ApartmentState.STA); thf.Start(); thf.Join();
    return 0;
}
if (args.Length >= 2 && args[0] == "--telas")
{
    var pasta = Path.GetFullPath(args[1]); Directory.CreateDirectory(pasta);
    var th = new Thread(() =>
    {
        // Estilos e imagens do app (App.xaml, /Assets) resolvidos como no executavel real.
        if (System.Windows.Application.Current == null)
        {
            new WavenVoIP.App().InitializeComponent();
        }
        void Foto(System.Windows.FrameworkElement raiz, double largura, double altura, double escala, string nome)
        {
            raiz.Measure(new System.Windows.Size(largura, double.IsNaN(altura) ? double.PositiveInfinity : altura));
            var h = double.IsNaN(altura) ? raiz.DesiredSize.Height : altura;
            raiz.Arrange(new System.Windows.Rect(0, 0, largura, h)); raiz.UpdateLayout();
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(largura * escala), (int)Math.Ceiling(h * escala), 96 * escala, 96 * escala, System.Windows.Media.PixelFormats.Pbgra32);
            var fundo = new System.Windows.Media.DrawingVisual();
            using (var dc = fundo.RenderOpen()) dc.DrawRectangle(System.Windows.Media.Brushes.Gainsboro, null, new System.Windows.Rect(0, 0, largura, h));
            bmp.Render(fundo); bmp.Render(raiz);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder(); enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using var fs = File.Create(Path.Combine(pasta, nome)); enc.Save(fs);

            // Verificação automática: nenhum botão pode ficar fora da área visível nem ser espremido.
            var cortados = new List<string>();
            void Varrer(System.Windows.DependencyObject o)
            {
                for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(o); i++)
                {
                    var f = System.Windows.Media.VisualTreeHelper.GetChild(o, i);
                    if (f is System.Windows.Controls.Button b && b.IsVisible)
                    {
                        var r = b.TransformToAncestor(raiz).TransformBounds(new System.Windows.Rect(0, 0, b.ActualWidth, b.ActualHeight));
                        b.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
                        var espremido = b.ActualWidth + 0.5 < b.DesiredSize.Width;
                        if (r.Right > largura + 0.5 || r.Left < -0.5 || r.Bottom > h + 0.5 || espremido)
                            cortados.Add($"{b.Content} x={r.Left:F0}..{r.Right:F0} largura={b.ActualWidth:F0}/{b.DesiredSize.Width:F0}");
                    }
                    Varrer(f);
                }
            }
            Varrer(raiz);
            Check($"tela {nome}: nenhum botao cortado ou espremido", cortados.Count == 0, string.Join(" | ", cortados.Take(4)));
        }

        ChamadaAtivaInfo Ch(string canal, StatusChamadaAtiva st, string num, string nome, string tronco, int seg, string atend = "", string fila = "") =>
            new() { CanalCliente = canal, Linkedid = canal, Uniqueid = canal, Numero = num, Nome = nome, Tronco = tronco, Status = st, RamalAtendente = atend, Fila = fila, DuracaoSeg = seg };
        var exemplos = new[]
        {
            Ch("SIP/Wavoip_VIVO-01", StatusChamadaAtiva.ForaDoHorario, "5591976103726", "Paulo Soares", "WhatsApp Vivo", 9),
            Ch("SIP/BRDID-02", StatusChamadaAtiva.NaFila, "6699887766", "Maria Aparecida de Oliveira Santos Albuquerque", "Operadora", 75, fila: "700"),
            Ch("SIP/0800-03", StatusChamadaAtiva.NaUra, "6633221100", "", "0800", 6),
            Ch("SIP/Wavoip-04", StatusChamadaAtiva.EmAtendimento, "5566999990000", "Posto Avenida", "WhatsApp TIM", 754, atend: "104"),
            Ch("SIP/Wavoip-05", StatusChamadaAtiva.TocandoRamais, "5566933334444", "", "WhatsApp TIM", 4),
            Ch("SIP/Wavoip-06", StatusChamadaAtiva.CaixaPostal, "5566911112222", "Restaurante e Churrascaria Boi na Brasa", "WhatsApp TIM", 21),
        };

        // Janela completa
        foreach (var (larg, alt, esc) in new[] { (744.0, 460.0, 1.0), (744.0, 460.0, 1.5), (864.0, 480.0, 1.25), (1180.0, 520.0, 1.0) })
        {
            var w = new WavenVoIP.Views.PuxarLigacaoWindow(null!, "100");
            var linhas = (System.Collections.IList)typeof(WavenVoIP.Views.PuxarLigacaoWindow).GetField("_linhas", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(w)!;
            var roxo = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(109, 40, 217)); var roxoF = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(245, 243, 255));
            var verde = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(21, 128, 61)); var verdeF = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(240, 253, 244));
            var ambar = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(180, 83, 9)); var ambarF = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 251, 235));
            var azul = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 99, 235)); var azulF = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 246, 255));
            foreach (var c in exemplos)
            {
                var (cor, corF) = c.Status switch { StatusChamadaAtiva.EmAtendimento => (verde, verdeF), StatusChamadaAtiva.NaFila or StatusChamadaAtiva.TocandoRamais => (ambar, ambarF), StatusChamadaAtiva.NaUra => (azul, azulF), _ => (roxo, roxoF) };
                var num = PhoneNumberNormalizer.NormalizeForDisplay(c.Numero);
                linhas.Add(new WavenVoIP.Views.PuxarLigacaoWindow.Linha { Chave = c.CanalCliente, Info = c, Cliente = c.Nome.Length > 0 ? c.Nome : num, Telefone = c.Nome.Length > 0 ? num : "", Canal = c.Tronco, Status = c.StatusTexto,
                    Duracao = $"{c.DuracaoSeg / 60:D2}:{c.DuracaoSeg % 60:D2}", Atendente = c.RamalAtendente.Length > 0 ? $"Sabrina Pereira ({c.RamalAtendente})" : "—", Cor = cor, CorFundo = corF });
            }
            typeof(WavenVoIP.Views.PuxarLigacaoWindow).GetMethod("AtualizarRodape", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(w, null);
            Foto((System.Windows.FrameworkElement)w.Content, larg, alt, esc, $"janela_{larg:F0}dip_{esc * 100:F0}pct.png");
        }

        // Escolha de destino (transferencia / conferencia)
        foreach (var (larg, alt, esc) in new[] { (504.0, 520.0, 1.5), (624.0, 520.0, 1.0) })
        {
            var d = new WavenVoIP.Views.EscolherDestinoWindow("Transferência cega", "A ligação vai direto para o destino, sem você falar antes", "100", "Transferir");
            var cards = (System.Collections.IList)typeof(WavenVoIP.Views.EscolherDestinoWindow).GetField("_ramais", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(d)!;
            var vd = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(21, 128, 61)); var vdF = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 252, 231));
            var am = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(180, 83, 9)); var amF = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(254, 243, 199));
            foreach (var (r, n, livre) in new[] { ("101", "Recepção", true), ("103", "Evelin", true), ("104", "Sabrina Pereira", false), ("105", "Financeiro Contas a Pagar", true), ("106", "João", true), ("107", "Estoque", false), ("109", "Maria", true) })
                cards.Add(new WavenVoIP.Views.EscolherDestinoWindow.RamalCard { Ramal = r, Nome = n, Livre = livre, StatusTexto = livre ? "Livre" : "Ocupado", Cor = livre ? vd : am, CorFundo = livre ? vdF : amF, Selecionado = r == "103" });
            typeof(WavenVoIP.Views.EscolherDestinoWindow).GetMethod("Selecionar", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(d, new object[] { "103", "Evelin (103) · Livre", true });
            Foto((System.Windows.FrameworkElement)d.Content, larg, alt, esc, $"destino_{larg:F0}dip_{esc * 100:F0}pct.png");
        }

        // Conferência e transferência assistida
        foreach (var (larg, alt, esc) in new[] { (684.0, 420.0, 1.5), (784.0, 520.0, 1.0) })
        {
            var cw = new WavenVoIP.Views.ConferenceControlWindow("800");
            ((System.Windows.Controls.TextBox)cw.FindName("txtDestino")).Text = "103";
            var cr = (System.Collections.IList)typeof(WavenVoIP.Views.ConferenceControlWindow).GetField("_ramais", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cw)!;
            var cvd = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(21, 128, 61)); var cvdF = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 252, 231));
            var cam = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(180, 83, 9)); var camF = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(254, 243, 199));
            foreach (var (r, n, livre) in new[] { ("101", "Recepção", true), ("103", "Evelin", true), ("104", "Sabrina Pereira", false), ("105", "Financeiro Contas a Pagar", true), ("9001", "Admin", true) })
                cr.Add(new WavenVoIP.Views.EscolherDestinoWindow.RamalCard { Ramal = r, Nome = n, Livre = livre, StatusTexto = livre ? "Livre" : "Ocupado", Cor = livre ? cvd : cam, CorFundo = livre ? cvdF : camF, Selecionado = r == "103" });
            cw.AdicionarOuAtualizarParticipante("104", "Na conferência");
            cw.AdicionarOuAtualizarParticipante("66984671226", "Chamando…");
            Foto((System.Windows.FrameworkElement)cw.Content, larg, alt, esc, $"conferencia_{larg:F0}dip_{esc * 100:F0}pct.png");
        }
        foreach (var esc in new[] { 1.0, 1.5 })
        {
            var tw = new WavenVoIP.Views.TransferControlWindow("Evelin (103)", "Paulo Soares (66984671226)");
            Foto((System.Windows.FrameworkElement)tw.Content, 504, 450, esc, $"transferencia_504dip_{esc * 100:F0}pct.png");
        }

        // Chamada em andamento
        foreach (var esc in new[] { 1.0, 1.5 })
        {
            var cwin = new WavenVoIP.Views.CallWindow("Maria Aparecida de Oliveira (66984671226)", "Em chamada");
            Foto((System.Windows.FrameworkElement)cwin.Content, cwin.Width - 16, cwin.Height - 39, esc, $"chamada_{esc * 100:F0}pct.png");
        }

        // Chamada em conferencia com o painel "Participantes da ligacao"
        foreach (var esc in new[] { 1.0, 1.5 })
        {
            var cpar = new WavenVoIP.Views.CallWindow("Entrar: Paulo Soares (66984671226)", "Em chamada");
            cpar.AtualizarParticipantes(new List<ParticipanteLigacao> {
                new("cliente", "5566984671226", "Paulo Soares", "SIP/Wavoip-1"),
                new("atendente", "104", "", "SIP/104-2") }, true, r => r == "104" ? "Sabrina Pereira" : r);
            Foto((System.Windows.FrameworkElement)cpar.Content, cpar.Width - 16, cpar.Height - 39, esc, $"chamada_participantes_{esc * 100:F0}pct.png");
        }

        // Configuracoes e Dashboard
        try
        {
            foreach (var esc in new[] { 1.0, 1.5 })
            {
                var sw = new WavenVoIP.Views.SettingsWindow();
                Foto((System.Windows.FrameworkElement)sw.Content, 784, 700, esc, $"configuracoes_{esc * 100:F0}pct.png");
                var dc = new WavenVoIP.Views.DashboardControl();
                Foto(dc, 1030, 640, esc, $"dashboard_{esc * 100:F0}pct.png");
            }
        }
        catch (Exception exTela) { Console.WriteLine("       (configuracoes/dashboard nao renderizados: " + exTela.Message + ")"); }

        // Chamada recebida
        foreach (var (quem, esc) in new[] { ("Admin (9001)", 1.0), ("Maria Aparecida de Oliveira Santos Albuquerque (66984671226)", 1.5), ("66984671226", 1.25) })
        {
            var inc = new WavenVoIP.Views.IncomingCallWindow(quem);
            Foto((System.Windows.FrameworkElement)inc.Content, 392, double.NaN, esc, $"recebida_{quem.Length}c_{esc * 100:F0}pct.png");
        }

        // Popup
        foreach (var esc in new[] { 1.0, 1.5 })
        {
            var pop = new WavenVoIP.Views.ChamadasAguardandoPopup();
            pop.Atualizar(exemplos.Where(WavenVoIP.Views.ChamadasAguardandoPopup.DeveAvisar).ToList());
            pop.MostrarMensagem("Seu ramal está tocando: atenda para assumir a ligação. Se não atender, nada muda.");
            Foto((System.Windows.FrameworkElement)pop.Content, 410, double.NaN, esc, $"popup_410dip_{esc * 100:F0}pct.png");
        }

        // Aviso de chamada nao atendida (cliente, telefone, horario, canal, Retornar e WhatsApp)
        foreach (var (nome, num, canal, motivo, esc) in new[] {
            ("Maria Aparecida de Oliveira Santos Albuquerque", "66984671226", "WhatsApp TIM", "ClienteDesistiu", 1.0),
            ("", "6633221100", "0800", "AbandonoFila", 1.25),
            ("Posto Avenida", "66999990000", "", "", 1.5) })
        {
            var itemNa = new HistoricoLigacaoItem { Tipo = TipoHistoricoLigacao.Perdida, Numero = num, Nome = nome, CanalEntrada = canal, MotivoPerdida = motivo, DataHora = DateTime.Today.AddHours(14).AddMinutes(32) };
            var mp = new WavenVoIP.Views.MissedCallPopup(itemNa);
            string T(string n) => ((System.Windows.Controls.TextBlock)mp.FindName(n)).Text;
            Foto((System.Windows.FrameworkElement)mp.Content, 380, double.NaN, esc, $"naoatendida_{(canal.Length > 0 ? canal.Replace(" ", "") : "semcanal")}_{esc * 100:F0}pct.png");
            var esperado = canal switch { "WhatsApp TIM" => "Retorno pelo canal: WhatsApp TIM", "0800" => "Retorno pelo canal: Operadora", _ => "Canal não identificado: o retorno pede a escolha" };
            Check($"aviso nao atendida ({(canal.Length > 0 ? canal : "sem canal")}): cliente, telefone, horario, canal e canal de retorno",
                T("txtCallerName") == (itemNa.NomeExibido == itemNa.NumeroLimpoVisual ? "Cliente sem cadastro" : itemNa.NomeExibido) && T("txtCallerNumber").Length >= 10 && T("txtHorario") == "Hoje às 14:32" &&
                T("txtCanal") == (canal.Length > 0 ? canal : "Não identificado") && T("txtCanalRetorno") == esperado &&
                T("txtMotivo") == (motivo == "ClienteDesistiu" ? "Cliente desistiu" : motivo == "AbandonoFila" ? "Abandono na fila" : "Perdida"),
                $"{T("txtCallerName")}|{T("txtCallerNumber")}|{T("txtHorario")}|{T("txtCanal")}|{T("txtCanalRetorno")}|{T("txtMotivo")}");
            if (canal == "0800")
            {
                // WhatsApp so abre a conversa: o aviso nao fecha; Retornar fecha e informa o numero
                bool fechou = false; string? zap = null, ret = null;
                mp.Closed += (_, _) => fechou = true; mp.WhatsAppSolicitado += n => zap = n; mp.RetornarSolicitado += n => ret = n;
                mp.Show();
                ((System.Windows.Controls.Button)mp.FindName("btnWhatsApp")).RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Check("aviso nao atendida: WhatsApp abre a conversa do numero e o aviso continua aberto", zap == num && !fechou && mp.IsVisible, $"{zap}/{fechou}");
                mp.RegistrarNovaChamada(new HistoricoLigacaoItem { Tipo = TipoHistoricoLigacao.Perdida, Numero = num, CanalEntrada = "WhatsApp Vivo", DataHora = DateTime.Today.AddHours(15) });
                Check("aviso nao atendida: outra chamada do mesmo numero atualiza o mesmo aviso (sem duplicar)", mp.Quantidade == 2 && T("txtHorario") == "Hoje às 15:00" && T("txtCanalRetorno") == "Retorno pelo canal: WhatsApp Vivo" && T("txtMotivo").Contains("2 chamadas"), $"{mp.Quantidade}|{T("txtHorario")}|{T("txtCanalRetorno")}|{T("txtMotivo")}");
                ((System.Windows.Controls.Button)mp.FindName("btnRetornar")).RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Check("aviso nao atendida: Retornar informa o numero e fecha o aviso", ret == num && fechou, $"{ret}/{fechou}");
            }
        }

        // Aviso de demonstracao: mesmo visual, mas nenhum botao dispara acao (nao liga, nao abre conversa, nao fecha)
        foreach (var (canal, esperado) in new[] { ("WhatsApp TIM", "Simulação: ligaria por WhatsApp TIM"), ("WhatsApp Vivo", "Simulação: ligaria por WhatsApp Vivo"), ("Operadora", "Simulação: ligaria por Operadora"), ("0800", "Simulação: ligaria por Operadora"), ("", "Simulação: pediria a escolha do canal") })
        {
            var dp = new WavenVoIP.Views.MissedCallPopup(new HistoricoLigacaoItem { Tipo = TipoHistoricoLigacao.Perdida, Numero = "66900000001", Nome = "Cliente Exemplo", CanalEntrada = canal, MotivoPerdida = "AbandonoFila", DataHora = DateTime.Now }) { Demonstracao = true };
            int eventos = 0; bool fechou = false;
            dp.RetornarSolicitado += _ => eventos++; dp.WhatsAppSolicitado += _ => eventos++; dp.VerHistoricoSolicitado += () => eventos++; dp.Closed += (_, _) => fechou = true;
            dp.Show();
            void Clicar(string n) => ((System.Windows.Controls.Button)dp.FindName(n)).RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            string Txt() => ((System.Windows.Controls.TextBlock)dp.FindName("txtCanalRetorno")).Text;
            Clicar("btnRetornar"); var tRet = Txt();
            Clicar("btnWhatsApp"); var tZap = Txt();
            Check($"demonstracao ({(canal.Length > 0 ? canal : "sem canal")}): Retornar e WhatsApp so informam o que fariam; nada e disparado e o aviso fica aberto",
                tRet == esperado && tZap == "Simulação: conversa com " + NaoAtendidasPolicy.DestinoWhatsApp("66900000001") && tZap.Contains("5566") && eventos == 0 && !fechou && dp.IsVisible &&
                ((System.Windows.FrameworkElement)dp.FindName("bdDemo")).Visibility == System.Windows.Visibility.Visible, $"{tRet}|{tZap}|{eventos}|{fechou}");
            if (canal == "0800") foreach (var e in new[] { 1.0, 1.25, 1.5 }) Foto((System.Windows.FrameworkElement)dp.Content, 380, double.NaN, e, $"naoatendida_demonstracao_{e * 100:F0}pct.png");
            dp.Close();
        }
        {
            // Botao WhatsApp: numero que a tela de WhatsApp do app recebe e destino final da conversa
            string W(string? n) => $"{NaoAtendidasPolicy.NumeroParaWhatsApp(n) ?? "null"}>{NaoAtendidasPolicy.DestinoWhatsApp(n) ?? "null"}";
            var casos = new[] { "66999990002", "5566999990002", "+55 (66) 99999-0002", "6635210003", "556635210003" };
            Check("whatsapp do aviso: celular e fixo chegam normalizados (sem 55 na tela, 55+DDD+numero no destino), com ou sem mascara/55",
                casos.Take(3).All(c => W(c) == "66999990002>5566999990002") && casos.Skip(3).All(c => W(c) == "6635210003>556635210003"), string.Join(" | ", casos.Select(W)));
            Check("whatsapp do aviso: celular antigo sem o 9o digito ganha o 9 no destino", NaoAtendidasPolicy.DestinoWhatsApp("6699990002") == "5566999990002", W("6699990002"));
            Check("whatsapp do aviso: ramal, fila, 0800 e vazio nao abrem conversa", new[] { "104", "700", "08007771234", "", null }.All(c => NaoAtendidasPolicy.NumeroParaWhatsApp(c) == null && NaoAtendidasPolicy.DestinoWhatsApp(c) == null));
            Check("aviso nao atendida: telefone com mascara", NaoAtendidasPolicy.FormatarTelefone("66999990002") == "(66) 99999-0002" && NaoAtendidasPolicy.FormatarTelefone("6635210003") == "(66) 3521-0003" &&
                NaoAtendidasPolicy.FormatarTelefone("5566999990002") == "(66) 99999-0002" && NaoAtendidasPolicy.FormatarTelefone("104") == "104");

            var z = new WavenVoIP.Views.MissedCallPopup(new HistoricoLigacaoItem { Tipo = TipoHistoricoLigacao.Perdida, Numero = "5566999990002", CanalEntrada = "WhatsApp Vivo", DataHora = DateTime.Now });
            string? recebido = null; z.WhatsAppSolicitado += n => recebido = n;
            ((System.Windows.Controls.Button)z.FindName("btnWhatsApp")).RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Check("whatsapp do aviso: o numero entregue pelo botao vira o destino correto", NaoAtendidasPolicy.DestinoWhatsApp(recebido) == "5566999990002" && ((System.Windows.Controls.TextBlock)z.FindName("txtCallerNumber")).Text == "(66) 99999-0002", $"{recebido}");
            var logo = (System.Windows.Shapes.Path)z.FindName("logoWhatsApp");
            var lim = logo.Data.Bounds;
            Check("whatsapp do aviso: logotipo vetorial (Path 24x24, verde oficial #25D366)", lim.Width > 23.5 && lim.Width < 24.1 && lim.Height > 23.5 && lim.Height < 24.1 &&
                ((System.Windows.Media.SolidColorBrush)logo.Fill).Color == System.Windows.Media.Color.FromRgb(0x25, 0xD3, 0x66), $"{lim}");
        }
        {
            // contador "Nao atendidas" como mensagens nao lidas (so a parte pura; o arquivo real nao e tocado)
            HistoricoLigacaoItem P(string linked, string uid, string num, int min) => new HistoricoLigacaoItem
                { Tipo = TipoHistoricoLigacao.Perdida, LinkedId = linked, UniqueId = uid, Numero = num, DataHora = DateTime.Today.AddHours(9).AddMinutes(min) };
            var l = new List<HistoricoLigacaoItem> {
                P("1700000001.1", "1700000001.1", "66999990001", 1), P("1700000001.1", "1700000001.7", "66999990001", 1), // mesma ligacao repetida
                P("1700000002.1", "1700000002.1", "66999990002", 2), P("", "", "66999990003", 3),
                new HistoricoLigacaoItem { Tipo = TipoHistoricoLigacao.Recebida, LinkedId = "1700000004.1", Numero = "66999990004", DataHora = DateTime.Today } };
            var vistas = new HashSet<string>();
            Check("nao atendidas (nao lidas): cada ligacao conta uma vez, recebida atendida nao conta", NaoAtendidasVistasService.ContarNaoVistas(l, vistas) == 3, NaoAtendidasVistasService.ContarNaoVistas(l, vistas).ToString());
            foreach (var i in l.Where(NaoAtendidasPolicy.EhNaoAtendida)) vistas.Add(NaoAtendidasVistasService.Chave(i));
            Check("nao atendidas (nao lidas): abrir a area zera o contador e nao apaga nada", NaoAtendidasVistasService.ContarNaoVistas(l, vistas) == 0 && l.Count == 5 && NaoAtendidasPolicy.Contar(l).total == 4);
            l.Add(P("1700000005.1", "1700000005.1", "66999990005", 5)); l.Add(P("1700000005.1", "1700000005.3", "66999990005", 5));
            l[1].UniqueId = "1700000001.9"; // UniqueId mudou com o CDR: a ligacao ja vista continua vista (chave pelo LinkedId)
            Check("nao atendidas (nao lidas): nova chamada depois volta a contar (uma so), ja vistas continuam vistas", NaoAtendidasVistasService.ContarNaoVistas(l, vistas) == 1, NaoAtendidasVistasService.ContarNaoVistas(l, vistas).ToString());
        }
        {
            // aviso normal nao mostra a etiqueta; pilha que nao cabe na tela abre outra coluna a esquerda
            var a = new WavenVoIP.Views.MissedCallPopup(new HistoricoLigacaoItem { Tipo = TipoHistoricoLigacao.Perdida, Numero = "66900000002", CanalEntrada = "Operadora", DataHora = DateTime.Now });
            Check("aviso nao atendida real: sem etiqueta de demonstracao", !a.Demonstracao && ((System.Windows.FrameworkElement)a.FindName("bdDemo")).Visibility == System.Windows.Visibility.Collapsed);
            a.PosicionarBottomRight(0, 0); var l0 = a.Left; var t0c = a.Top;
            a.PosicionarBottomRight(0, 1); var l1 = a.Left;
            Check("aviso nao atendida: segunda coluna fica a esquerda, sem sobrepor a primeira", l0 - l1 >= a.Width - 24 && a.Top == t0c && l1 >= System.Windows.SystemParameters.WorkArea.Left, $"{l0}/{l1}");
        }
    });
    th.SetApartmentState(ApartmentState.STA); th.Start(); th.Join();
    Console.WriteLine($"\nRESULTADO: {total - falhas}/{total} OK, {falhas} falha(s) | imagens em {pasta}");
    return falhas;
}

// ── Modo: recuperação do histórico local no primeiro sync ───────────────────────────
// Uso: V250Tests --recuperacao <cdr.json> <copia-do-historico.json>
// Roda o MESMO caminho do app (SincronizarAsync + MesclarCdr + CarregarComRetencao) sobre uma
// CÓPIA do historico.json. O arquivo real nunca é aberto para escrita: o caminho interno do
// HistoricoStorageService é trocado, só neste processo, para a cópia — e o teste aborta se a
// troca não for confirmada por um registro-sentinela que só existe na cópia.
if (args.Length >= 3 && args[0] == "--recuperacao")
{
    var copia = Path.GetFullPath(args[2]);
    var campo = typeof(HistoricoStorageService).GetField("_path", Priv)!;
    var real = (string)campo.GetValue(null)!;
    var dm = new System.Reflection.Emit.DynamicMethod("DefinirCaminho", null, new[] { typeof(string) }, typeof(HistoricoStorageService), true);
    var il = dm.GetILGenerator(); il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0); il.Emit(System.Reflection.Emit.OpCodes.Stsfld, campo); il.Emit(System.Reflection.Emit.OpCodes.Ret);
    dm.Invoke(null, new object[] { copia });
    var antes = HistoricoStorageService.Carregar();
    if ((string)campo.GetValue(null)! != copia || !antes.Any(i => i.Id == "SENTINELA-TESTE-V250"))
    {
        Console.WriteLine("ABORTADO: nao foi possivel isolar o historico (sentinela ausente). Nada foi gravado.");
        return 1;
    }
    var realAntes = File.GetLastWriteTimeUtc(real);
    Console.WriteLine($"historico isolado em copia: {antes.Count - 1} registros (arquivo real intocado por este teste)");

    var stubR = new ApiLocal(File.ReadAllBytes(args[1]));
    SipConfig.CarregarSalva();
    var instR = (SipConfig)typeof(SipConfig).GetField("_cacheInstancia", Priv)!.GetValue(null)!;
    instR.WavenApiUrl = stubR.Url; instR.UsarWavenApi = true;
    var cfgR = SipConfig.CarregarSalva()!;
    var diasR = cfgR.HistoricoRetencaoDias;

    var sync1 = await IssabelCdrService.SincronizarAsync(cfgR, diasR, forcarCompleto: true);
    var novos1 = HistoricoStorageService.MesclarCdr(sync1);
    var depois = HistoricoStorageService.CarregarComRetencao(diasR);
    // segundo ciclo (como o timer do app): nao pode acrescentar nem remover nada
    var sync2 = await IssabelCdrService.SincronizarAsync(cfgR, diasR, forcarCompleto: false);
    var novos2 = HistoricoStorageService.MesclarCdr(sync2);
    var depois2 = HistoricoStorageService.CarregarComRetencao(diasR);
    stubR.Parar();

    var cdrAntes = antes.Where(i => i.FonteCdr && !string.IsNullOrEmpty(i.UniqueId)).ToList();
    var uidsDepois = depois.Where(i => !string.IsNullOrEmpty(i.UniqueId)).Select(i => i.UniqueId).ToHashSet();
    Check("recuperacao: registros existentes mantem o proprio UniqueId (chaves de aviso preservadas)", antes.Where(i => i.FonteCdr && !string.IsNullOrEmpty(i.UniqueId)).All(i => uidsDepois.Contains(i.UniqueId)));
    var uidsAntes = cdrAntes.Select(i => i.UniqueId).ToHashSet();
    var perdidos = cdrAntes.Where(i => !uidsDepois.Contains(i.UniqueId)).ToList();
    var recuperados = depois.Where(i => i.FonteCdr && !string.IsNullOrEmpty(i.UniqueId) && !uidsAntes.Contains(i.UniqueId)).OrderBy(i => i.DataHora).ToList();
    var inicioJanela = sync1.Count > 0 ? sync1.Min(i => i.DataHora) : DateTime.MinValue;
    var fimJanela = sync1.Count > 0 ? sync1.Max(i => i.DataHora) : DateTime.MinValue;

    Console.WriteLine($"retencao={diasR} dias | janela do CDR: {inicioJanela:dd/MM HH:mm} a {fimJanela:dd/MM HH:mm} | ligacoes (linkedids) na janela: {sync1.Count}");
    Console.WriteLine($"antes: {antes.Count - 1} registros ({cdrAntes.Count} do CDR, {antes.Count(i => !i.FonteCdr) - 1} locais) | depois do 1o sync: {depois.Count - 1} | MesclarCdr informou novos={novos1}");
    Console.WriteLine("recuperados por tipo: " + string.Join(" ", recuperados.GroupBy(i => i.TipoTexto).Select(g => $"{g.Key}={g.Count()}")));
    foreach (var r in recuperados)
        Console.WriteLine($"   + {r.DataHora:ddd dd/MM HH:mm:ss} ..{(r.Numero.Length > 4 ? r.Numero[^4..] : r.Numero)} {r.TipoTexto} {r.Duracao} {r.RamalExibido} grav={(string.IsNullOrEmpty(r.GravacaoArquivo) ? "-" : "S")}");

    Check("recuperacao: nenhum registro do CDR ja salvo foi perdido", perdidos.Count == 0, string.Join(",", perdidos.Take(5).Select(i => i.UniqueId)));
    Check("recuperacao: toda ligacao da janela do CDR esta no historico, uma vez so",
        sync1.All(i => depois.Count(d => d.LinkedId == i.LinkedId) == 1) && depois.Count(d => d.FonteCdr && d.DataHora >= inicioJanela) == sync1.Count,
        $"ligacoes={sync1.Count} registros na janela={depois.Count(d => d.FonteCdr && d.DataHora >= inicioJanela)}");
    var lidRep = depois.Where(i => i.FonteCdr && !string.IsNullOrEmpty(i.LinkedId)).GroupBy(i => i.LinkedId).Where(g => g.Count() > 1).ToList();
    foreach (var g in lidRep.Take(5)) Console.WriteLine("   ! linkedid repetido " + g.Key + ": " + string.Join(" ; ", g.Select(i => $"{i.DataHora:dd/MM HH:mm:ss} uid={i.UniqueId} {i.Tipo} fonteCdr={i.FonteCdr}")));
    Check("recuperacao: sem LinkedID repetido entre registros do CDR", lidRep.Count == 0, $"{lidRep.Count}");
    Check("recuperacao: registros locais e fora da janela preservados", antes.Where(i => i.Id != "SENTINELA-TESTE-V250" && (i.DataHora < inicioJanela || i.DataHora > fimJanela) ).All(a => depois.Any(d => d.Id == a.Id) || a.DataHora < DateTime.Now.AddDays(-diasR)));
    var mantidos = cdrAntes.Where(i => uidsDepois.Contains(i.UniqueId)).ToList();
    int mudouTipo = 0, mudouAtend = 0, mudouGrav = 0, mudouDur = 0;
    foreach (var a in mantidos)
    {
        var d = depois.First(x => x.UniqueId == a.UniqueId);
        if (a.RamalDestino != d.RamalDestino || a.Duracao != d.Duracao) Console.WriteLine($"   ~ dados {a.DataHora:dd/MM HH:mm} ..{(a.Numero.Length > 4 ? a.Numero[^4..] : a.Numero)}: destino '{a.RamalDestino}' {a.Duracao} -> '{d.RamalDestino}' {d.Duracao}");
        if (a.Tipo != d.Tipo) { mudouTipo++; Console.WriteLine($"   ~ tipo {a.DataHora:dd/MM HH:mm} ..{(a.Numero.Length > 4 ? a.Numero[^4..] : a.Numero)}: {a.Tipo} -> {d.TipoTexto}"); }
        if (a.RamalAtendeu != d.RamalAtendeu) { mudouAtend++; Console.WriteLine($"   ~ atendente {a.DataHora:dd/MM HH:mm} ..{(a.Numero.Length > 4 ? a.Numero[^4..] : a.Numero)}: '{a.RamalAtendeu}' -> '{d.RamalAtendeu}'"); }
        if (a.GravacaoArquivo != d.GravacaoArquivo) mudouGrav++;
        if (a.Duracao != d.Duracao) mudouDur++;
    }
    Console.WriteLine($"registros ja existentes atualizados pelo sync: tipo={mudouTipo} atendente={mudouAtend} gravacao={mudouGrav} duracao={mudouDur} (de {mantidos.Count})");
    Check("recuperacao: segundo ciclo de sync nao acrescenta nem remove nada", novos2 == 0 && depois2.Count == depois.Count, $"novos={novos2} {depois.Count}->{depois2.Count}");
    var limiteAviso = DateTime.Now.AddMinutes(-15);
    Check("recuperacao: perdidas recuperadas sao antigas (fora da janela de 15 min de aviso) — sem popup retroativo",
        recuperados.Where(i => i.Tipo == TipoHistoricoLigacao.Perdida).All(i => i.DataHora < limiteAviso));
    var tecnicos = depois.Where(i => i.EhEventoTecnico).ToList();
    Check("recuperacao: evento tecnico nunca e Perdida, nem conta em indicador de perdida/recebida", tecnicos.All(i => i.Tipo != TipoHistoricoLigacao.Perdida && !i.EhRecebidaPorOperador));
    Console.WriteLine($"fora do horario no historico recuperado: {depois.Count(i => i.EhForaDoHorario)} | URA sem atendente: {depois.Count(i => i.AtendimentoAutomatico == "URA")} | eventos tecnicos: {tecnicos.Count}");
    Check("recuperacao: arquivo real nao foi gravado por este processo", (string)campo.GetValue(null)! == copia);
    Console.WriteLine($"(arquivo real: ultima gravacao antes={realAntes:HH:mm:ss} agora={File.GetLastWriteTimeUtc(real):HH:mm:ss} UTC — alteracoes sao do app em uso)");
    Console.WriteLine($"\nRESULTADO: {total - falhas}/{total} OK, {falhas} falha(s)");
    return falhas;
}

// ── 0. v2.5.1 — tom de chamando (180 / 183 early media) ─────────────────────────────
if (typeof(SipService).GetMethod("TratarProgressoSaida", BindingFlags.NonPublic | BindingFlags.Instance) is { } mProg)
{
    const BindingFlags PrivI = BindingFlags.NonPublic | BindingFlags.Instance;
    SipService NovoSip()
    {
        var s = (SipService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(SipService));
        typeof(SipService).GetField("_ringback", PrivI)!.SetValue(s, new RingbackTone());
        typeof(SipService).GetField("_ringbackOutIdx", PrivI)!.SetValue(s, -1);
        return s;
    }
    void Discando(SipService s)
    {
        foreach (var (n, v) in new (string, object)[] { ("_earlyMediaAnunciada", false), ("_earlyMediaAudivel", false), ("_earlyMediaPacotesAudiveis", 0), ("_ringbackDiscando", true) })
            typeof(SipService).GetField(n, PrivI)!.SetValue(s, v);
    }
    bool Tom(SipService s) => ((RingbackTone)typeof(SipService).GetField("_ringback", PrivI)!.GetValue(s)!).Ativo;
    void Prog(SipService s, SIPSorcery.SIP.SIPResponseStatusCodesEnum st, bool sdp)
    {
        var r = new SIPSorcery.SIP.SIPResponse(st, null, System.Text.Encoding.UTF8, System.Text.Encoding.UTF8);
        if (sdp) r.Body = "v=0\r\no=- 1 1 IN IP4 10.0.0.1\r\ns=-\r\nc=IN IP4 10.0.0.1\r\nt=0 0\r\nm=audio 10000 RTP/AVP 0\r\n";
        mProg.Invoke(s, new object?[] { r });
    }
    var mRtp = typeof(SipService).GetMethod("ObservarEarlyMedia", PrivI)!;
    byte[] Pcmu(double amp) => Enumerable.Range(0, 160).Select(i => NAudio.Codecs.MuLawEncoder.LinearToMuLawSample((short)(amp * 32767 * Math.Sin(2 * Math.PI * 425 * i / 8000.0)))).ToArray();
    void Rtp(SipService s, double amp, int n) { for (int i = 0; i < n; i++) mRtp.Invoke(s, new object?[] { 0, Pcmu(amp) }); }
    void Parar(SipService s, string m) => typeof(SipService).GetMethod("PararRingback", PrivI)!.Invoke(s, new object[] { m });
    var Ring = SIPSorcery.SIP.SIPResponseStatusCodesEnum.Ringing;
    var Prg = SIPSorcery.SIP.SIPResponseStatusCodesEnum.SessionProgress;

    var a = NovoSip(); Discando(a);
    Prog(a, Ring, false);
    Check("ringback: 180 Ringing sem SDP liga o tom local", Tom(a));
    Prog(a, Ring, false);
    Check("ringback: 180 repetido nao cria segundo tom", Tom(a));
    Parar(a, "atendida");
    Check("ringback: atender para o tom na hora", !Tom(a));
    Prog(a, Ring, false);
    Check("ringback: 180 atrasado depois de atendida nao religa", !Tom(a));

    var b = NovoSip(); Discando(b);
    Prog(b, Prg, true);
    Check("ringback: 183 com SDP nao liga o tom de imediato (espera o audio do PABX)", !Tom(b));
    Rtp(b, 0.3, 6);
    Prog(b, Ring, false);
    await Task.Delay(1800);
    Check("ringback: early media audivel -> sem tom local (sem duplicar)", !Tom(b));
    Parar(b, "fim");

    var c = NovoSip(); Discando(c);
    Prog(c, Prg, true);
    Rtp(c, 0.0005, 20);   // early media muda / ruido de conforto
    await Task.Delay(1800);
    Check("ringback: 183 com early media muda -> tom local apos 1,5 s", Tom(c));
    Rtp(c, 0.3, 6);
    Check("ringback: early media fica audivel -> tom local sai", !Tom(c));
    Prog(c, Ring, false);
    Check("ringback: 180 depois de early media audivel nao religa", !Tom(c));
    Parar(c, "fim");

    var d = NovoSip(); Discando(d);
    Prog(d, Prg, false);
    Check("ringback: 183 sem SDP liga o tom local", Tom(d));
    Parar(d, "falhou");
    Check("ringback: falha/desligar para o tom", !Tom(d));

    var e = NovoSip();
    Prog(e, Ring, false);
    Check("ringback: sem discagem em andamento nunca toca", !Tom(e));
}

// ── 0b. v2.5.1 — desligar recebida depois de realizada cancelada (reprodução SIP real) ───
if (typeof(SipService).GetMethod("DesligarDialogoAtual", BindingFlags.NonPublic | BindingFlags.Instance) is { } mDesligar)
{
    const BindingFlags PI = BindingFlags.NonPublic | BindingFlags.Instance;
    SIPSorcery.Net.RTPSession Midia()
    {
        var s = new SIPSorcery.Net.RTPSession(false, false, false);
        s.addTrack(new SIPSorcery.Net.MediaStreamTrack(SIPSorcery.Net.SDPMediaTypesEnum.audio, false,
            new List<SIPSorcery.Net.SDPAudioVideoMediaFormat> { new(SIPSorceryMedia.Abstractions.SDPWellKnownMediaFormatsEnum.PCMU) }));
        return s;
    }
    var portaBase = 15060;
    async Task<(bool pabxRecebeuBye, string detalhe)> Cenario(bool codigoNovo, string previa = "cancelada", bool finalRecebida = true)
    {
        var portaPabx = (portaBase += 4); var portaApp = portaPabx + 1;
        var tPabx = new SIPSorcery.SIP.SIPTransport(); tPabx.AddSIPChannel(new SIPSorcery.SIP.SIPUDPChannel(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, portaPabx)));
        var tApp = new SIPSorcery.SIP.SIPTransport(); tApp.AddSIPChannel(new SIPSorcery.SIP.SIPUDPChannel(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, portaApp)));
        var pabx = new SIPSorcery.SIP.App.SIPUserAgent(tPabx, null);
        var app = new SIPSorcery.SIP.App.SIPUserAgent(tApp, null);
        try
        {
            // 1) PABX: a ligação realizada fica tocando (180) e nunca é atendida
            var okRealizada = false;
            var pabxDesligado = new TaskCompletionSource<bool>();
            SIPSorcery.SIP.App.SIPUserAgent? pabxAtendeu = null;
            var invitesNoPabx = 0;
            pabx.OnIncomingCall += async (ua, req) =>
            {
                var u = ua.AcceptCall(req);
                if (previa == "cancelada" && ++invitesNoPabx == 1) { u.Progress(SIPSorcery.SIP.SIPResponseStatusCodesEnum.Ringing, null, null, null, null); return; }
                // atende: um agente por chamada atendida (como o PABX)
                var ag = new SIPSorcery.SIP.App.SIPUserAgent(tPabx, null);
                ag.OnCallHungup += _ => pabxDesligado.TrySetResult(true);
                pabxAtendeu = ag;
                await ag.Answer(u, Midia());
            };
            if (previa == "cancelada")
            {
                var tRealizada = app.Call($"sip:266984671226@127.0.0.1:{portaPabx}", null, null, Midia());
                await Task.Delay(700);
                app.Cancel();                                     // operador desiste enquanto toca
                okRealizada = await tRealizada;
            }
            else if (previa == "atendida")
            {
                okRealizada = await app.Call($"sip:266984671226@127.0.0.1:{portaPabx}", null, null, Midia());
                await Task.Delay(300);
                app.Hangup();                                     // realizada atendida e desligada aqui
                await Task.Delay(300);
                pabxDesligado = new TaskCompletionSource<bool>();
                pabxAtendeu = null;
            }
            await Task.Delay(300);
            var uacVelho = typeof(SIPSorcery.SIP.App.SIPUserAgent).GetField("m_uac", PI)!.GetValue(app) != null;

            // 2) PABX liga para o ramal; o app atende
            app.OnIncomingCall += async (ua, req) => { var u = ua.AcceptCall(req); await ua.Answer(u, Midia()); };
            var pabxSaida = new SIPSorcery.SIP.App.SIPUserAgent(tPabx, null);
            pabxSaida.OnCallHungup += _ => pabxDesligado.TrySetResult(true);
            bool atendida;
            if (finalRecebida) atendida = await pabxSaida.Call($"sip:100@127.0.0.1:{portaApp}", null, null, Midia());
            else atendida = await app.Call($"sip:166984671226@127.0.0.1:{portaPabx}", null, null, Midia());
            await Task.Delay(300);
            if (!atendida || !app.IsCallActive) return (false, $"setup falhou atendida={atendida} ativa={app.IsCallActive}");

            // 3) operador clica Desligar
            if (codigoNovo)
            {
                var s = (SipService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(SipService));
                typeof(SipService).GetField("_userAgent", PI)!.SetValue(s, app);
                typeof(SipService).GetField("_sipTransport", PI)!.SetValue(s, tApp);
                typeof(SipService).GetField("_numeroRemotoAtual", PI)!.SetValue(s, "556684671226");
                mDesligar.Invoke(s, new object[] { "teste" });
            }
            else
            {
                app.Cancel();                                     // caminho da 2.5.0: Cancel() e depois Hangup()
                if (app.IsCallActive) app.Hangup();
            }
            var venceu = await Task.WhenAny(pabxDesligado.Task, Task.Delay(3000));
            var recebeu = venceu == pabxDesligado.Task;
            try { pabxSaida.Hangup(); } catch { }
            return (recebeu, $"realizadaOk={okRealizada} uacObsoleto={uacVelho}");
        }
        finally { await Task.Delay(200); tPabx.Shutdown(); tApp.Shutdown(); }
    }
    var antigo = await Cenario(codigoNovo: false);
    Check("desligar: REPRODUZ o bug da 2.5.0 (realizada cancelada -> recebida desligada nao manda BYE ao PABX)", !antigo.pabxRecebeuBye, antigo.detalhe);
    var novo = await Cenario(codigoNovo: true);
    Check("desligar: 2.5.1 manda o BYE da recebida e o PABX encerra a chamada", novo.pabxRecebeuBye, novo.detalhe);
    foreach (var (prev, rec) in new[] { ("nenhuma", true), ("atendida", true), ("cancelada", false), ("atendida", false), ("nenhuma", false) })
    {
        var r = await Cenario(codigoNovo: true, previa: prev, finalRecebida: rec);
        Check($"desligar 2.5.1: {(rec ? "recebida" : "realizada")} atendida e desligada (antes: realizada {prev}) -> PABX encerra", r.pabxRecebeuBye, r.detalhe);
    }

    // escolha do canal para a recuperação (nunca cliente, outro ramal ou ligação nova)
    var mSel2 = typeof(SipService).GetMethod("SelecionarCanalRemanescente", BindingFlags.NonPublic | BindingFlags.Static)!;
    (bool fonte, CanalAtivo? alvo, bool amb) Sel2(string num, double desde, double conversa, params CanalAtivo[] c) =>
        ((bool, CanalAtivo?, bool))mSel2.Invoke(null, new object[] { c.ToList(), "100", num, desde, conversa })!;
    CanalAtivo K(string ch, string lid, string con, int dur, string app = "AppDial", string bridge = "B1") => new(ch, lid, "U", bridge, "100", "", con, "from-internal", "", app, "", dur);
    var cli = K("SIP/BRDID-0000f01", "LX", "100", 40, "Queue");
    var meu = K("SIP/100-0000f02", "LX", "556684671226", 40);
    var r1 = Sel2("6684671226", 2.5, 30, cli, meu);
    Check("recuperacao: acha so o canal do MEU ramal da ligacao desligada", r1.fonte && r1.alvo?.Channel == "SIP/100-0000f02" && !r1.amb);
    var novaMinha = K("SIP/100-0000f03", "LY", "556684671226", 2);
    Check("recuperacao: ligacao NOVA deste ramal (canal mais novo que o desligar) nunca e escolhida", Sel2("6684671226", 2.5, 30, novaMinha).alvo == null && !Sel2("6684671226", 2.5, 30, novaMinha).amb);
    Check("recuperacao: canal de OUTRO ramal ou do cliente nunca e escolhido", Sel2("6684671226", 2.5, 30, cli, K("SIP/104-0000f04", "LX", "556684671226", 40)).alvo == null);
    var r4 = Sel2("6684671226", 2.5, 30, K("SIP/100-0000f05", "LZ", "5511999990000", 40));
    Check("recuperacao: canal antigo meu com outro numero -> ambiguo (nao derruba, so avisa)", r4.alvo == null && r4.amb);
    var conf = K("SIP/100-0000f06", "LC", "556684671226", 60, "ConfBridge", "BC");
    var r5 = Sel2("6684671226", 2.5, 50, conf, K("SIP/104-0000f07", "LC", "556684671226", 60, "ConfBridge", "BC"), K("SIP/BRDID-0000f08", "LC", "100", 60, "ConfBridge", "BC"));
    Check("recuperacao: conferencia -> so o meu canal sai, cliente e outro atendente ficam", r5.alvo?.Channel == "SIP/100-0000f06");
    Check("recuperacao: ramal interno compara numero exato", Sel2("104", 2.5, 10, K("SIP/100-0000f09", "LI", "104", 20)).alvo != null && Sel2("104", 2.5, 10, K("SIP/100-0000f0a", "LJ", "1040", 20)).amb);
}

// ── 1. Seleção de canais para a sala de conferência ─────────────────────────────────
var mSel = typeof(SipService).GetMethod("SelecionarCanaisParaSala", Priv);
if (mSel == null)
{
    Console.WriteLine("[--  ] SelecionarCanaisParaSala inexistente nesta versao (linha de base)");
}
else
{
    List<string>? Sel(string ramal, params (string Channel, string Linkedid, string Uniqueid, string BridgeId)[] canais) =>
        (List<string>?)mSel.Invoke(null, new object[] { canais.ToList(), ramal, ramal });
    string J(List<string>? l) => l == null ? "null" : string.Join("|", l);

    // Chamada direta (grupo de toque): ramal em ponte com o tronco.
    var r = Sel("100", ("SIP/Wavoip-0000003b", "L1", "U1", "B1"), ("SIP/100-0000003c", "L1", "U2", "B1"));
    Check("conf: chamada direta", J(r) == "SIP/100-0000003c|SIP/Wavoip-0000003b", J(r));

    // Chamada entregue por fila: 4 canais no mesmo linkedid, em TODAS as ordens possíveis.
    var fila = new (string, string, string, string)[]
    {
        ("SIP/BRDID-6631998716-0000021e", "L1", "U1", "B1"),
        ("Local/100@from-queue-0000114e;1", "L1", "U2", "B1"),
        ("Local/100@from-queue-0000114e;2", "L1", "U3", "B2"),
        ("SIP/100-00000221", "L1", "U4", "B2"),
    };
    var okFila = true; var visto = "";
    foreach (var p in Permutar(fila))
    {
        visto = J(Sel("100", p));
        if (visto != "SIP/100-00000221|SIP/BRDID-6631998716-0000021e") { okFila = false; break; }
    }
    Check("conf: fila (24 ordens) nunca leva canal Local para a sala", okFila, visto);

    // Mesma fila sem BridgeId no evento: decide pelo linkedid.
    var semPonte = fila.Select(c => (c.Item1, c.Item2, c.Item3, "")).ToArray();
    r = Sel("100", semPonte);
    Check("conf: fila sem BridgeId", J(r) == "SIP/100-00000221|SIP/BRDID-6631998716-0000021e", J(r));

    // Log real de 15/05/2026 12:46:28 (ramal 104): a versão antiga redirecionou a perna do
    // participante (SIP/Wavoip-000002bc + Local/...;2).
    r = Sel("104",
        ("SIP/Wavoip-000002bc", "1778859955.162657", "a", "Bp"),
        ("SIP/BRDID-6631998716-000002bb", "1778859940.162649", "b", "Bh"),
        ("Local/366981288216@from-internal-00003844;2", "1778859955.162657", "c", "Bp"),
        ("Local/366981288216@from-internal-00003844;1", "1778859955.162657", "d", ""),
        ("SIP/104-000002ba", "1778859940.162649", "e", "Bh"));
    Check("conf: caso real 15/05 escolhe a chamada do operador", J(r) == "SIP/104-000002ba|SIP/BRDID-6631998716-000002bb", J(r));

    // Participante interno 101 originado com CallerID do operador (ConnectedLine=100): o canal
    // SIP/101 não pode ser tomado como canal do operador.
    r = Sel("100",
        ("SIP/101-00000010", "L2", "p1", ""),
        ("Local/101@from-internal-00000001;2", "L2", "p2", ""),
        ("Local/101@from-internal-00000001;1", "L2", "p3", ""),
        ("SIP/Wavoip-00000005", "L1", "h1", "B1"),
        ("SIP/100-00000006", "L1", "h2", "B1"));
    Check("conf: participante interno nao vira canal do operador", J(r) == "SIP/100-00000006|SIP/Wavoip-00000005", J(r));

    // Ramal 100 não pode casar com SIP/1001 nem com Local/100@...
    r = Sel("100", ("SIP/1001-00000001", "L1", "U1", "B1"), ("Local/100@from-queue-1;1", "L1", "U2", "B1"));
    Check("conf: sem canal do ramal devolve null (usa heuristica antiga)", r == null, J(r));

    // Só o canal do ramal (ex.: já sozinho): redireciona só ele.
    r = Sel("100", ("SIP/100-00000001", "L1", "U1", ""));
    Check("conf: so o canal do ramal", J(r) == "SIP/100-00000001", J(r));

    // Ambíguo (dois canais reais no mesmo linkedid, sem ponte): não arrisca.
    r = Sel("100", ("SIP/100-1", "L1", "U1", ""), ("SIP/Wavoip-2", "L1", "U2", ""), ("SIP/BRDID-3", "L1", "U3", ""));
    Check("conf: ambiguo devolve null", r == null, J(r));

    // PJSIP
    r = Sel("9001", ("PJSIP/9001-00000001", "L1", "U1", "B1"), ("SIP/100-00000002", "L1", "U2", "B1"));
    Check("conf: ramal PJSIP", J(r) == "PJSIP/9001-00000001|SIP/100-00000002", J(r));
}

// ── 1b. Puxar ligação: canal do outro operador + ponta real do cliente ─────────────
{
    var mPux = typeof(SipService).GetMethod("SelecionarCanaisParaPuxar", Priv)!;
    string Pux(string alvo, params (string Channel, string Linkedid, string Uniqueid, string BridgeId)[] c)
    { var r = mPux.Invoke(null, new object[] { c.ToList(), alvo }); if (r == null) return "null"; var t = ((string CanalOperador, string CanalCliente))r; return t.CanalOperador + "|" + t.CanalCliente; }

    Check("puxar: chamada direta (tronco <-> ramal 104)", Pux("104", ("SIP/Wavoip-00000aff", "L1", "U1", "B1"), ("SIP/104-00000b00", "L1", "U2", "B1")) == "SIP/104-00000b00|SIP/Wavoip-00000aff");
    var fila = new[] { ("SIP/BRDID-6631998716-00000adc", "L1", "U1", "B1"), ("Local/104@from-queue-0000e8a6;1", "L1", "U2", "B1"),
                       ("Local/104@from-queue-0000e8a6;2", "L1", "U3", "B2"), ("SIP/104-00000ade", "L1", "U4", "B2") };
    Check("puxar: chamada da fila leva o TRONCO, nunca o canal Local", Pux("104", fila) == "SIP/104-00000ade|SIP/BRDID-6631998716-00000adc");
    Check("puxar: fila em qualquer ordem dos canais", Pux("104", fila.Reverse().ToArray()) == "SIP/104-00000ade|SIP/BRDID-6631998716-00000adc");
    Check("puxar: ramal ainda tocando (sem ponte) = recusado", Pux("104", ("SIP/Wavoip-00000aff", "L1", "U1", ""), ("SIP/104-00000b00", "L1", "U2", "")) == "null");
    Check("puxar: ramal sem ligacao = recusado", Pux("104", ("SIP/Wavoip-00000aff", "L1", "U1", "B1"), ("SIP/103-00000b00", "L1", "U2", "B1")) == "null");
    Check("puxar: nao confunde com a ligacao de outro ramal", Pux("104", ("SIP/Wavoip-1", "L1", "U1", "B1"), ("SIP/103-2", "L1", "U2", "B1"), ("SIP/Wavoip-3", "L2", "U3", "B2"), ("SIP/104-4", "L2", "U4", "B2")) == "SIP/104-4|SIP/Wavoip-3");
    Check("puxar: ramal sozinho na ponte (cliente ja saiu) = recusado", Pux("104", ("SIP/104-00000b00", "L1", "U2", "B1")) == "null");
}

// ── 1c. Puxar ligação: todas as chamadas vivas (atendimento, fila, URA, fora do horário, caixa postal) ──
{
    var mMontar = typeof(SipService).GetMethod("MontarChamadasAtivas", Priv)!;
    var mMotivo = typeof(SipService).GetMethod("MotivoParaNaoPuxar", Priv)!;
    List<ChamadaAtivaInfo> Montar(string meu, params CanalAtivo[] c) => (List<ChamadaAtivaInfo>)mMontar.Invoke(null, new object[] { c.ToList(), meu })!;
    string? Motivo(string canal, string lid, string meu, params CanalAtivo[] c) => (string?)mMotivo.Invoke(null, new object[] { c.ToList(), canal, lid, meu });
    CanalAtivo C(string ch, string lid, string bridge, string app, string ctx = "from-internal", string num = "5566999990000", string dados = "", string exten = "s", int dur = 30, string nome = "") =>
        new(ch, lid, lid + ch.GetHashCode(), bridge, num, nome, "", ctx, exten, app, dados, dur);

    var tronco1 = C("SIP/Wavoip-00000a01", "L1", "B1", "Dial", "ext-group", dados: "SIP/101&SIP/104");
    var ramal104 = C("SIP/104-00000a02", "L1", "B1", "AppDial", num: "104");
    var filaEsp  = C("SIP/BRDID-6631998716-00000a03", "L2", "", "Queue", "ext-queues", num: "6699887766", dados: "700,tc,,,160", dur: 75);
    var ura      = C("SIP/Tronco 0800 VONO_copy_2-00000a04", "L3", "", "BackGround", "ivr-1", num: "6633221100", dur: 6);
    var foraHor  = C("SIP/Wavoip_VIVO-00000a05", "L4", "", "Playback", "app-announcement-1", num: "5591976103726", dados: "custom/NovoForadeServicoQuerencia08as14,noanswer", dur: 9);
    var cxPostal = C("SIP/Wavoip-00000a06", "L5", "", "VoiceMail", "macro-vm", num: "5566911112222", dados: "400@default,u", exten: "s-NOANSWER", dur: 20);
    var tocando  = C("SIP/Wavoip-00000a07", "L6", "", "Dial", "ext-group", num: "5566933334444", dados: "SIP/101&SIP/102", dur: 4);
    var saidaChamando = C("SIP/Wavoip-00000a08", "L7", "", "AppDial", num: "66955556666");
    var ramalDiscando = C("SIP/109-00000a09", "L7", "", "Dial", num: "109");
    // fila atendida: tronco <-> Local;1 | Local;2 <-> SIP/103
    var filaTronco = C("SIP/BRDID-6631998716-00000a10", "L8", "B8", "Queue", "ext-queues", num: "6677778888", dados: "700,tc", dur: 140);
    var loc1 = C("Local/103@from-queue-0000aa;1", "L8", "B8", "AppQueue", num: "103");
    var loc2 = C("Local/103@from-queue-0000aa;2", "L8", "B9", "Dial", num: "6677778888");
    var ramal103 = C("SIP/103-00000a11", "L8", "B9", "AppDial", num: "103");
    // conferencia e meu proprio atendimento: fora da lista
    var conf = C("SIP/Wavoip-00000a12", "L9", "BC", "ConfBridge", dados: "800");
    var meuTronco = C("SIP/Wavoip-00000a13", "L10", "BM", "Dial", "ext-group"); var meuRamal = C("SIP/100-00000a14", "L10", "BM", "AppDial", num: "100");
    var todos = new[] { tronco1, ramal104, filaEsp, ura, foraHor, cxPostal, tocando, saidaChamando, ramalDiscando, filaTronco, loc1, loc2, ramal103, conf, meuTronco, meuRamal };

    var lista = Montar("100", todos);
    ChamadaAtivaInfo? Por(string canal) => lista.FirstOrDefault(x => x.CanalCliente == canal);
    Check("ativas: em atendimento direto mostra o atendente", Por(tronco1.Channel)?.Status == StatusChamadaAtiva.EmAtendimento && Por(tronco1.Channel)?.RamalAtendente == "104");
    Check("ativas: aguardando na fila (sem atendente, com a fila)", Por(filaEsp.Channel)?.Status == StatusChamadaAtiva.NaFila && Por(filaEsp.Channel)?.Fila == "700" && Por(filaEsp.Channel)?.RamalAtendente == "" && Por(filaEsp.Channel)?.Tronco == "Operadora");
    Check("ativas: na URA", Por(ura.Channel)?.Status == StatusChamadaAtiva.NaUra && Por(ura.Channel)?.Tronco == "0800");
    Check("ativas: ouvindo a mensagem de fora do horario", Por(foraHor.Channel)?.Status == StatusChamadaAtiva.ForaDoHorario && Por(foraHor.Channel)?.Tronco == "WhatsApp Vivo");
    Check("ativas: na caixa postal", Por(cxPostal.Channel)?.Status == StatusChamadaAtiva.CaixaPostal);
    Check("ativas: tocando nos ramais", Por(tocando.Channel)?.Status == StatusChamadaAtiva.TocandoRamais);
    Check("ativas: fila ja atendida mostra o ramal certo atraves do canal Local", Por(filaTronco.Channel)?.Status == StatusChamadaAtiva.EmAtendimento && Por(filaTronco.Channel)?.RamalAtendente == "103");
    Check("ativas: canal do cliente e sempre o tronco (nunca Local/ nem ramal)", lista.All(x => !x.CanalCliente.StartsWith("Local/") && !System.Text.RegularExpressions.Regex.IsMatch(x.CanalCliente, @"^SIP/\d{2,5}-")));
    Check("ativas: saida ainda chamando, conferencia e a MINHA ligacao ficam fora", Por(saidaChamando.Channel) == null && Por(conf.Channel) == null && Por(meuTronco.Channel) == null, string.Join(",", lista.Select(x => x.CanalCliente)));
    Check("ativas: uma linha por chamada", lista.Count == 7 && lista.Select(x => x.Linkedid).Distinct().Count() == 7, $"{lista.Count}");

    // Contrato com a Waven API (/api/ami/calls devolve camelCase) e o aviso de cliente aguardando
    var jsonApi = "[{\"channel\":\"SIP/Wavoip_VIVO-00000a05\",\"linkedid\":\"L4\",\"uniqueid\":\"U4\",\"bridgeId\":\"\",\"callerIDNum\":\"5591976103726\",\"callerIDName\":\"Paulo\",\"connectedLineNum\":\"\",\"context\":\"app-announcement-1\",\"exten\":\"s\",\"application\":\"Playback\",\"applicationData\":\"custom/NovoForadeServicoQuerencia08as14,noanswer\",\"duracaoSeg\":9}]";
    var doApi = JsonSerializer.Deserialize<List<CanalAtivo>>(jsonApi, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    var viaApi = Montar("100", doApi.ToArray());
    Check("api: resposta de /api/ami/calls vira 'Fora do horario' com nome, numero, canal e tempo", viaApi.Count == 1 && viaApi[0].Status == StatusChamadaAtiva.ForaDoHorario && viaApi[0].Nome == "Paulo" && viaApi[0].Numero == "5591976103726" && viaApi[0].Tronco == "WhatsApp Vivo" && viaApi[0].DuracaoSeg == 9 && viaApi[0].Linkedid == "L4");
    var tAviso = typeof(SipService).Assembly.GetType("WavenVoIP.Views.ChamadasAguardandoPopup")!.GetMethod("DeveAvisar")!;
    bool Avisa(ChamadaAtivaInfo c) => (bool)tAviso.Invoke(null, new object[] { c })!;
    Check("aviso: popup so para cliente parado na fila", Avisa(Por(filaEsp.Channel)!));
    Check("aviso: SEM popup na URA nem na mensagem de fora do horario", !Avisa(Por(ura.Channel)!) && !Avisa(Por(foraHor.Channel)!));
    Check("puxar ligacao: URA, fora do horario, fila, tocando, caixa postal e em atendimento continuam na lista",
        Por(ura.Channel) != null && Por(foraHor.Channel) != null && Por(filaEsp.Channel) != null && Por(tocando.Channel) != null && Por(cxPostal.Channel) != null && Por(tronco1.Channel) != null);
    {
        // sequencia real de uma ligacao: URA -> fila -> atendida -> volta a fila -> encerra
        ChamadaAtivaInfo Em(StatusChamadaAtiva st, string lid = "LQ1", string canal = "SIP/BRDID-0000c01", string atend = "") =>
            new ChamadaAtivaInfo { CanalCliente = canal, Linkedid = lid, Numero = "6699887766", Status = st, RamalAtendente = atend, Fila = st == StatusChamadaAtiva.NaFila ? "700" : "" };
        var pol = new WavenVoIP.Services.AvisoFilaPolicy();
        int Mostra(params ChamadaAtivaInfo[] a) => pol.Avaliar(a, out _).Count;
        int Novas(params ChamadaAtivaInfo[] a) { pol.Avaliar(a, out var n); return n.Count; }
        Check("aviso fila: cliente na URA nao avisa, por mais que demore", Mostra(Em(StatusChamadaAtiva.NaUra)) == 0 && Mostra(Em(StatusChamadaAtiva.NaUra)) == 0 && Mostra(Em(StatusChamadaAtiva.NaUra)) == 0);
        Check("aviso fila: 1a leitura na fila ainda nao avisa (espera confirmar)", Mostra(Em(StatusChamadaAtiva.NaFila)) == 0);
        Check("aviso fila: 2a leitura seguida na fila avisa, uma vez", Novas(Em(StatusChamadaAtiva.NaFila)) == 1);
        Check("aviso fila: continua no popup sem ser notificacao nova", Mostra(Em(StatusChamadaAtiva.NaFila)) == 1 && Novas(Em(StatusChamadaAtiva.NaFila)) == 0);
        Check("aviso fila: atendida -> sai do popup na hora", Mostra(Em(StatusChamadaAtiva.EmAtendimento, atend: "104")) == 0);
        Check("aviso fila: em espera (Hold) segue em ponte com o ramal -> nenhum popup, por mais que dure",
            Mostra(Em(StatusChamadaAtiva.EmAtendimento, atend: "104")) == 0 && Mostra(Em(StatusChamadaAtiva.EmAtendimento, atend: "104")) == 0 && Mostra(Em(StatusChamadaAtiva.EmAtendimento, atend: "104")) == 0);
        Check("aviso fila: atendida que VOLTA a fila -> aviso novo, uma linha so",
            Mostra(Em(StatusChamadaAtiva.NaFila)) == 0 && Novas(Em(StatusChamadaAtiva.NaFila)) == 1 && Novas(Em(StatusChamadaAtiva.NaFila)) == 0 && Mostra(Em(StatusChamadaAtiva.NaFila)) == 1);
        {
            var polF = new WavenVoIP.Services.AvisoFilaPolicy();
            ChamadaAtivaInfo NaFilaN(string fila, string lid) => new ChamadaAtivaInfo { CanalCliente = "SIP/BRDID-f" + fila, Linkedid = lid, Numero = "6699887766", Status = StatusChamadaAtiva.NaFila, Fila = fila };
            var f700 = NaFilaN("700", "LF1"); var f701 = NaFilaN("701", "LF2"); var f702 = NaFilaN("702", "LF3"); var fSem = NaFilaN("", "LF4");
            polF.Avaliar(new[] { f700, f701, f702, fSem }, out _);
            var vis = polF.Avaliar(new[] { f700, f701, f702, fSem }, out _).Select(x => x.Fila).OrderBy(x => x).ToList();
            Check("aviso fila: so as filas 700 e 701 avisam (outra fila ou fila desconhecida nao)", string.Join(",", vis) == "700,701", string.Join(",", vis));
            polF.DefinirFilas("702");
            polF.Avaliar(new[] { f700, f702 }, out _);
            Check("aviso fila: lista de filas e configuravel", polF.Avaliar(new[] { f700, f702 }, out _).Select(x => x.Fila).SequenceEqual(new[] { "702" }));

            var polS = new WavenVoIP.Services.AvisoFilaPolicy();
            polS.Avaliar(new[] { f700 }, out _, out _); polS.Avaliar(new[] { f700 }, out _, out _);
            polS.Avaliar(new[] { new ChamadaAtivaInfo { CanalCliente = f700.CanalCliente, Linkedid = "LF1", Numero = f700.Numero, Status = StatusChamadaAtiva.EmAtendimento, RamalAtendente = "103" } }, out _, out var s1);
            Check("aviso fila: fechamento informa 'atendida' com o ramal", s1.Count == 1 && s1[0].motivo == "atendida ramal=103", s1.Count > 0 ? s1[0].motivo : "-");
            polS.Avaliar(new[] { f700 }, out _, out _); polS.Avaliar(new[] { f700 }, out _, out _);
            polS.Avaliar(Array.Empty<ChamadaAtivaInfo>(), out _, out var s2);
            Check("aviso fila: fechamento informa 'encerrada' quando o cliente desliga", s2.Count == 1 && s2[0].motivo == "encerrada");
            polS.Avaliar(Array.Empty<ChamadaAtivaInfo>(), out _, out var s3);
            Check("aviso fila: fechamento e registrado uma vez so", s3.Count == 0);
        }
        Check("aviso fila: encerrada -> popup vazio", Mostra() == 0);
        Check("aviso fila: outra ligacao no MESMO canal depois avisa normalmente",
            Mostra(Em(StatusChamadaAtiva.NaFila, lid: "LQ2")) == 0 && Novas(Em(StatusChamadaAtiva.NaFila, lid: "LQ2")) == 1);

        var pol2 = new WavenVoIP.Services.AvisoFilaPolicy();
        var q = Em(StatusChamadaAtiva.NaFila, lid: "LQ3");
        pol2.Avaliar(new[] { q }, out _); pol2.Avaliar(new[] { q }, out _);
        pol2.DispensarCanal(q.CanalCliente);
        Check("aviso fila: dispensada pelo operador nao volta enquanto o cliente segue na mesma espera",
            pol2.Avaliar(new[] { q }, out _).Count == 0 && pol2.Avaliar(new[] { q }, out _).Count == 0 && pol2.Avaliar(new[] { q }, out _).Count == 0);

        var pol3 = new WavenVoIP.Services.AvisoFilaPolicy();
        var grupo = Em(StatusChamadaAtiva.TocandoRamais, lid: "LQ4", canal: "SIP/Wavoip-0000c04");
        var fora = Em(StatusChamadaAtiva.ForaDoHorario, lid: "LQ5", canal: "SIP/Wavoip-0000c05");
        var cx = Em(StatusChamadaAtiva.CaixaPostal, lid: "LQ6", canal: "SIP/Wavoip-0000c06");
        var msg = Em(StatusChamadaAtiva.Mensagem, lid: "LQ7", canal: "SIP/Wavoip-0000c07");
        var lote = new[] { grupo, fora, cx, msg };
        Check("aviso fila: grupo tocando, fora do horario, caixa postal e mensagem nunca avisam",
            pol3.Avaliar(lote, out _).Count == 0 && pol3.Avaliar(lote, out _).Count == 0 && pol3.Avaliar(lote, out _).Count == 0);

        var pol4 = new WavenVoIP.Services.AvisoFilaPolicy();
        var passou = Em(StatusChamadaAtiva.NaFila, lid: "LQ8");
        Check("aviso fila: passou pela fila e foi atendido na leitura seguinte -> nenhum aviso",
            pol4.Avaliar(new[] { passou }, out _).Count == 0 && pol4.Avaliar(new[] { Em(StatusChamadaAtiva.EmAtendimento, lid: "LQ8", atend: "103") }, out _).Count == 0);

        {
            // v2.5.1 — tocando no meu ramal / LinkedID único
            ChamadaAtivaInfo F(string lid, string canal = "SIP/BRDID-0000d01", string num = "5566998877665", params string[] tocando) =>
                new ChamadaAtivaInfo { CanalCliente = canal, Linkedid = lid, Numero = num, Status = StatusChamadaAtiva.NaFila, Fila = "700", RamaisTocando = tocando };
            var pt = new WavenVoIP.Services.AvisoFilaPolicy();
            pt.DefinirMeuRamal("100", "");
            var tocAqui = F("LT1", tocando: new[] { "100", "103" });
            pt.Avaliar(new[] { tocAqui }, out _);
            Check("2.5.1 aviso: fila tocando no MEU ramal (PABX) -> sem popup", pt.Avaliar(new[] { tocAqui }, out _).Count == 0 && pt.Avaliar(new[] { tocAqui }, out _).Count == 0);
            var parouDeTocar = F("LT1", tocando: new[] { "103" });
            Check("2.5.1 aviso: mesma espera depois de parar de tocar aqui -> continua sem popup", pt.Avaliar(new[] { parouDeTocar }, out _).Count == 0 && pt.Avaliar(new[] { parouDeTocar }, out _).Count == 0);
            var outro = F("LT2", canal: "SIP/BRDID-0000d02", num: "5566911112222", tocando: new[] { "103" });
            pt.Avaliar(new[] { parouDeTocar, outro }, out _);
            Check("2.5.1 aviso: outra ligacao tocando so em OUTRO ramal -> popup normal", pt.Avaliar(new[] { parouDeTocar, outro }, out _).Select(x => x.Linkedid).SequenceEqual(new[] { "LT2" }));

            var pn = new WavenVoIP.Services.AvisoFilaPolicy();
            pn.DefinirMeuRamal("100", "66998877665");   // INVITE chegou antes da leitura do PABX
            var semCanalAqui = F("LT3");
            pn.Avaliar(new[] { semCanalAqui }, out _);
            Check("2.5.1 aviso: numero do INVITE tocando aqui (sem 55/DDD igual) -> sem popup", pn.Avaliar(new[] { semCanalAqui }, out _).Count == 0);

            var pj = new WavenVoIP.Services.AvisoFilaPolicy();
            var vis1 = F("LT4"); pj.Avaliar(new[] { vis1 }, out _); pj.Avaliar(new[] { vis1 }, out _);
            var canaisFora = pj.SuprimirTocandoAqui("66998877665");
            Check("2.5.1 aviso: comecou a tocar aqui com popup aberto -> sai na hora", canaisFora.SequenceEqual(new[] { vis1.CanalCliente }));
            pj.DefinirMeuRamal("100", "");
            pj.Avaliar(new[] { vis1 }, out _, out var sj);
            Check("2.5.1 aviso: fechamento registra motivo tocando_no_meu_ramal", sj.Count == 1 && sj[0].motivo == "tocando_no_meu_ramal", sj.Count > 0 ? sj[0].motivo : "-");

            var pd = new WavenVoIP.Services.AvisoFilaPolicy();
            var c1 = F("LT5", canal: "SIP/BRDID-0000d05"); var c2 = F("LT5", canal: "SIP/Wavoip-0000d06");
            Check("2.5.1 aviso: dois canais do mesmo LinkedID nao confirmam a espera na 1a leitura", pd.Avaliar(new[] { c1, c2 }, out _).Count == 0);
            var v = pd.Avaliar(new[] { c1, c2 }, out var nv);
            Check("2.5.1 aviso: mesmo LinkedID -> uma linha e uma notificacao", v.Count == 1 && nv.Count == 1);
            var c3 = F("LT5", canal: "SIP/BRDID-0000d07");   // PABX trocou o nome do canal
            var v2 = pd.Avaliar(new[] { c3 }, out var nv2);
            Check("2.5.1 aviso: canal renomeado na mesma ligacao -> sem notificacao nova", v2.Count == 1 && nv2.Count == 0);
            Check("2.5.1 aviso: comparacao de numero ignora 55 e prefixo", WavenVoIP.Services.AvisoFilaPolicy.MesmoNumero("5566998877665", "266998877665") && !WavenVoIP.Services.AvisoFilaPolicy.MesmoNumero("5566998877665", "5566998877000"));
        }

        var pol5 = new WavenVoIP.Services.AvisoFilaPolicy();
        var a1 = Em(StatusChamadaAtiva.NaFila, lid: "LQ9", canal: "SIP/BRDID-0000c09"); var a2 = Em(StatusChamadaAtiva.NaFila, lid: "LQ10", canal: "SIP/BRDID-0000c10");
        pol5.Avaliar(new[] { a1, a2 }, out _);
        Check("aviso fila: dois clientes na fila -> duas linhas; um atendido -> sobra o outro",
            pol5.Avaliar(new[] { a1, a2 }, out _).Count == 2 &&
            pol5.Avaliar(new[] { Em(StatusChamadaAtiva.EmAtendimento, lid: "LQ9", canal: "SIP/BRDID-0000c09", atend: "104"), a2 }, out _).Select(x => x.Linkedid).SequenceEqual(new[] { "LQ10" }));
    }
    Check("aviso: SEM popup para ligacao em atendimento em outro ramal, tocando nos ramais/grupo ou na caixa postal",
        !Avisa(Por(tronco1.Channel)!) && !Avisa(Por(filaTronco.Channel)!) && !Avisa(Por(tocando.Channel)!) && !Avisa(Por(cxPostal.Channel)!));
    Check("aviso: a chamada que EU atendo nunca entra (nao esta na lista)", lista.All(x => x.RamalAtendente != "100"));

    {
        // Ponta a ponta: fotos dos canais do PABX (como o /api/ami/calls devolve) de UMA ligacao
        // passando por URA -> fila 700 -> agente tocando -> atendida -> em espera -> volta a fila -> desliga
        const string T = "SIP/BRDID-6631998716-0000e01";
        var pe = new WavenVoIP.Services.AvisoFilaPolicy();
        (int popup, int novas, string saida) Foto(params CanalAtivo[] canais)
        {
            var vis = pe.Avaliar(Montar("100", canais), out var nv, out var sd);
            return (vis.Count, nv.Count, string.Join(";", sd.Select(x => x.motivo)));
        }
        var naUra    = C(T, "LE1", "", "BackGround", "ivr-1", num: "6699112233", dur: 3);
        var naFila   = C(T, "LE1", "", "Queue", "ext-queues", num: "6699112233", dados: "700,tc,,,160", dur: 12);
        var agTocaL1 = C("Local/103@from-queue-0000ee;1", "LE1", "", "AppQueue", num: "103");
        var agTocaL2 = C("Local/103@from-queue-0000ee;2", "LE1", "", "Dial", num: "6699112233");
        var agTocaR  = C("SIP/103-0000e02", "LE1", "", "AppDial", num: "103");
        var atTronco = C(T, "LE1", "BE1", "Queue", "ext-queues", num: "6699112233", dados: "700,tc,,,160", dur: 30);
        var atL1     = C("Local/103@from-queue-0000ee;1", "LE1", "BE1", "AppQueue", num: "103");
        var atL2     = C("Local/103@from-queue-0000ee;2", "LE1", "BE2", "Dial", num: "6699112233");
        var atR      = C("SIP/103-0000e02", "LE1", "BE2", "AppDial", num: "103");

        var f1 = Foto(naUra); var f2 = Foto(naUra);
        Check("ponta a ponta: na URA, nenhuma leitura gera popup", f1.popup == 0 && f2.popup == 0);
        var f3 = Foto(naFila); var f4 = Foto(naFila, agTocaL1, agTocaL2, agTocaR);
        Check("ponta a ponta: entrou na fila 700 -> popup na 2a leitura, mesmo com o agente tocando", f3.popup == 0 && f4.popup == 1 && f4.novas == 1, $"{f3.popup}/{f4.popup}/{f4.novas}");
        { var mm = Montar("100", new[] { naFila, agTocaL1, agTocaL2, agTocaR }); Check("2.5.1 ponta a ponta: ramal do agente tocando vem dos canais do PABX (sem ponte)", mm.Count == 1 && mm[0].RamaisTocando.SequenceEqual(new[] { "103" }), string.Join(",", mm.SelectMany(x => x.RamaisTocando))); var mAt = Montar("100", new[] { atTronco, atL1, atL2, atR }); Check("2.5.1 ponta a ponta: agente em conversa nao conta como tocando", mAt.All(x => x.RamaisTocando.Count == 0)); }
        var f5 = Foto(naFila, agTocaL1, agTocaL2, agTocaR);
        Check("ponta a ponta: seguiu na fila -> mesma linha, sem aviso novo", f5.popup == 1 && f5.novas == 0);
        var f6 = Foto(atTronco, atL1, atL2, atR);
        Check("ponta a ponta: agente 103 atendeu -> popup fecha sozinho", f6.popup == 0 && f6.saida == "atendida ramal=103", f6.saida);
        var f7 = Foto(atTronco, atL1, atL2, atR); var f8 = Foto(atTronco, atL1, atL2, atR);
        Check("ponta a ponta: em conversa/em espera (Hold mantem a ponte) -> sem popup", f7.popup == 0 && f8.popup == 0);
        var f9 = Foto(naFila); var f10 = Foto(naFila);
        Check("ponta a ponta: transferida de volta para a fila -> aviso novo (uma linha)", f9.popup == 0 && f10.popup == 1 && f10.novas == 1);
        var f11 = Foto();
        Check("ponta a ponta: cliente desligou na fila -> popup fecha sozinho", f11.popup == 0 && f11.saida == "encerrada", f11.saida);
        var grp = C("SIP/Wavoip-0000e09", "LE2", "", "Dial", "ext-group", num: "5566933334444", dados: "SIP/101&SIP/102", dur: 4);
        var g1 = Foto(grp); var g2 = Foto(grp); var g3 = Foto(grp);
        Check("ponta a ponta: grupo 600 tocando nunca gera popup", g1.popup == 0 && g2.popup == 0 && g3.popup == 0);
    }

    {
        // Nao atendidas do Historico: soma Perdida + Cliente desistiu + Abandono na fila, separadas
        HistoricoLigacaoItem Na(TipoHistoricoLigacao t, string motivo = "", string canal = "Operadora") => new() { Tipo = t, MotivoPerdida = motivo, CanalEntrada = canal, Numero = "66984671226", DataHora = DateTime.Now };
        var listaNa = new List<HistoricoLigacaoItem>
        {
            Na(TipoHistoricoLigacao.Perdida), Na(TipoHistoricoLigacao.Perdida), Na(TipoHistoricoLigacao.Perdida, "ClienteDesistiu"),
            Na(TipoHistoricoLigacao.Perdida, "AbandonoFila"), Na(TipoHistoricoLigacao.Perdida, "AbandonoFila"), Na(TipoHistoricoLigacao.Perdida, "AbandonoFila"),
            Na(TipoHistoricoLigacao.NaoAtendida), Na(TipoHistoricoLigacao.Recebida), Na(TipoHistoricoLigacao.Realizada), Na(TipoHistoricoLigacao.NaoAtendidaNesseRamal),
            Na(TipoHistoricoLigacao.Recusada), Na(TipoHistoricoLigacao.Cancelada), Na(TipoHistoricoLigacao.CaixaPostal),
        };
        var cNa = NaoAtendidasPolicy.Contar(listaNa);
        Check("nao atendidas: total = perdida + cliente desistiu + abandono na fila", cNa == (6, 2, 1, 3), cNa.ToString());
        Check("nao atendidas: realizada sem resposta, recebida, recusada, cancelada, caixa postal e 'outro ramal' ficam de fora",
            listaNa.Where(NaoAtendidasPolicy.EhNaoAtendida).All(i => i.Tipo == TipoHistoricoLigacao.Perdida) && listaNa.Count(NaoAtendidasPolicy.EhNaoAtendida) == 6);
        Check("nao atendidas: as tres classificacoes continuam com rotulo proprio",
            string.Join("|", listaNa.Where(NaoAtendidasPolicy.EhNaoAtendida).Select(i => i.TipoTexto).Distinct()) == "Perdida|Cliente desistiu|Abandono na fila");
        var vermelho = System.Windows.Media.Color.FromRgb(220, 38, 38);
        Check("nao atendidas: selo vermelho cheio com texto branco, nome e faixa em vermelho",
            listaNa.Where(NaoAtendidasPolicy.EhNaoAtendida).All(i => i.EhNaoAtendidaEntrada && ((System.Windows.Media.SolidColorBrush)i.CorSeloFundo).Color == vermelho && ((System.Windows.Media.SolidColorBrush)i.CorSeloTexto).Color == System.Windows.Media.Colors.White &&
                ((System.Windows.Media.SolidColorBrush)i.CorFaixa).Color == vermelho && ((System.Windows.Media.SolidColorBrush)i.CorNome).Color.R > 180 && ((System.Windows.Media.SolidColorBrush)i.CorNome).Color.G < 40));
        Check("nao atendidas: os demais tipos mantem as cores de antes e nao ganham faixa",
            listaNa.Where(i => !NaoAtendidasPolicy.EhNaoAtendida(i)).All(i => !i.EhNaoAtendidaEntrada && ((System.Windows.Media.SolidColorBrush)i.CorFaixa).Color.A == 0 &&
                ((System.Windows.Media.SolidColorBrush)i.CorSeloFundo).Color == ((System.Windows.Media.SolidColorBrush)i.CorTipoFundo).Color && ((System.Windows.Media.SolidColorBrush)i.CorSeloTexto).Color == ((System.Windows.Media.SolidColorBrush)i.CorTipo).Color));
        var mDash = DashboardService.Calcular(listaNa, DateTime.Today, DateTime.Today.AddDays(1));
        Check("dashboard: indicadores de antes preservados (perdidas = tocou ate o fim + atendida em outro ramal)", mDash.Perdidas == 3, mDash.Perdidas.ToString());

        // Retorno automatico pelo mesmo canal da chamada original
        Check("retorno automatico: WhatsApp TIM -> TIM", NaoAtendidasPolicy.SaidaDeRetorno("WhatsApp TIM") == WavenVoIP.SaidaChamada.WhatsAppTim);
        Check("retorno automatico: WhatsApp Vivo -> Vivo", NaoAtendidasPolicy.SaidaDeRetorno("WhatsApp Vivo") == WavenVoIP.SaidaChamada.WhatsAppVivo);
        Check("retorno automatico: Operadora -> Operadora", NaoAtendidasPolicy.SaidaDeRetorno("Operadora") == WavenVoIP.SaidaChamada.Operadora);
        Check("retorno automatico: 0800 -> sempre Operadora", NaoAtendidasPolicy.SaidaDeRetorno("0800") == WavenVoIP.SaidaChamada.Operadora && NaoAtendidasPolicy.SaidaDeRetorno(" 0800 ") == WavenVoIP.SaidaChamada.Operadora);
        Check("retorno automatico: canal desconhecido nao presume nada (abre a escolha)",
            NaoAtendidasPolicy.SaidaDeRetorno("") == null && NaoAtendidasPolicy.SaidaDeRetorno(null) == null && NaoAtendidasPolicy.SaidaDeRetorno("Entrada não identificada") == null && NaoAtendidasPolicy.SaidaDeRetorno("Queue") == null && NaoAtendidasPolicy.SaidaDeRetorno("Ramal interno") == null);
        Check("retorno automatico: numero discado sai com a regra do canal escolhido",
            DialPlanService.NomeSaida(NaoAtendidasPolicy.SaidaDeRetorno("0800")!.Value) == DialPlanService.NomeSaida(WavenVoIP.SaidaChamada.Operadora) &&
            DialPlanService.AplicarRegraDeDiscagem("66984671226", NaoAtendidasPolicy.SaidaDeRetorno("WhatsApp TIM")!.Value) == DialPlanService.AplicarRegraDeDiscagem("66984671226", WavenVoIP.SaidaChamada.WhatsAppTim) &&
            DialPlanService.AplicarRegraDeDiscagem("66984671226", NaoAtendidasPolicy.SaidaDeRetorno("WhatsApp TIM")!.Value) != DialPlanService.AplicarRegraDeDiscagem("66984671226", WavenVoIP.SaidaChamada.WhatsAppVivo));
    }

    {
        // Volta da suspensao: espera a rede responder e so entao dispara a reconexao, uma vez
        var mRet = typeof(SipService).Assembly.GetType("WavenVoIP.Services.ResumeRecoveryService")!.GetMethod("RetomarAsync", Priv)!;
        (bool voltou, int tentativas) Retomar(Func<Task> acao, Func<string, Task<bool>> rede, int limite = 5) =>
            ((Task<(bool, int)>)mRet.Invoke(null, new object?[] { "api.exemplo", acao, rede, limite, 10 })!).GetAwaiter().GetResult();
        int disparos = 0, consultas = 0;
        var r1 = Retomar(() => { disparos++; return Task.CompletedTask; },
            _ => { consultas++; if (consultas < 4) throw new System.Net.Sockets.SocketException(11001); return Task.FromResult(true); });
        Check("suspensao: sem DNS nas 3 primeiras consultas -> espera e reconecta quando a rede responde", r1.voltou && r1.tentativas == 4 && disparos == 1, $"{r1.voltou}/{r1.tentativas}/{disparos}");
        disparos = 0;
        var r2 = Retomar(() => { disparos++; return Task.CompletedTask; }, _ => Task.FromResult(false), limite: 1);
        Check("suspensao: rede nao volta no prazo -> dispara a reconexao assim mesmo (cada servico tem retentativa)", !r2.voltou && disparos == 1, $"{r2.voltou}/{disparos}");
        var r3 = Retomar(() => throw new InvalidOperationException("falha"), _ => Task.FromResult(true));
        Check("suspensao: erro na reconexao nao derruba o app", r3.voltou);
    }

    // Aviso ao atendente original: quem esta na mesma sala de conferencia que este ramal
    var mSala = typeof(SipService).GetMethod("RamaisNaConferenciaDe", Priv)!;
    List<string> NaSala(string meu, params CanalAtivo[] c) => (List<string>)mSala.Invoke(null, new object[] { c.ToList(), meu })!;
    var sala = new[] { C("SIP/BRDID-6631998716-0000b01", "L20", "BC1", "ConfBridge", dados: "800,,,"), C("SIP/104-0000b02", "L20", "BC1", "ConfBridge", num: "104", dados: "800,,,"), C("SIP/100-0000b03", "L21", "BC1", "ConfBridge", num: "100", dados: "800") };
    Check("conferencia: atendente original (104) ve que o ramal 100 entrou", string.Join(",", NaSala("104", sala)) == "100");
    Check("conferencia: quem entrou (100) ve o 104; cliente/tronco nao conta como operador", string.Join(",", NaSala("100", sala)) == "104");
    Check("conferencia: fora de sala nao ha aviso", NaSala("104", todos).Count == 0 && NaSala("109", sala).Count == 0);
    // Sala unica do Issabel: ligacoes independentes nunca se misturam
    var mOcup = typeof(SipService).GetMethod("MotivoSalaOcupadaPorOutraLigacao", Priv)!;
    string? Ocupada(string meu, params CanalAtivo[] c) => (string?)mOcup.Invoke(null, new object[] { c.ToList(), "800", meu });
    Check("sala: vazia pode ser usada", Ocupada("109", C("SIP/104-0000e01", "L50", "BE", "Dial", num: "104")) == null);
    Check("sala: em uso por outra ligacao e recusada para um terceiro ramal (109)", Ocupada("109", sala)?.Contains("em uso por outra") == true);
    Check("sala: quem ja esta nela (100 e 104) pode adicionar participantes", Ocupada("100", sala) == null && Ocupada("104", sala) == null);
    var chamado = new[] { C("Local/101@from-internal-0000c01;1", "L30", "BC2", "ConfBridge", num: "109", dados: "800"), C("SIP/109-0000c02", "L31", "B9", "Dial", num: "109") };
    Check("sala: participante chamado por mim (ainda nao entrei) conta como minha; para outro ramal esta ocupada", Ocupada("109", chamado) == null && Ocupada("104", chamado) != null);
    Check("sala: outra sala (801) em uso nao bloqueia a 800", Ocupada("109", C("SIP/104-0000d01", "L40", "BD", "ConfBridge", num: "104", dados: "801")) == null);
    // seletor de destino: quais ramais aparecem e como
    var tDest = typeof(SipService).Assembly.GetType("WavenVoIP.Views.EscolherDestinoWindow")!;
    bool Online(StatusRamal st) => (bool)tDest.GetMethod("EhOnline", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { st })!;
    Check("destino: lista so ramais online (livres ou ocupados), nunca offline/indisponivel",
        Online(StatusRamal.Online) && Online(StatusRamal.EmLigacao) && Online(StatusRamal.Tocando) && Online(StatusRamal.Chamando) && !Online(StatusRamal.Offline) && !Online(StatusRamal.Indisponivel) && !Online(StatusRamal.Desconhecido));

    // ramais PJSIP (serie 9000): estado vem com espacos do Asterisk
    var mNorm = typeof(SipService).Assembly.GetType("WavenVoIP.Services.AmiMonitorService")!.GetMethod("NormalizarStatusPeer", BindingFlags.NonPublic | BindingFlags.Static)!;
    string Norm(string status, string raw) => (string)mNorm.Invoke(null, new object[] { new WavenVoIP.Models.ApiAmiPeer { Ramal = "9001", Status = status, RawStatus = raw } })!;
    Check("ramal PJSIP registrado e livre (Not in use) aparece online", Norm("indisponivel", "PJSIP|DS=Not in use|Contacts=9001/sip:9001@10.0.0.5:5060") == "online");
    Check("ramal PJSIP em uso / tocando", Norm("indisponivel", "PJSIP|DS=In use|Contacts=9001/sip:x") == "emligacao" && Norm("indisponivel", "PJSIP|DS=Ringing|Contacts=9001/sip:x") == "tocando");
    Check("ramal PJSIP sem registro continua offline", Norm("offline", "PJSIP|DS=Unavailable|Contacts=") == "offline" && Norm("indisponivel", "PJSIP|DS=Not in use|Contacts=") == "offline");
    Check("ramal SIP nao e alterado; status ja definido pela API e mantido", Norm("indisponivel", "SIP|Status=UNKNOWN") == "indisponivel" && Norm("emligacao", "PJSIP|DS=Not in use|Contacts=x") == "emligacao");
    Check("ramal 9001 e tratado como ramal interno na transferencia", DialPlanService.EhRamalInterno("9001"));

    // microfone: teto de ganho por acoplamento e ajuste do nivel de captura no Windows
    var mTeto = typeof(MicAutoGain).GetMethod("TetoPorAcoplamento", Priv)!;
    double Teto(double a) => (double)mTeto.Invoke(null, new object[] { a })!;
    Check("mic: +30 dB so com microfone isolado (headset); em viva-voz o teto volta a +18 dB", Teto(-45) == 30 && Teto(-40) == 30 && Teto(-25) == 18 && Teto(-30) == 18 && Math.Abs(Teto(-35) - 24) < 0.01);
    var tNiv = typeof(MicAutoGain).Assembly.GetType("WavenVoIP.Services.MicNivelWindows")!;
    float? Nivel(float atual, double fala, long blocos, long lim) => (float?)tNiv.GetMethod("NovoNivel")!.Invoke(null, new object[] { atual, fala, blocos, lim });
    Check("mic Windows: fala baixa (-42 dBFS, nivel 47%) sobe um degrau; nunca passa de 100%", Math.Abs(Nivel(0.47f, -42, 300, 0)!.Value - 0.62f) < 0.001 && Nivel(0.95f, -42, 300, 0) == 1.0f && Nivel(1.0f, -42, 300, 0) == null);
    Check("mic Windows: fala normal nao mexe; pouca fala (< 2 s) nao decide", Nivel(0.47f, -25, 300, 0) == null && Nivel(0.47f, -42, 60, 0) == null);
    Check("mic Windows: fala alta ou saturando desce um degrau, com piso de 30%", Math.Abs(Nivel(0.80f, -10, 300, 0)!.Value - 0.70f) < 0.001 && Math.Abs(Nivel(0.80f, -25, 300, 40)!.Value - 0.70f) < 0.001 && Nivel(0.30f, -10, 300, 0) == null);

    // perdida: quem desistiu x abandono na fila x perdida de fato
    {
        var mMot = typeof(IssabelCdrService).GetMethod("MotivoDaPerdida", Priv)!;
        string Motivo2(params CdrChamada[] l) => (string)mMot.Invoke(null, new object[] { l.ToList() })!;
        var t0 = new DateTime(2026, 10, 13, 9, 2, 18);
        CdrChamada R(string app, int dur, int offset = 0, string disp = "NO ANSWER") => new() { CallDate = t0.AddSeconds(offset), LastApp = app, Duration = dur, Disposition = disp, DContext = "ext-group", Dst = "600", Channel = "SIP/Wavoip-00000073" };
        Check("perdida: caso real 13/10 09:02 (terminou dentro do Dial do grupo 600) = cliente desistiu", Motivo2(R("Dial", 3)) == "ClienteDesistiu");
        Check("perdida: sem regra de tempo - desligou apos 90 s ainda tocando = cliente desistiu", Motivo2(R("Dial", 90)) == "ClienteDesistiu");
        Check("perdida: toque esgotou e o plano seguiu (ultimo registro nao e Dial/Queue) = perdida de fato", Motivo2(R("Dial", 150), R("Hangup", 1, 150)) == "" && Motivo2(R("Dial", 30), R("Playback", 5, 30)) == "");
        Check("perdida: terminou dentro da fila (4 s ou 70 s) = abandono na fila", Motivo2(R("Queue", 70)) == "AbandonoFila" && Motivo2(R("Queue", 4)) == "AbandonoFila");
        Check("perdida: fila esgotou e seguiu para outro destino = perdida de fato", Motivo2(R("Queue", 120), R("VoiceMail", 3, 120)) == "");
        {
            // caso real 13/10 13:21 (fila 700): registros da fila no canal do cliente + pernas internas Local/ com Dial
            CdrChamada Q(string dch, int dur, int off, string disp) => new() { CallDate = t0.AddSeconds(off), LastApp = "Queue", Duration = dur, Disposition = disp, DContext = "ext-queues", Dst = "700", Channel = "SIP/BRDID-6631998716-0000009f", DstChannel = dch };
            CdrChamada L(string ramal, int dur, int off, string disp) => new() { CallDate = t0.AddSeconds(off), LastApp = "Dial", Duration = dur, Disposition = disp, DContext = "from-internal", Dst = ramal, Channel = $"Local/{ramal}@from-queue-00001;2", DstChannel = dur > 0 ? $"SIP/{ramal}-000000a0" : "" };
            Check("perdida: caso real 13/10 13:21 (cliente desligou na fila 700 com 104 e 109 tocando) = abandono na fila, nao 'cliente desistiu'",
                Motivo2(Q("Local/9004@from-queue", 19, 0, "CONGESTION"), L("100", 0, 18, "FAILED"), Q("Local/100@from-queue", 0, 19, "NO ANSWER"), Q("Local/102@from-queue", 0, 19, "CONGESTION"),
                        Q("Local/104@from-queue", 5, 19, "NO ANSWER"), Q("Local/109@from-queue", 5, 19, "NO ANSWER"), L("102", 0, 19, "CONGESTION"), L("104", 5, 19, "NO ANSWER"), L("109", 5, 19, "NO ANSWER")) == "AbandonoFila");
            Check("perdida: grupo de toque (sem fila) continua 'cliente desistiu' mesmo com perna interna", Motivo2(R("Dial", 3), R("Dial", 4, -1)) == "ClienteDesistiu");
        }
        Check("perdida: ramais ocupados/indisponiveis (ninguem tocou) nao e desistencia", Motivo2(R("Dial", 0, 0, "BUSY")) == "" && Motivo2(R("Dial", 0, 0, "FAILED")) == "");
        var qd = new HistoricoLigacaoItem { Tipo = TipoHistoricoLigacao.Perdida, MotivoPerdida = "ClienteDesistiu" };
        var qa = new HistoricoLigacaoItem { Tipo = TipoHistoricoLigacao.Perdida, MotivoPerdida = "AbandonoFila" };
        var qp = new HistoricoLigacaoItem { Tipo = TipoHistoricoLigacao.Perdida };
        Check("perdida: rotulos distintos no historico", qd.TipoTexto == "Cliente desistiu" && qa.TipoTexto == "Abandono na fila" && qp.TipoTexto == "Perdida", $"{qd.TipoTexto}|{qa.TipoTexto}|{qp.TipoTexto}");
        var jsonQd = System.Text.Json.JsonSerializer.Serialize(qd);
        Check("perdida: o motivo e gravado no historico e relido", jsonQd.Contains("\"MotivoPerdida\":\"ClienteDesistiu\"") && !jsonQd.Contains("EhEventoTecnico") &&
            System.Text.Json.JsonSerializer.Deserialize<HistoricoLigacaoItem>(jsonQd)!.EhDesistenciaRapida);
        var hoje = DateTime.Today.AddHours(9);
        foreach (var q in new[] { qd, qa, qp }) { q.DataHora = hoje; q.Numero = "66999990000"; }
        var met = DashboardService.Calcular(new List<HistoricoLigacaoItem> { qd, qa, qp }, DateTime.Today, DateTime.Today.AddDays(1));
        Check("perdida: tres indicadores separados (perdida, cliente desistiu, abandono na fila)", met.Perdidas == 1 && met.ClienteDesistiu == 1 && met.AbandonoFila == 1, $"{met.Perdidas}/{met.ClienteDesistiu}/{met.AbandonoFila}");
    }

    // contatos: leituras e gravacoes simultaneas nunca perdem um contato salvo
    {
        // arquivo temporario: o teste nunca toca no contatos.json de verdade
        var fPath = typeof(ContatoStorageService).GetField("_path", Priv)!;
        var arqReal = (string)fPath.GetValue(null)!;
        var arqTeste = Path.Combine(Path.GetTempPath(), "waven-teste-contatos-" + Guid.NewGuid().ToString("N") + ".json");
        fPath.SetValue(null, arqTeste);
        typeof(ContatoStorageService).GetField("_ultimoJsonBom", Priv)!.SetValue(null, null);
        try
        {
            var baseC = Enumerable.Range(0, 400).Select(i => new Contato { Nome = "Google " + i, Numero = "6699" + (1000000 + i), FonteGoogle = true }).ToList();
            baseC.Add(new Contato { Nome = "Suporte Dlink", Numero = "3832010900" });
            ContatoStorageService.Salvar(baseC);
            int erros = 0, vazias = 0;
            System.Threading.Tasks.Parallel.For(0, 300, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
            {
                try
                {
                    if (i % 3 == 0) ContatoStorageService.SincronizarContatosGoogle(baseC.Where(c => c.FonteGoogle).ToList());
                    else if (i % 3 == 1) ContatoStorageService.ComArquivoTravado(() => ContatoStorageService.Deduplicar(ContatoStorageService.Carregar(), salvarSeAlterou: true));
                    else if (ContatoStorageService.Carregar().Count == 0) System.Threading.Interlocked.Increment(ref vazias);
                }
                catch { System.Threading.Interlocked.Increment(ref erros); }
            });
            var fim = ContatoStorageService.Carregar();
            Check("contatos: 300 sincronizacoes/leituras simultaneas sem erro, sem leitura vazia e com o contato salvo preservado",
                erros == 0 && vazias == 0 && fim.Any(c => c.Numero == "3832010900" && c.Nome == "Suporte Dlink" && !c.FonteGoogle), $"erros={erros} vazias={vazias} total={fim.Count}");
        }
        finally
        {
            fPath.SetValue(null, arqReal);
            typeof(ContatoStorageService).GetField("_ultimoJsonBom", Priv)!.SetValue(null, null);
            try { File.Delete(arqTeste); File.Delete(arqTeste + ".tmp"); } catch { }
        }
    }

    // puxar ligacao: participantes da ligacao (cliente + atendentes), para a area de privacidade
    {
        var mPL = typeof(SipService).GetMethod("ParticipantesDaLigacaoDe", Priv)!;
        List<ParticipanteLigacao> PL(string meu, params CanalAtivo[] c) => (List<ParticipanteLigacao>)mPL.Invoke(null, new object[] { c.ToList(), meu })!;
        var confPL = new[] {
            C("SIP/Wavoip-0000a001", "L70", "BX", "ConfBridge", num: "5566999990000", dados: "800"),
            C("SIP/104-0000a002", "L70", "BX", "ConfBridge", num: "104", dados: "800"),
            C("SIP/100-0000a003", "L71", "BX", "ConfBridge", num: "100", dados: "800"),
            C("SIP/105-0000a004", "L72", "BY", "Dial", num: "105") };
        var pl = PL("100", confPL);
        Check("privacidade: quem puxou (100) ve cliente e atendente original (104); ele mesmo e outras ligacoes ficam fora",
            pl.Count == 2 && pl[0].Tipo == "cliente" && pl[0].Identificador == "5566999990000" && pl[1].Tipo == "atendente" && pl[1].Identificador == "104", string.Join(",", pl.Select(p => p.Tipo + ":" + p.Identificador)));
        Check("privacidade: depois de remover o 104 sobra so o cliente (conversa privada)", PL("100", confPL.Where(c => !c.Channel.StartsWith("SIP/104-")).ToArray()) is { Count: 1 } so && so[0].Tipo == "cliente");
        Check("privacidade: fora de conferencia nao ha participantes a mostrar", PL("105", confPL).Count == 0 && PL("100", C("SIP/100-0000a009", "L80", "BZ", "Dial", num: "100")).Count == 0);
    }

    // gravacao em duas partes: endereco da continuacao
    {
        var mUrl = typeof(SipService).Assembly.GetType("WavenVoIP.Views.DialerShellWindow")!.GetMethod("UrlDaParteDaGravacao", Priv)!;
        string Parte(string alvo, string arq, DateTime dia, string data) => (string)mUrl.Invoke(null, new object[] { alvo, arq, dia, data })!;
        var d13 = new DateTime(2026, 10, 13, 23, 50, 0);
        Check("gravacao: parte 2 no mesmo endereco da principal, so trocando o arquivo",
            Parte("http://pabx/gravacoes/monitor/2026/10/13/force-1-100-x-1.2.wav", "force-1-100-y-1.2-parte2.wav", d13, "2026-10-13") == "http://pabx/gravacoes/monitor/2026/10/13/force-1-100-y-1.2-parte2.wav");
        Check("gravacao: parte iniciada depois da meia-noite usa a pasta do dia seguinte",
            Parte("http://pabx/gravacoes/monitor/2026/10/13/a.wav", "b-parte2.wav", d13, "2026-10-14") == "http://pabx/gravacoes/monitor/2026/10/14/b-parte2.wav");

        // v2.5.3 — arquivo e dia tirados do endereco antigo para pedir o link assinado a API
        var mExt = typeof(SipService).Assembly.GetType("WavenVoIP.Views.DialerShellWindow")!.GetMethod("ExtrairArquivoEDataDaUrlGravacao", Priv)!;
        (bool ok, string arq, DateTime? dia) Ext(string u) { var a = new object?[] { u, null, null }; var r = (bool)mExt.Invoke(null, a)!; return (r, (string)a[1]!, (DateTime?)a[2]); }
        var e1 = Ext("http://pabx.almeidagas.com/gravacoes/monitor/2026/10/20/force-556684263277-556684671226-20261020-211639-1792541799.31768.wav");
        Check("link gravacao: URL real do historico -> arquivo e dia da pasta",
            e1.ok && e1.arq == "force-556684263277-556684671226-20261020-211639-1792541799.31768.wav" && e1.dia == new DateTime(2026, 10, 20));
        var e2 = Ext("http://pabx/gravacoes/monitor/2026/10/14/b-1792000000.5-parte2.wav");
        Check("link gravacao: parte da gravacao (pasta do dia seguinte)", e2.ok && e2.arq == "b-1792000000.5-parte2.wav" && e2.dia == new DateTime(2026, 10, 14));
        var e3 = Ext("https://outro/rec/arquivo.wav");
        Check("link gravacao: URL sem pasta do dia -> so o arquivo", e3.ok && e3.arq == "arquivo.wav" && e3.dia == null);
        Check("link gravacao: caminho local/UNC nao usa a API", !Ext(@"\\servidor\gravacoes\a.wav").ok && !Ext(@"C:\gravacoes\a.wav").ok);
        Check("link gravacao: URL sem arquivo nao usa a API", !Ext("http://pabx/gravacoes/monitor/").ok);

        // v2.5.3 — Auto Update: quem esta na 2.5.1 (oficial) ou na 2.5.2 (teste local) recebe a 2.5.3; nada faz downgrade
        var mMaior = typeof(SipService).Assembly.GetType("WavenVoIP.Services.UpdateService")!.GetMethod("IsRemotaMaior", Priv)!;
        bool Maior(string r, string l) => (bool)mMaior.Invoke(null, new object[] { r, l })!;
        Check("auto update: 2.5.1 -> 2.5.3", Maior("2.5.3", "2.5.1"));
        Check("auto update: 2.5.2 -> 2.5.3", Maior("2.5.3", "2.5.2"));
        Check("auto update: 2.4.7 -> 2.5.3", Maior("2.5.3", "2.4.7"));
        Check("auto update: 2.5.3 nao reinstala a si mesma", !Maior("2.5.3", "2.5.3"));
        Check("auto update: version.json de rollback (2.5.1) nao rebaixa quem esta na 2.5.3", !Maior("2.5.1", "2.5.3"));

        // v2.5.3 — quando usar o link, quando cair no endereco antigo e quando recusar
        var mDec = typeof(SipService).Assembly.GetType("WavenVoIP.Views.DialerShellWindow")!.GetMethod("DecidirLinkGravacao", Priv)!;
        (string acao, bool marca) Dec(int st, string erro, bool soApi)
        {
            var t = (System.Runtime.CompilerServices.ITuple)mDec.Invoke(null, new object[] { st, erro, soApi })!;
            return (t[0]!.ToString()!, (bool)t[1]!);
        }
        Check("link gravacao: API nova emitiu o link -> usa o link e desliga o endereco publico", Dec(200, "", false) == ("UsarLink", true));
        Check("link gravacao: ramal sem permissao -> recusa, nunca cai no endereco publico", Dec(403, "Este ramal nao tem acesso as gravacoes", false) == ("Recusar", true));
        Check("link gravacao: API nova sem o arquivo -> recusa (nao tenta o endereco publico)", Dec(404, "Gravacao nao encontrada", false) == ("Recusar", true));
        Check("link gravacao: API antiga (rota inexistente, 404 sem corpo) -> endereco antigo", Dec(404, "", false) == ("UsarEnderecoAntigo", false));
        Check("link gravacao: API fora do ar antes da troca -> endereco antigo", Dec(0, "timeout", false) == ("UsarEnderecoAntigo", false));
        Check("link gravacao: API fora do ar DEPOIS da troca -> recusa, sem endereco publico", Dec(0, "timeout", true) == ("Recusar", false));
        Check("link gravacao: erro 5xx depois da troca -> recusa", Dec(502, "", true) == ("Recusar", false));
        Check("link gravacao: API antiga depois da troca (rollback da API) -> recusa", Dec(404, "", true) == ("Recusar", false));

        // v2.5.4 — com a Waven API configurada o sync nao faz HEAD nem listagem em /gravacoes do PABX
        var mApi = typeof(SipService).Assembly.GetType("WavenVoIP.Services.IssabelCdrService")!.GetMethod("GravacoesPelaWavenApi", Priv)!;
        bool PelaApi(SipConfig? c) => (bool)mApi.Invoke(null, new object?[] { c })!;
        Check("gravacoes v2.5.4: API ligada e configurada -> sem HEAD/listagem em /gravacoes",
            PelaApi(new SipConfig { UsarWavenApi = true, WavenApiUrl = "https://api.almeidagas.com", WavenApiToken = "x" }));
        Check("gravacoes v2.5.4: API desligada -> valida pelo endereco antigo como antes",
            !PelaApi(new SipConfig { UsarWavenApi = false, WavenApiUrl = "https://api.almeidagas.com", WavenApiToken = "x" }));
        Check("gravacoes v2.5.4: API sem URL ou sem token -> endereco antigo",
            !PelaApi(new SipConfig { UsarWavenApi = true, WavenApiUrl = "", WavenApiToken = "x" }) &&
            !PelaApi(new SipConfig { UsarWavenApi = true, WavenApiUrl = "https://api.almeidagas.com", WavenApiToken = "" }) && !PelaApi(null));
        Check("auto update: 2.5.3 -> 2.5.4", Maior("2.5.4", "2.5.3"));
    }

    // fim da conversa em conferencia (cliente desligou) e canais de servico da sala
    {
        var mEnc = typeof(SipService).Assembly.GetType("WavenVoIP.Views.DialerShellWindow")!.GetMethod("DeveEncerrarConferenciaSemCliente", Priv)!;
        bool teve = false; int n = 0;
        bool Enc(bool conf, bool cli) { var a = new object[] { conf, cli, teve, n }; var r = (bool)mEnc.Invoke(null, a)!; teve = (bool)a[2]; n = (int)a[3]; return r; }
        Check("fim: caso real 13/10 11:41 - cliente sai e na 2a leitura a chamada do operador e encerrada", !Enc(true, true) && !Enc(true, true) && !Enc(true, false) && Enc(true, false));
        teve = false; n = 0;
        Check("fim: operador que entra primeiro e espera (nunca houve cliente) nao e desligado", !Enc(true, false) && !Enc(true, false) && !Enc(true, false));
        teve = false; n = 0;
        Check("fim: leitura isolada sem cliente nao encerra (cliente reaparece)", !Enc(true, true) && !Enc(true, false) && !Enc(true, true) && !Enc(true, false));
        teve = false; n = 0;
        Check("fim: fora de conferencia nada e encerrado e o estado e zerado", !Enc(true, true) && !Enc(false, false) && !Enc(true, false) && !Enc(true, false));
        var mPL2 = typeof(SipService).GetMethod("ParticipantesDaLigacaoDe", Priv)!;
        var comAnuncio = new[] {
            C("SIP/100-0000b001", "L90", "BW", "ConfBridge", num: "100", dados: "800"),
            C("CBAnn/800-0000113b;2", "L91", "BW", "", num: ""),
            C("PJSIP/9001-00000005", "L92", "BW", "ConfBridge", num: "9001", dados: "800") };
        var semCli = (List<ParticipanteLigacao>)mPL2.Invoke(null, new object[] { comAnuncio.ToList(), "100" })!;
        Check("fim: canal de anuncio da sala (CBAnn) nao conta como cliente; atendente PJSIP 9001 aparece", semCli.Count == 1 && semCli[0].Tipo == "atendente" && semCli[0].Identificador == "9001", string.Join(",", semCli.Select(p => p.Tipo + ":" + p.Identificador)));
        var mConf = typeof(SipService).GetMethod("EstaEmConferencia", Priv)!;
        Check("fim: reconhece que o ramal esta na sala mesmo sozinho", (bool)mConf.Invoke(null, new object[] { new[] { comAnuncio[0] }.ToList(), "100" })! && !(bool)mConf.Invoke(null, new object[] { new[] { comAnuncio[0] }.ToList(), "104" })!);
    }

    // contatos: nome editado nao volta ao antigo (WavenVoIP x Waven API x Google)
    {
        var fPath = typeof(ContatoStorageService).GetField("_path", Priv)!;
        var arqReal = (string)fPath.GetValue(null)!;
        var arqTeste = Path.Combine(Path.GetTempPath(), "waven-teste-conflito-" + Guid.NewGuid().ToString("N") + ".json");
        fPath.SetValue(null, arqTeste);
        typeof(ContatoStorageService).GetField("_ultimoJsonBom", Priv)!.SetValue(null, null);
        try
        {
            var junho = new DateTime(2026, 6, 29, 12, 0, 0, DateTimeKind.Utc); var setembro = new DateTime(2026, 9, 10, 15, 0, 0, DateTimeKind.Utc);
            List<Contato> Google(string nome, DateTime? quando) => new() { new Contato { Nome = nome, Numero = "66981288216", FonteGoogle = true, GoogleContactId = "people/c1", AtualizadoEm = quando },
                                                                          new Contato { Nome = "Outro Contato", Numero = "66999990001", FonteGoogle = true, GoogleContactId = "people/c2", AtualizadoEm = junho } };
            Contato Local() => ContatoStorageService.Carregar().First(c => !c.FonteGoogle && c.Numero == "66981288216");
            while (ContatoSyncCoordinator.RenomearNaApi.TryDequeue(out _)) { }

            // 1) empresa (API) com nome de junho x Google alterado em setembro -> vale o Google e a API e avisada
            ContatoStorageService.Salvar(new List<Contato> { new() { Nome = "Edivaldo Entregador", Numero = "66981288216", WavenApiId = "api-1", AtualizadoEm = DateTime.SpecifyKind(junho, DateTimeKind.Unspecified) } });
            ContatoStorageService.SincronizarContatosGoogle(Google("Sebastião Entregador", setembro));
            var okFila = ContatoSyncCoordinator.RenomearNaApi.TryDequeue(out var ren);
            Check("contatos: Google mais recente que a empresa -> nome do Google vale e segue para a Waven API", Local().Nome == "Sebastião Entregador" && okFila && ren.wavenApiId == "api-1" && ren.nome == "Sebastião Entregador" && Local().SincronizacaoPendente == "");
            Check("contatos: o mesmo numero nao fica duplicado (empresa + Google)", ContatoStorageService.Carregar().Count(c => c.Numero == "66981288216") == 1 && ContatoStorageService.Carregar().Count == 2);

            // 2) usuario renomeia agora; o Google continua com o nome antigo -> a edicao fica, em toda sincronizacao
            var todosC = ContatoStorageService.Carregar(); var eC = todosC.First(c => c.Numero == "66981288216"); eC.Nome = "Sebastião Motoboy"; eC.AtualizadoEm = DateTime.UtcNow; ContatoStorageService.Salvar(todosC);
            for (int i = 0; i < 3; i++) ContatoStorageService.SincronizarContatosGoogle(Google("Sebastião Entregador", setembro));
            Check("contatos: nome editado no WavenVoIP nao volta ao nome antigo do Google (3 sincronizacoes)", Local().Nome == "Sebastião Motoboy" && Local().SincronizacaoPendente == "google" && !ContatoSyncCoordinator.RenomearNaApi.TryDequeue(out _));
            Check("contatos: o contato mostra a origem e o aviso de nao enviado", Local().LinhaOrigem.Contains("Empresa (Waven API)") && Local().LinhaOrigem.Contains("não enviada ao Google") && Local().TemPendencia, Local().LinhaOrigem);

            // 3) o Google recebe o mesmo nome -> pendencia some
            ContatoStorageService.SincronizarContatosGoogle(Google("Sebastião Motoboy", DateTime.UtcNow));
            Check("contatos: quando o Google fica com o mesmo nome a pendencia some", Local().Nome == "Sebastião Motoboy" && Local().SincronizacaoPendente == "" && !Local().TemPendencia);

            // 4) alguem altera no Google DEPOIS da edicao local -> vale o Google
            ContatoStorageService.SincronizarContatosGoogle(Google("Sebastião Silva", DateTime.UtcNow.AddMinutes(10)));
            Check("contatos: alteracao posterior feita no Google prevalece sobre a edicao local mais antiga", Local().Nome == "Sebastião Silva" && ContatoSyncCoordinator.RenomearNaApi.TryDequeue(out _));

            // 5) Google sem data de alteracao nunca sobrescreve
            todosC = ContatoStorageService.Carregar(); eC = todosC.First(c => c.Numero == "66981288216"); eC.Nome = "Nome Local"; eC.AtualizadoEm = junho; ContatoStorageService.Salvar(todosC);
            ContatoStorageService.SincronizarContatosGoogle(Google("Nome Google", null));
            Check("contatos: Google sem data de alteracao nao sobrescreve o nome local", Local().Nome == "Nome Local");

            // 6) datas: sem fuso = UTC (Waven API); relogio local convertido
            var semFuso = new DateTime(2026, 10, 13, 13, 0, 0, DateTimeKind.Unspecified);
            Check("contatos: data da API sem fuso e lida como UTC (nao parece horas mais nova)", ContatoSyncCoordinator.ParaUtc(semFuso) == new DateTime(2026, 10, 13, 13, 0, 0, DateTimeKind.Utc) &&
                ContatoSyncCoordinator.Decidir("Novo", new DateTime(2026, 10, 13, 13, 30, 0, DateTimeKind.Utc), "Antigo", semFuso) == ContatoSyncCoordinator.Decisao.LocalVence);
            var jc = System.Text.Json.JsonSerializer.Serialize(new Contato { Nome = "x", Numero = "1", SincronizacaoPendente = "google" });
            Check("contatos: a pendencia e gravada e relida; campos calculados nao vao para o arquivo", jc.Contains("\"SincronizacaoPendente\":\"google\"") && !jc.Contains("LinhaOrigem") && !jc.Contains("TemPendencia"));
        }
        finally
        {
            fPath.SetValue(null, arqReal);
            typeof(ContatoStorageService).GetField("_ultimoJsonBom", Priv)!.SetValue(null, null);
            while (ContatoSyncCoordinator.RenomearNaApi.TryDequeue(out _)) { }
            try { File.Delete(arqTeste); File.Delete(arqTeste + ".tmp"); } catch { }
        }
    }

    // conferencia: presenca em tempo real
    var mPart = typeof(SipService).GetMethod("ParticipantesDaSala", Priv)!;
    var naSala800 = (List<string>)mPart.Invoke(null, new object[] { new[] {
        C("SIP/104-0000f01", "L60", "BF", "ConfBridge", num: "104", dados: "800"),
        C("Local/266984671226@from-internal-0000f02;1", "L61", "BF", "ConfBridge", num: "100", dados: "800"),
        C("SIP/105-0000f03", "L62", "BG", "ConfBridge", num: "105", dados: "801"),
        C("SIP/106-0000f04", "L63", "BH", "Dial", num: "106") }.ToList(), "800" })!;
    Check("conferencia: participantes da sala = ramal e numero chamado; outra sala e chamadas comuns ficam fora", naSala800.SequenceEqual(new[] { "104", "266984671226" }), string.Join(",", naSala800));
    var mMesmo = typeof(WavenVoIP.Views.ConferenceControlWindow).GetMethod("MesmoParticipante", BindingFlags.NonPublic | BindingFlags.Static)!;
    bool Mesmo(string a, string b) => (bool)mMesmo.Invoke(null, new object[] { a, b })!;
    Check("conferencia: numero externo casa com e sem prefixo de rota; ramais diferentes nao casam", Mesmo("66984671226", "266984671226") && Mesmo("104", "104") && !Mesmo("104", "1104") && !Mesmo("9001", "001"));

    Check("trava: chamada viva e livre pode ser puxada", Motivo(ura.Channel, "L3", "100", todos) == null);
    Check("trava: chamada encerrada nao e puxada", Motivo("SIP/Wavoip-0000dead", "L99", "100", todos)?.Contains("encerrada") == true);
    Check("trava: canal reaproveitado por outra chamada nao e puxado", Motivo(ura.Channel, "L-antigo", "100", todos)?.Contains("encerrada") == true);
    var outroPuxando = C("SIP/105-00000a20", "L11", "", "Bridge", num: "105", dados: ura.Channel);
    Check("trava: outro operador ja esta puxando = recusado (sem captura duplicada)", Motivo(ura.Channel, "L3", "100", todos.Append(outroPuxando).ToArray())?.Contains("105") == true);
    Check("trava: ligacao que ja e minha nao e puxada", Motivo(meuTronco.Channel, "L10", "100", todos)?.Contains("seu ramal") == true);
    Check("trava: nunca aceita canal Local ou de ramal como alvo", Motivo(loc1.Channel, "L8", "100", todos) != null && Motivo(ramal104.Channel, "L1", "100", todos) != null);
}

// ── 2. Atendimento automático (URA / anúncio) ───────────────────────────────────────
var mAuto = typeof(IssabelCdrService).GetMethod("IdentificarAtendimentoAutomatico", Priv);
var pAuto = typeof(HistoricoLigacaoItem).GetProperty("AtendimentoAutomatico");
if (mAuto == null || pAuto == null)
{
    Console.WriteLine("[--  ] AtendimentoAutomatico inexistente nesta versao (linha de base)");
}
else
{
    string Auto(params CdrChamada[] l) => (string)mAuto.Invoke(null, new object[] { l.ToList() })!;
    CdrChamada L(string ctx, string app, string dstch, string ch = "SIP/Wavoip-0000003d", string data = "") =>
        new() { DContext = ctx, LastApp = app, DstChannel = dstch, Channel = ch, Disposition = "ANSWERED", BillSec = 11, Dst = "s", LastData = data };

    Check("auto: fora do horario (caso real 11/10 16:17)", Auto(L("app-announcement-1", "Playback", "", data: "custom/NovoForadeServicoQuerencia08as14")) == "ForaHorario");
    Check("auto: fora do horario das filiais (anuncios 3 e 4)", Auto(L("app-announcement-3", "Playback", "", data: "custom/FECHAMENTO_FILIA")) == "ForaHorario" && Auto(L("app-announcement-4", "Playback", "")) == "ForaHorario");
    Check("auto: fora do horario pelo audio em outro contexto", Auto(L("app-announcement-9", "Playback", "", data: "custom/Fora_de_Servico_Novo")) == "ForaHorario");
    Check("auto: anuncio comum (espera) nao e fora do horario", Auto(L("app-announcement-2", "Playback", "", data: "custom/teste_espera")) == "Anuncio");
    Check("auto: URA BackGround", Auto(L("ivr-1", "BackGround", "")) == "URA");
    Check("auto: URA WaitExten", Auto(L("ivr-1", "WaitExten", "")) == "URA");
    Check("auto: URA + anuncio = URA", Auto(L("ivr-1", "BackGround", ""), L("app-announcement-1", "Playback", "")) == "URA");
    CdrChamada V(string app, string data) => new() { DContext = "ext-local", LastApp = app, LastData = data, DstChannel = "", Channel = "SIP/Wavoip-1", Disposition = "ANSWERED", BillSec = 25, Dst = "vmu400" };
    Check("auto: desligou na saudacao da caixa postal 400 = fora do horario", Auto(V("Hangup", "")) == "ForaHorario");
    Check("auto: caixa postal de outro ramal nao e fora do horario", Auto(new CdrChamada { DContext = "ext-local", LastApp = "Hangup", Dst = "vmu104", DstChannel = "", Disposition = "ANSWERED" }) == "");
    var cx = new HistoricoLigacaoItem { Tipo = TipoHistoricoLigacao.CaixaPostal, Numero = "5566999990000" }; pAuto.SetValue(cx, "ForaHorario");
    Check("fora do horario + caixa postal: rotulos", cx.EhForaDoHorario && cx.TipoTexto == "Fora do horário" && cx.RamalExibido == "Mensagem automática + caixa postal", $"{cx.TipoTexto}|{cx.RamalExibido}");
    Check("caixa postal comum continua como antes", new HistoricoLigacaoItem { Tipo = TipoHistoricoLigacao.CaixaPostal }.TipoTexto == "Caixa postal" && new HistoricoLigacaoItem { Tipo = TipoHistoricoLigacao.CaixaPostal }.RamalExibido == "");
    Check("auto: fora do horario seguido de ramal = vazio", Auto(L("app-announcement-1", "Playback", ""), L("from-internal", "Dial", "SIP/104-00000209")) == "");
    Check("auto: grupo de toque atendido por ramal = vazio", Auto(L("ext-group", "Dial", "SIP/103-0000003c")) == "");
    Check("auto: fila atendida = vazio", Auto(L("ext-queues", "Queue", "Local/109@from-queue-00001158;1"), L("app-blackhole", "Hangup", "")) == "");
    Check("auto: URA seguida de ramal = vazio", Auto(L("ivr-1", "BackGround", ""), L("from-internal", "Dial", "SIP/104-00000209")) == "");
    Check("auto: Hangup sozinho = vazio", Auto(L("app-blackhole", "Hangup", "")) == "");
    Check("auto: lista vazia = vazio", Auto() == "");

    HistoricoLigacaoItem Item(TipoHistoricoLigacao t, string auto, string atendeu = "", string destino = "")
    {
        var i = new HistoricoLigacaoItem { Tipo = t, Numero = "559198213493", RamalAtendeu = atendeu, RamalDestino = destino };
        pAuto.SetValue(i, auto);
        return i;
    }
    Check("exibicao: anuncio", Item(TipoHistoricoLigacao.Recebida, "Anuncio").RamalExibido == "Mensagem automática (sem atendente)");
    Check("exibicao: URA", Item(TipoHistoricoLigacao.Recebida, "URA").RamalExibido == "URA (sem atendente)");
    Check("exibicao: atendente real tem prioridade", Item(TipoHistoricoLigacao.Recebida, "URA", atendeu: "103").RamalExibido.Contains("103"));
    Check("exibicao: sem marcacao continua como antes", Item(TipoHistoricoLigacao.Recebida, "", destino: "600").RamalExibido.Contains("600"));
    Check("exibicao: Perdida nao usa a marcacao", Item(TipoHistoricoLigacao.Perdida, "URA").RamalExibido == "");

    var fh = Item(TipoHistoricoLigacao.Recebida, "ForaHorario");
    Check("fora do horario: rotulos", fh.EhForaDoHorario && fh.TipoTexto == "Fora do horário" && fh.RamalExibido == "Mensagem automática", $"{fh.TipoTexto}|{fh.RamalExibido}");
    Check("fora do horario: Tipo persistido continua Recebida (nao e Perdida)", fh.Tipo == TipoHistoricoLigacao.Recebida);
    var ura = Item(TipoHistoricoLigacao.Recebida, "URA");
    Check("URA no expediente nao e fora do horario", !ura.EhForaDoHorario && ura.EhAtendimentoAutomatico && ura.TipoTexto == "Recebida");
    Check("atendida por operador nao e automatica", Item(TipoHistoricoLigacao.Recebida, "", atendeu: "103").EhRecebidaPorOperador && !Item(TipoHistoricoLigacao.Recebida, "ForaHorario", atendeu: "103").EhForaDoHorario);
    Check("Perdida com marcacao nao vira fora do horario", !Item(TipoHistoricoLigacao.Perdida, "ForaHorario").EhForaDoHorario);

    // Indicadores: so o que um operador atendeu conta como Recebida / atendimento.
    var agora = DateTime.Now;
    HistoricoLigacaoItem D(TipoHistoricoLigacao t, string auto, string atendeu, string dur) { var i = Item(t, auto, atendeu); i.DataHora = agora; i.Duracao = dur; return i; }
    var m = DashboardService.Calcular(new List<HistoricoLigacaoItem> {
        D(TipoHistoricoLigacao.Recebida, "", "103", "01:00"), D(TipoHistoricoLigacao.Recebida, "", "104", "03:00"),
        D(TipoHistoricoLigacao.Recebida, "URA", "", "00:03"), D(TipoHistoricoLigacao.Recebida, "ForaHorario", "", "00:11"),
        D(TipoHistoricoLigacao.Realizada, "", "", "02:00"), D(TipoHistoricoLigacao.Perdida, "", "", "00:00") }, agora.AddHours(-1), agora.AddHours(1));
    Check("painel: Recebidas so de operador", m.Recebidas == 2 && m.TotalAtendimentos == 3 && m.Perdidas == 1, $"rec={m.Recebidas} tot={m.TotalAtendimentos} per={m.Perdidas}");
    Check("painel: tempo medio ignora automaticas", m.TempoMedio == "2m 00s", m.TempoMedio);
    Check("painel: por ramal nao atribui automaticas", m.PorRamal.Sum(r => r.Recebidas) == 2, string.Join(",", m.PorRamal.Select(r => r.Ramal + "=" + r.Recebidas)));
    Check("painel: grafico por hora mantem o volume total", m.ChamadasPorHora.Sum(h => h.Contagem) == 6);

    var antigo = JsonSerializer.Deserialize<HistoricoLigacaoItem>("{\"Numero\":\"1\",\"Tipo\":1,\"RamalAtendeu\":\"103\"}")!;
    Check("json: registro antigo sem o campo carrega vazio", (string)pAuto.GetValue(antigo)! == "" && antigo.RamalAtendeu == "103");
}

// ── 2b. Nivelamento automático do microfone ────────────────────────────────────────
{
    // "Fala" sintética: tom de 300 Hz + harmônicos com envelope silábico (4 Hz), em rajadas
    // de 1,5 s separadas por 0,7 s de ruído de fundo.
    short[] Fala(double falaDb, double ruidoDb, double seg, int seed = 7)
    {
        var rnd = new Random(seed); var n = (int)(seg * 8000); var x = new short[n];
        var a = Math.Pow(10, falaDb / 20) * 32768 * 1.9; var r = Math.Pow(10, ruidoDb / 20) * 32768 * 1.7;
        for (int i = 0; i < n; i++)
        {
            var t = i / 8000.0; var emFala = (t % 2.2) < 1.5;
            var env = 0.55 + 0.45 * Math.Sin(2 * Math.PI * 4 * t);
            var v = (rnd.NextDouble() * 2 - 1) * r;
            if (emFala) v += a * env * (0.6 * Math.Sin(2 * Math.PI * 300 * t) + 0.3 * Math.Sin(2 * Math.PI * 900 * t) + 0.1 * Math.Sin(2 * Math.PI * 2100 * t));
            x[i] = (short)Math.Clamp(v, -32768, 32767);
        }
        return x;
    }
    // RMS (dBFS) dos blocos de 20 ms com fala, no trecho final (ganho já estabilizado), e pico.
    (double rmsFala, double rmsRuido, double pico) Medir(short[] x, double desdeSeg)
    {
        double sf = 0, sr = 0; long nf = 0, nr = 0; double pk = 0;
        for (int i = (int)(desdeSeg * 8000); i < x.Length; i++)
        {
            var t = i / 8000.0; var v = x[i] / 32768.0; pk = Math.Max(pk, Math.Abs(v));
            var fase = t % 2.2;
            if (fase > 0.1 && fase < 1.4) { sf += v * v; nf++; } else if (fase > 1.6 && fase < 2.1) { sr += v * v; nr++; }
        }
        return (10 * Math.Log10(sf / nf + 1e-12), 10 * Math.Log10(sr / nr + 1e-12), 20 * Math.Log10(pk + 1e-9));
    }
    short[] Rodar(short[] entrada)
    {
        MicAutoGain.ZerarGanhoInicialParaTeste();
        var g = new MicAutoGain(); var saida = (short[])entrada.Clone();
        for (int i = 0; i + 160 <= saida.Length; i += 160) { var b = new short[160]; Array.Copy(saida, i, b, 0, 160); g.Processar(b, 8000); Array.Copy(b, 0, saida, i, 160); }
        Console.WriteLine("       " + g.EncerrarEResumir());
        return saida;
    }

    var baixoIn = Fala(-34, -66, 20); var baixo = Rodar(baixoIn);
    var (bi, _, _) = Medir(baixoIn, 12); var (bo, bro, bpk) = Medir(baixo, 12);
    Check("mic: operador baixo (-34) sobe ~15 dB", bo - bi > 12 && bo - bi < 18.5, $"entrada={bi:F1} saida={bo:F1} ruido_saida={bro:F1} pico={bpk:F1}");
    Check("mic: operador baixo fica na faixa do cliente (-22..-14 dBFS)", bo > -22 && bo < -14, $"{bo:F1}");

    var okIn = Fala(-15, -62, 20); var ok = Rodar(okIn);
    var (oi, _, _) = Medir(okIn, 12); var (oo, _, opk) = Medir(ok, 12);
    Check("mic: nivel ja correto quase nao muda (|dif| <= 3 dB)", Math.Abs(oo - oi) <= 3, $"entrada={oi:F1} saida={oo:F1}");

    var altoIn = Fala(-6, -55, 20); var alto = Rodar(altoIn);
    var (ai, _, _) = Medir(altoIn, 12); var (ao, _, apk) = Medir(alto, 12);
    Check("mic: microfone alto e atenuado, nunca amplificado", ao <= ai + 0.1 && ao > ai - 6.5, $"entrada={ai:F1} saida={ao:F1}");
    Check("mic: nenhum pico acima de -1 dBFS (sem clipping)", bpk <= -0.9 && opk <= -0.9 && apk <= -0.9, $"{bpk:F1}/{opk:F1}/{apk:F1}");

    var silIn = Fala(-120, -58, 20); var sil = Rodar(silIn);
    var (_, sri, _) = Medir(silIn, 12); var (_, sro, _) = Medir(sil, 12);
    Check("mic: so ruido de fundo nao e amplificado", Math.Abs(sro - sri) < 0.5, $"entrada={sri:F1} saida={sro:F1}");

    var ruidosoIn = Fala(-34, -48, 20); var ruidoso = Rodar(ruidosoIn);
    var (_, rri, _) = Medir(ruidosoIn, 12); var (_, rro, _) = Medir(ruidoso, 12);
    Check("mic: ambiente ruidoso limita o ganho (ruido final <= -40 dBFS)", rro <= -40 && rro - rri <= 8.5, $"ruido entrada={rri:F1} saida={rro:F1}");

    // Estalo isolado (batida no microfone) no meio da fala baixa: sem clipping e sem derrubar o nivel depois.
    var estaloIn = Fala(-34, -66, 20); for (int i = 80000; i < 80040; i++) estaloIn[i] = (short)(i % 2 == 0 ? 30000 : -30000);
    var estalo = Rodar(estaloIn); var (eo, _, epk) = Medir(estalo, 14);
    Check("mic: estalo nao clipa e a fala se recupera", epk <= -0.9 && eo > -24, $"fala_depois={eo:F1} pico={epk:F1}");

    // Ida e volta G.711 (como no envio real): u-law e A-law preservam o nivel nivelado.
    double RmsG711(Func<short, byte> enc, Func<byte, short> dec) { var y = baixo.Select(v => dec(enc(v))).ToArray(); return Medir(y, 12).rmsFala; }
    var mu = RmsG711(NAudio.Codecs.MuLawEncoder.LinearToMuLawSample, NAudio.Codecs.MuLawDecoder.MuLawToLinearSample);
    var al = RmsG711(NAudio.Codecs.ALawEncoder.LinearToALawSample, NAudio.Codecs.ALawDecoder.ALawToLinearSample);
    Check("mic: G.711 u-law/A-law preservam o nivel (+-0,5 dB)", Math.Abs(mu - bo) < 0.5 && Math.Abs(al - bo) < 0.5, $"{mu:F1}/{al:F1} vs {bo:F1}");

    // Audio que ja chega saturado (microfone/placa no maximo, fala "estourada"): o nivelamento
    // nunca soma ganho — so pode atenuar — e o limitador continua valendo.
    {
        MicAutoGain.ZerarGanhoInicialParaTeste();
        var g = new MicAutoGain(); var x = Fala(-34, -66, 20);
        for (int i = 0; i < x.Length; i++) x[i] = (short)Math.Clamp(x[i] * 60, -32768, 32767);   // +35 dB com corte no teto
        var satIn = x.Count(v => Math.Abs((int)v) >= 32000) * 100.0 / x.Length;
        var ganhoMax = double.NegativeInfinity; var y = (short[])x.Clone();
        for (int i = 0; i + 160 <= y.Length; i += 160) { var b = new short[160]; Array.Copy(y, i, b, 0, 160); g.Processar(b, 8000); Array.Copy(b, 0, y, i, 160); if (i >= 8000) ganhoMax = Math.Max(ganhoMax, g.GanhoAtualDb); }
        var satOut = y.Count(v => Math.Abs((int)v) >= 32000) * 100.0 / y.Length;
        Check("mic: audio ja saturado nunca recebe ganho positivo", ganhoMax <= 0.0, $"ganho maximo={ganhoMax:F1} dB (entrada com {satIn:F1}% das amostras no teto)");
        Check("mic: saida do audio saturado fica abaixo do teto (limitador preservado)", satOut == 0 && y.Max(v => Math.Abs((int)v)) <= 0.9 * 32768, $"amostras no teto na saida={satOut:F2}%");
    }

    // Eco acustico (viva-voz): o cliente fala nos ciclos pares, o operador nos impares. O microfone
    // capta o operador (-40 dBFS) + o cliente saindo do alto-falante 150 ms depois (tambem ~-40).
    {
        const int N = 8000 * 44; const double ciclo = 2.2;
        bool CicloPar(int i) => ((int)(i / 8000.0 / ciclo)) % 2 == 0;
        var farBruto = Fala(-15, -120, 44, 11); var nearBruto = Fala(-36, -120, 44, 22); var ruido = Fala(-120, -70, 44, 33);
        var far = new short[N]; var near = new short[N];
        for (int i = 0; i < N; i++) { far[i] = CicloPar(i) ? farBruto[i] : (short)0; near[i] = CicloPar(i) ? (short)0 : nearBruto[i]; }
        short[] Mic(double acoplamentoDb, bool operadorFalaJunto = false)
        {
            var k = Math.Pow(10, acoplamentoDb / 20); var m = new short[N];
            for (int i = 0; i < N; i++)
            {
                // "fala junto": alem dos proprios turnos, o operador fala por cima do cliente em 1 de cada 3 turnos dele
                double v = ruido[i] + (operadorFalaJunto && ((int)(i / 8000.0 / ciclo)) % 6 == 0 ? nearBruto[i] : near[i]);
                if (i >= 1200) v += far[i - 1200] * k;                      // eco 150 ms depois
                m[i] = (short)Math.Clamp(v, -32768, 32767);
            }
            return m;
        }
        double FarDb(int ini) { double sm = 0; for (int j = ini; j < ini + 160; j++) sm += (double)far[j] * far[j]; return 10 * Math.Log10(sm / 160 / (32768.0 * 32768.0) + 1e-14); }
        (double nearDb, double ecoDb, MicAutoGain g) Rodar2(short[] mic, bool comFar, bool soSobreposto = false)
        {
            MicAutoGain.ZerarGanhoInicialParaTeste();
            var g = new MicAutoGain(); var y = (short[])mic.Clone();
            for (int i = 0; i + 160 <= N; i += 160) { var b = new short[160]; Array.Copy(y, i, b, 0, 160); g.Processar(b, 8000, comFar ? FarDb(i) : -120.0); Array.Copy(b, 0, y, i, 160); }
            double sn = 0, se = 0; long nn = 0, ne = 0;
            for (int i = 8000 * 22; i < N; i++)      // metade final: tudo ja estabilizado
            {
                var fase = (i / 8000.0) % ciclo; if (fase < 0.3 || fase > 1.4) continue;
                var v = y[i] / 32768.0;
                if (!CicloPar(i)) { sn += v * v; nn++; }
                else if (!soSobreposto || ((int)(i / 8000.0 / ciclo)) % 6 == 0) { se += v * v; ne++; }
            }
            Console.WriteLine("       " + g.EncerrarEResumir());
            return (10 * Math.Log10(sn / nn + 1e-12), 10 * Math.Log10(se / ne + 1e-12), g);
        }
        var vivaVoz = Mic(-25);
        var antes = Rodar2(vivaVoz, false); var depois = Rodar2(vivaVoz, true);
        double sEco = 0; long nEco = 0; for (int i = 8000 * 22; i < N; i++) { var f = (i / 8000.0) % ciclo; if (CicloPar(i) && f >= 0.3 && f <= 1.4) { var v = vivaVoz[i] / 32768.0; sEco += v * v; nEco++; } }
        var ecoMic = 10 * Math.Log10(sEco / nEco + 1e-12);
        Check("eco: sem a supressao o retorno do alto-falante era AMPLIFICADO (defeito reproduzido)", antes.ecoDb > ecoMic + 6, $"eco no microfone={ecoMic:F1} -> enviado={antes.ecoDb:F1} dBFS");
        Check("eco: com a supressao o retorno sai ATENUADO (>= 10 dB abaixo do captado, >= 20 dB abaixo de antes)", depois.ecoDb <= ecoMic - 10 && depois.ecoDb <= antes.ecoDb - 20, $"enviado={depois.ecoDb:F1} dBFS (antes {antes.ecoDb:F1})");
        Check("eco: a voz do operador continua nivelada (diferenca <= 3 dB e dentro de -24..-14 dBFS)", Math.Abs(depois.nearDb - antes.nearDb) <= 3 && depois.nearDb > -24 && depois.nearDb < -14, $"antes={antes.nearDb:F1} depois={depois.nearDb:F1}");

        // Headset (sem retorno): operador falando JUNTO com o cliente nao pode perder volume.
        var headset = Mic(-90, operadorFalaJunto: true);
        var hAntes = Rodar2(headset, false, soSobreposto: true); var hDepois = Rodar2(headset, true, soSobreposto: true);
        // com headset o acoplamento aprendido e baixo e o teto de ganho sobe (ate +30 dB): a fala fica mais perto do alvo,
        // nunca mais baixa que antes, e falar junto com o cliente nao e atenuado (<= 1,5 dB da fala sozinho)
        Check("eco: com headset a fala simultanea nao e atenuada (<= 1,5 dB) e nunca fica abaixo de antes", Math.Abs(hDepois.ecoDb - hDepois.nearDb) <= 1.5 && hDepois.nearDb >= hAntes.nearDb - 0.5 && hDepois.nearDb <= -14,
            $"fala simultanea antes={hAntes.ecoDb:F1} depois={hDepois.ecoDb:F1} | fala sozinho antes={hAntes.nearDb:F1} depois={hDepois.nearDb:F1} | acoplamento aprendido={hDepois.g.AcoplamentoDb:F0} dB");
    }

    // Oscilacao ("bombeamento"): com fala estavel, o ganho nao pode ficar subindo e descendo.
    {
        MicAutoGain.ZerarGanhoInicialParaTeste();
        var g = new MicAutoGain(); var x = Fala(-34, -66, 30); var ganhos = new List<double>();
        for (int i = 0; i + 160 <= x.Length; i += 160) { var b = new short[160]; Array.Copy(x, i, b, 0, 160); g.Processar(b, 8000); if (i >= 12 * 8000) ganhos.Add(g.GanhoAtualDb); }
        var passoMax = ganhos.Zip(ganhos.Skip(1), (p, q) => Math.Abs(q - p)).Max();
        Check("mic: ganho estavel durante a fala (variacao <= 1,5 dB em 18 s)", ganhos.Max() - ganhos.Min() <= 1.5, $"min={ganhos.Min():F2} max={ganhos.Max():F2} dB");
        Check("mic: sem salto de ganho entre blocos (<= 0,6 dB a cada 20 ms, aplicado em rampa)", passoMax <= 0.6, $"{passoMax:F3} dB");
    }

    // Distorcao: rajadas de tom puro de 1 kHz a -30 dBFS (1,5 s de som, 0,7 s de pausa, como
    // frases). Depois de estabilizar, o que sobra alem do tom dentro de uma rajada (harmonicos
    // + qualquer coisa gerada pelo processamento) tem de ficar muito abaixo dele.
    {
        MicAutoGain.ZerarGanhoInicialParaTeste();
        var g = new MicAutoGain(); var n = 8000 * 22; var x = new short[n]; var rnd = new Random(3);
        for (int i = 0; i < n; i++) { var t = i / 8000.0; x[i] = (short)Math.Round(((t % 2.2) < 1.5 ? 0.0447 * 32767 * Math.Sin(2 * Math.PI * 1000 * t) : 0) + (rnd.NextDouble() * 2 - 1) * 12); }
        for (int i = 0; i + 160 <= n; i += 160) { var b = new short[160]; Array.Copy(x, i, b, 0, 160); g.Processar(b, 8000); Array.Copy(b, 0, x, i, 160); }
        double ss = 0, cc = 0, tot = 0; int ini = (int)(8000 * (2.2 * 8 + 0.3)), m = 8000; // 1 s no meio da 9a rajada
        for (int i = ini; i < ini + m; i++) { var w = 2 * Math.PI * 1000 * i / 8000.0; ss += x[i] * Math.Sin(w); cc += x[i] * Math.Cos(w); tot += (double)x[i] * x[i]; }
        var fund = 2 * (ss * ss + cc * cc) / m; var resto = Math.Max(tot - fund, 1e-9);
        var thd = 10 * Math.Log10(resto / fund);
        Check("mic: distorcao do processamento abaixo de -40 dB (1%)", thd < -40, $"{thd:F1} dB, ganho={g.GanhoAtualDb:F1} dB");
    }

    // Latencia: o processamento e bloco a bloco, sem fila nem "olhar a frente" — a amostra sai
    // na mesma posicao em que entrou.
    {
        MicAutoGain.ZerarGanhoInicialParaTeste();
        var g = new MicAutoGain(); var b = new short[160]; b[37] = 12000; g.Processar(b, 8000);
        var pos = Array.FindIndex(b, v => v != 0);
        Check("mic: atraso adicionado = 0 amostras", pos == 37 && b.Count(v => v != 0) == 1, $"posicao={pos}");
        var blocos = 50000; var buf = Fala(-30, -60, 0.02); var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int k = 0; k < blocos; k++) { var c = (short[])buf.Clone(); g.Processar(c, 8000); }
        var usPorBloco = sw.Elapsed.TotalMilliseconds * 1000 / blocos;
        Check("mic: custo por bloco de 20 ms desprezivel (< 100 microssegundos)", usPorBloco < 100, $"{usPorBloco:F1} us");
    }

    // Caminho real de envio: MuteableAudioSource (proxy usado na chamada) com G.711 u-law.
    try
    {
        var tProxy = typeof(MicAutoGain).Assembly.GetType("WavenVoIP.Services.MuteableAudioSource")!;
        var endpoint = new SIPSorceryMedia.Windows.WindowsAudioEndPoint(new SIPSorcery.Media.AudioEncoder(), -1, -1);
        byte[] Mu(short[] p) => p.Select(NAudio.Codecs.MuLawEncoder.LinearToMuLawSample).ToArray();
        double RmsMu(byte[] e) => 10 * Math.Log10(e.Select(v => { var d = NAudio.Codecs.MuLawDecoder.MuLawToLinearSample(v) / 32768.0; return d * d; }).Average() + 1e-12);
        (double entrada, double saida, byte[] ultimo, int n) Passar(bool nivelar, bool mudo = false)
        {
            MicAutoGain.ZerarGanhoInicialParaTeste();
            object proxy = Activator.CreateInstance(tProxy, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic, null, new object[] { endpoint, nivelar }, null)!;
            var saidas = new List<byte[]>();
            ((SIPSorceryMedia.Abstractions.IAudioSource)proxy).OnAudioSourceEncodedSample += (d, smp) => saidas.Add(smp);
            if (mudo) tProxy.GetMethod("SetMute")!.Invoke(proxy, new object[] { true });
            var relay = tProxy.GetMethod("RelayEncodedSample", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var x = Fala(-34, -66, 16); var ins = new List<byte[]>();
            for (int i = 0; i + 160 <= x.Length; i += 160) { var b = new short[160]; Array.Copy(x, i, b, 0, 160); var e = Mu(b); ins.Add(e); relay.Invoke(proxy, new object[] { (uint)160, e }); }
            tProxy.GetMethod("Detach")!.Invoke(proxy, null);
            var k = ins.Count * 3 / 4; // ultimo quarto: ganho estabilizado
            return (RmsMu(ins.Skip(k).SelectMany(v => v).ToArray()), RmsMu(saidas.Skip(k).SelectMany(v => v).ToArray()), saidas[^1], saidas.Count);
        }
        var lig = Passar(true); var desl = Passar(false); var mudo = Passar(true, mudo: true);
        Check("proxy: nivelamento LIGADO sobe a fala baixa no envio real", lig.saida - lig.entrada > 10, $"entrada={lig.entrada:F1} saida={lig.saida:F1} dBFS");
        Check("proxy: nivelamento DESLIGADO entrega o audio identico (igual a 2.4.7)", Math.Abs(desl.saida - desl.entrada) < 0.01, $"entrada={desl.entrada:F1} saida={desl.saida:F1}");
        Check("proxy: mudo continua enviando silencio G.711 (0xFF)", mudo.ultimo.All(v => v == 0xFF));
        Check("proxy: um bloco entra, um bloco sai (sem fila)", lig.n == 800 && desl.n == 800, $"{lig.n}/{desl.n}");
        try { endpoint.CloseAudio().Wait(2000); } catch { }
    }
    catch (Exception ex) { Console.WriteLine("[--  ] proxy de envio nao testado neste PC: " + ex.GetBaseException()); }
}

// ── 2c. Uma ligação real = um registro (linkedid é a identidade) ───────────────────
{
    HistoricoLigacaoItem H(TipoHistoricoLigacao t, string lid, int seg, string num = "5566999990000", bool cdr = true, string atendeu = "") =>
        new() { Tipo = t, UniqueId = lid, LinkedId = lid, Numero = num, DataHora = new DateTime(2026, 10, 3, 21, 4, 0).AddSeconds(seg), FonteCdr = cdr, RamalAtendeu = atendeu, Duracao = "00:10" };
    var mSup = typeof(IssabelCdrService).GetMethod("SuprimirPerdidasAtendidasPorOutroRamal", Priv)!;
    List<HistoricoLigacaoItem> Sup(params HistoricoLigacaoItem[] l) => (List<HistoricoLigacaoItem>)mSup.Invoke(null, new object[] { l.ToList(), 180 })!;
    var mRaiz = typeof(IssabelCdrService).GetMethod("DeduplicarChamadasMesmaRaiz", Priv)!;
    List<HistoricoLigacaoItem> Raiz(params HistoricoLigacaoItem[] l) => (List<HistoricoLigacaoItem>)mRaiz.Invoke(null, new object[] { l.ToList() })!;
    var mMerge = typeof(IssabelCdrService).GetMethod("MergeGruposPorSrcJanela", Priv)!;
    int Merge(params List<CdrChamada>[] g) => ((List<List<CdrChamada>>)mMerge.Invoke(null, new object[] { g.ToList(), 180 })!).Count;
    CdrChamada R(string lid, int seg, string ch, string dstch = "", string ctx = "ext-group") => new()
        { LinkedId = lid, UniqueId = lid.Length > 0 ? lid : "u" + seg, CallDate = new DateTime(2026, 10, 3, 21, 4, 0).AddSeconds(seg), Src = "5566999990000", Dst = "600", Channel = ch, DstChannel = dstch, DContext = ctx, Disposition = "ANSWERED", BillSec = 5, Duration = 8 };

    Check("ids: perdida e retorno atendido 60 s depois = 2 registros", Sup(H(TipoHistoricoLigacao.Perdida, "L1", 0), H(TipoHistoricoLigacao.Recebida, "L2", 60, atendeu: "104")).Count == 2);
    Check("ids: duas perdidas seguidas do mesmo numero = 2 registros", Sup(H(TipoHistoricoLigacao.Perdida, "L1", 0), H(TipoHistoricoLigacao.Perdida, "L2", 40)).Count == 2);
    Check("ids: duas recebidas seguidas (cai e liga de novo) = 2 registros", Raiz(H(TipoHistoricoLigacao.Recebida, "L1", 0, atendeu: "109"), H(TipoHistoricoLigacao.Recebida, "L2", 39, atendeu: "104")).Count == 2);
    Check("ids: mesma ligacao (mesmo linkedid) nao duplica", Raiz(H(TipoHistoricoLigacao.Realizada, "L1", 0), H(TipoHistoricoLigacao.Realizada, "L1", 1)).Count == 1);
    Check("ids: sem linkedid (fonte antiga) a limpeza por numero+tempo continua", Sup(H(TipoHistoricoLigacao.Perdida, "", 0), H(TipoHistoricoLigacao.Recebida, "", 60, atendeu: "104")).Count == 1
        && Raiz(H(TipoHistoricoLigacao.Realizada, "", 0), H(TipoHistoricoLigacao.Realizada, "", 1)).Count == 1);
    Check("ids: registro local (nao CDR) continua sendo absorvido pelo do CDR", Sup(H(TipoHistoricoLigacao.Perdida, "L1", 0, cdr: false), H(TipoHistoricoLigacao.Recebida, "L2", 30, atendeu: "104")).Count == 1);

    Check("grupos: linkedids diferentes com troncos diferentes = 2 ligacoes", Merge(new() { R("L1", 0, "SIP/Wavoip-00000aff", "SIP/104-00000b00") }, new() { R("L2", 39, "SIP/Wavoip-00000b02", "SIP/104-00000b03") }) == 2);
    Check("grupos: anuncio e, 95 s depois, caixa postal = 2 ligacoes", Merge(new() { R("L1", 0, "SIP/Wavoip-0000003d", "", "app-announcement-1") }, new() { R("L2", 95, "SIP/Wavoip-0000003e", "", "ext-local") }) == 2);
    Check("grupos: fila com varias tentativas no mesmo linkedid = 1 ligacao", Merge(new List<CdrChamada> { R("L1", 0, "SIP/BRDID-1", "Local/109@from-queue-1;1", "ext-queues"), R("L1", 1, "SIP/BRDID-1", "Local/104@from-queue-2;1", "ext-queues") }) == 1);
    Check("grupos: mesmo canal fisico em dois linkedids = 1 ligacao", Merge(new() { R("L1", 0, "SIP/Wavoip-00000aff") }, new() { R("L2", 20, "Local/104@from-queue-9;2", "SIP/Wavoip-00000aff") }) == 1);
    Check("grupos: linhas sem linkedid (fonte antiga) continuam fundindo por numero+janela", Merge(new() { R("", 0, "SIP/Wavoip-1") }, new() { R("", 30, "SIP/104-2") }) == 1);
}

// ── 2d. Retorno da própria ligação e avisos de perdida ─────────────────────────────
{
    var t0 = new DateTime(2026, 9, 20, 10, 38, 14);
    var mLista = typeof(IssabelCdrService).GetMethod("ListarSaidasDiretas", Priv)!;
    var mRet = typeof(IssabelCdrService).GetMethod("EhRetornoDaPropriaLigacao", Priv)!;
    bool Retorno(List<CdrChamada> entrada, params List<CdrChamada>[] outros)
    {
        var todos = outros.Append(entrada).ToList();
        return (bool)mRet.Invoke(null, new object[] { entrada, mLista.Invoke(null, new object[] { todos })! })!;
    }
    List<CdrChamada> Saida(int seg, int dur, string ramal = "100", string linha = "556684263277") => new() { new CdrChamada {
        LinkedId = "S" + seg, UniqueId = "S" + seg, CallDate = t0.AddSeconds(seg), Src = linha, Dst = "66984671226", DContext = "from-internal",
        Channel = $"SIP/{ramal}-00000033", DstChannel = "SIP/Wavoip-00000034", Disposition = "ANSWERED", Duration = dur, BillSec = 0, LastApp = "Dial" } };
    List<CdrChamada> Entrada(int seg, string src = "556684263277", string ramal = "100", string disp = "NO ANSWER") => new() { new CdrChamada {
        LinkedId = "E" + seg, UniqueId = "E" + seg, CallDate = t0.AddSeconds(seg), Src = src, Dst = ramal, DContext = "from-did-direct",
        Channel = "SIP/Wavoip-00000035", DstChannel = $"SIP/{ramal}-00000036", Disposition = disp, Duration = 1, BillSec = 0, LastApp = "Dial" } };

    Check("retorno: caso real 20/09 10:38 (volta 9 s depois, mesmo ramal, mesma linha)", Retorno(Entrada(9), Saida(0, 11)));
    Check("retorno: operador desistiu em 2 s e a volta chegou 8 s depois", Retorno(Entrada(8), Saida(0, 2)));
    Check("retorno: cliente de verdade (outro numero) continua perdida", !Retorno(Entrada(9, src: "556699990416"), Saida(0, 11)));
    Check("retorno: linha da empresa ligando sem discagem em andamento continua perdida", !Retorno(Entrada(9)) && !Retorno(Entrada(120), Saida(0, 11)));
    Check("retorno: tocou em outro ramal (nao no que esta discando) continua perdida", !Retorno(Entrada(9, ramal: "104"), Saida(0, 11)));
    Check("retorno: entrada atendida nunca e evento tecnico", !Retorno(Entrada(9, disp: "ANSWERED"), Saida(0, 11)));

    var ev = new HistoricoLigacaoItem { Tipo = TipoHistoricoLigacao.Recebida, Numero = "556684263277" }; pAuto!.SetValue(ev, "RetornoProprio");
    Check("retorno: rotulo 'Evento tecnico', nao e perdida nem fora do horario", ev.EhEventoTecnico && ev.TipoTexto == "Evento técnico" && !ev.EhForaDoHorario && !ev.EhRecebidaPorOperador && ev.RamalExibido.StartsWith("Retorno"), $"{ev.TipoTexto}|{ev.RamalExibido}");

    ev.DataHora = DateTime.Now; ev.Duracao = "00:01"; ev.RamalDestino = "100";
    var mEv = DashboardService.Calcular(new List<HistoricoLigacaoItem> { ev }, DateTime.Now.AddHours(-1), DateTime.Now.AddHours(1));
    Check("retorno: nao entra em nenhum indicador (perdidas, recebidas, por ramal, por hora)", mEv.Perdidas == 0 && mEv.Recebidas == 0 && mEv.TotalAtendimentos == 0 && mEv.PorRamal.Count == 0 && mEv.ChamadasPorHora.Sum(h => h.Contagem) == 0);
    Check("retorno: nao e candidato a aviso de perdida (Tipo != Perdida)", ev.Tipo != TipoHistoricoLigacao.Perdida && ev.Tipo != TipoHistoricoLigacao.NaoAtendidaNesseRamal);

    // Avisos de perdida: registro preservado, popup so quando faz sentido.
    HistoricoLigacaoItem P(TipoHistoricoLigacao t, int seg, string num = "5566999990000", string atendeu = "", string auto = "")
    { var i = new HistoricoLigacaoItem { Tipo = t, Numero = num, DataHora = t0.AddSeconds(seg), UniqueId = "U" + seg, LinkedId = "U" + seg, FonteCdr = true, RamalAtendeu = atendeu }; pAuto.SetValue(i, auto); return i; }
    var perdida = P(TipoHistoricoLigacao.Perdida, 0);
    Check("aviso: perdida + retorno atendido por operador = sem popup", MissedCallPolicy.FoiAtendidaDepois(perdida, new[] { perdida, P(TipoHistoricoLigacao.Recebida, 60, atendeu: "104") }));
    Check("aviso: perdida + operador ligou de volta (com prefixo de rota) = sem popup", MissedCallPolicy.FoiAtendidaDepois(perdida, new[] { perdida, P(TipoHistoricoLigacao.Realizada, 90, num: "266999990000") }));
    Check("aviso: perdida + retorno que so caiu na URA = popup mantido", !MissedCallPolicy.FoiAtendidaDepois(perdida, new[] { perdida, P(TipoHistoricoLigacao.Recebida, 60, auto: "URA") }));
    Check("aviso: atendimento ANTERIOR a perdida nao a cancela", !MissedCallPolicy.FoiAtendidaDepois(perdida, new[] { perdida, P(TipoHistoricoLigacao.Recebida, -60, atendeu: "104") }));
    Check("aviso: outro numero atendido nao cancela", !MissedCallPolicy.FoiAtendidaDepois(perdida, new[] { perdida, P(TipoHistoricoLigacao.Recebida, 60, num: "5566988887777", atendeu: "104") }));
    Check("aviso: perdida sozinha = popup", !MissedCallPolicy.FoiAtendidaDepois(perdida, new[] { perdida }));
    var avisos = new Dictionary<string, DateTime> { [MissedCallPolicy.NumeroChave("5566999990000")] = t0 };
    Check("aviso: segunda perdida do mesmo numero em 40 s nao abre outro popup", MissedCallPolicy.PopupRecenteParaNumero("5566999990000", avisos, t0.AddSeconds(40)));
    Check("aviso: perdida do mesmo numero 10 min depois avisa de novo", !MissedCallPolicy.PopupRecenteParaNumero("5566999990000", avisos, t0.AddMinutes(10)));
    Check("aviso: perdida de outro numero avisa normalmente", !MissedCallPolicy.PopupRecenteParaNumero("5566988887777", avisos, t0.AddSeconds(40)));
}

// ── 2e. Evento técnico do PABX (Originate via /pbxapi, 20/10/2026) ─────────────────
{
    var mTec = typeof(IssabelCdrService).GetMethod("EhEventoTecnicoDoPabx", Priv)!;
    bool Tec(params CdrChamada[] g) => (bool)mTec.Invoke(null, new object[] { g.ToList() })!;
    CdrChamada C(string ch, string dstCh = "", string src = "", string disp = "FAILED", int bill = 0, string app = "", string rec = "", string ctx = "from-internal-custom", string dst = "1234")
        => new() { Channel = ch, DstChannel = dstCh, Src = src, Dst = dst, Disposition = disp, BillSec = bill, LastApp = app, RecordingFile = rec, DContext = ctx, LinkedId = "1792539965.31636", UniqueId = "1792539965.31636" };

    Check("tecnico: caso real 20/10 (Local/1234@from-internal-custom ;1 AppDial2 + ;2, FAILED)",
        Tec(C("Local/1234@from-internal-custom-00003b68;2"), C("Local/1234@from-internal-custom-00003b68;1", app: "AppDial2")));
    Check("tecnico: caso real 16/10 (Local/s@default System/Playback ANSWERED, billsec 0)",
        Tec(C("Local/s@default-0000215f;1", disp: "ANSWERED", app: "System", ctx: "default", dst: "s"), C("Local/s@default-0000215f;2", disp: "ANSWERED", app: "Playback", ctx: "default", dst: "s")));
    Check("tecnico: cliente pelo tronco nunca e tecnico", !Tec(C("SIP/Wavoip-000001e5", "SIP/100-000001e7", src: "556684671226", disp: "NO ANSWER", ctx: "ext-group", dst: "600")));
    Check("tecnico: cliente sem numero (privado) pelo tronco continua perdida", !Tec(C("SIP/Wavoip-000001e5", "SIP/100-000001e7", src: "", disp: "NO ANSWER", ctx: "ext-group", dst: "600")));
    Check("tecnico: perna de fila com numero do cliente nao e tecnica", !Tec(C("Local/109@from-queue-1;2", "SIP/109-2", src: "556699990416", disp: "NO ANSWER", ctx: "from-queue", dst: "109")));
    Check("tecnico: Originate do app (Local/ramal + SIP/ramal) nao e tecnico", !Tec(C("Local/100@from-internal-1;2", "SIP/100-3", ctx: "from-internal", dst: "100")));
    Check("tecnico: Local com conversa ou gravacao nao e tecnico", !Tec(C("Local/1234@x-1;1", bill: 5)) && !Tec(C("Local/1234@x-1;1", rec: "a.wav")));
    Check("tecnico: grupo com uma linha real junto nao e tecnico", !Tec(C("Local/1234@x-1;1"), C("SIP/Wavoip-9", src: "556684671226", ctx: "from-trunk")));
}

// ── 3. Sync completo sobre CDR real salvo (servidor local) ──────────────────────────
if (args.Length > 0)
{
    var stub = new ApiLocal(File.ReadAllBytes(args[0]));
    SipConfig.CarregarSalva();
    var inst = (SipConfig)typeof(SipConfig).GetField("_cacheInstancia", Priv)!.GetValue(null)!;
    inst.WavenApiUrl = stub.Url;   // só em memória, neste processo
    inst.UsarWavenApi = true;
    var cfg = SipConfig.CarregarSalva()!;

    var dias = args.Length > 2 && int.TryParse(args[2], out var dArg) ? dArg : 7;
    var itens = await IssabelCdrService.SincronizarAsync(cfg, dias, forcarCompleto: true);
    stub.Parar();
    Check("sync: CDR salvo processado", itens.Count > 0 && stub.Consultas >= 1, $"itens={itens.Count} consultas={stub.Consultas}");

    if (pAuto != null)
    {
        var recebidas = itens.Where(i => i.Tipo == TipoHistoricoLigacao.Recebida).ToList();
        var semNinguem = recebidas.Where(i => string.IsNullOrWhiteSpace(i.RamalExibido)).ToList();
        var auto = recebidas.Where(i => (string)pAuto.GetValue(i)! != "").ToList();
        Check("sync: nenhuma Recebida fica sem indicar quem atendeu", semNinguem.Count == 0,
            $"recebidas={recebidas.Count} automaticas={auto.Count} semNinguem={semNinguem.Count}");
        foreach (var s in semNinguem.Take(5))
            Console.WriteLine($"       sem atendente: {s.DataHora:s} uid={s.UniqueId} origem={s.OrigemSaida}");
        Check("sync: marcacao automatica nunca convive com atendente", auto.All(i => string.IsNullOrWhiteSpace(i.RamalAtendeu)));
        var paulo = itens.FirstOrDefault(i => i.UniqueId == "1791746223.3676");
        if (paulo != null)
            Check("sync: chamada de 11/10 16:17 = fora do horario", paulo.EhForaDoHorario && paulo.TipoTexto == "Fora do horário" && paulo.RamalExibido == "Mensagem automática" &&
                paulo.Tipo == TipoHistoricoLigacao.Recebida, paulo.RamalExibido);
        Console.WriteLine("       por tipo: " + string.Join(" ", itens.GroupBy(i => i.Tipo).OrderBy(g => g.Key).Select(g => $"{g.Key}={g.Count()}")));

        // Classificacao das entradas: humano x URA x fora do horario x anuncio comum.
        var fora = itens.Where(i => i.EhForaDoHorario).OrderBy(i => i.DataHora).ToList();
        var cxPostal = itens.Where(i => i.Tipo == TipoHistoricoLigacao.CaixaPostal).ToList();
        Console.WriteLine($"       recebidas: operador={recebidas.Count(i => i.EhRecebidaPorOperador)} URA={recebidas.Count(i => (string)pAuto.GetValue(i)! == "URA")} anuncio={recebidas.Count(i => (string)pAuto.GetValue(i)! == "Anuncio")} | fora do horario={fora.Count} (caixa postal={fora.Count(i => i.Tipo == TipoHistoricoLigacao.CaixaPostal)}) | caixa postal fora do filtro={cxPostal.Count(i => !i.EhForaDoHorario)}");
        foreach (var f in fora)
            Console.WriteLine($"       fora do horario: {f.DataHora:ddd dd/MM HH:mm} ..{(f.Numero.Length > 4 ? f.Numero[^4..] : f.Numero)} {f.Duracao} {f.TipoTexto} / {f.RamalExibido}");
        Check("sync: fora do horario nunca tem atendente nem e Perdida", fora.All(i => string.IsNullOrWhiteSpace(i.RamalAtendeu) && i.Tipo != TipoHistoricoLigacao.Perdida && i.TipoTexto == "Fora do horário"));
        Check("sync: URA do expediente nunca cai no filtro Fora do Horario", recebidas.Where(i => (string)pAuto.GetValue(i)! == "URA").All(i => !i.EhForaDoHorario));
        var lidsCdr = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(args[0])).RootElement.GetProperty("calls").EnumerateArray()
            .Select(e => { var l = e.GetProperty("linkedId").GetString(); return string.IsNullOrEmpty(l) ? e.GetProperty("uniqueId").GetString()! : l; }).Distinct().ToList();
        if (dias >= 30)
        {
            Check("sync: um registro por ligacao real (linkedid), nem a mais nem a menos", itens.Count == lidsCdr.Count && itens.Select(i => i.LinkedId).Distinct().Count() == itens.Count
                && lidsCdr.All(l => itens.Any(i => i.LinkedId == l)), $"registros={itens.Count} linkedids={lidsCdr.Count}");
            bool Tem(string fim, string hhmm, TipoHistoricoLigacao t) => itens.Any(i => i.Numero.EndsWith(fim) && i.DataHora.ToString("dd/MM HH:mm") == hhmm && i.Tipo == t);
            Check("sync: as 4 ligacoes antes engolidas voltaram, sem perder a vizinha",
                Tem("6767", "13/09 21:32", TipoHistoricoLigacao.CaixaPostal) && Tem("6767", "13/09 21:33", TipoHistoricoLigacao.Recebida) &&
                itens.Count(i => i.Numero.EndsWith("1649") && i.DataHora.ToString("dd/MM HH:mm") == "28/09 06:58") == 2 &&
                Tem("8147", "29/09 22:00", TipoHistoricoLigacao.CaixaPostal) && Tem("8147", "29/09 21:59", TipoHistoricoLigacao.Recebida) &&
                Tem("6243", "03/10 21:04", TipoHistoricoLigacao.Recebida) && Tem("6243", "03/10 21:05", TipoHistoricoLigacao.CaixaPostal));
            var tecnicos = itens.Where(i => i.EhEventoTecnico).OrderBy(i => i.DataHora).ToList();
            Check("sync: so os 3 retornos da propria ligacao de 20/09 viram evento tecnico", tecnicos.Count == 3 && tecnicos.All(i => i.DataHora.Date == new DateTime(2026, 9, 20) && i.RamalDestino == "100"),
                string.Join(", ", tecnicos.Select(i => i.DataHora.ToString("dd/MM HH:mm:ss"))));
            Check("sync: perdida real vizinha (20/09 10:40, outro numero) continua Perdida", itens.Any(i => i.DataHora.ToString("dd/MM HH:mm") == "20/09 10:40" && i.Tipo == TipoHistoricoLigacao.Perdida));
            var perdidasReais = itens.Where(i => i.Tipo == TipoHistoricoLigacao.Perdida).ToList();
            var comRetorno = perdidasReais.Count(p => MissedCallPolicy.FoiAtendidaDepois(p, itens.Where(o => (o.DataHora - p.DataHora).TotalMinutes <= 15)));
            Console.WriteLine($"       perdidas no periodo: {perdidasReais.Count} (todas no historico e no painel); {comRetorno} tiveram retorno atendido em ate 15 min e nao geram popup");
            Check("sync: cada registro tem a propria gravacao (nenhuma compartilhada)", itens.Where(i => !string.IsNullOrEmpty(i.GravacaoArquivo)).GroupBy(i => i.GravacaoArquivo).All(g => g.Count() == 1));

            // Incremental: janela completa ate 03/10 21:05:00 (entre o anuncio das 21:04 e a caixa
            // postal das 21:05 do mesmo numero) e o resto chegando depois, em duas consultas "desde".
            var doc = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(args[0]))!;
            var todas = doc["calls"]!.AsArray().Select(n => n!.DeepClone()).ToList();
            string Lid(System.Text.Json.Nodes.JsonNode n) => string.IsNullOrEmpty((string?)n["linkedId"]) ? (string)n["uniqueId"]! : (string)n["linkedId"]!;
            DateTime Dt(System.Text.Json.Nodes.JsonNode n) => DateTime.Parse((string)n["callDate"]!, System.Globalization.CultureInfo.InvariantCulture);
            var c1 = new DateTime(2026, 10, 3, 21, 5, 0); var c2 = new DateTime(2026, 10, 8, 12, 1, 0);
            var lidInicio = todas.GroupBy(Lid).ToDictionary(g => g.Key, g => g.Min(Dt));
            byte[] Corpo(Func<System.Text.Json.Nodes.JsonNode, bool> f, bool inc, DateTime agora) => System.Text.Encoding.UTF8.GetBytes(new System.Text.Json.Nodes.JsonObject {
                ["calls"] = new System.Text.Json.Nodes.JsonArray(todas.Where(f).Select(n => n.DeepClone()).ToArray()), ["incremental"] = inc,
                ["serverTime"] = agora.ToString("yyyy-MM-dd HH:mm:ss"), ["windowStart"] = (string?)doc["windowStart"] }.ToJsonString());
            var stub2 = new ApiLocal(Corpo(n => lidInicio[Lid(n)] < c1, false, c1));
            inst.WavenApiUrl = stub2.Url;
            var parte1 = await IssabelCdrService.SincronizarAsync(cfg, dias, forcarCompleto: true);
            stub2.CorpoIncremental = Corpo(n => lidInicio[Lid(n)] >= c1 && lidInicio[Lid(n)] < c2, true, c2);
            var parte2 = await IssabelCdrService.SincronizarAsync(cfg, dias, forcarCompleto: false);
            stub2.CorpoIncremental = Corpo(n => lidInicio[Lid(n)] >= c2, true, DateTime.Parse((string)doc["serverTime"]!));
            var parte3 = await IssabelCdrService.SincronizarAsync(cfg, dias, forcarCompleto: false);
            stub2.Parar();
            string Chave(HistoricoLigacaoItem i) => string.Join("|", i.UniqueId, i.LinkedId, i.DataHora.ToString("s"), i.Tipo, i.Numero, i.Duracao, i.RamalAtendeu, i.GravacaoArquivo, i.AtendimentoAutomatico);
            Check("incremental: as consultas com 'desde' foram usadas", stub2.ConsultasIncrementais == 2, $"{stub2.ConsultasIncrementais}");
            Check("incremental: chamadas novas entram sem juntar com as antigas do mesmo numero", parte1.Count < parte2.Count && parte2.Count < parte3.Count
                && parte2.Count(i => i.Numero.EndsWith("6243") && i.DataHora.Date == new DateTime(2026, 10, 3)) == 2, $"{parte1.Count} -> {parte2.Count} -> {parte3.Count}");
            Check("incremental: resultado final identico ao da consulta completa", parte3.Select(Chave).OrderBy(x => x).SequenceEqual(itens.Select(Chave).OrderBy(x => x)), $"{parte3.Count} x {itens.Count}");
        }
        Check("sync: atendida por operador nunca e marcada como automatica", itens.Where(i => !string.IsNullOrWhiteSpace(i.RamalAtendeu)).All(i => !i.EhAtendimentoAutomatico));
    }

    if (args.Length > 1)
    {
        // Comparável entre versões: sem Id (aleatório) e sem o campo novo.
        var linhas = itens.Select(i => string.Join("|", i.UniqueId, i.LinkedId, i.DataHora.ToString("s"), i.Tipo, i.Numero, i.Nome,
            i.Duracao, i.OrigemSaida, i.CanalEntrada, i.RamalOrigem, i.RamalDestino, i.RamalAtendeu, i.GravacaoArquivo,
            i.GravacaoUrl, i.CanceladaPeloOperador)).ToList();
        File.WriteAllLines(args[1], linhas);
        Console.WriteLine($"       historico gravado em {args[1]} ({linhas.Count} linhas)");
    }
}

Console.WriteLine($"\nRESULTADO: {total - falhas}/{total} OK, {falhas} falha(s)");
await Task.Delay(1500);
return falhas == 0 ? 0 : 1;

static IEnumerable<T[]> Permutar<T>(T[] itens)
{
    if (itens.Length <= 1) { yield return itens; yield break; }
    for (var i = 0; i < itens.Length; i++)
    {
        var resto = itens.Where((_, j) => j != i).ToArray();
        foreach (var p in Permutar(resto))
            yield return new[] { itens[i] }.Concat(p).ToArray();
    }
}

// Devolve sempre o mesmo corpo salvo para /api/cdr/calls.
sealed class ApiLocal
{
    private readonly System.Net.Sockets.TcpListener _l;
    private readonly byte[] _corpo;
    public byte[]? CorpoIncremental;   // resposta para consultas com "desde="
    public int ConsultasIncrementais;
    private volatile bool _parar;
    public int Consultas;
    public string Url { get; }

    public ApiLocal(byte[] corpo)
    {
        _corpo = corpo;
        _l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        _l.Start();
        Url = $"http://127.0.0.1:{((System.Net.IPEndPoint)_l.LocalEndpoint).Port}";
        _ = Task.Run(LoopAsync);
    }

    public void Parar() { _parar = true; try { _l.Stop(); } catch { } }

    private async Task LoopAsync()
    {
        while (!_parar)
        {
            System.Net.Sockets.TcpClient c;
            try { c = await _l.AcceptTcpClientAsync(); } catch { break; }
            _ = Task.Run(() => AtenderAsync(c));
        }
    }

    private async Task AtenderAsync(System.Net.Sockets.TcpClient c)
    {
        using (c)
        {
            var s = c.GetStream();
            var rd = new StreamReader(s, System.Text.Encoding.ASCII);
            var linha = await rd.ReadLineAsync() ?? "";
            while (!string.IsNullOrEmpty(await rd.ReadLineAsync() ?? "")) { }
            var alvo = linha.Split(' ').ElementAtOrDefault(1) ?? "/";
            var ehCdr = alvo.StartsWith("/api/cdr/calls");
            if (ehCdr) Interlocked.Increment(ref Consultas);
            var ehDelta = ehCdr && alvo.Contains("desde=") && CorpoIncremental != null;
            if (ehDelta) Interlocked.Increment(ref ConsultasIncrementais);
            var corpo = ehDelta ? CorpoIncremental! : ehCdr ? _corpo : System.Text.Encoding.UTF8.GetBytes("{}");
            var cab = $"HTTP/1.1 {(ehCdr ? "200 OK" : "404 Not Found")}\r\nContent-Type: application/json\r\nContent-Length: {corpo.Length}\r\nConnection: close\r\n\r\n";
            await s.WriteAsync(System.Text.Encoding.ASCII.GetBytes(cab));
            await s.WriteAsync(corpo);
        }
    }
}
