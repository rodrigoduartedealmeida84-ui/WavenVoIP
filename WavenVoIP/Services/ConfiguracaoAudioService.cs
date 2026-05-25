using System.IO;
using System.Text.Json;
using WavenVoIP.Models;

namespace WavenVoIP.Services
{
    public static class ConfiguracaoAudioService
    {
        private static readonly string _path = Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "WavenVoIP",
            "audio-config.json");

        public static ConfiguracaoAudio Carregar()
        {
            ConfiguracaoAudio cfg;
            try
            {
                cfg = File.Exists(_path)
                    ? (JsonSerializer.Deserialize<ConfiguracaoAudio>(File.ReadAllText(_path)) ?? new ConfiguracaoAudio())
                    : new ConfiguracaoAudio();
            }
            catch { cfg = new ConfiguracaoAudio(); }

            // Repair fields that may be empty from a previously zeroed save
            if (string.IsNullOrWhiteSpace(cfg.Microfone))   cfg.Microfone   = "Padrão do sistema";
            if (string.IsNullOrWhiteSpace(cfg.AltoFalante)) cfg.AltoFalante = "Padrão do sistema";
            if (string.IsNullOrWhiteSpace(cfg.Toque))       cfg.Toque       = "Assets\\toque_padrao.mp3";
            return cfg;
        }

        public static void Salvar(ConfiguracaoAudio configuracao)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var json = JsonSerializer.Serialize(configuracao, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_path, json);
        }
    }
}
