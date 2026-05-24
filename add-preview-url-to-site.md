# Plan: Use IPreviewUrlService in an Umbraco Site

## Context

This site references the `Umbraco.Community.RollbackPreviewer` NuGet package. That package provides a content finder that intercepts requests to `/ucrbp` and renders a historical version of a content node for preview purposes.

The package exposes a service called `IPreviewUrlService` that generates the correct preview URL given a content node GUID and a version GUID. This plan describes how to inject and use that service anywhere in the site.

## What the package registers automatically

When the package is installed, the following are available in the DI container without any extra setup:

- `IPreviewUrlService` — generates preview URLs
- `ITimeLimitedSecretService` — generates and validates time-limited secrets
- `IOptions<RollbackPreviewerOptions>` — exposes the package configuration from `appsettings.json`

## URL format produced

```
https://yoursite.com/ucrbp?cid={contentGuid}&vid={versionGuid}&culture={culture}&secret={secret}
```

- `culture` is only appended when provided and non-empty
- `secret` is only appended when `EnableFrontendPreviewAuthorisation` is `true` in config

## appsettings.json configuration (optional)

If you want the generated URL to include a secret so it can be shared with unauthenticated users, add the following section. Without it, the preview URL only works for logged-in back-office users.

```json
"RollbackPreviewer": {
  "EnableFrontendPreviewAuthorisation": true,
  "EnableTimeLimitedSecrets": true,
  "SecretExpirationMinutes": 60
}
```

Or with a static secret instead of time-limited:

```json
"RollbackPreviewer": {
  "EnableFrontendPreviewAuthorisation": true,
  "EnableTimeLimitedSecrets": false,
  "FrontendPreviewAuthorisationSecret": "your-secret-here"
}
```

## Task: inject IPreviewUrlService and call GetPreviewUrl

Find the place in this site where you need to generate a rollback preview URL (e.g. a surface controller, API controller, notification handler, or view component) and inject `IPreviewUrlService` from `Umbraco.Community.RollbackPreviewer.Services`.

### Method signature

```csharp
string? GetPreviewUrl(Guid contentKey, Guid versionKey, string? culture = null)
```

- `contentKey` — the `Key` (GUID) of the content node, e.g. `content.Key`
- `versionKey` — the GUID of the specific content version you want to preview
- `culture` — optional, e.g. `"en-US"`. Pass `null` or omit for invariant content
- Returns `null` if called outside an HTTP request context

### Example: surface controller

```csharp
using Umbraco.Community.RollbackPreviewer.Services;
using Umbraco.Cms.Web.Website.Controllers;

public class PreviewController : SurfaceController
{
    private readonly IPreviewUrlService _previewUrlService;

    public PreviewController(
        IUmbracoContextAccessor umbracoContextAccessor,
        IUmbracoDatabaseFactory databaseFactory,
        ServiceContext services,
        AppCaches appCaches,
        IProfilingLogger profilingLogger,
        IPublishedUrlProvider publishedUrlProvider,
        IPreviewUrlService previewUrlService)
        : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
    {
        _previewUrlService = previewUrlService;
    }

    public IActionResult GetPreviewUrl(Guid contentKey, Guid versionKey, string? culture = null)
    {
        var url = _previewUrlService.GetPreviewUrl(contentKey, versionKey, culture);
        if (url == null)
            return BadRequest("Could not generate preview URL.");

        return Ok(url);
    }
}
```

### Example: minimal API or management API controller

```csharp
using Umbraco.Community.RollbackPreviewer.Services;

public class MyController : Controller
{
    private readonly IPreviewUrlService _previewUrlService;

    public MyController(IPreviewUrlService previewUrlService)
    {
        _previewUrlService = previewUrlService;
    }

    public string? BuildPreviewUrl(Guid contentKey, Guid versionKey, string? culture = null)
    {
        return _previewUrlService.GetPreviewUrl(contentKey, versionKey, culture);
    }
}
```

### Example: notification handler

```csharp
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Community.RollbackPreviewer.Services;

public class ContentPublishedHandler : INotificationHandler<ContentPublishedNotification>
{
    private readonly IPreviewUrlService _previewUrlService;

    public ContentPublishedHandler(IPreviewUrlService previewUrlService)
    {
        _previewUrlService = previewUrlService;
    }

    public void Handle(ContentPublishedNotification notification)
    {
        foreach (var content in notification.PublishedEntities)
        {
            var url = _previewUrlService.GetPreviewUrl(content.Key, content.Key);
            // use url as needed
        }
    }
}
```

## Notes

- No additional DI registration is needed — the package composer registers `IPreviewUrlService` automatically.
- `GetPreviewUrl` must be called during an active HTTP request. It returns `null` if `IHttpContextAccessor` has no current context (e.g. background jobs).
- The `versionKey` is the GUID of the specific historical version, not the content node's `Key`. You can get version GUIDs from the Umbraco Management API (`/umbraco/management/api/v1/document/{id}/version`) or from `IContentVersionService`.
