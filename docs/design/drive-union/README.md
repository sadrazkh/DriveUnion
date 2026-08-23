# Handoff: Drive Union — پنل مدیریت چند اکانت گوگل درایو

## Overview
وب‌اپ اختصاصی برای مدیریت یکپارچه چند اکانت گوگل درایو (فعلاً ۲ اکانت × ۵TB)، انتقال فایل بین درایوها، آپلود سریع موازی، و ساخت لینک دانلود اختصاصی با صفحه‌ی پیش‌نمایش عمومی.

استک هدف (طبق بریف): **ASP.NET** بک‌اند + **Vue توکار داخل Razor/cshtml** (نه SPA جدا با build مستقل)، سرور OVH آلمان.

## About the Design Files
فایل `Drive Union.dc.html` در این باندل یک **مرجع طراحی (design reference)** است که با HTML ساخته شده — پروتوتایپی که ظاهر و رفتار مورد نظر را نشان می‌دهد، **نه کد پروداکشن برای کپی مستقیم**.

وظیفه این است که این طراحی در محیط کدبیس مقصد **بازسازی** شود: کامپوننت‌های Vue توکار (مثل `Vue.createApp({...}).mount('#app')` روی یک Razor View)، با استایل‌دهی از طریق CSS خودتان یا یک لایه‌ی توکن CSS Variables. مقادیر دقیق رنگ/تایپوگرافی/اسپیسینگ در بخش Design Tokens آمده.

فایل باز می‌شود در مرورگر و قابل کلیک است (تعویض صفحه، تیره/روشن، FA/EN) — برای فهم فلو ازش استفاده کنید.

## Fidelity
**High-fidelity.** رنگ‌ها، تایپوگرافی، اسپیسینگ و شعاع‌ها نهایی هستند و باید پیکسل‌به‌پیکسل بازسازی شوند. داده‌های داخل جدول‌ها نمونه (mock) هستند و باید از API واقعی بیایند.

- زبان/جهت: پنل ادمین **فارسی RTL**. صفحه‌ی عمومی دانلود **دوزبانه FA/EN** با کلید تعویض (در طراحی با `data-lang` روی کانتینر و نمایش/مخفی کردن `[data-t="fa"]` / `[data-t="en"]` پیاده شده).
- تم: **روشن و تیره**، از طریق `data-theme="light|dark"` روی ریشه و CSS Variables. حالت پیش‌فرض روشن.
- چیدمان با logical properties (`inline-start/end`, `padding-inline`) نوشته شده تا RTL/LTR هر دو بدون فورک کار کند — همین را حفظ کنید.

---

## Screens / Views

اپ یک shell دارد: سایدبار ثابت (عرض `232px`، در RTL سمت راست) + هدر چسبان + ناحیه محتوا با پدینگ `24px 26px 40px`. صفحه‌ی عمومی دانلود بدون shell و تمام‌عرض است.

### Shell — سایدبار
- عرض `232px`، `border-inline-end: 1px solid var(--line)`، `background: var(--surface)`، `position: sticky; top: 0; height: 100vh`، پدینگ `18px 12px`.
- لوگو: مربع `30×30`, `border-radius: 9px`, `background: var(--accent)`, حرف «د» سفید `font-weight: 800; font-size: 14px`. کنارش نام «درایو یونیون» (`15px/800`) و زیرنویس مونواسپیس `11px` رنگ `--muted`: `2 accounts · 10 TB`.
- آیتم‌های ناوبری: `display:flex; gap:10px; padding:10px 12px; border-radius:10px; font-size:13.5px`. حالت عادی `color: var(--muted)`، حالت فعال `background: var(--soft); color: var(--accent-ink); font-weight:700`. هر آیتم یک نقطه‌ی `6×6` دایره‌ای با `background: currentColor` دارد.
- ترتیب: داشبورد / فایل‌ها / صف انتقال (با بَج عددی `3`: پس‌زمینه `--accent`، متن سفید، `border-radius:20px; padding:1px 7px; font-size:11px` مونواسپیس) / لینک‌های اشتراک / اکانت‌های گوگل / تنظیمات.
- پایین سایدبار (`margin-top:auto`):
  - کارت سهمیه: `border:1px solid var(--line); border-radius:12px; padding:12px; background: var(--surface2)`. متن «سهمیه آپلود امروز» + `918 / 1500 GB` مونواسپیس؛ نوار `height:6px` با پرشدگی ۶۱٪ رنگ `--accent`.
  - دکمه‌ی خط‌چین «پیش‌نمایش صفحه عمومی ↗» (`border:1px dashed var(--line)`, شفاف, `--muted`).
  - کاربر جاری: آواتار دایره‌ای `28px` با `background: var(--soft)`، نام `12.5px/600`، نقش `10.5px` رنگ `--muted`.

