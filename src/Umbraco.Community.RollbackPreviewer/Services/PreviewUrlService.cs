using System;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Umbraco.Community.RollbackPreviewer.Configuration;

namespace Umbraco.Community.RollbackPreviewer.Services
{
    public interface IPreviewUrlService
    {
        /// <summary>
        /// Gets the absolute preview URL for a given content node and version.
        /// Appends a secret query parameter when frontend preview authorisation is enabled.
        /// </summary>
        /// <param name="contentKey">The unique key (GUID) of the content node.</param>
        /// <param name="versionKey">The unique key (GUID) of the content version.</param>
        /// <param name="culture">Optional culture for multilingual content.</param>
        /// <returns>The absolute preview URL, or null if it cannot be determined.</returns>
        string? GetPreviewUrl(Guid contentKey, Guid versionKey, string? culture = null);
    }

    public class PreviewUrlService : IPreviewUrlService
    {
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly RollbackPreviewerOptions _options;
        private readonly ITimeLimitedSecretService _timeLimitedSecretService;

        public PreviewUrlService(
            IHttpContextAccessor httpContextAccessor,
            IOptions<RollbackPreviewerOptions> options,
            ITimeLimitedSecretService timeLimitedSecretService)
        {
            _httpContextAccessor = httpContextAccessor;
            _options = options.Value;
            _timeLimitedSecretService = timeLimitedSecretService;
        }

        /// <inheritdoc />
        public string? GetPreviewUrl(Guid contentKey, Guid versionKey, string? culture = null)
        {
            var request = _httpContextAccessor.HttpContext?.Request;
            if (request == null)
                return null;

            var baseUrl = $"{request.Scheme}://{request.Host}";
            var url = $"{baseUrl}/ucrbp?cid={contentKey}&vid={versionKey}";

            if (!string.IsNullOrWhiteSpace(culture))
                url += $"&culture={Uri.EscapeDataString(culture)}";

            if (_options.EnableFrontendPreviewAuthorisation)
            {
                string? secret = null;

                if (_options.EnableTimeLimitedSecrets)
                    secret = _timeLimitedSecretService.GenerateSecret();
                else if (!string.IsNullOrWhiteSpace(_options.FrontendPreviewAuthorisationSecret))
                    secret = _options.FrontendPreviewAuthorisationSecret;

                if (!string.IsNullOrWhiteSpace(secret))
                    url += $"&secret={Uri.EscapeDataString(secret)}";
            }

            return url;
        }
    }
}
