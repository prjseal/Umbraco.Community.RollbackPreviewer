# Plan: vendor only the runtime URL-handling code from `Umbraco.Community.RollbackPreviewer` and patch the property-loading bug

## Context

The host web project consumes `Umbraco.Community.RollbackPreviewer` via NuGet. The piece you care about is the runtime path that handles the shareable preview URL (`?cid=...&vid=...&secret=...&culture=...`): an `IContentFinder` intercepts the request, validates the secret, builds an `IPublishedContent` from the rolled-back version, and hands it to the publish pipeline.

There is a bug in that path: when previewing via the URL, some properties on the resulting `IPublishedContent` come back as `null` or their default value instead of what's on the underlying `IContent` version. We're going to vendor only the C# files responsible for that path, drop everything else (front-end, v13/NET8 fallbacks, back-office config controllers, Swagger setup, test sites), and apply the fix.

Assume the host targets Umbraco 15+ (NET 9 or NET 10). Ignore the `NET9_0_OR_GREATER` `#else` branches — delete them.

## Step 1 — Create a new class library project

Place it next to the host project, e.g. `src/RollbackPreviewer.Local/RollbackPreviewer.Local.csproj`. Match the host's `TargetFramework`. Reference the same Umbraco packages the host uses, e.g. for Umbraco 17 / NET 10:

```xml
<PackageReference Include="Umbraco.Cms.Web.Common" Version="[17,18)" />
<PackageReference Include="Umbraco.Cms.Api.Common" Version="[17,18)" />
```

Use the same root namespace as the original package — `Umbraco.Community.RollbackPreviewer` — so existing config keys (`RollbackPreviewer:...` in appsettings) keep working.

Add the project to the solution and add a `<ProjectReference>` to it from the host, then **remove the `<PackageReference Include="Umbraco.Community.RollbackPreviewer" ... />`** from the host. Verify the solution still builds.

## Step 2 — Copy these five files only

