using System.Text;

namespace CarRental.Web.Components.Emails;

/// <summary>
/// What an e-mail to a customer says, already localized: rendered to HTML by <see cref="CustomerEmailTemplate"/> and to
/// plain text by <see cref="ToText"/> (same content, same order).
/// </summary>
/// <param name="Language">Language of the e-mail (<c>fr</c>, <c>en</c>): <c>lang</c> of the document.</param>
/// <param name="Subject">Subject line (the brand first).</param>
/// <param name="Preheader">Short summary shown by mail clients next to the subject.</param>
/// <param name="Heading">Title of the message.</param>
/// <param name="Greeting">"Bonjour …,".</param>
/// <param name="Paragraphs">The body.</param>
/// <param name="Facts">Label and value pairs (number, amounts, deadlines).</param>
/// <param name="CodeLabel">Label of the one-time code, when there is one.</param>
/// <param name="Code">The one-time code.</param>
/// <param name="Action">The button of the message, when there is one.</param>
/// <param name="Note">A last remark (validity of a link, what to do if not expected).</param>
/// <param name="Signature">"L'équipe …".</param>
/// <param name="Footer">Why this e-mail was sent.</param>
public sealed record CustomerEmailContent(
    string Language,
    string Subject,
    string Preheader,
    string Heading,
    string Greeting,
    IReadOnlyList<string> Paragraphs,
    IReadOnlyList<EmailFact> Facts,
    string? CodeLabel,
    string? Code,
    EmailAction? Action,
    string? Note,
    string Signature,
    string Footer)
{
    /// <summary>The colon of a label in the language of the e-mail (spaced in French).</summary>
    public string Separator => Language == "fr" ? " : " : ": ";

    /// <summary>The plain text alternative of the e-mail.</summary>
    public string ToText()
    {
        var text = new StringBuilder();
        var colon = Separator;
        text.Append(Greeting).Append("\n\n").Append(Heading).Append("\n\n");
        foreach (var paragraph in Paragraphs)
        {
            text.Append(paragraph).Append("\n\n");
        }

        foreach (var fact in Facts)
        {
            text.Append(fact.Label).Append(colon).Append(fact.Value).Append('\n');
        }

        if (Facts.Count > 0)
        {
            text.Append('\n');
        }

        if (Code is not null)
        {
            text.Append(CodeLabel).Append(colon).Append(Code).Append("\n\n");
        }

        if (Action is not null)
        {
            text.Append(Action.Label).Append(colon.TrimEnd()).Append('\n').Append(Action.Url).Append("\n\n");
        }

        if (Note is not null)
        {
            text.Append(Note).Append("\n\n");
        }

        text.Append(Signature).Append("\n\n-- \n").Append(Footer).Append('\n');
        return text.ToString();
    }
}

/// <summary>A label and its value in an e-mail.</summary>
public sealed record EmailFact(string Label, string Value);

/// <summary>The button of an e-mail: its text and its absolute address.</summary>
public sealed record EmailAction(string Label, string Url);
