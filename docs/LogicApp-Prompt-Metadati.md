# Logic App di classificazione — prompt metadati

## Dove si trova (verificato)

| | |
|---|---|
| Tenant | Enel (`d539d4bf-…`) |
| Sottoscrizione | Sottoscrizione di Visual Studio Professional (`3d72e432-e4d9-471d-9be5-2b0d1d8485bb`) |
| Resource group | `rg-classifier` (West Europe) |
| Logic App | **`resize-image-to-classify-001`** |
| Modello | `gpt-5-mini` via `https://api.openai.com/v1/chat/completions` |

## La catena reale

Ricostruita ispezionando le cinque Logic App del resource group, non più per deduzione:

```
SharePoint ImagesToClassify (nuovo file)
      |  enqueu-image-la-001
coda image-to-classify
      |  Function ResizeImageToClassify   (progetto resize-image)
blob shrinked-images + coda shrinked-image-to-classify
      |  resize-image-to-classify-001     <- genera i metadati con OpenAI
SharePoint: Title, Tags, OData__ExtendedDescription, Stato
      |  invia-to-sftp                    (trigger: elemento modificato)
coda images-to-send
      |  Function UploadFileViaSFTP       (EXIF + upload marketplace)
      |  move-sent-files                  (workflow 69a3684f...d228, quello in appsettings.json)
SharePoint ImagesSent
```

`resizing-la-001` ha un trigger HTTP e non risulta nella catena.

## Contratto di uscita — da non rompere

Il modello restituisce **`keywords` come array**. È la Logic App a unirlo:

```
"Tags": "@{join(body('Parse_OpenAI_Content_Response')?['keywords'],',')}"
```

Quindi il prompt deve continuare a produrre esattamente queste tre chiavi:

| Chiave JSON   | Destinazione SharePoint      | Poi |
|---------------|------------------------------|-----|
| `title`       | `Title`                      | exiftool `-Title`, `XPTitle` |
| `description` | `OData__ExtendedDescription` | exiftool `-Description`, `-ImageDescription` |
| `keywords`    | `Tags` (unite con virgola)   | exiftool con `-sep ","` |

Se il modello restituisse `keywords` come stringa, `join()` fallirebbe. Se cambiassero i nomi
delle chiavi, `Parse_OpenAI_Content_Response` non troverebbe più nulla e il flusso finirebbe sul
ramo di errore, che scrive `COMPLETION: Errore nel prompting verso OpenAI` nello stato SharePoint.

## Cosa è stato corretto (22 agosto 2026)

Il prompt precedente rispettava 2 regole della guida Adobe su 11.

| Regola della guida | Prima | Ora |
|---|---|---|
| Titolo conciso, 70 caratteri ideali | NO — ammetteva 150 caratteri su 2 frasi | SI — 70 obiettivo, 100 massimo |
| Niente marchi o nomi propri nel titolo | NO | SI |
| Nomi al singolare (`cat`, non `cats`) | NO | SI |
| Aggettivi descrittivi, non soggettivi | NO | SI |
| Test del dizionario sui composti | parziale — solo razze animali | SI — regola Adobe completa |
| Verbi alla forma base | NO | SI |
| `vector` + `graphic` sui vettoriali | NO | SI |
| `illustration` + `art` + `graphic` | NO | SI |
| `no people` + `nobody` senza persone | NO | SI |
| Parole del titolo nelle prime 10 keyword | SI | SI |
| 15–35 keyword | SI | SI |

Conservate perché migliori della guida: ordinamento a cinque tier, penalità di genericità,
costruzione in due stadi, regola sulle location non inventate, contratto di uscita.

Rimosso il riferimento a "GPT-5.2" nel prompt: il modello realmente invocato è `gpt-5-mini`.

## Verifica prima del rilascio

Il nuovo prompt è stato provato sull'immagine reale `running_cat_star.jpg` chiamando
direttamente l'API OpenAI con lo stesso modello e la stessa modalità (`detail: low`):

- titolo di 63 caratteri (entro i 70)
- 21 keyword (nell'intervallo 15–35)
- `vector`, `graphic`, `no people`, `nobody` presenti
- nomi al singolare, verbo alla forma base (`sit`), nessun aggettivo soggettivo
- descrizione di 245 caratteri (oltre i 150 richiesti)
- punteggio `POST /api/jobs/validate`: **100/100, nessun rilievo**

## Come tornare indietro

Il backup integrale della definizione precedente è **fuori dal repository**, perché contiene
il Bearer token OpenAI in chiaro:

```
C:\Users\a473221\.scout\copilot\session-state\9c57b2a5-...\files\logicapp-classify-BACKUP.json
```

Ripristino:

```powershell
$sub='3d72e432-e4d9-471d-9be5-2b0d1d8485bb'; $rg='rg-classifier'
$la='resize-image-to-classify-001'
$tok = az account get-access-token --resource https://management.azure.com --query accessToken -o tsv
$wf = (Get-Content '<percorso del backup>' -Raw) | ConvertFrom-Json
$payload = @{ location=$wf.location; properties=@{
    definition=$wf.properties.definition; parameters=$wf.properties.parameters; state=$wf.properties.state
} } | ConvertTo-Json -Depth 60 -Compress
Invoke-RestMethod -Method Put -ContentType 'application/json; charset=utf-8' `
  -Uri "https://management.azure.com/subscriptions/$sub/resourceGroups/$rg/providers/Microsoft.Logic/workflows/$la`?api-version=2019-05-01" `
  -Headers @{ Authorization = "Bearer $tok" } -Body ([Text.Encoding]::UTF8.GetBytes($payload))
```

## Nota di sicurezza

La chiave OpenAI è salvata **in chiaro** nell'header `Authorization` dentro la definizione della
Logic App. Chiunque abbia accesso in lettura al resource group può leggerla, e finisce in ogni
backup della definizione. Andrebbe spostata in Key Vault e referenziata come parametro sicuro.
Non è stato modificato nulla in merito: è una segnalazione, non un intervento.

## Fonte

Adobe Stock, *Guide to Mastering Metadata*, v2, agosto 2021.
Pagina: <https://adobestock.adobe.com/Metadata-Field-Guide.html> (visualizzatore)
PDF: <https://adobestock.adobe.com/rs/269-YNG-601/images/Adobe-Stock-Metadata-Field-Guide-v2.pdf>
Copia locale: `docs/Adobe-Stock-Metadata-Field-Guide-v2.pdf` — regole generali p. 2, oggetti p. 15,
illustrazioni e vettoriali p. 48.
