using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using XpressTest.Web.Services;

namespace XpressTest.Web;

/// <summary>
/// A back-end service answered 401 (the token was rejected, e.g. the account was deactivated
/// or keys rotated): sign the browser out and send the user to the sign-in page.
/// </summary>
public sealed class SessionExpiredFilter(ITempDataDictionaryFactory tempData) : IAsyncExceptionFilter
{
    public async Task OnExceptionAsync(ExceptionContext context)
    {
        if (context.Exception is not SessionExpiredException) return;

        var http = context.HttpContext;
        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        tempData.GetTempData(http)["Message"] = "Your session has ended. Please sign in again.";

        var returnUrl = http.Request.Method == HttpMethods.Get ? http.Request.Path + http.Request.QueryString : "/";
        context.Result = new RedirectToActionResult("Login", "Account", new { returnUrl });
        context.ExceptionHandled = true;
    }
}
