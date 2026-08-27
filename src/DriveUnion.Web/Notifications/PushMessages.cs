using System.Globalization;
using DriveUnion.Core.Application;
using DriveUnion.Web.Localization;

namespace DriveUnion.Web.Notifications;

/// <summary>
/// The words on a lock screen, in the language of the device that will draw them.
///
/// <para><b>It lives here and not in the infrastructure project</b> for the reason every
/// user-visible string in this product lives here: <c>UiText</c> is the one table, a key is a
/// member, and a typo is a build error. The sender is a background worker in another assembly, so
/// the seam is <see cref="IPushMessages"/> and this is the only implementation of it.</para>
///
/// <para><b>The culture is an argument and not the thread's.</b> There is no request behind a push:
/// <c>CultureInfo.CurrentUICulture</c> in a worker is whatever the operating system, the test runner
/// or a thread-pool thread happened to inherit, which on a Windows box is <c>en-US</c> — so a panel
/// whose readers are all Persian would send them English notifications, on some threads, depending
/// on load. The culture comes off the subscription row, set by the device when it subscribed, and is
/// pushed onto the thread for exactly the length of one <c>Pick</c>.</para>
/// </summary>
public sealed class PushMessages : IPushMessages
{
    public PushNotificationText Compose(PushEventKind kind, int count, string culture)
    {
        var previous = CultureInfo.CurrentUICulture;

        // An unknown or absent tag falls back to the product's default, which is what PanelCulture
        // does for a request it cannot resolve. Parse refuses anything not in Supported, so a row
        // carrying «de-DE» renders Persian rather than throwing inside a worker nobody is watching.
        CultureInfo.CurrentUICulture = PanelCulture.Parse(culture) ?? PanelCulture.Persian;

        try
        {
            return kind switch
            {
                PushEventKind.RemoteFetchCompleted => new PushNotificationText(
                    UiText.Notifications.FetchFinishedTitle,
                    UiText.Notifications.FetchFinishedBody,

                    // The screen that holds the answer, behind the reader's own session. A path and
                    // never an absolute address: a deployment moved to another host would otherwise
                    // send every notification it has ever sent to a domain it no longer owns.
                    FilesPath,
                    TagFor(kind)),

                PushEventKind.RemoteFetchFailed => new PushNotificationText(
                    UiText.Notifications.FetchFailedTitle,
                    UiText.Notifications.FetchFailedBody,
                    FilesPath,
                    TagFor(kind)),

                PushEventKind.DeletionCompleted => new PushNotificationText(
                    UiText.Notifications.DeletionFinishedTitle,

                    // Numerals and not ToString: a Persian sentence takes Persian digits, and this
                    // is prose rather than a technical readout an operator would copy.
                    UiText.Notifications.DeletionFinishedBody(Numerals.Count(count)),
                    TrashPath,
                    TagFor(kind)),

                PushEventKind.AbuseReportFiled => new PushNotificationText(
                    UiText.Notifications.AbuseReportTitle,
                    UiText.Notifications.AbuseReportBody,
                    AbuseQueuePath,
                    TagFor(kind)),

                _ => throw new ArgumentOutOfRangeException(
                    nameof(kind),
                    kind,
                    "This kind has no words. A notification with no sentence is a phone buzzing for "
                    + "nothing, so it is a refusal rather than a default."),
            };
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    /// <summary>
    /// One tag per kind, so a second notification of the same kind replaces the first.
    ///
    /// <para>Per kind rather than per event: five fetches finishing while a phone is asleep is one
    /// entry saying a fetch finished, not five identical ones the reader has to swipe away
    /// individually. What is lost is the count, which was never in the payload anyway — the screen
    /// behind the tap has it.</para>
    ///
    /// <para>The invariant name and not the localised words: a tag is an identifier the device
    /// compares, and one that changed with the reader's language would stack instead of replacing
    /// the moment somebody used the language switch.</para>
    /// </summary>
    private static string TagFor(PushEventKind kind) =>
        kind.ToString().ToLowerInvariant();

    /// <summary>
    /// Where a link-upload lands, which is the same screen it was started from.
    ///
    /// <para>The files list rather than a per-fetch address: an address with an id in it is an
    /// identifier for one of the customer's own rows, travelling in a notification that outlives
    /// itself on a phone. See <c>PushPayload</c> for why nothing of the customer's is in a payload.
    /// </para>
    /// </summary>
    private const string FilesPath = "/files";

    private const string TrashPath = "/trash";

    private const string AbuseQueuePath = "/operator/abuse";
}
