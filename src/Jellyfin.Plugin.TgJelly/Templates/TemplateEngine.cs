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
        catch (ScriptRuntimeException ex)
        {
            return new TemplateRenderResult(null, ToError(ex.OriginalMessage, ex.Span));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new TemplateRenderResult(null, new TemplateError(ex.Message, null, null));
        }
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
