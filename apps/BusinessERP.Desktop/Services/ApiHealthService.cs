using System.Net;

namespace BusinessERP.Desktop.Services;

public sealed record ApiHealthResult(bool IsOnline, string Message, string Detail);

public sealed class ApiHealthService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly Uri _baseUri;

    public ApiHealthService(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("ApiBaseUrl must be an absolute HTTP or HTTPS URL.", nameof(baseUrl));
        }

        _baseUri = uri;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(4)
        };
    }

    public async Task<ApiHealthResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var endpoint = new Uri(_baseUri, "/health/live");
            using var response = await _httpClient.GetAsync(endpoint, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return new ApiHealthResult(true, "الخادم يستجيب", $"HTTP {(int)response.StatusCode}");
            }

            return new ApiHealthResult(false, "الخادم ردّ بحالة غير ناجحة", $"HTTP {(int)response.StatusCode} ({response.StatusCode})");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ApiHealthResult(false, "انتهت مهلة الاتصال بالخادم", "Timeout");
        }
        catch (HttpRequestException ex)
        {
            // Deliberately keep host exception details out of the UI because they can disclose internals.
            var detail = ex.StatusCode is HttpStatusCode code ? $"HTTP {(int)code}" : "تعذر إنشاء الاتصال";
            return new ApiHealthResult(false, "لا يوجد اتصال بالخادم", detail);
        }
    }

    public void Dispose() => _httpClient.Dispose();
}
