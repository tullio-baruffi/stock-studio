using System;
using System.Collections.Generic;

namespace StockStudio.Shared.Vettoriale
{
    /// <summary>Un punto del piano, in pixel dell'immagine.</summary>
    public struct Punto
    {
        public double X;
        public double Y;
        public Punto(double x, double y) { X = x; Y = y; }
    }

    /// <summary>
    /// I confini fra le zone di colore, estratti **una volta sola** e condivisi.
    ///
    /// ## Il difetto che risolve
    /// Tracciare una maschera per colore, come fa potrace, significa che il confine fra due tinte
    /// viene disegnato **due volte**: una da ciascuna delle due. I due tracciati nascono da bitmap
    /// diverse e non coincidono mai, e ne seguono i due difetti che si vedono ingrandendo:
    ///
    /// 1. **Le forme si sovrappongono.** Fra le due curve resta una striscia coperta da entrambe --
    ///    o, senza un margine di sicurezza, una fessura di fondo. Nel file si vedono coppie di
    ///    linee parallele dove dovrebbe essercene una.
    /// 2. **Le linee sono imprecise.** Ogni passata ricostruisce il confine per conto suo dalla
    ///    scalinata di pixel, senza sapere che di la' c'e' una campitura che sta facendo la stessa
    ///    cosa: due approssimazioni diverse dello stesso bordo.
    ///
    /// ## Come funziona
    /// Si lavora sul **reticolo delle fessure**: i vertici stanno agli angoli dei pixel, e c'e' uno
    /// spigolo dove due pixel vicini appartengono a tinte diverse. E' la rete dei confini, e ogni
    /// confine ci compare una volta sola.
    ///
    /// La rete si spezza in **archi**: catene di spigoli fra un nodo e l'altro, dove un nodo e' un
    /// punto in cui si incontrano tre o piu' zone. Ogni arco viene lisciato e ridotto a curve una
    /// volta sola, e le due tinte che divide **riusano la stessa geometria**, una in un verso e una
    /// nell'altro. Non e' un accorgimento per far combaciare le forme: e' la stessa curva.
    ///
    /// Poi ogni zona si ricompone percorrendo i propri archi. Ogni spigolo del reticolo, in ciascuno
    /// dei due versi, appartiene a **una sola** zona -- quella che gli sta a destra -- quindi le
    /// tinte tassellano il disegno: niente sovrapposizioni e niente fessure, per costruzione e non
    /// per taratura.
    /// </summary>
    public static class Contorni
    {
        /// <summary>Un tratto di confine fra due nodi, gia' lisciato e ridotto a curve.</summary>
        public class Arco
        {
            /// <summary>Da dove parte.</summary>
            public Punto Inizio;
            /// <summary>Dove arriva. Su un arco chiuso coincide con l'inizio.</summary>
            public Punto Fine;
            /// <summary>
            /// Le cubiche in fila, tre punti l'una: i due punti di controllo e l'arrivo. Percorrere
            /// l'arco al contrario vuol dire leggerle a rovescio scambiando i due controlli.
            /// </summary>
            public List<Punto[]> Cubiche = new List<Punto[]>();
            /// <summary>Vero quando l'arco e' un anello chiuso che non tocca nessun nodo.</summary>
            public bool Chiuso;
        }

        /// <summary>Un arco percorso in un verso o nell'altro.</summary>
        public struct Passo
        {
            public int Arco;
            public bool Inverso;
            public Passo(int arco, bool inverso) { Arco = arco; Inverso = inverso; }
        }

        /// <summary>Il contorno chiuso di una zona, o di uno dei suoi buchi.</summary>
        public class Anello
        {
            public List<Passo> Passi = new List<Passo>();
        }

        public class Esito
        {
            public List<Arco> Archi = new List<Arco>();
            /// <summary>Gli anelli di ogni tinta, nell'ordine della tavolozza.</summary>
            public List<Anello>[] Zone = new List<Anello>[0];
        }

        // Direzioni sul reticolo, in senso orario sullo schermo: destra, giu', sinistra, su.
        private static readonly int[] Dx = { 1, 0, -1, 0 };
        private static readonly int[] Dy = { 0, 1, 0, -1 };

