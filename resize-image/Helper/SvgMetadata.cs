using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace MJ.Classifier.Helpers
{
    /// <summary>
    /// Writes title, description and keywords inside an SVG document.
    ///
    /// ExifTool reads SVG but cannot write it, so every vector left for the marketplaces with no
    /// title and no keywords: on a stock site a file nobody can search for is a file nobody buys.
    /// SVG carries its own metadata natively, so it is written into the document itself:
    /// &lt;title&gt; and &lt;desc&gt; for readers and assistive technology, and a Dublin Core
    /// packet inside &lt;metadata&gt; for the indexers, which is the same shape Illustrator emits.
    /// </summary>
    public static class SvgMetadata
    {
        private const string OpenMarker = "<!--svs:meta-->";
        private const string CloseMarker = "<!--/svs:meta-->";

        /// <summary>Matches the block written by a previous run, so re-sending never stacks copies.</summary>
        private static readonly Regex OwnBlock = new Regex(
            Regex.Escape(OpenMarker) + ".*?" + Regex.Escape(CloseMarker),
            RegexOptions.Singleline | RegexOptions.Compiled);

        /// <summary>Matches the placeholder element the vectorizer writes while generating the file.</summary>
        private static readonly Regex GeneratedMetadata = new Regex(
            "<metadata>.*?</metadata>",
            RegexOptions.Singleline | RegexOptions.Compiled);

        /// <summary>Matches the end of the opening svg tag, skipping over any quoted attribute value.</summary>
        private static readonly Regex SvgOpenTag = new Regex(
            "<svg\\b(?:[^>\"']|\"[^\"]*\"|'[^']*')*>",
            RegexOptions.Singleline | RegexOptions.Compiled);

        public static void Write(string filePath, string title, string description, string tags, ILogger log = null)
        {
            var svg = File.ReadAllText(filePath, Encoding.UTF8);
            var block = BuildBlock(title, description, tags);

            string updated;
            if (OwnBlock.IsMatch(svg))
            {
                updated = OwnBlock.Replace(svg, block.Replace("$", "$$"), 1);
            }
            else if (GeneratedMetadata.IsMatch(svg))
            {
                updated = GeneratedMetadata.Replace(svg, block.Replace("$", "$$"), 1);
            }
            else
            {
                var open = SvgOpenTag.Match(svg);
                if (!open.Success)
                {
                    log?.LogWarning($"{Path.GetFileName(filePath)} - opening svg tag not found, metadata not written");
                    return;
                }

                updated = svg.Insert(open.Index + open.Length, "\n" + block);
            }

            // No BOM: a byte order mark before the XML declaration makes strict SVG parsers refuse
            // the document.
            File.WriteAllText(filePath, updated, new UTF8Encoding(false));
            log?.LogInformation($"{Path.GetFileName(filePath)} - SVG metadata written ({title})");
        }

        private static string BuildBlock(string title, string description, string tags)
        {
            var keywords = (tags ?? string.Empty)
                .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim())
                .Where(t => t.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var sb = new StringBuilder();
            sb.Append(OpenMarker).Append('\n');
            sb.Append("<title>").Append(Escape(title)).Append("</title>\n");
            sb.Append("<desc>").Append(Escape(description)).Append("</desc>\n");
            sb.Append("<metadata>\n");
            sb.Append("<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\">\n");
            sb.Append("<rdf:Description rdf:about=\"\">\n");
            sb.Append("<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">").Append(Escape(title)).Append("</rdf:li></rdf:Alt></dc:title>\n");
            sb.Append("<dc:description><rdf:Alt><rdf:li xml:lang=\"x-default\">").Append(Escape(description)).Append("</rdf:li></rdf:Alt></dc:description>\n");
            sb.Append("<dc:subject><rdf:Bag>\n");
            foreach (var keyword in keywords)
                sb.Append("<rdf:li>").Append(Escape(keyword)).Append("</rdf:li>\n");
            sb.Append("</rdf:Bag></dc:subject>\n");
            sb.Append("</rdf:Description>\n");
            sb.Append("</rdf:RDF>\n");
            sb.Append("</metadata>\n");
            sb.Append(CloseMarker);
            return sb.ToString();
        }

        /// <summary>
        /// Escapes the five XML entities by hand: these values reach the file as raw text, and an
        /// ampersand or an angle bracket in a title would otherwise break the whole document.
        /// </summary>
        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&apos;");
        }
    }
}
