using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using WavenVoIP.Models;

namespace WavenVoIP.Services
{
    public static class WhatsAppLogService
    {
        private static readonly string _path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WavenVoIP",
            "whatsapp_envios.json");

        public static List<WhatsAppEnvioLog> Carregar()
        {
            try
            {
                if (!File.Exists(_path)) return new List<WhatsAppEnvioLog>();
                var json = File.ReadAllText(_path);
                return JsonSerializer.Deserialize<List<WhatsAppEnvioLog>>(json) ?? new List<WhatsAppEnvioLog>();
            }
            catch
            {
                return new List<WhatsAppEnvioLog>();
            }
        }

        public static bool JaExiste(string externalKey)
        {
            if (string.IsNullOrWhiteSpace(externalKey)) return false;
            return Carregar().Any(l => string.Equals(l.ExternalKey, externalKey, StringComparison.OrdinalIgnoreCase));
        }

        public static bool EnviadoRecentementeParaNumero(string numero, int minutos = 5)
        {
            if (string.IsNullOrWhiteSpace(numero)) return false;
            var limite = DateTime.Now.AddMinutes(-minutos);
            return Carregar().Any(l =>
                string.Equals(l.Numero, numero, StringComparison.OrdinalIgnoreCase) &&
                l.DataHora >= limite &&
                l.HttpStatusCode >= 200 && l.HttpStatusCode < 300);
        }

        public static void Registrar(WhatsAppEnvioLog log)
        {
            var logs = Carregar();
            logs.Insert(0, log);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var json = JsonSerializer.Serialize(logs, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_path, json);
        }
    }
}