        /// <summary>
        /// Estrae i confini di tutte le tinte.
        /// </summary>
        /// <param name="indici">A quale tinta appartiene ogni pixel.</param>
        /// <param name="opaco">Quali pixel sono disegno; null o vuoto vuol dire tutti.</param>
        /// <param name="quante">Quante tinte ha la tavolozza.</param>
        /// <param name="tolleranza">
        /// Di quanti pixel la curva puo' scostarsi dal confine misurato: vedi
        /// <see cref="AdattaCubiche"/>.
        /// </param>
        public static Esito Estrai(byte[] indici, bool[]? opaco, int larghezza, int altezza,
                                   int quante, double tolleranza = 0.6)
        {
            if (indici == null) throw new ArgumentNullException("indici");
            if (quante < 0) quante = 0;
            var zone = new List<Anello>[quante];
            for (var i = 0; i < quante; i++) zone[i] = new List<Anello>();
            if (larghezza < 1 || altezza < 1 || quante < 1)
                return new Esito { Zone = zone };

            var r = new Reticolo(indici, opaco, larghezza, altezza);

            // ---- Gli archi ------------------------------------------------------------------
            // Prima quelli che partono da un nodo, cosi' ogni catena viene presa per intero; poi
            // quel che resta, che sono anelli chiusi senza nodi -- una macchia tutta dentro
            // un'altra ha un confine che non incontra nessuno.
            var crudi = new List<List<Punto>>();
            r.PerOgniSpigolo((x, y, d) =>
            {
                if (r.Grado(x, y) == 2) return;
                if (r.ArcoDi(x, y, d) >= 0) return;
                crudi.Add(r.PercorriArco(x, y, d, crudi.Count));
            });
            r.PerOgniSpigolo((x, y, d) =>
            {
                if (r.ArcoDi(x, y, d) >= 0) return;
                crudi.Add(r.PercorriArco(x, y, d, crudi.Count));
            });

            var archi = new List<Arco>(crudi.Count);
            foreach (var punti in crudi)
            {
                var chiuso = punti.Count > 2
                          && Math.Abs(punti[0].X - punti[punti.Count - 1].X) < 1e-9
                          && Math.Abs(punti[0].Y - punti[punti.Count - 1].Y) < 1e-9;
                var arco = AdattaCubiche(punti, tolleranza, chiuso, larghezza, altezza);
                arco.Chiuso = chiuso;
                archi.Add(arco);
            }

            // ---- Le zone, ricomposte dagli archi ----------------------------------------------
            // Anche qui si parte prima dai nodi: un anello che cominciasse a meta' di un arco lo
            // spezzerebbe in due tronconi, mentre un arco va disegnato dal suo inizio.
            Action<bool> giro = soloNodi => r.PerOgniSpigolo((x, y, d) =>
            {
                if (soloNodi && r.Grado(x, y) == 2) return;
                if (r.Percorso(x, y, d)) return;
                var zona = r.ADestra(x, y, d);
                if (zona < 0 || zona >= quante) return;
                var anello = r.PercorriAnello(x, y, d, zona);
                if (anello.Passi.Count > 0) zone[zona].Add(anello);
            });
            giro(true);
            giro(false);

            return new Esito { Archi = archi, Zone = zone };
        }

        /// <summary>
        /// Il reticolo delle fessure e tutto quel che ci si legge sopra. Sta in una classe a parte
        /// perche' i suoi quattro indici -- pixel, spigoli orizzontali, verticali, vertici -- si
        /// sbagliano facilmente, e tenerli in un posto solo vuol dire scriverli una volta sola.
        /// </summary>
        private sealed class Reticolo
        {
            private readonly byte[] _indici;
            private readonly bool[]? _opaco;
            private readonly bool _haOpaco;
            private readonly int _w, _h, _lw;

            private readonly bool[] _orizz, _verti;     // dove c'e' un confine
            private readonly byte[] _grado;             // quanti confini escono da ogni vertice
            private readonly int[] _arcoOrizz, _arcoVerti;
            private readonly bool[] _versoOrizz, _versoVerti;
            // Quali versi di ogni spigolo sono gia' stati percorsi. Servono **due** bit per
            // spigolo, non uno: ogni confine appartiene al contorno di due zone, una per verso, e
            // segnarlo esaurito dopo la prima lascerebbe la seconda con il contorno a pezzi.
            private readonly byte[] _usatoOrizz, _usatoVerti;

            // La posizione lisciata di ogni vertice di confine. Sta qui, e non dentro i singoli
            // archi, perche' un vertice deve avere **una** posizione sola: e' quella che tiene
            // insieme gli archi che ci si incontrano, e quindi le zone che quegli archi separano.
            private readonly int[] _indiceVertice;      // -1 dove non passa nessun confine
            private double[] _vx = new double[0];
            private double[] _vy = new double[0];

            public Reticolo(byte[] indici, bool[]? opaco, int larghezza, int altezza)
            {
                _indici = indici; _w = larghezza; _h = altezza; _lw = larghezza + 1;
                _opaco = opaco;
                _haOpaco = opaco != null && opaco.Length == larghezza * altezza;

                _orizz = new bool[_w * (_h + 1)];
                _verti = new bool[_lw * _h];
                for (var y = 0; y <= _h; y++)
                    for (var x = 0; x < _w; x++)
                        _orizz[y * _w + x] = Etichetta(x, y - 1) != Etichetta(x, y);
                for (var y = 0; y < _h; y++)
                    for (var x = 0; x <= _w; x++)
                        _verti[y * _lw + x] = Etichetta(x - 1, y) != Etichetta(x, y);

                _grado = new byte[_lw * (_h + 1)];
                for (var y = 0; y <= _h; y++)
                    for (var x = 0; x <= _w; x++)
                    {
                        var n = 0;
                        for (var d = 0; d < 4; d++) if (Spigolo(x, y, d)) n++;
                        _grado[y * _lw + x] = (byte)n;
                    }

                _arcoOrizz = new int[_orizz.Length];
                _arcoVerti = new int[_verti.Length];
                for (var i = 0; i < _arcoOrizz.Length; i++) _arcoOrizz[i] = -1;
                for (var i = 0; i < _arcoVerti.Length; i++) _arcoVerti[i] = -1;
                _versoOrizz = new bool[_orizz.Length];
                _versoVerti = new bool[_verti.Length];
                _usatoOrizz = new byte[_orizz.Length];
                _usatoVerti = new byte[_verti.Length];

                _indiceVertice = new int[_grado.Length];
                Liscia();
            }

