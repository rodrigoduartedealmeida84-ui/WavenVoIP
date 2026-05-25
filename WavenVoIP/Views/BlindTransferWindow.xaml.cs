using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;

namespace WavenVoIP.Views
{
    public partial class BlindTransferWindow : Window
    {
        public bool Confirmado { get; private set; }

        private static readonly string[] _avatarColors =
            { "#6366F1", "#8B5CF6", "#EC4899", "#10B981", "#3B82F6", "#F59E0B" };

        public BlindTransferWindow(string destino)
        {
            InitializeComponent();

            var (nome, numero) = ParsearDestino(destino);
            var exibicao = string.IsNullOrEmpty(nome) ? numero : nome;
            txtNomeContato.Text = string.IsNullOrEmpty(exibicao) ? "Destino" : exibicao;
            txtNumeroContato.Text = string.IsNullOrEmpty(nome) ? string.Empty : numero;
            txtAvatar.Text = ComputarIniciais(exibicao);
            var cor = _avatarColors[Math.Abs(exibicao.GetHashCode()) % _avatarColors.Length];
            borderAvatar.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(cor));
        }

        private static (string nome, string numero) ParsearDestino(string destino)
        {
            if (string.IsNullOrWhiteSpace(destino)) return ("Destino", string.Empty);
            var m = Regex.Match(destino.Trim(), @"^(.+?)\s*\((\d[\d\s\-+]*)\)\s*$");
            if (m.Success) return (m.Groups[1].Value.Trim(), m.Groups[2].Value.Trim());
            if (destino.Trim().All(c => char.IsDigit(c) || c == '+' || c == ' ' || c == '-'))
                return (string.Empty, destino.Trim());
            return (destino.Trim(), string.Empty);
        }

        private static string ComputarIniciais(string texto)
        {
            if (string.IsNullOrWhiteSpace(texto)) return "?";
            var palavras = texto.Trim()
                .Split(new[] { ' ', '-', '(', ')', '/' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(p => p.Length > 0 && char.IsLetter(p[0]))
                .ToList();
            if (palavras.Count == 0) return "#";
            if (palavras.Count == 1) return char.ToUpper(palavras[0][0]).ToString();
            return $"{char.ToUpper(palavras[0][0])}{char.ToUpper(palavras[^1][0])}";
        }

        private void BtnConfirmar_Click(object sender, RoutedEventArgs e)
        {
            Confirmado = true;
            try { Close(); } catch { }
        }

        private void BtnCancelar_Click(object sender, RoutedEventArgs e)
        {
            Confirmado = false;
            try { Close(); } catch { }
        }
    }
}
