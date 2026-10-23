using System.Net;
using System.Text;

namespace SharpEngine.Editor;

/// <summary>Builds the availability part of editor buttons. The native UI dispatcher emits an
/// element's <c>data-action</c> regardless of its <c>disabled</c> attribute, so availability and
/// dispatchability have to be written together: a disabled control must not carry an action at
/// all, otherwise a greyed menu entry or toolbar button still requests its command and the
/// registry answers with a misleading "unavailable" status.</summary>
internal static class EditorActionButton
{
    /// <summary>Closes the caller's class attribute and marks the control disabled when it is
    /// not available. Use together with <see cref="AppendAction"/>.</summary>
    internal static StringBuilder AppendAvailability(this StringBuilder Markup, bool Enabled) =>
        Markup.Append(Enabled ? "\"" : " disabled\" disabled=\"true\"");

    /// <summary>Writes the action attribute only for an available control, so an unavailable
    /// control has nothing to dispatch.</summary>
    internal static StringBuilder AppendAction(this StringBuilder Markup, string Action,
        bool Enabled)
    {
        if (!Enabled)
        {
            return Markup;
        }
        return Markup.Append(" data-action=\"")
            .Append(WebUtility.HtmlEncode(Action)).Append('"');
    }
}
