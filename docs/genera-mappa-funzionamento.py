# -*- coding: utf-8 -*-
"""Genera la mappa di funzionamento di Stock Vector Studio.

Il layout e' calcolato: le card si dimensionano sul contenuto e i connettori
vivono solo nelle gutter, che non contengono mai testo di card. Cosi' non
si ripresenta il problema delle linee sovrapposte alle frasi.
"""
import html

W = 1840
MARGIN = 40
BAND_PAD = 18
CARD_GAP = 18
CARD_PAD = 14
GUTTER = 92

PAL = {
    "local":   ("#e8f2fe", "#1b6ec2", "#0d3b66"),
    "azure":   ("#e3f1e6", "#2e7d32", "#1b4d20"),
    "extern":  ("#fdf0e3", "#c77700", "#7a4a00"),
    "missing": ("#fdeaea", "#c62828", "#7f1d1d"),
    "store":   ("#f0ecfb", "#5b3fa8", "#33236b"),
}
BAND_BG = "#f7f9fc"
BAND_ST = "#c9d6e4"
INK = "#16232e"
MUTED = "#5a6b7a"


def tw(text, size, bold=False):
    return len(text) * size * (0.575 if bold else 0.535)


def wrap(text, size, maxw, bold=False):
    words, lines, cur = text.split(), [], ""
    for word in words:
        trial = word if not cur else cur + " " + word
        if tw(trial, size, bold) <= maxw or not cur:
            cur = trial
        else:
            lines.append(cur)
            cur = word
    if cur:
        lines.append(cur)
    return lines


class Card:
    def __init__(self, title, kind, lines, badge=None):
        self.title, self.kind, self.lines, self.badge = title, kind, lines, badge
        self.x = self.y = self.w = self.h = 0

    def layout(self, width):
        self.w = width
        inner = width - 2 * CARD_PAD
        self.tlines = wrap(self.title, 16, inner, True)
        self.blocks = [wrap("- " + l, 12.5, inner) for l in self.lines]
        h = CARD_PAD + len(self.tlines) * 21
        if self.badge:
            h += 20
        h += 6
        for b in self.blocks:
            h += len(b) * 16 + 4
        self.h = h + CARD_PAD - 4
        return self.h

    def svg(self):
        bg, st, ink = PAL[self.kind]
        dash = ' stroke-dasharray="7 5"' if self.kind == "missing" else ""
        o = [f'<rect x="{self.x}" y="{self.y}" width="{self.w}" height="{self.h}" rx="10" '
             f'fill="{bg}" stroke="{st}" stroke-width="2"{dash}/>']
        o.append(f'<rect x="{self.x}" y="{self.y}" width="5" height="{self.h}" rx="2.5" fill="{st}"/>')
        y = self.y + CARD_PAD + 15
        for ln in self.tlines:
            o.append(f'<text x="{self.x + CARD_PAD}" y="{y}" font-size="16" font-weight="700" fill="{ink}">{html.escape(ln)}</text>')
            y += 21
        if self.badge:
            label, colour = self.badge
            bw = tw(label, 11, True) + 16
            o.append(f'<rect x="{self.x + CARD_PAD}" y="{y - 11}" width="{bw:.0f}" height="17" rx="8.5" fill="{colour}"/>')
            o.append(f'<text x="{self.x + CARD_PAD + 8}" y="{y + 1.5}" font-size="11" font-weight="700" fill="#ffffff">{html.escape(label)}</text>')
            y += 20
        y += 6
        for blk in self.blocks:
            for ln in blk:
                o.append(f'<text x="{self.x + CARD_PAD}" y="{y}" font-size="12.5" fill="{MUTED}">{html.escape(ln)}</text>')
                y += 16
            y += 4
        return "\n".join(o)


