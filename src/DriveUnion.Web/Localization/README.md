# Localisation

The panel renders in Persian and in English. One mechanism, and this folder is all of it.

- **Strings** live in [`UiText.cs`](UiText.cs) — one table, both languages on the same line.
- **The language of a request** is resolved by the framework's `RequestLocalizationMiddleware`,
  configured in [`DriveUnionLocalizationExtensions.cs`](DriveUnionLocalizationExtensions.cs).
- **The switch** a person clicks is [`CultureController.cs`](CultureController.cs) and the form in
  `Views/Shared/_Layout.cshtml`.
- **Numbers inside prose** go through [`Numerals.cs`](Numerals.cs).

`UiText`'s own doc comment argues the choice — why this and not `.resx` + `IStringLocalizer`. Read it
before proposing the other one; the short version is that a mistyped `.resx` key compiles, renders
its own name to a customer, and fails no test.

## The two lines Program.cs needs

They are not there yet. Without them the panel renders Persian on every request, which is what it did
before this folder existed — so this is safe to ship in either order, and useless until both land.

```csharp
using DriveUnion.Web.Localization;             // beside the other DriveUnion.Web usings

builder.Services.AddDriveUnionLocalization();  // beside builder.Services.AddControllersWithViews();

app.UseRequestLocalization();                  // after app.UseStaticFiles(); before app.UseRouting();
```

`tests/DriveUnion.Tests/Localization/LocalizationHarness.cs` makes exactly these two registrations
through an `IStartupFilter`. When Program.cs has them, delete that filter from the harness and every
test in the folder still passes.

## How a request gets its language

In this order, first answer wins:

1. **The culture cookie** (`.AspNetCore.Culture`, `Path=/`, one year). Only `CultureController`
   writes it, so it *is* the explicit choice.
2. **`?lang=fa|en`** — the same spelling the public download page has always answered.
3. **`Accept-Language`**, with the framework's parent fallback, so `en-GB` is English and `fa-IR` is
   Persian.
4. **Persian.**

The public download page orders the first two the other way round, on purpose. Over there `?lang=` is
the visitor clicking FA/EN because there is nothing else to click; over here the cookie is. Both rules
say the same thing: the explicit act beats the ambient guess.

**Formatting never varies.** `SupportedCultures` is the invariant culture alone and no request can
move it — only `CurrentUICulture` changes. `DisplayFormats` and `PersianDigits` format explicitly, and
a request that could turn `CurrentCulture` into `fa-IR` would silently swap the decimal point in every
byte size and quota readout in the product for `٫`.

---

# Migrating a screen

`Views/Shared/**` and `Areas/Identity/**` are done and are the worked examples. The recipe:

### 1. Move each string into `UiText`

Keys are members, not strings, so a typo is a build error. Group by screen:

```csharp
public static class Files                       // = Views/Files/**
{
    public static string Title => Pick("فایل‌ها", "Files");
}
```

- **Section name** = the view folder (`Shell`, `Identity`, `Files`, `Links`, `Accounts`, `Home`).
  `Brand` and `Validation` are the two that belong to no folder.
- **Entry name** = what the string *is*, not what it says: `EmptyStateHeading`, not
  `NoFilesYet`. It has to still read correctly after the copy is rewritten.
- Persian first, English second, always, so a glance down the file is one language per column.

### 2. A string with a placeholder is a method

```csharp
public static string SelectedCount(int files) => Pick(
    $"{Numerals.Count(files)} فایل انتخاب شده",
    $"{files} files selected");
```

The compiler checks the arity and the types, which `.resx`'s `{0}` does not. Keep the parameters to
numbers and strings: `LocalizationCatalogueTests` invokes every entry in both languages and fails on a
parameter type it cannot supply, because an entry it cannot call is an entry nothing checks.

### 3. Numbers in prose go through `Numerals`

`Numerals.Count`, `.Plain`, `.Percent`, `.InProse`. The rule is unchanged and it is about direction,
not language: **digits in prose take that prose's numerals; digits in an LTR technical readout stay
Latin in both languages.** A byte size, a transfer speed, a slug, a Drive id, an address — those keep
calling `DisplayFormats` or formatting invariantly, exactly as before, and they belong nowhere near
`UiText`.

### 4. A string that is deliberately not translated

If both languages really do render the same words — a domain, a unit, a product name, a language
naming itself — say so where the entry is:

```csharp
[VerbatimText("each language names itself, so the reader of neither can still find the switch")]
public static string LanguageSwitch => Pick("English", "فارسی");
```

