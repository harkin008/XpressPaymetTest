using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using XpressTest.Web.Models;

namespace XpressTest.Web.Controllers;

[AllowAnonymous]
public class HomeController : Controller
{
    public IActionResult Index() => View();

    /// <summary>
    /// Target of the exception handler and status-code pages. Re-executed with the original method,
    /// so it accepts any verb and skips antiforgery (a failed check is itself one of the errors shown).
    /// </summary>
    [Route("error/{statusCode:int?}")]
    [IgnoreAntiforgeryToken]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error(int? statusCode)
    {
        var model = new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier };

        if (HttpContext.Features.Get<IExceptionHandlerFeature>()?.Error is HttpRequestException or TaskCanceledException)
        {
            // A back-end service is down or timed out.
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            model.Title = "Service unavailable";
            model.Message = "We couldn't reach one of our services. Please try again in a moment.";
        }
        else if (statusCode is { } code)
        {
            (model.Title, model.Message) = code switch
            {
                404 => ("Page not found", "The page you were looking for doesn't exist or has moved."),
                400 => ("Bad request", "Your request couldn't be processed. Refresh the page and try again."),
                403 => ("Access denied", "You don't have permission to view this page."),
                503 => ("Service unavailable", "The site is temporarily unavailable. Please try again in a moment."),
                _ => (model.Title, model.Message)
            };
        }

        model.StatusCode = Response.StatusCode;
        return View(model);
    }
}
