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

**They have landed.** `builder.Services.AddDriveUnionLocalization()` sits beside
`AddControllersWithViews()`, and `app.UseRequestLocalization()` after `app.UseStaticFiles()` and
before `app.UseRouting()`. Do not add them again.

`tests/DriveUnion.Tests/Localization/LocalizationHarness.cs` used to make the same two registrations
through an `IStartupFilter`, because Program.cs was not that slice's to edit. It no longer does: the
harness boots the shipped pipeline, and every test in the folder passes against it.

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

Done: `Views/Shared/**`, `Areas/Identity/**`, and every panel screen — `Views/Home`, `Views/Files`,
`Views/Links`, `Views/Accounts`, `Views/Design`, with their controllers and view models. What is
left is the Telegram surface and the public download page (see the last section).

`Views/Shared/_Layout.cshtml` is the worked example for a shell, `Views/Files/Index.cshtml` for a
table, and `Views/Accounts/_GoogleSetup.cshtml` for prose with untranslatable terms set into it.
The recipe:

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

Add the view folder to `Migrated` in
`tests/DriveUnion.Tests/Localization/MigratedScreensTests.cs`, **and the screen's controller and
view model to `MigratedSources` beside it**. That test reads the source of every migrated screen and
refuses a Persian character outside a comment, which is the only signal that a literal escaped the
catalogue — a stray one renders perfectly and fails nothing else.

The controller half is not optional and is the easy half to forget. A sentence a controller puts in
`TempData` and a status word a view model maps an enum to are as much part of the screen as its
markup, and a folder list cannot see either: `Controllers/` and `Models/` also hold the Telegram
surface, so they are listed one file at a time.

Then pin the screen in both cultures in
`tests/DriveUnion.Tests/Localization/PanelScreenLanguageTests.cs`. The source-level guard cannot see
a page that names the wrong entry, or one whose two languages were swapped; that file renders the
page and asks it.

### 8. Measure the English

English labels are longer than the Persian ones and this product's tables have fixed tracks. «نزدیک
سقف» became `Near cap` and not `Near the limit` because the status column is 90px — 62px of content
at `--row-pad` — and the literal translation measured 73px and wrapped, leaving that one row a line
taller than the rest of the table. Render what you change and measure it before you believe it.

### 9. Verify

```
dotnet build DriveUnion.slnx
dotnet test  DriveUnion.slnx --filter FullyQualifiedName~DriveUnion.Tests.Localization
```

When asserting on rendered Persian, **HTML-decode first**. Razor's default encoder writes everything
outside Basic Latin as `&#x641;…`, so `Contain("فایل‌ها")` against raw markup passes on a page that
says it and on a page that does not. `LocalizationHarness.TextAsync` does the decoding.

---

## The one screen that is still on the other mechanism

### The public download page

`/d/{slug}` resolves its own language in `PublicDownloadController` via `PublicLanguageResolver`, and
writes its pairs inline in the view with `PublicText.Pick(lang, fa, en)`. It is untouched, twice over
now: the panel migration left it alone on purpose, and here is the whole of why.

Its strings are not the obstacle — `Views/Public/**` and `PublicDownloadController` could be moved
across. Its **layout** is. `Views/Shared/_PublicLayout.cshtml` builds the document's `lang` and `dir`,
the FA/EN control and the two `hreflang` alternates from `ViewData["Lang"]`, not from `PanelCulture`.
Move the card without the chrome around it and the page says one thing in its body and another in its
`<html>` tag. So the two views and their layout are one change, and it is the layout that decides when.

There is a behavioural difference under it, and it is not incidental. In the panel the **cookie**
outranks `?lang=`, because the cookie is the operator clicking the switch. On `/d/{slug}` **`?lang=`
outranks everything**, because over there it is the visitor clicking FA/EN and there is nothing else
to click — and it is what the landing page's own `hreflang` alternates point at.
`PanelScreenLanguageTests.The_public_download_page_still_answers_to_its_own_lang_and_not_to_the_panels_cookie`
holds that contract while the two mechanisms are apart.

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

Folding the page's *strings* into `UiText` also means moving `PublicDownloadViewModel`'s
pre-formatted `SizeText`/`ExpiryText`/`DownloadCountText` off the controller, and it must not change
what the page renders for a visitor who has never seen the panel. Do it when the public page is next
opened for its own reasons, not as a side effect of a panel change.

When it lands, three things go together in one commit: `_PublicLayout.cshtml` and the two views move
onto `PanelCulture`/`UiText`; `ResolveLanguage` keeps `?lang=` first for this route; and
`ApplyCurrentCultureToResponseHeaders` is turned on in `DriveUnionLocalizationExtensions` — it is off
today only because that header would be right about the panel and wrong about this one route.

### Identity's own error messages

`DriveUnionIdentityErrorDescriber` **is registered** — `.AddErrorDescriber<…>()` on the
`AddIdentity` chain in Program.cs. Identity now refuses a password in the panel's own two languages.
This section used to say it was not; it is kept only so somebody who read the old version knows the
line has landed and does not add it twice.

It is not made here for two reasons. Program.cs is not this slice's to edit; and
`tests/DriveUnion.Tests/Identity/FirstRunSetupTests.A_password_the_policy_refuses_is_answered_in_identitys_own_words`
asserts `"must be at least 10 characters"` on the rendered first-run screen. That assertion is correct
today — the sentence *is* Identity's, deliberately, so the operator is told exactly what the policy
wanted. Once the describer is registered it is only correct in English, and the test wants a second
case in Persian. Both changes belong in one commit, made by whoever owns those two files.