            /// <summary>
            /// Raddrizza la scalinata di pixel, muovendo i vertici del reticolo **una volta sola**.
            ///
            /// ## Perche' serve
            /// Sul reticolo il confine e' fatto di soli tratti orizzontali e verticali: una
            /// diagonale dell'originale ci arriva come una scala a gradini alti un pixel, e adattare
            /// delle curve a quella scala vuol dire ricalcarne i gradini.
            ///
            /// ## Come
            /// Smorzamento di Taubin sulla rete dei confini: un passo che liscia e uno che ridilata.
            /// Il solo smorzamento ripetuto restringerebbe ogni curva verso il suo centro -- una
            /// macchia tonda diventerebbe piu' piccola a ogni giro -- mentre il secondo passo, di
            /// segno opposto e appena piu' forte, la rimette dov'era.
            ///
            /// ## Perche' sui vertici e non sui singoli archi
            /// Perche' un nodo, dove si incontrano tre zone, appartiene a tutti gli archi che ci
            /// arrivano. Lisciando arco per arco bisognerebbe tenerlo fermo per non staccarli, e
            /// ogni nodo diventerebbe uno spigolo: su un disegno quantizzato i nodi sono tanti, e il
            /// contorno ne uscirebbe spezzettato. Muovendo invece il vertice, tutti gli archi che ci
            /// passano lo seguono insieme e restano attaccati -- e la lisciatura attraversa il nodo
            /// invece di fermarsi li'.
            ///
            /// ## Cosa non si tocca
            /// I vertici sul bordo della tavola, che devono restare allineati o l'immagine finirebbe
            /// con i lati ondulati. E nessun punto si allontana di piu' di
            /// <see cref="SpostamentoMassimo"/> da dove stava: la scalinata da togliere e' alta un
            /// pixel, quindi oltre quella misura non si sta piu' raddrizzando un gradino ma
            /// cancellando uno spigolo del disegno.
            /// </summary>
            private void Liscia()
            {
                var quanti = 0;
                for (var i = 0; i < _indiceVertice.Length; i++)
                    _indiceVertice[i] = _grado[i] == 0 ? -1 : quanti++;
                if (quanti == 0) return;

                var vx = new int[quanti];
                var vy = new int[quanti];
                for (var y = 0; y <= _h; y++)
                    for (var x = 0; x <= _w; x++)
                    {
                        var k = _indiceVertice[y * _lw + x];
                        if (k >= 0) { vx[k] = x; vy[k] = y; }
                    }

                var px = new double[quanti];
                var py = new double[quanti];
                var qx = new double[quanti];
                var qy = new double[quanti];
                for (var k = 0; k < quanti; k++) { px[k] = vx[k]; py[k] = vy[k]; }

                for (var giro = 0; giro < GiriDiLisciatura; giro++)
                {
                    UnPasso(vx, vy, px, py, qx, qy, Lambda);
                    UnPasso(vx, vy, qx, qy, px, py, Mu);
                }

                for (var k = 0; k < quanti; k++)
                {
                    var dx = px[k] - vx[k];
                    var dy = py[k] - vy[k];
                    var d = Math.Sqrt(dx * dx + dy * dy);
                    if (d > SpostamentoMassimo)
                    {
                        var f = SpostamentoMassimo / d;
                        px[k] = vx[k] + dx * f;
                        py[k] = vy[k] + dy * f;
                    }
                }
                _vx = px; _vy = py;
            }

            /// <summary>Un passo: ogni vertice si sposta verso la media dei vertici a cui e' unito.</summary>
            private void UnPasso(int[] vx, int[] vy, double[] px, double[] py,
                                 double[] qx, double[] qy, double peso)
            {
                for (var k = 0; k < px.Length; k++)
                {
                    int x = vx[k], y = vy[k];
                    // Il bordo della tavola resta dritto.
                    if (x == 0 || y == 0 || x == _w || y == _h) { qx[k] = px[k]; qy[k] = py[k]; continue; }

                    double sx = 0, sy = 0;
                    var n = 0;
                    for (var d = 0; d < 4; d++)
                    {
                        if (!Spigolo(x, y, d)) continue;
                        var j = _indiceVertice[(y + Dy[d]) * _lw + (x + Dx[d])];
                        if (j < 0) continue;
                        sx += px[j]; sy += py[j]; n++;
                    }
                    if (n == 0) { qx[k] = px[k]; qy[k] = py[k]; continue; }
                    qx[k] = px[k] + peso * (sx / n - px[k]);
                    qy[k] = py[k] + peso * (sy / n - py[k]);
                }
            }

