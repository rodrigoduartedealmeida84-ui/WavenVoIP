using System;
using System.Net.Sockets;

namespace WavenVoIP.Services
{
    public enum FalhaTipo { Temporaria, AcaoNecessaria }

    public static class IntegrationFailureClassifier
    {
        // Keywords that definitively indicate an auth / credential problem
        private static readonly string[] _authKeywords =
        {
            "access denied", "authentication failed", "authentication fail",
            "invalid_grant", "token revoked", "session revoked",
            "invalid credentials", "wrong password", "permission denied",
            "not authorized", "invalid user", "invalid password",
            "bad credentials", "account blocked", "login failed",
            "unauthorized access", "invalid grant", "401 "
        };

        // Keywords that definitively indicate a transient network problem
        private static readonly string[] _tempKeywords =
        {
            "timeout", "timed out", "connection refused", "connection reset",
            "unable to connect", "network path", "host unreachable", "no route",
            "name or service not known", "name resolution", "dns",
            "502 ", "503 ", "504 ", "service unavailable", "bad gateway",
            "gateway timeout", "broken pipe", "end of stream",
            "remotely closed", "connection closed", "socket error",
            "network error", "i/o error", "eof"
        };

        public static FalhaTipo Classificar(Exception ex)
        {
            // Fast-path: known permanent exception types
            if (ex is UnauthorizedAccessException)                     return FalhaTipo.AcaoNecessaria;

            // Fast-path: known transient exception types
            if (ex is OperationCanceledException
                or SocketException
                or TimeoutException
                or System.IO.IOException)                               return FalhaTipo.Temporaria;
            if (ex.InnerException is SocketException
                or TimeoutException
                or System.IO.IOException)                               return FalhaTipo.Temporaria;

            var msg = BuildMsg(ex);

            // Auth keywords beat temp keywords
            if (ContainsAny(msg, _authKeywords)) return FalhaTipo.AcaoNecessaria;
            if (ContainsAny(msg, _tempKeywords)) return FalhaTipo.Temporaria;

            // Default: treat as temporary — never bother user for unrecognised errors
            return FalhaTipo.Temporaria;
        }

        private static string BuildMsg(Exception ex)
        {
            var sb = new System.Text.StringBuilder();
            var e = ex;
            while (e != null)
            {
                sb.Append(' ').Append(e.Message).Append(' ').Append(e.GetType().Name);
                e = e.InnerException;
            }
            return sb.ToString().ToLowerInvariant();
        }

        private static bool ContainsAny(string haystack, string[] needles)
        {
            foreach (var n in needles)
                if (haystack.Contains(n, StringComparison.Ordinal)) return true;
            return false;
        }
    }
}
