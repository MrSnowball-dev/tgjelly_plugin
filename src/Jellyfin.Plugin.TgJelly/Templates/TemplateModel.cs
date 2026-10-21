using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.TgJelly.Templates;

/// <summary>
/// A template model together with the parts the fitter may shorten: one text field and one list.
/// </summary>
public sealed class TemplateModel
{
    private readonly Func<IReadOnlyDictionary<string, object?>, string?, int, IReadOnlyDictionary<string, object?>> _with;

    private TemplateModel(
        IReadOnlyDictionary<string, object?> values,
        string? shrinkText,
        int listCount,
        int minListKept,
        Func<IReadOnlyDictionary<string, object?>, string?, int, IReadOnlyDictionary<string, object?>> with)
    {
        Values = values;
        ShrinkText = shrinkText;
        ListCount = listCount;
        MinListKept = minListKept;
        _with = with;
    }

    /// <summary>Gets the full model.</summary>
    public IReadOnlyDictionary<string, object?> Values { get; }

    /// <summary>Gets the full value of the shrinkable text field, or null.</summary>
    public string? ShrinkText { get; }

    /// <summary>Gets the item count of the shrinkable list; 0 when there is none.</summary>
    public int ListCount { get; }

    /// <summary>Gets the fewest list items the fitter keeps: 1, or 0 for the digest, which v0.2 could empty.</summary>
    public int MinListKept { get; }

    /// <summary>
    /// The generic rule: the text field is a top-level string, the list field a top-level array.
    /// </summary>
    /// <param name="values">The model.</param>
    /// <param name="textField">The shrinkable text field, or null.</param>
    /// <param name="listField">The shrinkable list field, or null.</param>
    /// <param name="moreField">The field that receives the number of removed list items, or null.</param>
    /// <returns>A model that shortens by field name.</returns>
    public static TemplateModel Of(IReadOnlyDictionary<string, object?> values, string? textField, string? listField, string? moreField)
    {
        ArgumentNullException.ThrowIfNull(values);
        var text = textField is not null && values.TryGetValue(textField, out var t) && t is string s && s.Length > 0 ? s : null;
        IReadOnlyList<object?>? list = listField is not null && values.TryGetValue(listField, out var l) ? l as IReadOnlyList<object?> : null;
        return new TemplateModel(
            values,
            text,
            list?.Count ?? 0,
            1,
            (source, newText, kept) =>
            {
                var copy = new Dictionary<string, object?>(source);
                if (textField is not null)
                {
                    copy[textField] = newText;
                }

                if (listField is not null && list is not null)
                {
                    var keep = Math.Clamp(kept, 0, list.Count);
                    copy[listField] = list.Take(keep).ToList();
                    if (moreField is not null)
                    {
                        copy[moreField] = (long)(list.Count - keep);
                    }
                }

                return copy;
            });
    }

    /// <summary>
    /// A model with its own rule for shortening (the digest).
    /// </summary>
    /// <param name="values">The full model.</param>
    /// <param name="textField">The field holding the shrinkable text, or null.</param>
    /// <param name="listCount">The item count of the shrinkable list.</param>
    /// <param name="with">Builds a copy from the full model, the text and the number of list items to keep.</param>
    /// <param name="minListKept">The fewest list items the fitter keeps.</param>
    /// <returns>The model.</returns>
    public static TemplateModel Custom(
        IReadOnlyDictionary<string, object?> values,
        string? textField,
        int listCount,
        Func<IReadOnlyDictionary<string, object?>, string?, int, IReadOnlyDictionary<string, object?>> with,
        int minListKept = 1)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(with);
        var text = textField is not null && values.TryGetValue(textField, out var t) && t is string s && s.Length > 0 ? s : null;
        return new TemplateModel(values, text, listCount, Math.Max(minListKept, 0), with);
    }

    /// <summary>
    /// Builds a copy with a shortened text and list. The source is never changed.
    /// </summary>
    /// <param name="text">The text field's value; null sets it to null (the key stays).</param>
    /// <param name="listKept">How many list items to keep.</param>
    /// <returns>The new model.</returns>
    public IReadOnlyDictionary<string, object?> With(string? text, int listKept) => _with(Values, text, listKept);
}