            /// <summary>Dove sta davvero il vertice, dopo la lisciatura.</summary>
            public Punto Posizione(int x, int y)
            {
                var k = _indiceVertice[y * _lw + x];
                return k < 0 ? new Punto(x, y) : new Punto(_vx[k], _vy[k]);
            }

            /// <summary>
            /// A quale tinta appartiene un pixel. Fuori dall'immagine, e dove non c'e' disegno, non
            /// appartiene a nessuna: e' il confine esterno della tavola.
            /// </summary>
            private int Etichetta(int x, int y)
            {
                if (x < 0 || y < 0 || x >= _w || y >= _h) return -1;
                var i = y * _w + x;
                if (_haOpaco && !_opaco![i]) return -1;
                return _indici[i];
            }

            public byte Grado(int x, int y) { return _grado[y * _lw + x]; }

            /// <summary>C'e' un confine uscendo dal vertice nella direzione indicata?</summary>
            public bool Spigolo(int x, int y, int dir)
            {
                switch (dir)
                {
                    case 0: return x < _w && _orizz[y * _w + x];
                    case 1: return y < _h && _verti[y * _lw + x];
                    case 2: return x > 0 && _orizz[y * _w + (x - 1)];
                    default: return y > 0 && _verti[(y - 1) * _lw + x];
                }
            }

            /// <summary>Dove sta memorizzato lo spigolo che si imbocca da qui in questa direzione.</summary>
            private void Dove(int x, int y, int dir, out bool orizzontale, out int i)
            {
                switch (dir)
                {
                    case 0: orizzontale = true; i = y * _w + x; break;
                    case 2: orizzontale = true; i = y * _w + (x - 1); break;
                    case 1: orizzontale = false; i = y * _lw + x; break;
                    default: orizzontale = false; i = (y - 1) * _lw + x; break;
                }
            }

            /// <summary>Le direzioni 0 e 1 percorrono lo spigolo nel verso in cui e' memorizzato.</summary>
            private static bool VersoNaturale(int dir) { return dir == 0 || dir == 1; }

            public int ArcoDi(int x, int y, int dir)
            {
                bool o; int i; Dove(x, y, dir, out o, out i);
                return o ? _arcoOrizz[i] : _arcoVerti[i];
            }

            /// <summary>Questo spigolo, percorso cosi', va nello stesso verso del suo arco?</summary>
            public bool ComeLArco(int x, int y, int dir)
            {
                bool o; int i; Dove(x, y, dir, out o, out i);
                var versoArco = o ? _versoOrizz[i] : _versoVerti[i];
                return versoArco == VersoNaturale(dir);
            }

            public bool Percorso(int x, int y, int dir)
            {
                bool o; int i; Dove(x, y, dir, out o, out i);
                var bit = (byte)(VersoNaturale(dir) ? 1 : 2);
                return ((o ? _usatoOrizz[i] : _usatoVerti[i]) & bit) != 0;
            }

            private void Percorri(int x, int y, int dir)
            {
                bool o; int i; Dove(x, y, dir, out o, out i);
                var bit = (byte)(VersoNaturale(dir) ? 1 : 2);
                if (o) _usatoOrizz[i] |= bit; else _usatoVerti[i] |= bit;
            }

            private void Assegna(int x, int y, int dir, int arco)
            {
                bool o; int i; Dove(x, y, dir, out o, out i);
                if (o) { _arcoOrizz[i] = arco; _versoOrizz[i] = VersoNaturale(dir); }
                else { _arcoVerti[i] = arco; _versoVerti[i] = VersoNaturale(dir); }
            }

            public void PerOgniSpigolo(Action<int, int, int> cosa)
            {
                for (var y = 0; y <= _h; y++)
                    for (var x = 0; x <= _w; x++)
                        for (var d = 0; d < 4; d++)
                            if (Spigolo(x, y, d)) cosa(x, y, d);
            }

            /// <summary>
            /// Quale zona resta a destra percorrendo lo spigolo nella direzione indicata. In
            /// coordinate schermo, con la y verso il basso, la destra di chi va verso destra e' il
            /// basso.
            /// </summary>
            public int ADestra(int x, int y, int dir)
            {
                switch (dir)
                {
                    case 0: return Etichetta(x, y);          // verso destra: il pixel sotto
                    case 2: return Etichetta(x - 1, y - 1);  // verso sinistra: quello sopra
                    case 1: return Etichetta(x - 1, y);      // verso il basso: quello a sinistra
                    default: return Etichetta(x, y - 1);     // verso l'alto: quello a destra
                }
            }

            /// <summary>
            /// Segue un confine da un vertice all'altro finche' non incontra un nodo, o finche' non
            /// torna al punto di partenza. Nei vertici di grado due la strada e' una sola: si esce
            /// dall'unico spigolo che non e' quello da cui si e' arrivati.
            /// </summary>
            public List<Punto> PercorriArco(int x0, int y0, int d0, int idArco)
            {
                var punti = new List<Punto> { Posizione(x0, y0) };
                int x = x0, y = y0, d = d0;
                while (true)
                {
                    Assegna(x, y, d, idArco);
                    x += Dx[d]; y += Dy[d];
                    punti.Add(Posizione(x, y));

                    if (Grado(x, y) != 2) break;      // un nodo: l'arco finisce qui
                    if (x == x0 && y == y0) break;    // anello chiuso, senza nodi

                    var indietro = (d + 2) & 3;
                    var avanti = -1;
                    for (var k = 0; k < 4; k++)
                        if (k != indietro && Spigolo(x, y, k)) { avanti = k; break; }
                    if (avanti < 0) break;
                    d = avanti;
                }
                return punti;
            }