class Band:
    def __init__(self, num, title, subtitle, cards, arrows_between=False):
        self.num, self.title, self.subtitle = num, title, subtitle
        self.cards, self.arrows = cards, arrows_between

    def layout(self, y):
        self.y = y
        avail = W - 2 * MARGIN - 2 * BAND_PAD
        n = len(self.cards)
        extra = (n - 1) * 26 if self.arrows else 0
        cw = (avail - (n - 1) * CARD_GAP - extra) / n
        # Con poche card una larghezza piena le stirerebbe: le limito e centro la riga,
        # cosi' tutte le bande hanno card di dimensione confrontabile.
        cw = min(cw, 604)
        used = n * cw + (n - 1) * CARD_GAP + extra
        top = y + 62
        hmax = 0
        x = MARGIN + BAND_PAD + (avail - used) / 2
        for c in self.cards:
            c.layout(cw)
            c.x, c.y = x, top
            hmax = max(hmax, c.h)
            x += cw + CARD_GAP + (26 if self.arrows else 0)
        self.h = 62 + hmax + BAND_PAD
        return self.h

    def svg(self):
        o = [f'<rect x="{MARGIN}" y="{self.y}" width="{W - 2 * MARGIN}" height="{self.h}" rx="14" '
             f'fill="{BAND_BG}" stroke="{BAND_ST}" stroke-width="1.5"/>']
        o.append(f'<circle cx="{MARGIN + 34}" cy="{self.y + 34}" r="17" fill="{INK}"/>')
        o.append(f'<text x="{MARGIN + 34}" y="{self.y + 40}" font-size="17" font-weight="700" fill="#ffffff" text-anchor="middle">{self.num}</text>')
        o.append(f'<text x="{MARGIN + 62}" y="{self.y + 33}" font-size="20" font-weight="700" fill="{INK}">{html.escape(self.title)}</text>')
        o.append(f'<text x="{MARGIN + 62}" y="{self.y + 51}" font-size="13" fill="{MUTED}">{html.escape(self.subtitle)}</text>')
        for c in self.cards:
            o.append(c.svg())
        if self.arrows:
            for a, b in zip(self.cards, self.cards[1:]):
                x1, x2 = a.x + a.w, b.x
                ym = a.y + min(a.h, b.h) / 2
                o.append(f'<line x1="{x1 + 5}" y1="{ym}" x2="{x2 - 9}" y2="{ym}" stroke="{INK}" stroke-width="2.5" marker-end="url(#ar)"/>')
        return "\n".join(o)


def gutter(y, label, sub):
    """Pastiglia centrata con due frecce: nessuna linea attraversa mai il testo."""
    o = []
    pw = max(tw(label, 14, True), tw(sub, 11.5)) + 40
    px = (W - pw) / 2
    py = y + (GUTTER - 44) / 2
    cx = W / 2
    o.append(f'<line x1="{cx}" y1="{y - 2}" x2="{cx}" y2="{py - 9}" stroke="{INK}" stroke-width="2.5" marker-end="url(#ar)"/>')
    o.append(f'<rect x="{px:.0f}" y="{py:.0f}" width="{pw:.0f}" height="44" rx="22" fill="#ffffff" stroke="{INK}" stroke-width="2"/>')
    o.append(f'<text x="{cx}" y="{py + 19}" font-size="14" font-weight="700" fill="{INK}" text-anchor="middle">{html.escape(label)}</text>')
    o.append(f'<text x="{cx}" y="{py + 35}" font-size="11.5" fill="{MUTED}" text-anchor="middle">{html.escape(sub)}</text>')
    o.append(f'<line x1="{cx}" y1="{py + 46}" x2="{cx}" y2="{y + GUTTER - 2}" stroke="{INK}" stroke-width="2.5" marker-end="url(#ar)"/>')
    return "\n".join(o)


GEN = ("generativo", "#7b2ff7")
DET = ("deterministico", "#2e7d32")

