using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using WavenVoIP.Models;

namespace WavenVoIP.Services
{
    public static class FavoritesStorageService
    {
        private static readonly string _path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WavenVoIP", "favorites.json");

        public static List<FavoriteItem> Carregar()
        {
            try
            {
                if (!File.Exists(_path)) return new List<FavoriteItem>();
                var json = File.ReadAllText(_path);
                var list = JsonSerializer.Deserialize<List<FavoriteItem>>(json) ?? new List<FavoriteItem>();
                return list.OrderBy(f => f.Ordem).ThenBy(f => f.Nome).ToList();
            }
            catch
            {
                return new List<FavoriteItem>();
            }
        }

        public static void Salvar(List<FavoriteItem> favoritos)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var json = JsonSerializer.Serialize(favoritos, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_path, json);
            }
            catch { }
        }

        public static bool Adicionar(FavoriteItem item)
        {
            var lista = Carregar();
            var numero = SomenteDigitos(item.Numero);
            if (lista.Any(f => SomenteDigitos(f.Numero) == numero)) return false;
            item.Ordem = lista.Count > 0 ? lista.Max(f => f.Ordem) + 1 : 0;
            lista.Add(item);
            Salvar(lista);
            Log($"FAVORITE_ADDED | nome={item.Nome} numero={item.Numero}");
            return true;
        }

        public static bool Remover(string numero)
        {
            var lista = Carregar();
            var n = SomenteDigitos(numero);
            var antes = lista.Count;
            lista.RemoveAll(f => SomenteDigitos(f.Numero) == n);
            if (lista.Count == antes) return false;
            Salvar(lista);
            Log($"FAVORITE_REMOVED | numero={numero}");
            return true;
        }

        public static bool EhFavorito(string numero)
        {
            try
            {
                var lista = Carregar();
                var n = SomenteDigitos(numero);
                return lista.Any(f => SomenteDigitos(f.Numero) == n);
            }
            catch { return false; }
        }

        private static string SomenteDigitos(string? valor)
        {
            if (string.IsNullOrWhiteSpace(valor)) return string.Empty;
            return new string(valor.Where(char.IsDigit).ToArray());
        }

        private static void Log(string msg)
        {
            try { LogHelper.Info(msg); }
            catch { }
        }
    }
}
