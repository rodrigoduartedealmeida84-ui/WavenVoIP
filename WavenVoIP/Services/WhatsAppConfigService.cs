using System;
using System.IO;
using System.Text.Json;
using WavenVoIP.Models;

namespace WavenVoIP.Services
{
    public static class WhatsAppConfigService
    {
        private static readonly string _path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WavenVoIP",
            "whatsapp_config.json");

        public static WhatsAppConfig Carregar()
        {
            WhatsAppConfig cfg;
            try
            {
                cfg = File.Exists(_path)
                    ? (JsonSerializer.Deserialize<WhatsAppConfig>(File.ReadAllText(_path)) ?? new WhatsAppConfig())
                    : new WhatsAppConfig();
            }
            catch { cfg = new WhatsAppConfig(); }

            if (string.IsNullOrWhiteSpace(cfg.ApiUrl))         cfg.ApiUrl         = "https://api.wavenchat.com.br/v2/api/external/SEU_API_ID";
            if (string.IsNullOrWhiteSpace(cfg.MensagemTeste))  cfg.MensagemTeste  = "Teste de envio pelo Waven VoIP.";
            return cfg;
        }

        public static void Salvar(WhatsAppConfig config)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_path, json);
        }
    }
}