bands = [
    (Band(1, "Creazione e caricamento", "Sulla tua macchina, nel browser", [
        Card("Flow (Google)", "extern", [
            "Genera l'immagine di partenza",
            "Esporta un JPEG",
            "Fuori dall'applicativo: passaggio manuale",
        ], GEN),
        Card("Interfaccia web", "local", [
            "React 18 + TypeScript + Vite",
            "http://127.0.0.1:5173",
            "Caricamento file, scelta modalità vettoriale o raster",
            "Pagine: Studio, Trend, Configurazione, Guida",
        ], DET),
    ]), "POST /api/jobs", "Il browser invia il file al backend locale"),

    (Band(2, "Elaborazione locale", "StockStudio.Api - ASP.NET Core .NET 8 - http://127.0.0.1:5080", [
        Card("Vettorializzazione", "local", [
            "potrace: tools/potrace/potrace.exe (predefinito)",
            "oppure Adobe Illustrator via COM + JSX",
            "Ingrandimento 200%, B&N Silhouette Auto Group",
            "Soglia automatica Otsu, timeout 180s",
            "Produce SVG, EPS e JPG con lo stesso nome",
        ], DET),
        Card("Metadati e agente AI", "local", [
            "OpenAI gpt-4o-mini (Ai:Provider = openai)",
            "Vision: guarda l'immagine e scrive titolo e keyword",
            "Immagine ridotta a 1024px, max 40 keyword, temp 0,4",
            "Agente opportunità con strumenti reali",
            "Cache risposte 12 ore",
        ], GEN),
        Card("Dati e file", "store", [
            "Azurite Table stockstudiojobs (porta 10002)",
            "Cartella Storage/ su disco per i deliverable",
            "CSV Adobe Stock e Freepik",
            "Formule neutralizzate contro CSV injection",
        ], DET),
    ]), "Dispatch", "Il JPG viene consegnato alla pipeline"),

    (Band(3, "Consegna alla pipeline", "Punto di giunzione fra il nuovo sito e il vecchio impianto", [
        Card("QueueDispatcher", "azure", [
            "Accoda un ResizeQueueMessage",
            "Coda image-to-classify",
            "Contratto condiviso: StockStudio.Shared",
            "In locale punta ad Azurite: le code non esistono, è un no-op",
        ], DET),
        Card("SharePoint (back-office)", "azure", [
            "cosdh.sharepoint.com/sites/Classifier",
            "Librerie ImagesToClassify, ImagesToSend, ImagesSent",
            "Autenticazione PnP con certificato pnp.pfx",
            "Usato per imbuto di avanzamento e controllo duplicati",
        ], DET),
    ]), "Coda image-to-classify", "Da qui in avanti gira il progetto resize-image"),

    (Band(4, "Pipeline Azure esistente", "Progetto resize-image (.NET 6 Windows) più Logic App non presenti nel repository", [
        Card("Function ResizeImageToClassify", "azure", [
            "Trigger: coda image-to-classify",
            "Legge il file da SharePoint",
            "Scrive nel blob shrinked-images",
            "Accoda shrinked-image-to-classify",
        ], DET),
        Card("Logic App di classificazione", "missing", [
            "NON presente nel repository",
            "Dedotta: nessun componente locale legge quella coda",
            "Produce titolo, descrizione e tag definitivi",
            "Accoda images-to-send",
        ], GEN),
        Card("Function UploadFileViaSFTP", "azure", [
            "Trigger: coda images-to-send",
            "Scrive i metadati EXIF con exiftool",
            "Carica sui marketplace",
            "Notifica la Logic App e richiama il sito",
        ], DET),
    ], arrows_between=True), "Upload", "Consegna ai siti e ritorno di stato"),

    (Band(5, "Pubblicazione e ritorno", "Marketplace, spostamento file e aggiornamento della dashboard", [
        Card("Marketplace", "extern", [
            "SFTP Adobe Stock - porta 22",
            "SFTP Freepik - porta 60022",
            "FTP YayImages - porta 21",
            "FTP DreamsTime - porta 21",
        ], DET),
        Card("Logic App di spostamento", "azure", [
            "Workflow 69a3684f...d228 (West Europe)",
            "Unica Logic App tracciata nel repository",
            "Sposta il file nella libreria ImagesSent",
            "Attenzione: l'URL contiene una firma SAS",
        ], DET),
        Card("Ritorno alla dashboard", "local", [
            "Callback firmato con header X-Callback-Secret",
            "POST /api/pipeline/callback",
            "Aggiorna lo stato del job nella tabella",
            "Il job compare come pubblicato nell'interfaccia",
        ], DET),
    ]), None, None),
]

