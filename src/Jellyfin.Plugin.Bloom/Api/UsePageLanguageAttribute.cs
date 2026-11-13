using System.Threading.Tasks;
using Jellyfin.Plugin.Bloom.Services;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.Bloom.Api;

/// <summary>
/// Answers the settings page in its own language (the one of the person using Jellyfin), which it sends in
/// <see cref="Text.LanguageHeader"/>. Without the header, texts follow the server's language.
/// </summary>
public sealed class UsePageLanguageAttribute : ActionFilterAttribute
{
    /// <inheritdoc />
    public override async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        // Set inside this async method: it applies to the action and everything it awaits, and to nothing else.
        Text.UseRequestLanguage(context.HttpContext.Request.Headers[Text.LanguageHeader].ToString());
        await next().ConfigureAwait(false);
    }
}