### Shell — هدر
`display:flex; gap:14px; padding:14px 26px; border-bottom:1px solid var(--line); background: var(--surface); position:sticky; top:0; z-index:5`.
- سرچ: عرض حداکثر `420px`, `border:1px solid var(--line); border-radius:10px; padding:8px 12px; background: var(--surface2)`. آیکن `⌕`، placeholder «جست‌وجو در همه‌ی اکانت‌ها…»، شورتکات `⌘K` در کادر `11px` مونواسپیس.
- سمت انتهایی: دکمه‌ی «تیره / روشن» (outline) و دکمه‌ی اصلی «آپلود فایل» (`background: var(--accent)`, متن سفید, `border-radius:9px; padding:8px 16px; font-weight:600`).

---

### ۱. داشبورد (`screen = dash`)
**هدف:** یک نگاه به وضعیت فضا، کارهای در جریان، لینک‌های پربازدید و خطاها.

**چیدمان:**
1. تیتر `22px/800` «داشبورد» + زمان همگام‌سازی (مونواسپیس `12px`, `--muted`) در دو سر ردیف.
2. گرید `repeat(2,1fr)` gap `14px`: دو کارت اکانت.
3. گرید `1.35fr / 1fr` gap `14px`: ستون چپ (کارهای فعال + لینک‌های پربازدید)، ستون راست (نمودار ترافیک + کارت خطاها).

**کارت اکانت** (`border:1px solid var(--line); border-radius:14px; background: var(--surface); padding:18px; box-shadow: var(--shadow)`):
- سربرگ: مربع `34px/radius 10px` با `background: var(--soft); color: var(--accent-ink)` و متن `A1`/`A2`؛ ایمیل `13.5px/700`؛ زیرنویس مونواسپیس `11px`؛ بَج «سالم» (`--soft` / `--accent-ink`, `border-radius:20px; padding:3px 10px; font-size:11px`).
- دو نوار پیشرفت `height:8px; border-radius:8px; background: var(--line)`:
  - «فضای مصرفی» — A1: `3.42 / 5 TB` (۶۸٪، رنگ `--accent`)؛ A2: `1.08 / 5 TB` (۲۱.۶٪).
  - «آپلود امروز» — A1: `612 / 750 GB` (۸۱.۶٪، **رنگ `--warn`** چون از ۸۰٪ سهمیه گذشته)؛ A2: `306 / 750 GB` (۴۰.۸٪، `--accent`).
  - قانون: نوار سهمیه‌ی روزانه ≥۸۰٪ → `--warn`؛ ≥۹۵٪ → `--danger`.

**کارت «کارهای فعال»:** سربرگ `14px/700` + لینک «همه‌ی صف ←». هر ردیف: نام فایل `13px/600`، توضیح مونواسپیس `11px` `--muted`، درصد در انتها، نوار `6px`، و یک خط زیرنویس مونواسپیس `11px`.
ردیف‌ها: `Season-04-Master.mkv` (A1→A2 · files.copy، ۷۳٪، «کپی سمت گوگل · بدون مصرف پهنای باند سرور»)؛ `backup-2026-08.tar.zst` (آپلود موازی · ۸ chunk، ۴۱٪، «218 MB/s · باقی‌مانده ۰۰:۱۹:۲۴»)؛ `raw-photos-2026.zip` (در انتظار سهمیه A1، وضعیت «صف» رنگ `--warn`، نوار ۰٪، «شروع خودکار ساعت ۰۰:۰۰ بعد از ریست سهمیه»).

**کارت «لینک‌های پربازدید امروز»:** گرید `1fr auto auto`؛ نام فایل، اسلاگ مونواسپیس (`/d/kx91mz`)، تعداد دانلود. سه ردیف نمونه: ۲۴۱ / ۱۸۹ / ۷۶.