            /// <summary>
            /// Percorre il contorno di una zona tenendosela sempre **a destra**, e annota quali
            /// archi attraversa e in che verso.
            ///
            /// ## Perche' le tinte non si sovrappongono
            /// Ogni spigolo, in ciascuno dei due versi, ha una sola zona a destra. Percorrendo solo
            /// gli spigoli che lasciano a destra la zona giusta, e marcandoli via via, ogni tratto di
            /// confine finisce nel contorno di **esattamente due** zone -- una per verso -- e di
            /// nessun'altra. Il disegno risulta cosi' tassellato senza che nessuno debba verificarlo.
            ///
            /// ## Gli incroci
            /// Dove quattro zone si toccano in diagonale ci sono due uscite valide. Si preferisce la
            /// svolta a sinistra, cioe' la piu' larga: una catena di pixel in diagonale resta unita
            /// invece di spezzarsi in tanti rombi staccati. Le due zone opposte si toccano allora nel
            /// solo punto d'incrocio, che essendo un nodo non viene lisciato e resta comune a
            /// entrambe: si toccano, non si accavallano.
            /// </summary>
            public Anello PercorriAnello(int x0, int y0, int d0, int zona)
            {
                var anello = new Anello();
                int x = x0, y = y0, d = d0;
                // Una rete malformata non deve poter far girare a vuoto: ogni spigolo si percorre
                // una volta sola per verso, quindi oltre quel conto c'e' senz'altro un errore.
                var limite = 2L * (_orizz.Length + _verti.Length) + 8;

                for (long n = 0; n < limite; n++)
                {
                    Percorri(x, y, d);

                    var idArco = ArcoDi(x, y, d);
                    if (idArco >= 0)
                    {
                        var passo = new Passo(idArco, !ComeLArco(x, y, d));
                        var ultimo = anello.Passi.Count - 1;
                        if (ultimo < 0 || anello.Passi[ultimo].Arco != passo.Arco
                                       || anello.Passi[ultimo].Inverso != passo.Inverso)
                            anello.Passi.Add(passo);
                    }

                    x += Dx[d]; y += Dy[d];

                    // Fra le uscite che lasciano la zona a destra si prende la piu' a sinistra:
                    // sinistra, dritto, destra, indietro. Quando non ce n'e' piu' nessuna il
                    // contorno e' chiuso, ed e' tornato al punto di partenza.
                    var scelta = -1;
                    for (var k = 0; k < 4; k++)
                    {
                        var cand = (d + 3 + k) & 3;
                        if (!Spigolo(x, y, cand)) continue;
                        if (ADestra(x, y, cand) != zona) continue;
                        if (Percorso(x, y, cand)) continue;
                        scelta = cand; break;
                    }
                    if (scelta < 0) break;
                    d = scelta;
                }
                return anello;
            }
        }

        // ---- Quanto lisciare, e quanto e' lecito muovere il disegno --------------------------
        // Vedi Reticolo.Liscia per il perche' di ciascuno.

        private const int GiriDiLisciatura = 12;
        private const double Lambda = 0.55;
        /// <summary>
        /// Il passo che ridilata. Deve valere poco piu' di <see cref="Lambda"/> in negativo: e' la
        /// condizione perche' la forma non si restringa a ogni giro.
        /// </summary>
        private const double Mu = -0.58;
        /// <summary>
        /// Di quanto al massimo un punto puo' allontanarsi dal confine misurato. La scalinata da
        /// raddrizzare e' alta un pixel: oltre un pixel non si toglie piu' un gradino, si smussa uno
        /// spigolo vero.
        /// </summary>
        private const double SpostamentoMassimo = 1.0;

        // ---- Dalle spezzate alle curve ------------------------------------------------------

