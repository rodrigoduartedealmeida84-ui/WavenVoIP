using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WavenVoIP.Services
{
    public class ContactTombstone
    {
        [JsonPropertyName("deletedContactSourceId")] public string? DeletedContactSourceId { get; set; }
        [JsonPropertyName("googleContactId")]        public string? GoogleContactId        { get; set; }
        [JsonPropertyName("numeroNormalizado")]      public string? NumeroNormalizado      { get; set; }
        [JsonPropertyName("deletedAt")]              public DateTime DeletedAt             { get; set; }
        [JsonPropertyName("deletedBy")]              public string? DeletedBy              { get; set; }
        [JsonPropertyName("sourceType")]             public string  SourceType             { get; set; } = string.Empty;
    }

    public static class ContactTombstoneService
    {
        private static readonly string _path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WavenVoIP", "contact-tombstones.json");

        public static void Adicionar(ContactTombstone tombstone)
        {
            var lista = Carregar();
            lista.Add(tombstone);
            Salvar(lista);
        }

        public static void Remover(string? googleContactId, string? numeroNormalizado)
        {
            var lista = Carregar();
            lista.RemoveAll(t =>
                (!string.IsNullOrWhiteSpace(googleContactId) &&
                 string.Equals(t.GoogleContactId, googleContactId, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(numeroNormalizado) &&
                 string.Equals(t.NumeroNormalizado, numeroNormalizado, StringComparison.OrdinalIgnoreCase)));
            Salvar(lista);
        }

        public static bool EstaSuprimido(string? googleContactId, string? numeroNormalizado)
        {
            var lista = Carregar();
            return lista.Any(t =>
                (!string.IsNullOrWhiteSpace(googleContactId) &&
                 !string.IsNullOrWhiteSpace(t.GoogleContactId) &&
                 string.Equals(t.GoogleContactId, googleContactId, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(numeroNormalizado) &&
                 !string.IsNullOrWhiteSpace(t.NumeroNormalizado) &&
                 string.Equals(t.NumeroNormalizado, numeroNormalizado, StringComparison.OrdinalIgnoreCase)));
        }

        public static bool EstaSuprimidoPorWavenApiId(string wavenApiId)
        {
            if (string.IsNullOrWhiteSpace(wavenApiId)) return false;
            var lista = Carregar();
            return lista.Any(t =>
                !string.IsNullOrWhiteSpace(t.DeletedContactSourceId) &&
                string.Equals(t.DeletedContactSourceId, wavenApiId, StringComparison.OrdinalIgnoreCase));
        }

        public static List<ContactTombstone> Carregar()
        {
            try
            {
                if (!File.Exists(_path)) return new List<ContactTombstone>();
                return JsonSerializer.Deserialize<List<ContactTombstone>>(File.ReadAllText(_path))
                       ?? new List<ContactTombstone>();
            }
            catch { return new List<ContactTombstone>(); }
        }

        private static void Salvar(List<ContactTombstone> lista)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, JsonSerializer.Serialize(lista,
                    new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
    }
}