**کارت ترافیک خروجی OVH:** ۷ میله، ارتفاع کانتینر `96px`, `gap:5px`, `border-radius:4px 4px 0 0`. ارتفاع‌ها: ۳۸٪, ۵۲٪, ۴۴٪, ۷۱٪, ۶۳٪, ۸۸٪, ۱۰۰٪ — پنج‌تای اول `--soft`، دوتای آخر `--accent`. زیرنویس: «۷ روز اخیر» / «اوج ۴.۱ Gb/s».

**کارت «کارهای ناموفق»** (قابل خاموش‌کردن با prop `showErrorPanel`): نقطه‌ی `8px` رنگ `--danger` کنار تیتر، شمارنده در انتها. دو مورد، هرکدام با عنوان، توضیح `11.5px` و دکمه‌ی اقدام outline:
- `dataset-full.img · 812 GB` — «بزرگ‌تر از سقف ۷۵۰GB برای files.copy — نیاز به آپلود مستقیم دارد.» → «آپلود مستقیم به‌جای کپی».
- `nightly-sync #4471` — «خطای ۴۰۳ userRateLimitExceeded — عبور از ۱۲٬۰۰۰ کوئری در ۶۰ ثانیه.» → «تلاش مجدد با backoff».

### ۲. فایل‌ها (`screen = files`)
**هدف:** نمای یکپارچه (union) فایل‌های همه‌ی اکانت‌ها با پنل جزئیات کناری.

- تیتر + زیرنویس «نمای یکپارچه از ۲ اکانت · ۱۴٬۲۸۶ آیتم».
- ردیف فیلترها (چیپ‌های `border-radius:20px; padding:5px 12px; font-size:12px`): «همه اکانت‌ها» (فعال: `border:1px solid var(--accent); background: var(--soft); color: var(--accent-ink)`)، «A1 archive.main»، «A2 archive.cold»، «فقط لینک‌دار»، «بزرگ‌تر از ۱۰GB».
- گرید اصلی: `minmax(0,1fr) 340px`, gap `14px`, `align-items:start`.

**جدول فشرده** — گرید ستون‌ها: `28px minmax(0,2.4fr) 84px 92px 110px 118px 96px` = چک‌باکس، نام، اکانت، حجم، تغییر، لینک، دانلود.
- هدر: `background: var(--surface2)`, `font-size:11.5px`, `color: var(--muted)`, `font-weight:600`.
- ردیف‌ها: `font-size:12.5px`, پدینگ از توکن `--row-pad` (`11px 14px` عادی، `7px 14px` در حالت compact)، `border-bottom:1px solid var(--line)`.
- ردیف انتخاب‌شده: `background: var(--soft)`، نام `600`، چک‌باکس `11×11` پرشده با `--accent` و `border-radius:3px`.
- ستون‌های حجم/اکانت/لینک/دانلود مونواسپیس `11.5px`؛ اسلاگ لینک رنگ `--accent-ink`؛ نبود لینک = `—` رنگ `--muted`.
- ردیف‌های نمونه: Q3-Report-Final.pdf (A1, 18.4MB, /d/kx91mz, 241) · Season-04-Master.mkv (A1, 214GB, —) · promo-reel-4k.mp4 (A2, 4.7GB, /d/8vaq2c, 189) · دفترچه-راهنما-نسخه۳.pdf (A2, 6.2MB, /d/rt40ab, 76) · dataset-full.img (A1, 812GB, —) · backup-2026-08.tar.zst (A2, 96GB, «در حال آپلود») · client-assets-2026/ (A1, 28.9GB).
- فوتر جدول: `background: var(--surface2)`, «۱ فایل انتخاب شده» + دکمه‌های «انتقال به A2» (outline) و «ساخت لینک» (اصلی).

**پنل جزئیات** (`aside`, `position:sticky; top:88px`, `border-radius:14px`):
- بالای پنل placeholder پیش‌نمایش: ارتفاع `150px`، پس‌زمینه `repeating-linear-gradient(135deg, var(--line) 0 1px, transparent 1px 9px)` با برچسب مونواسپیس در وسط. در پیاده‌سازی واقعی: `thumbnailLink` گوگل یا رندر صفحه اول PDF.
- نام فایل `14.5px/700`، خط متادیتا مونواسپیس `11.5px` (`18.4 MB · PDF · 42 صفحه`).
- گرید `auto 1fr` (`gap:8px 14px`, `12.5px`): اکانت، مسیر (مونواسپیس)، شناسه درایو (کوتاه‌شده `1aB…9Zk`)، تاریخ ساخت.
- کارت «لینک فعال» داخل `--surface2`: بَج «عمومی»، فیلد لینک `dir="ltr"` مونواسپیس `11px` با ellipsis + اکشن «کپی»، و سه شاخص مونواسپیس `11px`: `۲۴۱ / ۵۰۰ دانلود`، `انقضا ۱۲ روز`، `رمزدار`.
- دو دکمه: «تنظیمات لینک» (outline) و «دیدن صفحه عمومی» (اصلی).