        /// <summary>
        /// Trasforma la spezzata in poche cubiche di Bezier.
        ///
        /// ## Perche' non basta la spezzata
        /// Dopo la lisciatura il confine ha ancora un punto per pixel: fedele, ma su un'immagine
        /// grande sono centinaia di migliaia di punti, e fra l'uno e l'altro restano segmenti dritti
        /// che ingranditi si vedono.
        ///
        /// ## Come
        /// Si spezza dove ci sono spigoli veri (vedi <see cref="TrovaSpigoli"/>) e fra uno spigolo e
        /// l'altro si adatta una cubica ai minimi quadrati. Se lo scarto peggiore supera la
        /// tolleranza si taglia proprio li' e si riprova sulle due meta': dove il confine e' dolce
        /// basta una curva, dove e' mosso ne servono di piu', e non deve deciderlo nessuno prima.
        /// </summary>
        private static Arco AdattaCubiche(List<Punto> punti, double tolleranza, bool chiuso, double larghezza, double altezza)
        {
            var arco = new Arco { Inizio = punti[0], Fine = punti[punti.Count - 1] };
            if (punti.Count < 2) return arco;
            if (punti.Count == 2)
            {
                arco.Cubiche.Add(Retta(punti[0], punti[1]));
                return arco;
            }

            var tagli = TrovaSpigoli(punti);

            // Agli estremi dell'arco la direzione si prende da un lato solo, perche' di la' non c'e'
            // altro. Ma in un anello chiuso i due estremi sono lo **stesso punto**: prendendo ognuno
            // la propria, la curva si richiude formando uno spigolo che nel disegno non esiste. Alla
            // cucitura la direzione si misura quindi a cavallo, come in ogni altro punto interno --
            // a meno che li' il confine giri davvero, e allora lo spigolo va tenuto.
            var tInizio = Tangente(punti, 0, +1);
            var tFine = Tangente(punti, punti.Count - 1, -1);
            if (chiuso)
            {
                var cucitura = TangenteCucitura(punti);
                if (cucitura.HasValue)
                {
                    tInizio = cucitura.Value;
                    tFine = new Punto(-cucitura.Value.X, -cucitura.Value.Y);
                }
            }

            for (var t = 0; t + 1 < tagli.Count; t++)
                Adatta(punti, tagli[t], tagli[t + 1],
                       t == 0 ? tInizio : Tangente(punti, tagli[t], +1),
                       t + 2 == tagli.Count ? tFine : Tangente(punti, tagli[t + 1], -1),
                       tolleranza, arco.Cubiche, larghezza, altezza);

            if (arco.Cubiche.Count == 0) arco.Cubiche.Add(Retta(punti[0], punti[punti.Count - 1]));
            return arco;
        }

        /// <summary>
        /// La direzione del confine attraverso la cucitura di un anello, guardando indietro dalla
        /// fine e avanti dall'inizio. Restituisce null quando li' c'e' uno spigolo vero, che va
        /// lasciato tale.
        /// </summary>
        private static Punto? TangenteCucitura(List<Punto> punti)
        {
            var n = punti.Count;
            if (n < 2 * Finestra + 2) return null;

            // L'ultimo punto ripete il primo: il passo indietro parte da quello prima.
            var prima = punti[n - 1 - Finestra];
            var dopo = punti[Finestra];

            var ax = punti[0].X - prima.X;
            var ay = punti[0].Y - prima.Y;
            var bx = dopo.X - punti[0].X;
            var by = dopo.Y - punti[0].Y;
            var la = Math.Sqrt(ax * ax + ay * ay);
            var lb = Math.Sqrt(bx * bx + by * by);
            if (la < 1e-9 || lb < 1e-9) return null;
            if ((ax * bx + ay * by) / (la * lb) <= CosenoSpigolo) return null;   // spigolo vero

            var dx = dopo.X - prima.X;
            var dy = dopo.Y - prima.Y;
            var l = Math.Sqrt(dx * dx + dy * dy);
            if (l < 1e-9) return null;
            return new Punto(dx / l, dy / l);
        }

        private static Punto[] Retta(Punto a, Punto b)
        {
            return new[]
            {
                new Punto(a.X + (b.X - a.X) / 3.0, a.Y + (b.Y - a.Y) / 3.0),
                new Punto(a.X + (b.X - a.X) * 2.0 / 3.0, a.Y + (b.Y - a.Y) * 2.0 / 3.0),
                b,
            };
        }

        /// <summary>
        /// Dove il confine gira davvero, e non sta solo salendo un gradino.
        ///
        /// La direzione si misura fra punti distanti <see cref="Finestra"/> passi, non fra vicini
        /// immediati: sul reticolo ogni tratto e' orizzontale o verticale, quindi fra due punti
        /// vicini l'angolo o e' zero o e' novanta gradi, e ogni gradino sembrerebbe uno spigolo.
        /// Guardando piu' lontano una scalinata risulta per quel che e', cioe' una diagonale.
        /// </summary>
        private static List<int> TrovaSpigoli(List<Punto> punti)
        {
            var n = punti.Count;
            var tagli = new List<int> { 0 };
            for (var i = Finestra; i < n - Finestra; i++)
            {
                var ax = punti[i].X - punti[i - Finestra].X;
                var ay = punti[i].Y - punti[i - Finestra].Y;
                var bx = punti[i + Finestra].X - punti[i].X;
                var by = punti[i + Finestra].Y - punti[i].Y;
                var la = Math.Sqrt(ax * ax + ay * ay);
                var lb = Math.Sqrt(bx * bx + by * by);
                if (la < 1e-9 || lb < 1e-9) continue;
                if ((ax * bx + ay * by) / (la * lb) > CosenoSpigolo) continue;
                if (i - tagli[tagli.Count - 1] < Finestra) continue;   // uno spigolo per curva
                tagli.Add(i);
            }
            if (tagli[tagli.Count - 1] != n - 1) tagli.Add(n - 1);
            return tagli;
        }

