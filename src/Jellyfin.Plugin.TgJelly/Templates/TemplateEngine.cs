using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Scriban;
using Scriban.Parsing;
using Scriban.Runtime;
using Scriban.Syntax;

namespace Jellyfin.Plugin.TgJelly.Templates;

/// <summary>
/// Parses and renders user templates with Scriban under the spec's limits (spec §8.5, F41).
/// </summary>
public sealed class TemplateEngine
{
    private const int CacheSize = 200;
    private const int Limit = 1000;
    private const int Depth = 20;
    private const int TextLimit = 16384;

    // How Scriban words the failures of the output limit and of the string limit (a value or a joined string too long).
    private const string OutputLimitMessage = "Rendered output exceeds OutputLimit";
    private const string StringLimitWord = "LimitToString";

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    private readonly ConcurrentDictionary<(string EventKey, string Hash), Template> _cache = new();
    private readonly TimeProvider _time;
    private readonly TimeSpan _timeout;
    private readonly Func<string, Template> _parse;

    /// <summary>
    /// Initializes a new instance of the <see cref="TemplateEngine"/> class.
    /// </summary>
    /// <param name="time">The clock.</param>
    public TemplateEngine(TimeProvider time)
        : this(time, DefaultTimeout)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TemplateEngine"/> class with a render timeout.
    /// </summary>
    /// <param name="time">The clock.</param>
    /// <param name="timeout">How long one render may run.</param>
    internal TemplateEngine(TimeProvider time, TimeSpan timeout)
        : this(time, timeout, text => Template.Parse(text))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TemplateEngine"/> class with a render timeout and a parser (tests).
    /// </summary>
    /// <param name="time">The clock.</param>
    /// <param name="timeout">How long one render may run.</param>
    /// <param name="parse">Parses template text.</param>
    internal TemplateEngine(TimeProvider time, TimeSpan timeout, Func<string, Template> parse)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(parse);
        _time = time;
        _timeout = timeout;
        _parse = parse;
    }

    /// <summary>Gets the clock the render timeouts run on.</summary>
    internal TimeProvider Time => _time;

    /// <summary>Gets the number of cached parsed templates.</summary>
    internal int CachedCount => _cache.Count;

    /// <summary>
    /// Parses template text. Never throws.
    /// </summary>
    /// <param name="text">The template text.</param>
    /// <returns>Every parse problem, 1-based; empty when the text parses.</returns>
    public IReadOnlyList<TemplateError> Parse(string text)
    {
        try
        {
            var template = _parse(text ?? string.Empty);
            var errors = new List<TemplateError>();
            foreach (var message in template.Messages)
            {
                errors.Add(ToError(message.Message, message.Span));
            }

            return errors;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return [new TemplateError(ex.Message, null, null)];
        }
    }

    /// <summary>
    /// Renders a template. Never throws.
    /// </summary>
    /// <param name="eventKey">The event the template belongs to (a cache key part).</param>
    /// <param name="text">The template text.</param>
    /// <param name="model">The flat template model.</param>
    /// <param name="strict">True to fail on unknown variables (validation renders).</param>
    /// <param name="language">The language <c>plural</c> uses.</param>
    /// <returns>The output, or the error.</returns>
    public TemplateRenderResult Render(string eventKey, string text, IReadOnlyDictionary<string, object?> model, bool strict, string language = "en")
    {
        try
        {
            var template = GetTemplate(eventKey, text ?? string.Empty);
            if (template.HasErrors)
            {
                var first = template.Messages[0];
                return new TemplateRenderResult(null, ToError(first.Message, first.Span));
            }

            return Run(template, model, strict, language);
        }
        catch (ScriptRuntimeException ex)
        {
            var tooLong = ex.OriginalMessage.StartsWith(OutputLimitMessage, StringComparison.Ordinal)
                || ex.OriginalMessage.Contains(StringLimitWord, StringComparison.Ordinal);
            return new TemplateRenderResult(null, ToError(ex.OriginalMessage, ex.Span), tooLong);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new TemplateRenderResult(null, new TemplateError(ex.Message, null, null));
        }
    }

    /// <summary>
    /// The template's top-level pieces (design §6.5): each run of literal text, each <c>{{ … }}</c> expression and each
    /// whole block, with its place in <paramref name="text"/>. Never throws.
    /// </summary>
    /// <param name="text">The template text.</param>
    /// <returns>The pieces in order; empty when the text does not parse.</returns>
    public IReadOnlyList<TemplatePiece> Pieces(string text)
    {
        try
        {
            var template = _parse(text ?? string.Empty);
            return template.HasErrors ? [] : [.. TopLevel(template).Select((p, i) => new TemplatePiece(i, p.Start, p.End - p.Start))];
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return [];
        }
    }

    /// <summary>
    /// Renders a template for the dashboard's preview only, with each top-level piece's output between invisible
    /// markers (<see cref="TemplateMarkers"/>). Once the markers are stripped, the output is exactly what
    /// <see cref="Render"/> gives without <c>strict</c>: the markers are statements of their own, added around the
    /// parsed pieces, so whitespace trimming, blocks and loops render as they do unmarked. Never throws.
    /// </summary>
    /// <param name="text">The template text.</param>
    /// <param name="model">The flat template model.</param>
    /// <param name="language">The language <c>plural</c> uses.</param>
    /// <returns>The marked output, or the error.</returns>
    public TemplateRenderResult RenderMarked(string text, IReadOnlyDictionary<string, object?> model, string language = "en")
    {
        try
        {
            // A parse of its own: the markers change the statements, and the cached template must stay as parsed.
            var template = _parse(text ?? string.Empty);
            if (template.HasErrors)
            {
                var first = template.Messages[0];
                return new TemplateRenderResult(null, ToError(first.Message, first.Span));
            }

            // Pieces exist only when the page has statements (TopLevel), so the loop never runs without them.
            var pieces = TopLevel(template);
            for (var i = pieces.Count - 1; i >= 0; i--)
            {
                var statements = template.Page!.Body!.Statements;
                var at = statements.IndexOf(pieces[i].Statement);
                statements.Insert(at + 1, Marker(TemplateMarkers.End.ToString()));
                statements.Insert(at, Marker(string.Create(CultureInfo.InvariantCulture, $"{TemplateMarkers.Start}{i}{TemplateMarkers.IndexEnd}")));
            }

            return Run(template, model, strict: false, language);
        }
        catch (ScriptRuntimeException ex)
        {
            return new TemplateRenderResult(null, ToError(ex.OriginalMessage, ex.Span));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new TemplateRenderResult(null, new TemplateError(ex.Message, null, null));
        }
    }

    private static ScriptRawStatement Marker(string text) => new() { Text = new ScriptStringSlice(text) };

    // The top-level statements that are pieces, each with its source range: an expression or block takes in the {{ and
    // }} around it, literal text is its own (already trimmed) span. The {{ and }} themselves are no pieces.
    private static List<(ScriptStatement Statement, int Start, int End)> TopLevel(Template template)
    {
        var result = new List<(ScriptStatement, int, int)>();
        if (template.Page?.Body?.Statements is not { } statements)
        {
            return result;
        }

        for (var i = 0; i < statements.Count; i++)
        {
            var statement = statements[i];
            if (statement is ScriptEscapeStatement || (statement is ScriptRawStatement raw && raw.Text.Length == 0))
            {
                continue;
            }

            var start = statement.Span.Start.Offset;
            var end = statement.Span.End.Offset + 1;
            if (statement is not ScriptRawStatement)
            {
                if (i > 0 && statements[i - 1] is ScriptEscapeStatement open)
                {
                    start = open.Span.Start.Offset;
                }

                if (i + 1 < statements.Count && statements[i + 1] is ScriptEscapeStatement close)
                {
                    end = close.Span.End.Offset + 1;
                }
            }

            result.Add((statement, start, Math.Max(start, end)));
        }

        return result;
    }

    // Renders a parsed template under the limits, with the model and the raw and plural functions. Throws as Scriban does.
    private TemplateRenderResult Run(Template template, IReadOnlyDictionary<string, object?> model, bool strict, string language)
    {
        using var cts = new CancellationTokenSource(_timeout, _time);
        var context = new TelegramTemplateContext
        {
            LoopLimit = Limit,
            RecursiveLimit = Depth,
            ObjectRecursionLimit = Depth,
            LimitToString = TextLimit,
            OnStringLimit = ScriptLimitBehavior.Throw,
            OutputLimit = TextLimit,
            OnOutputLimit = ScriptLimitBehavior.Throw,
            RegexTimeOut = TimeSpan.FromSeconds(1),
            StrictVariables = strict,
            EnableRelaxedMemberAccess = !strict,
            CancellationToken = cts.Token,
        };

        var globals = new ScriptObject();
        foreach (var (key, value) in model)
        {
            globals[key] = ToScript(value);
        }

        // Imported last, so a model key named raw can never replace the function.
        globals.Import("raw", new Func<object?, RawHtml>(value => new RawHtml(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty)));

        // Same for plural.
        globals.Import("plural", new Func<object?, object?[], string>((count, forms) =>
            PluralRules.Pick(language, Convert.ToInt64(count, CultureInfo.InvariantCulture), forms.Select(f => Convert.ToString(f, CultureInfo.InvariantCulture) ?? string.Empty).ToList())));

        context.PushGlobal(globals);
        return new TemplateRenderResult(template.Render(context), null);
    }

    private static TemplateError ToError(string message, SourceSpan span) =>
        span.Start.Line < 0 && span.Start.Column < 0
            ? new TemplateError(message, null, null)
            : new TemplateError(message, span.Start.Line + 1, span.Start.Column + 1);

    private static object? ToScript(object? value)
    {
        switch (value)
        {
            case IReadOnlyDictionary<string, object?> dictionary:
                var obj = new ScriptObject();
                foreach (var (key, item) in dictionary)
                {
                    obj[key] = ToScript(item);
                }

                return obj;
            case IReadOnlyList<object?> list:
                var array = new ScriptArray();
                foreach (var item in list)
                {
                    array.Add(ToScript(item));
                }

                return array;
            default:
                return value;
        }
    }

    private Template GetTemplate(string eventKey, string text)
    {
        var key = (eventKey, TemplateText.Hash(text));
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var template = _parse(text);
        if (_cache.Count >= CacheSize)
        {
            _cache.Clear();
        }

        _cache[key] = template;
        return template;
    }
}