### ۳. صف انتقال و آپلود (`screen = queue`)
- تیتر + زیرنویس: «کپی بین درایوها سمت گوگل انجام می‌شود؛ آپلود از سرور OVH با chunkهای موازی.»
- چهار کارت آمار (`repeat(4,1fr)`, `border-radius:12px; padding:14px`): برچسب `11.5px` رنگ `--muted` + عدد `22px/800` مونواسپیس. مقادیر: در حال اجرا `2` · در صف `7` · سرعت کل `341 MB/s` (واحد `12px/500` رنگ `--muted`) · ناموفق ۲۴ ساعت `2` رنگ `--danger`.
- جدول: ستون‌های `minmax(0,2fr) 130px 110px 1fr 90px` = کار / نوع / مقصد / پیشرفت / وضعیت.
  - `Season-04-Master.mkv` · `files.copy` · `A1 → A2` · ۷۳٪ · «در حال کپی» (`--accent-ink`)
  - `backup-2026-08.tar.zst` · `resumable ×8` · `→ A2` · ۴۱٪ · «آپلود»
  - `raw-photos-2026.zip` · `resumable ×4` · `→ A1` · ۰٪ · «صبر سهمیه» (`--warn`)
  - `dataset-full.img` · `files.copy` · `A1 → A2` · متن «سقف ۷۵۰GB رد شد» به‌جای نوار · «ناموفق» (`--danger`)
  - `invoices-2025.7z` · `S3 export` · `A2 → S3` · ۱۰۰٪ · «تمام شد» (`--muted`)

### ۴. لینک‌های اشتراک + پنل تنظیمات لینک (`screen = links`)
گرید `minmax(0,1fr) 380px`.

**جدول لینک‌ها** — ستون‌ها `minmax(0,1.8fr) 118px 100px 96px 90px` = فایل / آدرس / دانلود / انقضا / وضعیت. ردیف انتخاب‌شده `background: var(--soft)`.
- Q3-Report-Final.pdf · /d/kx91mz · ۲۴۱/۵۰۰ · ۱۲ روز · فعال (`--accent-ink`)
- promo-reel-4k.mp4 · /d/8vaq2c · ۱۸۹/∞ · بدون · فعال
- دفترچه-راهنما-نسخه۳.pdf · /d/rt40ab · ۷۶/۱۰۰ · ۳ روز · «نزدیک سقف» (`--warn`)
- contract-v7.docx · /d/we12nn · ۵/۵ · منقضی · غیرفعال (`--muted`)

**پنل تنظیمات** (`sticky top:88px`, `padding:18px`):
1. **آدرس اختصاصی** — فیلد `dir="ltr"` مونواسپیس: پیشوند `yourdomain.com/d/` رنگ `--muted` + اسلاگ قابل ویرایش رنگ `--text`.
2. **تاریخ انقضا** — سه گزینه‌ی هم‌عرض: «۲۴ ساعت» / «۱۴ روز» (فعال: `border:1px solid var(--accent); background: var(--soft); color: var(--accent-ink); font-weight:700`) / «بدون».
3. **محافظت با رمز** — ردیف با عنوان `13px/600` + توضیح `11px` + سوییچ. زیرش فیلد رمز `dir="ltr"` مونواسپیس با ماسک.
4. **سقف تعداد دانلود** — اسلایدر: ریل `height:6px; border-radius:6px; background: var(--line)`، پرشدگی `--accent`، دستگیره دایره `16px` با `border:2px solid var(--accent); background: var(--surface)`. مقدار `500` مونواسپیس کنارش.
5. **صفحه پیش‌نمایش** (روشن) — «به‌جای دانلود مستقیم».
6. **پنهان‌کردن نام اصلی فایل** (خاموش) — «نمایش نام مستعار به گیرنده».
7. دکمه‌ها: «ابطال لینک» (outline، متن `--danger`، `flex:1`) و «ذخیره تغییرات» (اصلی، `flex:2`).

