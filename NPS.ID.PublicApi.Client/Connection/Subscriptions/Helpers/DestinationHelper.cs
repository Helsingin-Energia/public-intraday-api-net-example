using System.Globalization;
using NPS.ID.PublicApi.Client.Connection.Enums;

namespace NPS.ID.PublicApi.Client.Connection.Subscriptions.Helpers;

public static class DestinationHelper
{
    public static string ComposeDestination(string user, string version, PublishingMode mode, string topic)
    {
        return ComposeDestination(user, version, $"{mode.ToString().ToLower(CultureInfo.InvariantCulture)}/{topic}");
    }

    public static string ComposeDestination(string user, string version, string topic)
    {
        return $"/user/{user}/{version}/{topic}";
    }

    public static string ComposeDestination(string version, string topic)
    {
        return $"/{version}/{topic}";
    }
}