        private const int Finestra = 3;
        /// <summary>
        /// Sotto questo coseno la direzione e' cambiata troppo per essere una curva: circa
        /// sessantacinque gradi. Piu' in basso si perderebbero gli angoli retti dei disegni
        /// geometrici, piu' in alto ogni ondulazione diventerebbe uno spigolo.
        /// </summary>
        private const double CosenoSpigolo = 0.42;

        /// <summary>
        /// Adatta una cubica al tratto indicato; se non basta, taglia nel punto peggiore e riprova.
        /// </summary>
        /// <param name="tDa">Direzione con cui la curva deve partire, in avanti.</param>
        /// <param name="tA">Direzione con cui deve arrivare, presa all'indietro dal punto finale.</param>
        private static void Adatta(List<Punto> p, int da, int a, Punto tDa, Punto tA,
                                   double tolleranza, List<Punto[]> fuori,
                                   double larghezza, double altezza)
        {
            if (a - da < 1) return;
            if (a - da == 1) { fuori.Add(Retta(p[da], p[a])); return; }

            var t1 = tDa;
            var t2 = tA;
            var u = Parametri(p, da, a);

            Punto c1, c2;
            if (!Controlli(p, da, a, u, t1, t2, larghezza, altezza, out c1, out c2))
            {
                fuori.Add(Retta(p[da], p[a]));
                return;
            }

            int peggiore;
            var errore = Scarto(p, da, a, u, p[da], c1, c2, p[a], out peggiore);
            if (errore <= tolleranza * tolleranza || a - da <= 2)
            {
                fuori.Add(new[] { c1, c2, p[a] });
                return;
            }

            // Il taglio non puo' cadere su un estremo, o la ricorsione non scenderebbe mai.
            if (peggiore <= da) peggiore = da + 1;
            if (peggiore >= a) peggiore = a - 1;

            // Le due meta' devono incontrarsi lungo la **stessa** direzione, altrimenti nel punto di
            // taglio si forma un angolo. Prendendo ciascuna la propria corda -- quella all'indietro
            // per chi arriva, quella in avanti per chi riparte -- le due direzioni differiscono di
            // quanto il confine gira in quel tratto, e ogni taglio lasciava uno spigolo su una linea
            // che spigoli non ne ha. Qui la direzione si misura una volta sola, a cavallo del punto,
            // e la si impone a entrambe: la curva prosegue senza scalino.
            var tm = TangenteCentrata(p, peggiore);
            Adatta(p, da, peggiore, t1, new Punto(-tm.X, -tm.Y), tolleranza, fuori, larghezza, altezza);
            Adatta(p, peggiore, a, tm, t2, tolleranza, fuori, larghezza, altezza);
        }

        /// <summary>
        /// La direzione del confine **attraverso** un punto, non da un lato solo: e' quella che le
        /// due curve che vi si incontrano devono condividere perche' il giunto non si veda.
        /// </summary>
        private static Punto TangenteCentrata(List<Punto> p, int i)
        {
            var prima = i - Finestra; if (prima < 0) prima = 0;
            var dopo = i + Finestra; if (dopo > p.Count - 1) dopo = p.Count - 1;
            var dx = p[dopo].X - p[prima].X;
            var dy = p[dopo].Y - p[prima].Y;
            var l = Math.Sqrt(dx * dx + dy * dy);
            if (l < 1e-9) return Tangente(p, i, +1);
            return new Punto(dx / l, dy / l);
        }

        /// <summary>La direzione con cui la curva deve partire o arrivare, presa su qualche punto.</summary>
        private static Punto Tangente(List<Punto> p, int i, int verso)
        {
            var fin = i + verso * Finestra;
            if (fin < 0) fin = 0;
            if (fin > p.Count - 1) fin = p.Count - 1;
            var dx = p[fin].X - p[i].X;
            var dy = p[fin].Y - p[i].Y;
            var l = Math.Sqrt(dx * dx + dy * dy);
            if (l < 1e-9) return new Punto(0, 0);
            return new Punto(dx / l, dy / l);
        }

        /// <summary>A che punto della curva corrisponde ogni punto misurato, secondo le distanze.</summary>
        private static double[]? Parametri(List<Punto> p, int da, int a)
        {
            var u = new double[a - da + 1];
            u[0] = 0;
            for (var i = da + 1; i <= a; i++)
            {
                var dx = p[i].X - p[i - 1].X;
                var dy = p[i].Y - p[i - 1].Y;
                u[i - da] = u[i - da - 1] + Math.Sqrt(dx * dx + dy * dy);
            }
            var tot = u[u.Length - 1];
            if (tot < 1e-12) return null;
            for (var i = 0; i < u.Length; i++) u[i] /= tot;
            return u;
        }