**سوییچ (Toggle):** `38×22`, `border-radius:20px`. روشن: `background: var(--accent)` و دایره‌ی `16px` سفید در `inset-inline-start:3px`. خاموش: `background: var(--line)` و دایره‌ی `var(--surface)` در `inset-inline-end:3px`.

### ۵. اکانت‌های گوگل (`screen = accounts`)
- سربرگ با زیرنویس «توکن‌ها رمزنگاری‌شده روی سرور OVH ذخیره می‌شوند.» + دکمه‌ی اصلی «+ افزودن اکانت با OAuth».
- کارت هر اکانت (`max-width:900px`): گرید `44px minmax(0,1fr) 200px auto` gap `16px`.
  - مربع `44px/radius 12px` با `A1`/`A2`؛ ایمیل `14px/700`؛ زیرنویس مونواسپیس `scope: drive · توکن معتبر تا ۵۹ دقیقه دیگر`.
  - ستون فضا: `3.42 TB` / `68٪` + نوار `6px`.
  - دکمه‌ها: «تازه‌سازی توکن» و «قطع اتصال» (متن `--danger`).
- کارت خط‌چین افزودن: «افزودن اکانت سوم — ظرفیت کل به ۱۵TB و سهمیه روزانه به ۲.۲۵TB می‌رسد» (`border:1px dashed var(--line); padding:22px`).
- **دسترسی کاربران پنل** (چون پنل چندکاربره با نقش است): جدول `minmax(0,1fr) 150px 130px auto` = کاربر / نقش / اکانت‌ها / اقدام.
  - رضا مرادی · مدیر کل · A1, A2 · «شما»
  - سارا کاظمی · آپلودر · A2 · «ویرایش دسترسی»
  - امیر ن. · فقط مشاهده · A1, A2 · «ویرایش دسترسی»
  - نقش‌های پیشنهادی: `Owner/مدیر کل` (همه‌چیز + مدیریت اکانت‌ها و کاربران)، `Uploader/آپلودر` (آپلود، انتقال، ساخت لینک روی اکانت‌های مجاز)، `Viewer/فقط مشاهده`.

### ۶. تنظیمات (`screen = settings`)
گرید `repeat(2, minmax(0,1fr))`, `max-width:1000px`.
- **سیاست آپلود** — سه رادیو-کارت (`border-radius:10px; padding:12px`)؛ انتخاب‌شده `border:1px solid var(--accent); background: var(--soft)`، دایره‌ی `14px` با `border:4px solid var(--accent)`:
  - «بیشترین فضای خالی» (پیش‌فرض) — «اکانتی با فضای آزاد بیشتر اول پر می‌شود»
  - «round-robin» — «پخش متناوب بین اکانت‌ها»
  - «ترتیب اولویت دستی» — «اول A1 تا پر شود، بعد A2»
- **کارایی انتقال** — دو اسلایدر: «تعداد chunk هم‌زمان» = `8` (۵۰٪)، «اندازه هر chunk» = `64 MB` (۳۵٪)؛ سوییچ روشن «توقف خودکار نزدیک سهمیه — در ۷۲۰GB از ۷۵۰GB روزانه».
- **IPهای واسط و پروکسی** (`grid-column: span 2`) — زیرنویس مهم: «فقط برای دور زدن throttling مسیر شبکه — سهمیه ۷۵۰GB به ازای اکانت است، نه IP.» جدول `minmax(0,1fr) 130px 120px 110px 90px` = آدرس (`dir="ltr"` مونواسپیس) / محل / تأخیر تا گوگل / سهم ترافیک / وضعیت:
  - `51.xx.xx.14 (OVH GRA)` · فرانسه · 6 ms · ۶۰٪ · فعال
  - `148.xx.xx.7 (OVH FRA)` · آلمان · 3 ms · ۴۰٪ · فعال
  - `185.xx.xx.92` · هلند · 11 ms · ۰٪ · غیرفعال
  - دکمه‌ی «+ افزودن IP» در سربرگ کارت.

### ۷. صفحه‌ی عمومی پیش‌نمایش/دانلود (`screen = public`)
**عمومی، بدون shell، دوزبانه، برندشده و رسمی.** کانتینر `max-width:760px; margin:0 auto; padding:26px 22px 60px`.

