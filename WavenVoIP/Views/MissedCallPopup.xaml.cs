using System;
using System.Windows;
using WavenVoIP.Services;

namespace WavenVoIP.Views
{
    public partial class MissedCallPopup : Window
    {
        public string CallerNumber { get; }
        public event Action<string>? RetornarSolicitado;
        public event Action? VerHistoricoSolicitado;

        public MissedCallPopup(string callerNumber, string callerName)
        {
            InitializeComponent();
            CallerNumber = callerNumber;
            var numDisplay = PhoneNumberNormalizer.NormalizeForDisplay(callerNumber);
            if (string.IsNullOrWhiteSpace(numDisplay)) numDisplay = callerNumber;
            var nomeDisplay = string.IsNullOrWhiteSpace(callerName) ||
                              string.Equals(callerName, callerNumber, StringComparison.OrdinalIgnoreCase)
                              ? numDisplay : callerName;
            txtCallerName.Text = nomeDisplay;
            // Não mostrar número se já está no nome (evita duplicidade quando não há contato salvo)
            var mesmoTexto = string.Equals(nomeDisplay, numDisplay, StringComparison.OrdinalIgnoreCase);
            txtCallerNumber.Text       = mesmoTexto ? string.Empty : numDisplay;
            txtCallerNumber.Visibility = mesmoTexto ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
            PosicionarBottomRight();
        }

        private void PosicionarBottomRight()
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - Width - 20;
            Top  = area.Bottom - Height - 20;
        }

        private void BtnRetornar_Click(object sender, RoutedEventArgs e)
        {
            RetornarSolicitado?.Invoke(CallerNumber);
            Close();
        }

        private void BtnVerHistorico_Click(object sender, RoutedEventArgs e)
        {
            VerHistoricoSolicitado?.Invoke();
            Close();
        }

        private void BtnFechar_Click(object sender, RoutedEventArgs e) => Close();
    }
}
