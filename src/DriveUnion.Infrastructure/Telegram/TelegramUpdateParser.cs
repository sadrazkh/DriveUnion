using System.Text.Json;
using DriveUnion.Core.Application;
using DriveUnion.Core.Telegram;

namespace DriveUnion.Infrastructure.Telegram;

/// <summary>
/// The webhook body, turned into an update or into nothing.
///
/// <para>It never throws. The endpoint that calls it is anonymous and reachable by anything on the
/// box, so a body that is not JSON, is JSON but not an update, or is an update with no
/// <c>update_id</c>, all have to be a null and a 200 rather than an exception page.</para>
/// </summary>
public sealed class TelegramUpdateParser : ITelegramUpdateParser
{
    public TelegramUpdate? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var document = JsonDocument.Parse(json);

            return TelegramWire.ReadUpdate(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