- هدر سبک: لوگو + نام برند سمت شروع؛ سمت انتها: «FA / EN»، کلید تم `◑` (در محصول واقعی دکمه‌ی بازگشت به پنل حذف می‌شود — فقط برای پیمایش پروتوتایپ است).
- کارت اصلی: `border-radius:18px; border:1px solid var(--line); background: var(--surface); box-shadow: var(--shadow); overflow:hidden`.
  - ناحیه‌ی پیش‌نمایش: ارتفاع `230px`، همان الگوی راه‌راه placeholder با برچسب مونواسپیس. واقعی: تصویر برای عکس، صفحه‌ی اول برای PDF، پوستر/فریم برای ویدیو، و آیکن نوع فایل برای بقیه.
  - بَج «فایل به اشتراک گذاشته‌شده / Shared file» (`--soft` / `--accent-ink`).
  - عنوان فایل `24px/800`, `line-height:1.35`.
  - توضیح `13.5px`, `line-height:1.9`, `max-width:56ch`, `text-wrap:pretty`, رنگ `--muted`.
  - نوار متادیتا: گرید `repeat(4,1fr)` با `gap:1px` روی `background: var(--line)` (ترفند خط جداکننده)، هر سلول `background: var(--surface); padding:13px 15px`. برچسب `11px` `--muted` + مقدار `13.5px/700`: حجم `18.4 MB` / نوع `PDF` / تاریخ `۱۴۰۵/۰۵/۳۱` / اعتبار لینک `۱۲ روز`.
  - CTA: `padding:15px 34px; font-size:15px; font-weight:700; border-radius:12px; background: var(--accent); color:#fff; box-shadow: 0 8px 20px -10px var(--accent)`. کنارش خط اطمینان `12px` `--muted`: «دانلود مستقیم و استریم‌شده از سرور ما · بدون تبلیغ، بدون انتظار».
  - فوتر کارت: `background: var(--surface2)`, آدرس لینک `dir="ltr"` مونواسپیس در یک سو و «۲۴۱ بار دانلود شده» در سوی دیگر.
- زیرنویس صفحه `11.5px` `--muted` وسط‌چین: «این لینک اختصاصی است و ممکن است منقضی شود. گزارش سوءاستفاده: abuse@yourdomain.com».

**حالت‌های اضافی که باید پیاده شوند (در طراحی نیامده، از همین اجزا بسازید):**
- لینک رمزدار → قبل از کارت، فرم رمز با همان کارت `18px` و یک ورودی + دکمه.
- لینک منقضی/سقف‌خورده → همان کارت با آیکن خنثی، عنوان «این لینک دیگر در دسترس نیست»، بدون CTA.
- لینک نامعتبر (404) → پیام مشابه، بدون افشای وجود/عدم وجود فایل.

---

## Interactions & Behavior
- **ناوبری:** کلیک روی آیتم سایدبار → `state.screen` عوض می‌شود؛ همه‌ی صفحه‌ها در DOM هستند و با انتخابگر `[data-screen="x"] [data-sc="x"]` نمایش داده می‌شوند. در پیاده‌سازی واقعی: مسیرهای سرور (Razor pages) یا `vue-router` سبک.
- **تم:** `toggleTheme` مقدار `data-theme` روی ریشه را بین `light`/`dark` عوض می‌کند؛ همه‌ی رنگ‌ها CSS Variables هستند. باید در `localStorage` ذخیره شود و `prefers-color-scheme` به‌عنوان مقدار اولیه خوانده شود.
- **زبان صفحه‌ی عمومی:** `data-lang` روی کانتینر؛ `[data-lang="fa"] [data-t="en"]{display:none}` و برعکس. در LTR جهت و تراز به `ltr/left` می‌رود. در پیاده‌سازی واقعی بهتر است زبان از `Accept-Language` یا پارامتر `?lang=` بیاید و سرور HTML درست را بدهد (بهتر برای SEO/کش).
- **پیشرفت‌ها:** نوارهای صف باید زنده باشند — SignalR (طبیعی‌ترین گزینه در ASP.NET) یا polling هر ۲ ثانیه روی `/api/jobs/active`.
- **انتخاب چندتایی فایل:** چک‌باکس ردیف؛ با ≥۱ انتخاب، فوتر جدول اکشن‌ها را نشان می‌دهد (در طراحی همیشه دیده می‌شود؛ در واقعیت با انتخاب صفر مخفی شود).
- **hover:** ردیف‌های جدول `background: var(--surface2)`؛ دکمه‌ی اصلی روی hover به `--accent-ink`؛ ترنزیشن `120ms ease`.
- **حالت بارگذاری:** اسکلتون با همان `--line` به‌عنوان پس‌زمینه و شیمر ملایم؛ ارتفاع ردیف برابر `--row-pad`.
- **حالت خالی:** متن `13px` رنگ `--muted` وسط جدول + دکمه‌ی اقدام مربوطه.
- **ریسپانسیو:** زیر `1200px` پنل‌های کناری (جزئیات فایل / تنظیمات لینک) به کشوی (drawer) روی‌هم می‌روند؛ زیر `900px` سایدبار به منوی جمع‌شونده تبدیل می‌شود. صفحه‌ی عمومی تا `760px` تک‌ستونی و کاملاً موبایل‌فرندلی است (نوار متادیتا `repeat(2,1fr)` می‌شود).