        /// <summary>
        /// Quanto allungare le due tangenti perche' la curva passi il piu' vicino possibile ai punti
        /// misurati: due incognite, e un sistema due per due ai minimi quadrati.
        /// </summary>
        private static bool Controlli(List<Punto> p, int da, int a, double[]? u,
                                      Punto t1, Punto t2, double larghezza, double altezza,
                                      out Punto c1, out Punto c2)
        {
            c1 = p[da]; c2 = p[a];
            if (u == null) return false;

            double c11 = 0, c12 = 0, c22 = 0, x1 = 0, x2 = 0;
            var p0 = p[da]; var p3 = p[a];

            for (var i = 0; i < u.Length; i++)
            {
                var t = u[i];
                var mt = 1 - t;
                var b0 = mt * mt * mt;
                var b1 = 3 * t * mt * mt;
                var b2 = 3 * t * t * mt;
                var b3 = t * t * t;

                var a1x = t1.X * b1; var a1y = t1.Y * b1;
                var a2x = t2.X * b2; var a2y = t2.Y * b2;

                c11 += a1x * a1x + a1y * a1y;
                c12 += a1x * a2x + a1y * a2y;
                c22 += a2x * a2x + a2y * a2y;

                var vx = p[da + i].X - (p0.X * (b0 + b1) + p3.X * (b2 + b3));
                var vy = p[da + i].Y - (p0.Y * (b0 + b1) + p3.Y * (b2 + b3));
                x1 += a1x * vx + a1y * vy;
                x2 += a2x * vx + a2y * vy;
            }

            var det = c11 * c22 - c12 * c12;
            var corda = Math.Sqrt((p3.X - p0.X) * (p3.X - p0.X) + (p3.Y - p0.Y) * (p3.Y - p0.Y));
            double alfa1, alfa2;
            if (Math.Abs(det) < 1e-12)
            {
                alfa1 = alfa2 = corda / 3.0;
            }
            else
            {
                alfa1 = (x1 * c22 - x2 * c12) / det;
                alfa2 = (c11 * x2 - c12 * x1) / det;
                // Tangenti negative o smisurate piegherebbero la curva all'indietro: meglio la
                // stima grossolana, un terzo della corda. Il tetto e' la corda stessa: oltre, il
                // punto di controllo esce dal riquadro del tratto che sta descrivendo -- e sul bordo
                // della tavola usciva proprio dalla tavola.
                if (alfa1 < 1e-6 || alfa2 < 1e-6 || alfa1 > corda || alfa2 > corda)
                    alfa1 = alfa2 = corda / 3.0;
            }

            // Un punto di controllo non deve uscire dalla tavola. Dove il confine corre sul bordo
            // dell'immagine una tangente presa a cavallo del taglio punta appena in fuori, e il
            // controllo finisce oltre il margine: il lato, che deve restare dritto, si inarca.
            //
            // Si accorcia **lungo la tangente**, non tagliando le coordinate: spostare il controllo
            // di lato cambierebbe la direzione con cui la curva parte, e rimetterebbe negli attacchi
            // proprio lo spigolo che si e' tolto. E si vincola alla sola tavola, non al riquadro del
            // tratto: quello e' piu' stretto della curva che deve descrivere, e la spezzerebbe in
            // molti piu' pezzi per nulla.
            if (larghezza > 0 && altezza > 0)
            {
                alfa1 = Accorcia(p0, t1, alfa1, larghezza, altezza);
                alfa2 = Accorcia(p3, t2, alfa2, larghezza, altezza);
            }

            c1 = new Punto(p0.X + t1.X * alfa1, p0.Y + t1.Y * alfa1);
            c2 = new Punto(p3.X + t2.X * alfa2, p3.Y + t2.Y * alfa2);
            return true;
        }

        /// <summary>Quanto si puo' andare lungo la tangente restando dentro la tavola.</summary>
        private static double Accorcia(Punto da, Punto t, double alfa, double larghezza, double altezza)
        {
            var limite = alfa;
            if (t.X > 1e-9) limite = Math.Min(limite, (larghezza - da.X) / t.X);
            else if (t.X < -1e-9) limite = Math.Min(limite, (0 - da.X) / t.X);
            if (t.Y > 1e-9) limite = Math.Min(limite, (altezza - da.Y) / t.Y);
            else if (t.Y < -1e-9) limite = Math.Min(limite, (0 - da.Y) / t.Y);
            return limite < 0 ? 0 : limite;
        }

        /// <summary>Lo scarto quadratico peggiore fra la curva e i punti misurati.</summary>
        private static double Scarto(List<Punto> p, int da, int a, double[]? u,
                                     Punto p0, Punto c1, Punto c2, Punto p3, out int peggiore)
        {
            peggiore = (da + a) / 2;
            if (u == null) return double.MaxValue;
            double massimo = 0;
            for (var i = 1; i < u.Length - 1; i++)
            {
                var t = u[i];
                var mt = 1 - t;
                var b0 = mt * mt * mt;
                var b1 = 3 * t * mt * mt;
                var b2 = 3 * t * t * mt;
                var b3 = t * t * t;
                var qx = p0.X * b0 + c1.X * b1 + c2.X * b2 + p3.X * b3;
                var qy = p0.Y * b0 + c1.Y * b1 + c2.Y * b2 + p3.Y * b3;
                var dx = qx - p[da + i].X;
                var dy = qy - p[da + i].Y;
                var d = dx * dx + dy * dy;
                if (d > massimo) { massimo = d; peggiore = da + i; }
            }
            return massimo;
        }
    }
}