Without the attribute, `LocalizationCatalogueTests` reads "same in both cultures" as a translation
somebody forgot, and fails. The reason is required — it is the whole of the exemption.

If the thing is not text at all (`—`, `∞`, `⌘K`, `☰`), leave it as a literal in the view. It has no
language.

### 5. Validation messages

A `[Required(ErrorMessage = "…")]` argument is a compile-time constant and cannot ask which language
the request is in. Add a case to `ValidationText`, an entry to `UiText.Validation`, and use the
attributes in [`LocalizedValidation.cs`](LocalizedValidation.cs):

```csharp
[LocalizedRequired(ValidationText.EmailRequired)]
[LocalizedEmailAddress(ValidationText.EmailInvalid)]
public string Email { get; set; } = string.Empty;
```

### 6. `dir` and `lang`

Already handled, once, on `<html>` in `_Layout.cshtml`. A view never sets either. `dir="ltr"` on an
individual box is still right where the *content* is a technical readout — a slug, a mono size, a
credential input — because that is true in both languages.

The stylesheet needs no change. Every box in `app.css` uses logical properties, and the two places
that cannot (`translateX` on the mobile drawers) already carry `[dir="rtl"]` overrides.

### 7. Say it is done

Add the folder to `Migrated` in `tests/DriveUnion.Tests/Localization/MigratedScreensTests.cs`. That
test reads the source of every migrated screen and refuses a Persian character outside a comment,
which is the only signal that a literal escaped the catalogue — a stray one renders perfectly and
fails nothing else.

### 8. Verify

```
dotnet build DriveUnion.slnx
dotnet test  DriveUnion.slnx --filter FullyQualifiedName~DriveUnion.Tests.Localization
```

When asserting on rendered Persian, **HTML-decode first**. Razor's default encoder writes everything
outside Basic Latin as `&#x641;…`, so `Contain("فایل‌ها")` against raw markup passes on a page that
says it and on a page that does not. `LocalizationHarness.TextAsync` does the decoding.

---

## Two things this slice deliberately did not do

### The public download page

`/d/{slug}` resolves its own language in `PublicDownloadController` via `PublicLanguageResolver`, and
writes its pairs inline in the view with `PublicText.Pick(lang, fa, en)`. It is untouched. Its strings
live on the controller and in `Views/Public/**`, both outside this slice's scope, and it works.

The two are already reconciled where it matters: the panel's cookie is the framework's standard one at
`Path=/`, so it is sent to `/d/{slug}` too. Honouring it is three lines in
`PublicDownloadController.ResolveLanguage`:

```csharp
private PublicLanguage ResolveLanguage(string? requested)
{
    // A customer who set the panel to English and then opened their own share link should not be
    // handed a Persian page. ?lang= still wins — over here it is the visitor clicking FA/EN.
    var stored = CookieRequestCultureProvider
        .ParseCookieValue(Request.Cookies[CookieRequestCultureProvider.DefaultCookieName] ?? string.Empty)
        ?.UICultures.FirstOrDefault().Value;

    return PublicLanguageResolver.Resolve(requested, stored ?? Request.Headers.AcceptLanguage.ToString());
}
```

Folding the page's *strings* into `UiText` is the larger, separate job: it means moving
`PublicDownloadViewModel`'s pre-formatted `SizeText`/`ExpiryText`/`DownloadCountText` off the
controller, and it must not change what the page renders for a visitor who has never seen the panel.
Do it when the public page is next opened for its own reasons, not as a side effect of a panel change.
When it lands, turn on `ApplyCurrentCultureToResponseHeaders` in
`DriveUnionLocalizationExtensions` — it is off today only because that header would be wrong on this
one route.

### Identity's own error messages

`DriveUnionIdentityErrorDescriber` is written, tested and **not registered**. Registering it is one
line:

```csharp
builder.Services
    .AddIdentity<AppUser, IdentityRole<Guid>>(options => { … })
    .AddErrorDescriber<DriveUnionIdentityErrorDescriber>()      // ← this
    .AddEntityFrameworkStores<DriveUnionDbContext>()
    .AddDefaultTokenProviders();
```

It is not made here for two reasons. Program.cs is not this slice's to edit; and
`tests/DriveUnion.Tests/Identity/FirstRunSetupTests.A_password_the_policy_refuses_is_answered_in_identitys_own_words`
asserts `"must be at least 10 characters"` on the rendered first-run screen. That assertion is correct
today — the sentence *is* Identity's, deliberately, so the operator is told exactly what the policy
wanted. Once the describer is registered it is only correct in English, and the test wants a second
case in Persian. Both changes belong in one commit, made by whoever owns those two files.