## State Management
سطح اپ:
- `screen`, `theme` (`light|dark`, persisted), `lang` (`fa|en`, فقط صفحه‌ی عمومی), `density` (`comfortable|compact`).
- `accounts[]`: `{ id, email, quotaTotal, quotaUsed, dailyUploadUsed, dailyUploadLimit, tokenExpiresAt, status }`
- `files[]` (union): `{ id, driveId, accountId, name, mimeType, size, modifiedTime, path, linkSlug|null, downloadCount }` + `selectedFileId`
- `jobs[]`: `{ id, fileName, type: 'copy'|'upload'|'export', source, target, progress, speed, etaSeconds, status: 'running'|'queued'|'quota_wait'|'failed'|'done', error }`
- `links[]`: `{ slug, fileId, expiresAt|null, maxDownloads|null, downloadCount, hasPassword, showPreviewPage, hideRealName, active }`
- `settings`: `{ uploadPolicy: 'most_free'|'round_robin'|'manual', concurrentChunks, chunkSizeMB, autoStopNearQuota, proxies[] }`
- `users[]`: `{ id, name, role: 'owner'|'uploader'|'viewer', accountIds[] }`

دیتافچینگ: `GET /api/accounts`, `GET /api/files?account=&q=&cursor=`, `GET /api/jobs`, `GET /api/links`, `POST /api/links`, `PATCH /api/links/{slug}`, `POST /api/transfer` (files.copy)، `POST /api/upload/session` (resumable)، و روت عمومی `GET /d/{slug}` (صفحه) + `GET /d/{slug}/file` (استریم).

## Design Tokens

### رنگ — روشن
```
--bg:       oklch(0.975 0.005 160)   /* ≈ #F5F7F6 */
--surface:  #FFFFFF
--surface2: oklch(0.985 0.004 160)
--line:     oklch(0.915 0.006 160)
--text:     oklch(0.25 0.012 160)
--muted:    oklch(0.56 0.012 160)
--accent:     #0F9D77
--accent-ink: #0B7A5C
--soft:     oklch(0.955 0.028 165)
--warn:     oklch(0.72 0.13 70)    --warn-soft:   oklch(0.96 0.04 80)
--danger:   oklch(0.57 0.16 27)    --danger-soft: oklch(0.96 0.03 27)
--shadow: 0 1px 2px rgba(16,40,32,.05), 0 8px 24px -12px rgba(16,40,32,.18)
```

### رنگ — تیره (`[data-theme="dark"]`)
```
--bg:       oklch(0.185 0.008 165)
--surface:  oklch(0.225 0.010 165)
--surface2: oklch(0.255 0.010 165)
--line:     oklch(0.315 0.012 165)
--text:     oklch(0.955 0.005 160)
--muted:    oklch(0.70 0.010 160)
--accent:     oklch(0.78 0.12 168)
--accent-ink: oklch(0.86 0.10 168)
--soft:     oklch(0.30 0.045 168)
--warn:     oklch(0.80 0.12 75)     --warn-soft:   oklch(0.31 0.05 75)
--danger:   oklch(0.70 0.14 27)     --danger-soft: oklch(0.30 0.06 27)
--shadow: 0 1px 2px rgba(0,0,0,.3), 0 10px 28px -14px rgba(0,0,0,.6)
```

