using System;
using System.Linq;
using NAudio.CoreAudioApi;

namespace WavenVoIP.Services
{
    // v2.5.0 — ajuste individual por microfone, na origem.
    //
    // Medição em gravações reais do ramal 100 (12–13/10/2026): com o microfone do notebook em 47%
    // no Windows a fala chegava ao aplicativo entre −40 e −47 dBFS. Corrigir isso só com ganho
    // digital (MicAutoGain) amplifica junto o ruído e o retorno do alto-falante. O certo é captar
    // mais alto: ao fim de cada chamada com fala suficiente, o nível de captura DESSE microfone no
    // Windows sobe um degrau se a fala chegou baixa demais, ou desce se chegou alta/saturando.
    // É gradual (um degrau por chamada), por dispositivo e fica registrado no log.
    public static class MicNivelWindows
    {
        public const double FalaBaixaDb = -34.0;   // abaixo disso o microfone está captando pouco
        public const double FalaAltaDb  = -14.0;   // acima disso há risco de saturar na captura
        public const float  DegrauSubida = 0.15f, DegrauDescida = 0.10f, Minimo = 0.30f;

        // Decisão pura (testável): novo nível 0..1, ou null para não mexer.
        public static float? NovoNivel(float atual, double falaEntradaDb, long blocosFala, long blocosLimitados)
        {
            if (blocosFala < 100 || double.IsNaN(falaEntradaDb)) return null;      // menos de 2 s de fala: não decide
            if (falaEntradaDb > FalaAltaDb || blocosLimitados > blocosFala / 20)
                return atual > Minimo ? Math.Max(Minimo, atual - DegrauDescida) : null;
            if (falaEntradaDb < FalaBaixaDb && atual < 0.995f)
                return Math.Min(1.0f, atual + DegrauSubida);
            return null;
        }

        private static MMDevice? Localizar(MMDeviceEnumerator en, string nomeConfigurado)
        {
            MMDevice? d = null;
            if (!string.IsNullOrWhiteSpace(nomeConfigurado))
                d = en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                      .FirstOrDefault(x => x.FriendlyName.Equals(nomeConfigurado, StringComparison.OrdinalIgnoreCase));
            // microfone configurado ausente (ex.: headset desconectado): a chamada usa o padrão do Windows
            return d ?? en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        }

        // Nome do microfone que a chamada realmente usa (para guardar o ganho por dispositivo).
        public static string NomeCapturaAtual(string nomeConfigurado)
        {
            try
            {
                using var en = new MMDeviceEnumerator();
                return Localizar(en, nomeConfigurado)?.FriendlyName ?? string.Empty;
            }
            catch { return string.Empty; }
        }

        public static string AjustarAposChamada(string nomeConfigurado, double falaEntradaDb, long blocosFala, long blocosLimitados)
        {
            try
            {
                using var en = new MMDeviceEnumerator();
                var d = Localizar(en, nomeConfigurado);
                if (d == null) return "sem_dispositivo";
                var vol = d.AudioEndpointVolume;
                var atual = vol.MasterVolumeLevelScalar;
                var novo = NovoNivel(atual, falaEntradaDb, blocosFala, blocosLimitados);
                if (novo == null) return FormattableString.Invariant($"mantido microfone=\"{d.FriendlyName}\" nivel={atual * 100:F0}%");
                vol.MasterVolumeLevelScalar = novo.Value;
                return FormattableString.Invariant($"ajustado microfone=\"{d.FriendlyName}\" de={atual * 100:F0}% para={novo.Value * 100:F0}% fala_entrada_dbfs={falaEntradaDb:F1}");
            }
            catch (Exception ex) { return "erro " + ex.GetType().Name; }
        }
    }
}
