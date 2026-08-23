/**
 * StockStudio — Illustrator trace bridge (ExtendScript / .jsx)
 *
 * Driven by a sidecar params.json placed next to this script. Reproduces the manual workflow:
 *   open JPEG → (optional +200% scale) → Image Trace "B&N Silhouette Auto Group" → Expand
 *   → save .ai + .eps + .jpg with the same base name.
 *
 * For EXACT fidelity to the custom preset, record an Illustrator Action that runs
 * Object ▸ Image Trace ▸ [B&N Silhouette Auto Group] then Object ▸ Expand, and pass its
 * actionSet/actionName in params.json. Without an Action, a black-&-white tracing fallback is used.
 *
 * Writes result.json ({ ok, error? }) next to the script so the host can detect success.
 */
#target illustrator
(function () {
    function scriptDir() { return $.fileName.replace(/[^\/\\]*$/, ""); }

    function readParams() {
        var f = File(scriptDir() + "params.json");
        f.encoding = "UTF-8";
        f.open("r");
        var s = f.read();
        f.close();
        return eval("(" + s + ")");
    }

    function writeResult(obj) {
        var f = File(scriptDir() + "result.json");
        f.encoding = "UTF-8";
        f.open("w");
        f.write(obj);
        f.close();
    }

    try {
        var p = readParams();
        var doc = app.open(File(p.input));

        if (p.actionSet && p.actionName) {
            // Exact user preset via a recorded Action (recommended).
            app.doScript(p.actionName, p.actionSet);
        } else {
            // Fallback: trace the first raster/placed item as a B&W silhouette.
            var item = doc.pageItems.length ? doc.pageItems[0] : null;
            if (item && (item.typename === "PlacedItem" || item.typename === "RasterItem")) {
                var traced = item.trace();
                var o = traced.tracing.tracingOptions;
                o.tracingMode = TracingModeType.TRACINGMODEBLACKANDWHITE;
                o.threshold = p.threshold || 128;
                o.ignoreWhite = true;
                traced.tracing.expandTracing();
            }
        }

        if (p.scalePercent && p.scalePercent !== 100) {
            for (var i = 0; i < doc.pageItems.length; i++) {
                doc.pageItems[i].resize(p.scalePercent, p.scalePercent);
            }
        }

        var base = p.output.replace(/[\/\\]$/, "") + "/" + p.baseName;

        var ai = new IllustratorSaveOptions();
        ai.compatibility = Compatibility.ILLUSTRATOR17;
        doc.saveAs(File(base + ".ai"), ai);

        var eps = new EPSSaveOptions();
        eps.compatibility = Compatibility.ILLUSTRATOR17;
        eps.embedAllFonts = true;
        eps.cmykPostScript = true;
        doc.saveAs(File(base + ".eps"), eps);

        var jpg = new ExportOptionsJPEG();
        jpg.qualitySetting = p.jpegQuality || 90;
        jpg.artBoardClipping = true;
        doc.exportFile(File(base + ".jpg"), ExportType.JPEG, jpg);

        doc.close(SaveOptions.DONOTSAVECHANGES);
        writeResult('{"ok":true}');
    } catch (e) {
        writeResult('{"ok":false,"error":"' + e.toString().replace(/"/g, "'") + '"}');
    }
})();