### تایپوگرافی
- خانواده اصلی: **Vazirmatn** (جایگزین آزاد و نزدیک به IRANSans). اگر لایسنس IRANSans دارید، فقط `@font-face` را عوض کنید — متریک‌ها نزدیک‌اند.
  `@import` فعلی: `https://cdn.jsdelivr.net/gh/rastikerdar/vazirmatn@33.003/Vazirmatn-font-face.css` — در پروداکشن فونت را **self-host** کنید (سرور آلمان، بدون CDN خارجی).
- مونواسپیس (اعداد، شناسه‌ها، آدرس‌ها، سرعت‌ها): `ui-monospace, SFMono-Regular, Menlo, monospace`.
- مقیاس: `22/800` عنوان صفحه · `24/800` عنوان فایل عمومی · `15/800` برند · `14–14.5/700` عنوان کارت · `13.5/700` عنوان ردیف اصلی · `13/600` برچسب ردیف · `12.5` بدنه‌ی جدول · `12` متادیتا · `11.5` هدر جدول و زیرنویس · `11` ریزنویس مونواسپیس.
- `line-height`: بدنه `1.7–1.9`، عناوین `1.3–1.4`.

### اسپیسینگ و شکل
- مقیاس: `3, 4, 6, 8, 9, 10, 12, 14, 16, 18, 22, 26px`.
- گپ گرید کارت‌ها `14px`؛ پدینگ کارت `18px`؛ پدینگ محتوا `24px 26px 40px`.
- شعاع: `8` دکمه‌ی کوچک · `9–10` ورودی/دکمه · `11–12` کارت داخلی · `14` کارت اصلی · `18` کارت صفحه‌ی عمومی · `20` بَج/چیپ · `50%` آواتار.
- `--row-pad`: `11px 14px` (comfortable) / `7px 14px` (compact).
- ارتفاع نوار پیشرفت: `6px` (ردیفی) / `8px` (کارت اکانت).

## Assets
هیچ تصویر یا آیکن باینری استفاده نشده. تمام «آیکن‌ها» گلیف متنی (`⌕`, `◑`, `←`, `↗`) یا شکل هندسی CSS هستند.
- **Placeholderها** با `repeating-linear-gradient(135deg, var(--line) 0 1px, transparent 1px 9px)` ساخته شده‌اند — این‌ها باید با محتوای واقعی جایگزین شوند (thumbnail از Drive API یا رندر سمت سرور).
- برای آیکن‌های واقعی: یک ست SVG یکدست (مثل Lucide/Phosphor) با `stroke-width: 1.5` و رنگ `currentColor` پیشنهاد می‌شود.
- لوگو فعلاً حرف «د» در مربع `--accent` است — جایگزین با لوگوی واقعی برند.

## Files
- `Drive Union.dc.html` — کل طراحی، هر ۷ نما، قابل باز شدن در مرورگر و کلیک‌کردنی (تعویض صفحه، تیره/روشن، FA/EN).
  - ساختار: هر نما یک `<section data-sc="...">` است؛ نمایش با CSS بر اساس `data-screen` روی ریشه.
  - توکن‌های رنگ در بلوک `<style>` بالای فایل، در `:root` و `[data-theme="dark"]`.

## نکات فنی که در UI لحاظ شده و نباید در پیاده‌سازی گم شود
- سهمیه‌ی **۷۵۰GB روزانه به ازای هر اکانت** است، نه IP — در کارت اکانت و در متن صفحه‌ی پروکسی صریحاً آمده. UI نباید القا کند که افزودن IP سهمیه را بالا می‌برد.
- `files.copy` فقط تا **۷۵۰GB**؛ فایل بزرگ‌تر باید مسیر «آپلود مستقیم» بگیرد — این خطا و اقدام اصلاحی‌اش در کارت «کارهای ناموفق» طراحی شده.
- نرخ **۱۲٬۰۰۰ کوئری در ۶۰ ثانیه** → خطای `403 userRateLimitExceeded` با اقدام «تلاش مجدد با backoff» (exponential backoff + jitter).
- دانلود کاربر نهایی باید **استریم** باشد (`Response.Body` مستقیم از استریم Drive API، بدون بافر کامل)؛ از `Range` header پشتیبانی کنید تا resume و seek ویدیو کار کند.
- کاربر نهایی هرگز نباید آدرس گوگل درایو را ببیند — هیچ redirect به `drive.google.com` نزنید؛ همه‌چیز از `/d/{slug}` عبور کند.
