// v2.6.0 — teste da interface responsiva, 100% offline.
// Monta as janelas REAIS (shell com as 5 abas, Configurações e Chamada) sem exibi-las, simula a
// área útil de cada monitor/escala do Windows, aplica o mesmo LayoutResponsivo do app e grava um
// PNG por tela (renderizado no DPI simulado). Confere automaticamente que nenhum botão, campo ou
// filtro fica fora da janela ou cortado pelo layout (rolagem só vertical, dentro de ScrollViewer).
// Não registra SIP, não consulta API/AMI/CDR, não grava histórico/configuração.
// Uso: WavenVoIP.UiTests <pastaSaida>
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WavenVoIP;
using WavenVoIP.Models;
using WavenVoIP.Services;
using WavenVoIP.Views;

var pasta = Path.GetFullPath(args.Length > 0 ? args[0] : "telas");
Directory.CreateDirectory(pasta);
int total = 0, falhas = 0;
var relatorio = new List<string>();
void Check(string nome, bool ok, string detalhe = "")
{
    total++; if (!ok) falhas++;
    if (!ok) Console.WriteLine($"[FAIL] {nome} {detalhe}");
}

var th = new Thread(() =>
{
    if (Application.Current == null) new App().InitializeComponent();

    // LayoutResponsivo é internal: acessado por reflexão (mesma conta do app)
    var tipoLayout = typeof(App).Assembly.GetType("WavenVoIP.Services.LayoutResponsivo")!;
    var opcoesPadrao = Activator.CreateInstance(tipoLayout.GetNestedType("Opcoes", BindingFlags.NonPublic)!, nonPublic: true)!;
    var sugeridaMi = tipoLayout.GetMethod("EscalaSugerida", BindingFlags.NonPublic | BindingFlags.Static)!;
    double Sugerida(Rect util) => (double)sugeridaMi.Invoke(null, new object[] { util, opcoesPadrao })!;

    // Para timers e ícone de bandeja criados no construtor: nada pode registrar/consultar.
    void Neutralizar(Window w)
    {
        foreach (var f in w.GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Instance))
        {
            var v = f.GetValue(w);
            if (v is DispatcherTimer t) t.Stop();
            if (v is System.Windows.Forms.NotifyIcon ni) { ni.Visible = false; ni.Dispose(); }
        }
    }

    bool Visivel(DependencyObject o)
    {
        for (var p = o; p != null; p = VisualTreeHelper.GetParent(p))
            if (p is UIElement u && u.Visibility != Visibility.Visible) return false;
        return true;
    }

    // Controles interativos fora da janela ou recortados por um painel (corte real de layout).
    List<string> Cortes(FrameworkElement raiz, double cw, double ch)
    {
        var cortes = new List<string>();
        void Varrer(DependencyObject o)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(o); i++)
            {
                var f = VisualTreeHelper.GetChild(o, i);
                if (f is UIElement ue && ue.Visibility != Visibility.Visible) continue;
                // cabeçalho de DataGrid fica de fora: colunas "*" só são calculadas num passe adiado do
                // dispatcher, que não roda aqui (no app real a coluna ocupa o espaço normalmente)
                if (f is ButtonBase or ComboBox or TextBox or DatePicker && f is not DataGridColumnHeader && f is FrameworkElement c && c.ActualWidth > 0)
                {
                    var r = c.TransformToAncestor(raiz).TransformBounds(new Rect(0, 0, c.ActualWidth, c.ActualHeight));
                    var emRolagem = false;
                    string? motivo = null;
                    for (var p = VisualTreeHelper.GetParent(c); p != null && p != raiz; p = VisualTreeHelper.GetParent(p))
                    {
                        if (p is ScrollContentPresenter) emRolagem = true;
                        if (p is FrameworkElement pe && (LayoutInformation.GetLayoutClip(pe) != null || pe is ScrollContentPresenter))
                        {
                            var pr = pe.TransformToAncestor(raiz).TransformBounds(new Rect(0, 0, pe.ActualWidth, pe.ActualHeight));
                            var foraH = r.Left < pr.Left - 1 || r.Right > pr.Right + 1;
                            var foraV = !emRolagem && (r.Top < pr.Top - 1 || r.Bottom > pr.Bottom + 1);
                            if (foraH || foraV) { motivo = $"recortado por {pe.GetType().Name}{(string.IsNullOrEmpty(pe.Name) ? "" : "#" + pe.Name)}"; break; }
                        }
                    }
                    if (motivo == null && (r.Left < -1 || r.Right > cw + 1 || (!emRolagem && r.Bottom > ch + 1)))
                        motivo = "fora da janela";
                    c.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    var pedida = c.DesiredSize.Width - c.Margin.Left - c.Margin.Right;   // DesiredSize inclui a margem
                    if (motivo == null && c is ButtonBase && c.ActualWidth + 1 < pedida && c.Width is double.NaN)
                        motivo = $"espremido {c.ActualWidth:F0}/{pedida:F0}";
                    if (motivo != null)
                        cortes.Add($"{c.GetType().Name}{(string.IsNullOrEmpty(c.Name) ? "" : "#" + c.Name)}[{Rotulo(c)}] {motivo} x={r.Left:F0}..{r.Right:F0} y={r.Top:F0}..{r.Bottom:F0}");
                }
                Varrer(f);
            }
        }
        Varrer(raiz);
        return cortes;
    }

    string Rotulo(FrameworkElement c)
    {
        if (c.ToolTip is string t) return t;
        if (c is ContentControl cc && cc.Content is string s) return s;
        return "";
    }

    void Foto(FrameworkElement alvo, double cw, double ch, double dpi, string nome)
    {
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(cw * dpi), (int)Math.Ceiling(ch * dpi), 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
        var fundo = new DrawingVisual();
        using (var dc = fundo.RenderOpen()) dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xF7, 0xF8, 0xFC)), null, new Rect(0, 0, cw, ch));
        bmp.Render(fundo); bmp.Render(alvo);
        var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(Path.Combine(pasta, nome)); enc.Save(fs);
    }

    // Dimensiona a janela como o app faria (tamanho inicial / mínimo / maximizada) e faz o layout.
    (FrameworkElement area, double cw, double ch, double escala) Montar(Window w, Rect util, double dpi, string estado, bool aumentar)
    {
        VisualTreeHelper.SetRootDpi(w, new DpiScale(dpi, dpi));
        var minW = Math.Min(w.MinWidth, util.Width);
        var minH = Math.Min(w.MinHeight, util.Height);
        double ww, wh, cw, ch;
        if (estado == "maximizada") { cw = util.Width; ch = util.Height - 30; }
        else
        {
            if (estado == "reduzida") { ww = minW; wh = minH; }
            else
            {
                var s = aumentar ? Sugerida(util) : 1.0;
                ww = Math.Min(w.Width * s, util.Width); wh = Math.Min(w.Height * s, util.Height);
            }
            cw = ww - 16; ch = wh - 39;
        }
        // Janela nunca exibida não faz layout: o conteúdo (já envolvido pelo LayoutResponsivo) vira a
        // raiz, com os mesmos recursos (dicionário compartilhado: o que o código da janela muda em
        // Resources chega ao conteúdo) e as mesmas propriedades herdadas do estilo de Window.
        var area = (FrameworkElement)w.Content;
        w.Content = null;
        area.Resources = w.Resources;
        TextElement.SetFontFamily(area, w.FontFamily);
        TextElement.SetForeground(area, w.Foreground);
        TextOptions.SetTextFormattingMode(area, TextOptions.GetTextFormattingMode(w));
        area.UseLayoutRounding = true; area.SnapsToDevicePixels = true;
        VisualTreeHelper.SetRootDpi(area, new DpiScale(dpi, dpi));
        area.Measure(new Size(cw, ch)); area.Arrange(new Rect(0, 0, cw, ch)); area.UpdateLayout();
        area.Measure(new Size(cw, ch)); area.Arrange(new Rect(0, 0, cw, ch)); area.UpdateLayout();
        var conteudo = (FrameworkElement)((Panel)area).Children[0];
        var escala = conteudo.LayoutTransform is ScaleTransform st ? st.ScaleX : 1.0;
        return (area, cw, ch, escala);
    }

    var resolucoes = new[] { (1366, 768), (1920, 1080), (2560, 1440), (3840, 2160) };
    var dpis = new[] { 1.0, 1.25, 1.5 };
    var estados = new[] { "normal", "reduzida", "maximizada" };
    var abas = new[] { "Discagem", "Contatos", "Historico", "Configuracoes", "Dashboard" };

    var contatos = new List<Contato>
    {
        new() { Nome = "Maria Aparecida de Oliveira Santos Albuquerque", Numero = "66984671226", Observacao = "Cliente frota — Sinop" },
        new() { Nome = "Posto Avenida", Numero = "6635311234", EhFavorito = true },
        new() { Nome = "Edinei", Numero = "104", EhRamalIssabel = true },
        new() { Nome = "Transportadora Rodovia Norte Ltda", Numero = "5566999990000" },
    };
    var historico = new List<HistoricoLigacaoItem>
    {
        new() { Nome = "Maria Aparecida de Oliveira Santos Albuquerque", Numero = "66984671226", Tipo = TipoHistoricoLigacao.Recebida, Duracao = "03:12", RamalAtendeu = "100", CanalEntrada = "WhatsApp Vivo" },
        new() { Nome = "", Numero = "6635311234", Tipo = TipoHistoricoLigacao.Perdida, Duracao = "00:00", CanalEntrada = "Operadora" },
        new() { Nome = "Posto Avenida", Numero = "6635311234", Tipo = TipoHistoricoLigacao.Realizada, Duracao = "12:40", OrigemSaida = "WhatsApp TIM" },
        new() { Nome = "Transportadora Rodovia Norte Ltda", Numero = "5566999990000", Tipo = TipoHistoricoLigacao.NaoAtendida, Duracao = "00:00" },
    };
    var favoritos = new List<FavoriteItem>
    {
        new() { Nome = "Sr. Valter", Numero = "66999887766" }, new() { Nome = "Izabela Cristina Ferreira", Numero = "102" },
        new() { Nome = "Jedson", Numero = "103" }, new() { Nome = "Marlley", Numero = "66984001122" },
    };

    relatorio.Add("| Resolução | Escala Win | Janela | Área cliente (DIP) | Escala Waven | Área lógica | Texto 13 px → px reais | Discagem rola? | Cortes |");
    relatorio.Add("|---|---|---|---|---|---|---|---|---|");

    foreach (var (rw, rh) in resolucoes)
    foreach (var dpi in dpis)
    {
        // área útil: tela inteira menos a barra de tarefas (48 DIP), em DIP
        var util = new Rect(0, 0, rw / dpi, rh / dpi - 48);
        foreach (var estado in estados)
        {
            var w = new DialerShellWindow(new SipService());
            Neutralizar(w);
            ((ItemsControl)w.FindName("listFavoritos")).ItemsSource = favoritos;
            ((UIElement)w.FindName("txtFavoritosVazio")).Visibility = Visibility.Collapsed;
            ((ItemsControl)w.FindName("gridContatosShell")).ItemsSource = contatos;
            ((ItemsControl)w.FindName("gridHistoricoShell")).ItemsSource = historico;
            ((TextBlock)w.FindName("txtDndNomeRamal")).Text = "Rodrigo Almeida · 100";
            ((TextBlock)w.FindName("txtStatus")).Text = "Registrado com sucesso no Issabel (ramal 100)";
            var (area, cw, ch, escala) = Montar(w, util, dpi, estado, aumentar: true);
            var tabs = (TabControl)w.FindName("MainTabs");
            var cortesTotal = 0; var rola = "";
            for (int i = 0; i < abas.Length; i++)
            {
                tabs.SelectedIndex = i; area.UpdateLayout();
                // a troca de aba anima Opacity 0→1; sem relógio de animação aqui, fixa no estado final
                if (tabs.SelectedContent is UIElement sel)
                {
                    sel.BeginAnimation(UIElement.OpacityProperty, null); sel.Opacity = 1;
                    if (sel.RenderTransform is TranslateTransform tt) { tt.BeginAnimation(TranslateTransform.YProperty, null); tt.Y = 0; }
                }
                area.UpdateLayout();
                var nome = $"{rw}x{rh}_{dpi * 100:0}_{estado}_{i}{abas[i]}.png";
                Foto(area, cw, ch, dpi, nome);
                var cortes = Cortes(area, cw, ch);
                cortesTotal += cortes.Count;
                Check($"{nome}: sem cortes", cortes.Count == 0, string.Join(" | ", cortes.Take(5)));
                if (i == 0)
                {
                    var btn = (FrameworkElement)w.FindName("btnPuxarLigacao");
                    DependencyObject p = btn; while (p != null && p is not ScrollViewer) p = VisualTreeHelper.GetParent(p);
                    rola = p is ScrollViewer sv && sv.ScrollableHeight > 0.5 ? $"sim ({sv.ScrollableHeight:F0} DIP)" : "não";
                }
            }
            var conteudo = (FrameworkElement)((Panel)area).Children[0];
            relatorio.Add($"| {rw}×{rh} | {dpi * 100:0}% | {estado} | {cw:F0}×{ch:F0} | {escala:0.00} | {cw / escala:F0}×{ch / escala:F0} | {13 * escala * dpi:F1} | {rola} | {cortesTotal} |");

        }
    }

    // Configurações e Chamada: notebook pequeno e monitor grande em cada escala
    relatorio.Add("");
    relatorio.Add("| Janela | Resolução | Escala Win | Estado | Área cliente | Escala Waven | Cortes |");
    relatorio.Add("|---|---|---|---|---|---|---|");
    foreach (var (rw, rh) in resolucoes)
    foreach (var dpi in dpis)
    {
        var util = new Rect(0, 0, rw / dpi, rh / dpi - 48);
        foreach (var estado in new[] { "normal", "reduzida" })
        {
            var sw = new SettingsWindow();
            Neutralizar(sw);
            var (a1, c1w, c1h, e1) = Montar(sw, util, dpi, estado, aumentar: true);
            var n1 = $"{rw}x{rh}_{dpi * 100:0}_{estado}_Configuracoes-janela.png";
            Foto(a1, c1w, c1h, dpi, n1);
            var k1 = Cortes(a1, c1w, c1h);
            Check($"{n1}: sem cortes", k1.Count == 0, string.Join(" | ", k1.Take(5)));
            relatorio.Add($"| Configurações | {rw}×{rh} | {dpi * 100:0}% | {estado} | {c1w:F0}×{c1h:F0} | {e1:0.00} | {k1.Count} |");

        }

        var cwin = new CallWindow("Maria Aparecida de Oliveira Santos", "Chamando...");
        Neutralizar(cwin);
        var (a2, c2w, c2h, e2) = Montar(cwin, util, dpi, "normal", aumentar: false);
        var n2 = $"{rw}x{rh}_{dpi * 100:0}_Chamada.png";
        Foto(a2, c2w, c2h, dpi, n2);
        var k2 = Cortes(a2, c2w, c2h);
        Check($"{n2}: sem cortes", k2.Count == 0, string.Join(" | ", k2.Take(5)));
        relatorio.Add($"| Chamada | {rw}×{rh} | {dpi * 100:0}% | normal | {c2w:F0}×{c2h:F0} | {e2:0.00} | {k2.Count} |");

    }
});
th.SetApartmentState(ApartmentState.STA);
th.Start();
th.Join();

File.WriteAllLines(Path.Combine(pasta, "relatorio.md"), relatorio);
foreach (var l in relatorio) Console.WriteLine(l);
Console.WriteLine($"\nTOTAL {total} verificações, {falhas} falha(s). PNGs em {pasta}");
return falhas == 0 ? 0 : 1;
