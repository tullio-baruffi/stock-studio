using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using FluentFTP.Exceptions;
using Microsoft.Extensions.Logging;
using Renci.SshNet.Common;
using WinSCP;

namespace MJ.Classifier.Clients
{
    /// <summary>
    /// Ripete un invio quando la destinazione non ha risposto, e solo allora.
    ///
    /// ## Il difetto che risolve
    /// Ogni file partiva con un tentativo solo. Quando Freepik non rispondeva in tempo -- SSH.NET
    /// lo chiama "Session operation has timed out" -- il file restava fra quelli da inviare mentre
    /// gli altri della stessa immagine partivano, e veniva rimandato giorni dopo a mano. Misurato
    /// sui log: a settembre 11 EPS e 5 JPG rifiutati così, l'11/10 altri due. E un'immagine spedita
    /// in giorni diversi finisce divisa nei Pubblicati: è il caso di AB34, col suo SVG partito il
    /// 27/09 e JPG ed EPS il 11/10.
    ///
    /// ## Cosa si ripete e cosa no
    /// Si ripete quel che dipende dalla rete o dal carico del server: timeout, connessione caduta,
    /// codici FTP 4xx, che il protocollo dichiara temporanei. **Non** si ripete un errore che il
    /// tempo non cura -- password sbagliata, permesso negato, cartella inesistente -- perché
    /// insistere non serve, e sulle credenziali è dannoso: molti server bloccano l'account dopo
    /// qualche accesso fallito, e un tentativo in più trasformerebbe un errore in un blocco.
    /// Un errore che non si riconosce non si ripete: meglio un invio fallito in chiaro che lo
    /// stesso errore misterioso tre volte.
    ///
    /// ## Quanto si aspetta
    /// Tre tentativi, con dieci e venti secondi fra l'uno e l'altro, e mai oltre <see cref="Budget"/>
    /// complessivi. Il tetto esiste perché la Function ha un tempo massimo: se lo superasse verrebbe
    /// interrotta, e la coda rimanderebbe l'intero invio anche alle destinazioni che l'avevano già
    /// ricevuto.
    /// </summary>
    public static class Ritenta
    {
        /// <summary>Le attese fra un tentativo e il successivo: tanti tentativi quante attese, più uno.</summary>
        public static readonly TimeSpan[] PausePredefinite = { TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20) };

        /// <summary>Oltre questo tempo dal primo tentativo non se ne comincia un altro.</summary>
        public static readonly TimeSpan Budget = TimeSpan.FromSeconds(150);

        /// <summary>
        /// Esegue l'invio, ripetendolo dopo un errore passeggero. Restituisce quanti tentativi sono
        /// serviti; se l'ultimo fallisce, rilancia il suo errore così com'è.
        /// </summary>
        public static int Esegui(ILogger log, string destinazione, string fileName, Action invio,
                                 TimeSpan[] pause = null, Action<TimeSpan> attendi = null)
        {
            pause ??= PausePredefinite;
            attendi ??= Thread.Sleep;
            var orologio = Stopwatch.StartNew();

            for (var tentativo = 1; ; tentativo++)
            {
                try
                {
                    invio();
                    if (tentativo > 1)
                        log?.LogInformation($"{fileName} verso {destinazione}: riuscito al tentativo {tentativo}");
                    return tentativo;
                }
                catch (Exception e) when (tentativo <= pause.Length
                                          && Passeggero(e)
                                          && orologio.Elapsed + pause[tentativo - 1] < Budget)
                {
                    log?.LogWarning($"{fileName} verso {destinazione}: tentativo {tentativo} di {pause.Length + 1} " +
                                    $"non riuscito ({Motivo(e)}), riprovo fra {pause[tentativo - 1].TotalSeconds:0} s");
                    attendi(pause[tentativo - 1]);
                }
            }
        }

        /// <summary>
        /// True quando l'errore dipende dal momento e un nuovo tentativo può riuscire.
        ///
        /// Si guarda tutta la catena delle eccezioni interne, perché le librerie di trasferimento
        /// incartano spesso il motivo vero in un involucro generico. Le cause permanenti si
        /// cercano **prima**: un permesso negato arrivato dentro un errore di rete resta un
        /// permesso negato.
        /// </summary>
        public static bool Passeggero(Exception e)
        {
            for (var x = e; x != null; x = x.InnerException)
            {
                switch (x)
                {
                    case SshAuthenticationException:
                    case SftpPermissionDeniedException:
                    case SftpPathNotFoundException:
                    case FtpAuthenticationException:
                    case UnauthorizedAccessException:
                    case FileNotFoundException:
                    case DirectoryNotFoundException:
                        return false;
                    case SessionRemoteException w when Credenziali.IsMatch(w.Message ?? ""):
                        return false;
                    case FtpCommandException f when f.CompletionCode != null && f.CompletionCode.StartsWith("5"):
                        return false;
                }
            }

            for (var x = e; x != null; x = x.InnerException)
            {
                switch (x)
                {
                    case SshOperationTimeoutException:
                    case SshConnectionException:
                    case ProxyException:
                    case FtpMissingSocketException:
                    case TimeoutException:
                    case SocketException:
                    case IOException:
                        return true;
                    case FtpCommandException f when f.CompletionCode != null && f.CompletionCode.StartsWith("4"):
                        return true;
                    // WinSCP riferisce ogni guasto remoto con lo stesso tipo: chi lo distingue è il
                    // testo, e quelli sulle credenziali sono già stati esclusi sopra.
                    case SessionRemoteException:
                        return true;
                }
            }
            return false;
        }

        /// <summary>I messaggi di WinSCP che parlano di accesso negato, non di rete.</summary>
        private static readonly Regex Credenziali =
            new Regex("auth|denied|password|permission|login incorrect", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static string Motivo(Exception e) => e.InnerException?.Message ?? e.Message;
    }
}
