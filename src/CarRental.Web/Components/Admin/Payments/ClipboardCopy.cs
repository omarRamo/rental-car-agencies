using Microsoft.JSInterop;

namespace CarRental.Web.Components.Admin.Payments;

/// <summary>Copies a text with the clipboard module of the back office (<c>wwwroot/js/clipboard.js</c>).</summary>
internal static class ClipboardCopy
{
    /// <summary>False when the browser refuses or the circuit is gone: the user copies the text by hand.</summary>
    public static async Task<bool> TryAsync(IJSRuntime js, string text)
    {
        ArgumentNullException.ThrowIfNull(js);
        try
        {
            await using var module = await js.InvokeAsync<IJSObjectReference>("import", "./js/clipboard.js");
            return await module.InvokeAsync<bool>("copyText", text);
        }
        catch (JSException)
        {
            return false;
        }
        catch (JSDisconnectedException)
        {
            return false;
        }
    }
}
