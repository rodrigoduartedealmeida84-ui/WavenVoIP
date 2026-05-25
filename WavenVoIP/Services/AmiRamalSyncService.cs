using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using WavenVoIP.Models;

namespace WavenVoIP.Services
{
    public static class AmiRamalSyncService
    {
        public static async Task<List<Contato>> BuscarRamaisAsync(SipConfig config)
        {
            var resultado = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (config == null || !config.AmiAtivo)
                return new List<Contato>();

            var host = string.IsNullOrWhiteSpace(config.AmiHost) ? config.ServerIp : config.AmiHost.Trim();
            var porta = config.AmiPorta <= 0 ? 5038 : config.AmiPorta;
            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(config.AmiUsuario) || string.IsNullOrWhiteSpace(config.AmiSenha))
                return new List<Contato>();

            using var client = new TcpClient();
            var connectTask = client.ConnectAsync(host, porta);
            var completed = await Task.WhenAny(connectTask, Task.Delay(5000));
            if (completed != connectTask || !client.Connected)
                throw new TimeoutException("Não foi possível conectar ao AMI do Issabel.");

            client.ReceiveTimeout = 5000;
            client.SendTimeout = 5000;

            using var stream = client.GetStream();
            await LerDisponivelAsync(stream, 700); // banner

            await EnviarAsync(stream,
                "Action: Login\r\n" +
                $"Username: {config.AmiUsuario}\r\n" +
                $"Secret: {config.AmiSenha}\r\n" +
                "Events: off\r\n" +
                "ActionID: WAVEN_LOGIN\r\n\r\n");

            var login = await LerAteAsync(stream, "ActionID: WAVEN_LOGIN", 5000);
            if (login.IndexOf("Success", StringComparison.OrdinalIgnoreCase) < 0)
                throw new InvalidOperationException("AMI recusou login. Confira usuário, senha e permissões.");

            // FreePBX/Issabel normalmente guarda nomes em /AMPUSER/<ramal>/cidname.
            await EnviarAsync(stream,
                "Action: Command\r\n" +
                "Command: database show AMPUSER\r\n" +
                "ActionID: WAVEN_AMPUSER\r\n\r\n");
            var ampuser = await LerAteAsync(stream, "--END COMMAND--", 7000);
            ParseDatabaseAmpuser(ampuser, resultado);

            // Chan_SIP: lista ramais mesmo quando o cidname não veio no database.
            await EnviarAsync(stream,
                "Action: SIPpeers\r\n" +
                "ActionID: WAVEN_SIPPEERS\r\n\r\n");
            var peers = await LerAteAsync(stream, "PeerlistComplete", 7000);
            ParseSipPeers(peers, resultado);

            // PJSIP: compatibilidade caso o Issabel esteja usando PJSIP.
            await EnviarAsync(stream,
                "Action: PJSIPShowEndpoints\r\n" +
                "ActionID: WAVEN_PJSIP\r\n\r\n");
            var pjsip = await LerAteAsync(stream, "EndpointListComplete", 7000);
            ParsePjsipEndpoints(pjsip, resultado);

            await EnviarAsync(stream, "Action: Logoff\r\nActionID: WAVEN_LOGOFF\r\n\r\n");

            return resultado
                .Where(kv => EhRamalValido(kv.Key))
                .Select(kv => new Contato
                {
                    Numero = kv.Key,
                    Nome = string.IsNullOrWhiteSpace(kv.Value) ? kv.Key : kv.Value,
                    Observacao = "Ramal Issabel (AMI)",
                    EhRamalIssabel = true,
                    AtualizadoEm = DateTime.Now
                })
                .OrderBy(c => c.Numero)
                .ToList();
        }

        private static void ParseDatabaseAmpuser(string texto, Dictionary<string, string> ramais)
        {
            foreach (Match m in Regex.Matches(texto ?? string.Empty, @"/AMPUSER/(?<ramal>\d{2,6})/cidname\s*:\s*(?<nome>.+)"))
            {
                var ramal = m.Groups["ramal"].Value.Trim();
                var nome = LimparNome(m.Groups["nome"].Value);
                if (EhRamalValido(ramal)) ramais[ramal] = string.IsNullOrWhiteSpace(nome) ? ramal : nome;
            }

            foreach (Match m in Regex.Matches(texto ?? string.Empty, @"/AMPUSER/(?<ramal>\d{2,6})/device\s*:\s*(?<device>\d{2,6})"))
            {
                var ramal = m.Groups["ramal"].Value.Trim();
                if (EhRamalValido(ramal) && !ramais.ContainsKey(ramal)) ramais[ramal] = ramal;
            }
        }