From the upstream repo (https://github.com/prjseal/Umbraco.Community.RollbackPreviewer, `src/Umbraco.Community.RollbackPreviewer/`), copy:

1. `Constants.cs`
2. `Configuration/RollbackPreviewerOptions.cs`
3. `Services/TimeLimitedSecretService.cs`
4. `Services/RollBackContentFinder.cs`
5. `Extensions/PublishedContentExtensions.cs`

Do **not** copy:
- `Controllers/*` (back-office config API — front-end concern)
- `Composers/UmbracoCommunityRollbackPreviewerApiComposer.cs` (you'll write a slim replacement)
- `Client/`, `wwwroot/` (front-end assets)
- `TestSite.*` projects
- The original `.csproj`

## Step 3 — Strip the v13 / NET 8 conditional code

In `RollBackContentFinder.cs`, the file is littered with `#if NET9_0_OR_GREATER ... #else ... #endif` blocks for the v13 fallback. Delete the `#else` branches and the directives themselves; keep only the NET 9+ code. In particular:

- `Guid` keys instead of `int` for `cid`/`vid` parsing.
- The `GetVersion(int)` overload — delete it entirely.
- Inside `GetVersion(Guid)`, drop the `#if`/`#else` and keep only the `IContentVersionService.GetAsync` call.

In any other copied file, do the same: delete `NET9_0_OR_GREATER` guards and the v13 branches.

## Step 4 — Write a slim composer

Create `Composers/RollbackPreviewerComposer.cs` in the new project. It only needs to register the content finder, the converter, the secret service, and bind the options — none of the Swagger / back-office API plumbing from the original.

```csharp
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Routing;
using Umbraco.Community.RollbackPreviewer.Configuration;
using Umbraco.Community.RollbackPreviewer.Extensions;
using Umbraco.Community.RollbackPreviewer.Services;

namespace Umbraco.Community.RollbackPreviewer.Composers;

public class RollbackPreviewerComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        builder.ContentFinders().InsertBefore<ContentFinderByUrlNew, RollbackContentFinder>();
        builder.Services.AddTransient<PublishedContentConverter>();
        builder.Services.AddSingleton<ITimeLimitedSecretService, TimeLimitedSecretService>();
        builder.Services.Configure<RollbackPreviewerOptions>(
            builder.Config.GetSection(RollbackPreviewerOptions.SectionName));
    }
}
```

## Step 5 — Apply the bug fix

Two edits inside the vendored project.

### Edit A — `Extensions/PublishedContentExtensions.cs`

**Why.** `IProperty.GetValue(culture)` rejects mismatched variation: an invariant property called with a non-null culture returns `null`, and a variant property called with `null` returns `null`. The original code passes the same culture to every property regardless of its variation, so on a multilingual site every invariant property comes back null. The original fallback (`Values.FirstOrDefault().EditedValue`) hid this for invariant props but on variant content could return the wrong language's value.

**A1 — `PublishedPropertyWrapper` constructor.** Replace:

```csharp
_sourceValue = property?.GetValue(culture);

if (_sourceValue == null)
{
    // Block properties return null for GetValue...
    _sourceValue = property?.Values?.FirstOrDefault()?.EditedValue;
}
```

with:

```csharp
// IProperty.GetValue requires a null culture for invariant properties and the
// matching culture for variant ones; passing the wrong one returns null.
var propertyCulture = propertyType.VariesByCulture() ? culture : null;
_sourceValue = property?.GetValue(propertyCulture);

if (_sourceValue == null && property != null)
{
    var matchingValue = property.Values.FirstOrDefault(v =>
        string.Equals(v.Culture ?? string.Empty, propertyCulture ?? string.Empty, StringComparison.OrdinalIgnoreCase)
        && string.IsNullOrEmpty(v.Segment));
    _sourceValue = matchingValue?.EditedValue ?? matchingValue?.PublishedValue;
}
```

**A2 — propagate `_isPreviewing` through traversal.** In the `_parent` and `_children` lazies on `PublishedContentWrapper`, change the two `converter.ToPublishedContent(p, culture)` / `converter.ToPublishedContent(x, culture)` calls to pass `_isPreviewing` as the third argument:

```csharp
return converter.ToPublishedContent(p, culture, _isPreviewing).CreateModel(publishedModelFactory);
```

```csharp
return c.Select(x => converter.ToPublishedContent(x, culture, _isPreviewing).CreateModel(publishedModelFactory))
        .OrderBy(x => x.SortOrder);
```

### Edit B — `Services/RollBackContentFinder.cs`

**Why.** The rollback render is a preview, but the converter was being called with the default `isPreview: false`. Property converters for pickers and block editors then resolve only published references, so links/images that point to draft content disappear from the preview.

Replace:

```csharp
IPublishedContent? pubContent = _publishedContentConverter.ToPublishedContent(content, culture)?
    .CreateModel(_publishedModelFactory);
```

with:

```csharp
IPublishedContent? pubContent = _publishedContentConverter.ToPublishedContent(content, culture, isPreview: true)?
    .CreateModel(_publishedModelFactory);
```

## Step 6 — Verify

1. `dotnet build` the solution; expect zero errors.
2. Run the host, hit a known shareable preview URL with valid `cid`/`vid`/`secret`/`culture`.
3. Confirm in the rendered page:
   - Invariant properties (e.g. site-level fields) render their values, not blanks.
   - On a multilingual node, the previewed culture's variant values render correctly.
   - Pickers / block editors that reference unpublished content also render.

## Out of scope

- The back-office UI for generating share URLs. If the host depends on it, that comes from the original NuGet package's front-end assets and config controllers — those are not being vendored here. If you need both, keep the NuGet package alongside this local project but expect a duplicate-registration conflict on `IContentFinder` / `PublishedContentConverter` and resolve it by removing the NuGet's composer effect (e.g. remove the package and copy the controllers + front-end assets too). Confirm with the user before going down that path.
- The `Url` getter on `PublishedContentWrapper` returns `null` by design in the current code; do not "fix" it as part of this task.
