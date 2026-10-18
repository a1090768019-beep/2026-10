using Duende.IdentityModel.OidcClient.Browser;
using System.Diagnostics;
using System.Net;
using System.Text;

namespace BusinessERP.Desktop.Services;

/// <summary>
/// Uses the operating-system browser and a loopback-only HTTP callback, as recommended for native
/// OAuth/OIDC apps. The library validates state, PKCE, issuer, audience and the ID token.
/// </summary>
public sealed class LoopbackSystemBrowser(Uri callbackUri) : IBrowser
{
    private readonly Uri _callbackUri = callbackUri;

    public async Task<BrowserResult> InvokeAsync(BrowserOptions options, CancellationToken cancellationToken = default)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add(_callbackUri.AbsoluteUri);

        try
        {
            listener.Start();
            Process.Start(new ProcessStartInfo(options.StartUrl) { UseShellExecute = true });

            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().WaitAsync(options.Timeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                return new BrowserResult
                {
                    ResultType = BrowserResultType.Timeout,
                    Error = "Timed out waiting for the identity provider callback."
                };
            }
            catch (OperationCanceledException)
            {
                return new BrowserResult
                {
                    ResultType = BrowserResultType.UserCancel,
                    Error = "Sign-in was cancelled."
                };
            }

            using (context.Response)
            {
                var url = context.Request.Url;
                if (!string.Equals(context.Request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase) ||
                    url is null ||
                    !string.Equals(url.AbsolutePath, _callbackUri.AbsolutePath, StringComparison.Ordinal) ||
                    !string.Equals(url.Host, _callbackUri.Host, StringComparison.OrdinalIgnoreCase) ||
                    url.Port != _callbackUri.Port)
                {
                    context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                    await WriteResponseAsync(context.Response, "Invalid sign-in callback.", cancellationToken);
                    return new BrowserResult
                    {
                        ResultType = BrowserResultType.HttpError,
                        Error = "The identity provider returned to an unexpected callback URL."
                    };
                }

                var query = url.Query;
                if (string.IsNullOrWhiteSpace(query))
                {
                    context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                    await WriteResponseAsync(context.Response, "Missing sign-in response.", cancellationToken);
                    return new BrowserResult
                    {
                        ResultType = BrowserResultType.HttpError,
                        Error = "The identity provider callback did not include a response."
                    };
                }

                context.Response.StatusCode = (int)HttpStatusCode.OK;
                await WriteResponseAsync(
                    context.Response,
                    "تم تسجيل الدخول أو معالجة استجابة الهوية. يمكنك العودة إلى Business ERP.",
                    cancellationToken);

                // OidcClient expects the query string, including OAuth error responses, and then
                // validates state/PKCE and the tokens itself.
                return new BrowserResult { ResultType = BrowserResultType.Success, Response = query };
            }
        }
        catch (HttpListenerException)
        {
            return new BrowserResult
            {
                ResultType = BrowserResultType.UnknownError,
                Error = "تعذر فتح منفذ رد تسجيل الدخول. تحقّق من أن Oidc:RedirectUri يستخدم منفذًا غير مشغول."
            };
        }
        catch (InvalidOperationException)
        {
            return new BrowserResult
            {
                ResultType = BrowserResultType.UnknownError,
                Error = "تعذر بدء متصفح النظام لتسجيل الدخول."
            };
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new BrowserResult
            {
                ResultType = BrowserResultType.UnknownError,
                Error = "تعذر فتح متصفح النظام. افتح المتصفح الافتراضي ثم أعد المحاولة."
            };
        }
    }

    private static async Task WriteResponseAsync(
        HttpListenerResponse response, string message, CancellationToken cancellationToken)
    {
        response.ContentType = "text/html; charset=utf-8";
        response.Headers[HttpResponseHeader.CacheControl] = "no-store";
        var safeMessage = System.Net.WebUtility.HtmlEncode(message);
        var html = $"<!doctype html><html lang=\"ar\"><meta charset=\"utf-8\"><body dir=\"rtl\" style=\"font-family:Segoe UI,Arial;padding:3rem\"><h2>{safeMessage}</h2><p>أغلق هذه النافذة بعد العودة إلى التطبيق.</p></body></html>";
        var bytes = Encoding.UTF8.GetBytes(html);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, cancellationToken);
    }
}