        private static void ParseSipPeers(string texto, Dictionary<string, string> ramais)
        {
            foreach (var bloco in SepararEventos(texto))
            {
                if (ObterCampo(bloco, "Event")?.IndexOf("PeerEntry", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                var ramal = ObterCampo(bloco, "ObjectName") ?? ObterCampo(bloco, "Peer") ?? string.Empty;
                ramal = SomenteDigitos(ramal);
                if (!EhRamalValido(ramal)) continue;

                var nome = LimparNome(ObterCampo(bloco, "Description") ?? ObterCampo(bloco, "Callerid") ?? string.Empty);
                if (!ramais.ContainsKey(ramal)) ramais[ramal] = string.IsNullOrWhiteSpace(nome) ? ramal : nome;
            }
        }

        private static void ParsePjsipEndpoints(string texto, Dictionary<string, string> ramais)
        {
            foreach (var bloco in SepararEventos(texto))
            {
                if (ObterCampo(bloco, "Event")?.IndexOf("EndpointList", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                var ramal = SomenteDigitos(ObterCampo(bloco, "ObjectName") ?? string.Empty);
                if (EhRamalValido(ramal) && !ramais.ContainsKey(ramal)) ramais[ramal] = ramal;
            }
        }

        private static IEnumerable<string> SepararEventos(string texto)
        {
            return (texto ?? string.Empty).Split(new[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static string? ObterCampo(string bloco, string campo)
        {
            foreach (var linha in (bloco ?? string.Empty).Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                var idx = linha.IndexOf(':');
                if (idx <= 0) continue;
                var nome = linha.Substring(0, idx).Trim();
                if (string.Equals(nome, campo, StringComparison.OrdinalIgnoreCase))
                    return linha.Substring(idx + 1).Trim();
            }
            return null;
        }

        private static string LimparNome(string nome)
        {
            nome = (nome ?? string.Empty).Trim().Trim('"');
            var match = Regex.Match(nome, "\\\"(?<nome>[^\\\"]+)\\\"");
            if (match.Success) nome = match.Groups["nome"].Value;
            nome = Regex.Replace(nome, @"<[^>]+>", string.Empty).Trim();
            return nome;
        }

        private static string SomenteDigitos(string valor) => new string((valor ?? string.Empty).Where(char.IsDigit).ToArray());

        private static bool EhRamalValido(string ramal)
        {
            if (string.IsNullOrWhiteSpace(ramal)) return false;
            return ramal.All(char.IsDigit) && ramal.Length >= 2 && ramal.Length <= 6;
        }

        private static async Task EnviarAsync(NetworkStream stream, string texto)
        {
            var bytes = Encoding.ASCII.GetBytes(texto);
            await stream.WriteAsync(bytes, 0, bytes.Length);
            await stream.FlushAsync();
        }

        private static async Task<string> LerAteAsync(NetworkStream stream, string marcador, int timeoutMs)
        {
            var sb = new StringBuilder();
            var buffer = new byte[8192];
            var limite = DateTime.Now.AddMilliseconds(timeoutMs);

            while (DateTime.Now < limite)
            {
                while (stream.DataAvailable)
                {
                    var lidos = await stream.ReadAsync(buffer, 0, buffer.Length);
                    if (lidos <= 0) break;
                    sb.Append(Encoding.ASCII.GetString(buffer, 0, lidos));
                    if (sb.ToString().IndexOf(marcador, StringComparison.OrdinalIgnoreCase) >= 0)
                        return sb.ToString();
                }
                await Task.Delay(80);
            }

            return sb.ToString();
        }

        private static async Task<string> LerDisponivelAsync(NetworkStream stream, int timeoutMs)
        {
            var sb = new StringBuilder();
            var buffer = new byte[4096];
            var limite = DateTime.Now.AddMilliseconds(timeoutMs);
            while (DateTime.Now < limite)
            {
                while (stream.DataAvailable)
                {
                    var lidos = await stream.ReadAsync(buffer, 0, buffer.Length);
                    if (lidos <= 0) break;
                    sb.Append(Encoding.ASCII.GetString(buffer, 0, lidos));
                }
                await Task.Delay(50);
            }
            return sb.ToString();
        }
    }
}