y = 132
body, gutters = [], []
for band, glabel, gsub in bands:
    h = band.layout(y)
    body.append(band.svg())
    y += h
    if glabel:
        gutters.append(gutter(y, glabel, gsub))
        y += GUTTER

legend_y = y + 26
H = legend_y + 108

out = ['<?xml version="1.0" encoding="UTF-8"?>']
out.append(f'<svg xmlns="http://www.w3.org/2000/svg" width="{W}" height="{H}" viewBox="0 0 {W} {H}" font-family="Segoe UI, Selawik, Arial, sans-serif">')
out.append(f'<defs><marker id="ar" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse">'
           f'<path d="M 0 0 L 10 5 L 0 10 z" fill="{INK}"/></marker></defs>')
out.append(f'<rect width="{W}" height="{H}" fill="#ffffff"/>')
out.append(f'<text x="{MARGIN}" y="58" font-size="30" font-weight="700" fill="{INK}">Stock Vector Studio - mappa del funzionamento</text>')
out.append(f'<text x="{MARGIN}" y="88" font-size="15" fill="{MUTED}">Dal JPEG generato con Flow alla pubblicazione su Adobe Stock e Freepik, con le risorse usate in ogni blocco.</text>')
out.append(f'<text x="{MARGIN}" y="110" font-size="13" fill="{MUTED}">Stato verificato il 22 agosto 2026. Le risorse non tracciate nel repository sono segnate con bordo tratteggiato rosso.</text>')
out += body
out += gutters

items = [("local", "In esecuzione in locale"), ("azure", "Azure / Microsoft 365"),
         ("extern", "Servizio esterno"), ("missing", "Non presente nel repository"),
         ("store", "Archiviazione dati")]
out.append(f'<rect x="{MARGIN}" y="{legend_y}" width="{W - 2 * MARGIN}" height="84" rx="12" fill="{BAND_BG}" stroke="{BAND_ST}" stroke-width="1.5"/>')
out.append(f'<text x="{MARGIN + 20}" y="{legend_y + 26}" font-size="14" font-weight="700" fill="{INK}">Legenda</text>')
lx = MARGIN + 20
for kind, label in items:
    bg, st, _ = PAL[kind]
    dash = ' stroke-dasharray="5 4"' if kind == "missing" else ""
    out.append(f'<rect x="{lx}" y="{legend_y + 40}" width="20" height="15" rx="4" fill="{bg}" stroke="{st}" stroke-width="2"{dash}/>')
    out.append(f'<text x="{lx + 27}" y="{legend_y + 52}" font-size="12.5" fill="{MUTED}">{html.escape(label)}</text>')
    lx += 34 + tw(label, 12.5) + 28
out.append(f'<rect x="{MARGIN + 20}" y="{legend_y + 63}" width="88" height="17" rx="8.5" fill="{DET[1]}"/>')
out.append(f'<text x="{MARGIN + 26}" y="{legend_y + 75.5}" font-size="11" font-weight="700" fill="#ffffff">deterministico</text>')
out.append(f'<text x="{MARGIN + 116}" y="{legend_y + 75.5}" font-size="12.5" fill="{MUTED}">stesso input, stesso risultato</text>')
gx = MARGIN + 340
out.append(f'<rect x="{gx}" y="{legend_y + 63}" width="72" height="17" rx="8.5" fill="{GEN[1]}"/>')
out.append(f'<text x="{gx + 6}" y="{legend_y + 75.5}" font-size="11" font-weight="700" fill="#ffffff">generativo</text>')
out.append(f'<text x="{gx + 82}" y="{legend_y + 75.5}" font-size="12.5" fill="{MUTED}">passa da un modello AI: il risultato può variare</text>')
out.append('</svg>')

path = r"C:\Code\ResizeImage\docs\StockVectorStudio-Mappa-Funzionamento.svg"
with open(path, "w", encoding="utf-8") as fh:
    fh.write("\n".join(out))
print("scritto:", path)
print("dimensioni:", W, "x", H)