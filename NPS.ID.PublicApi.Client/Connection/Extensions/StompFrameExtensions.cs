using System.Text;
using System.Text.Json;
using NPS.ID.PublicApi.Client.Connection.Enums;
using NPS.ID.PublicApi.Client.Connection.Messages;
using Stomp.Net.Stomp.Protocol;

namespace NPS.ID.PublicApi.Client.Connection.Extensions;

public static class StompFrameExtensions
{
    public static bool IsSnapshot(this StompFrame frame)
    {
        return frame.Properties.TryGetValue(Headers.Server.IsSnapshot, out var isSnapshotString) && string.Equals(isSnapshotString, "true", StringComparison.Ordinal);
    }

    public static string GetDestination(this StompFrame frame)
    {
        _ = frame.Properties.TryGetValue(Headers.Destination, out var destination);
        return destination;
    }

    public static long GetSequenceNumber(this StompFrame frame)
    {
        _ = frame.Properties.TryGetValue(Headers.Server.SequenceNumber, out var sequenceNumber);
        return long.TryParse(sequenceNumber, out var sequenceNumberLong) ? sequenceNumberLong : 0;
    }

    public static PublishingMode GetPublishingMode(this StompFrame frame)
    {
        return frame.Properties.TryGetValue(Headers.Destination, out var dest) && dest.Contains("/streaming/", StringComparison.Ordinal)
            ? PublishingMode.STREAMING
            : PublishingMode.CONFLATED;
    }

    public static DateTimeOffset? GetSentAtTimestamp(this StompFrame frame)
    {
        return frame.Properties.TryGetValue(Headers.Server.SentAt, out var sentAtHeaderValue) && long.TryParse(sentAtHeaderValue, out var sentAtMs)
            ? DateTimeOffset.FromUnixTimeMilliseconds(sentAtMs)
            : null;
    }

    public static Task SendStompFrameAsync(this WebSocketConnector connector, StompFrame frame, CancellationToken cancellationToken)
    {
        return connector.SendAsync(frame.ConvertToMessageBytes(), cancellationToken);
    }

    public static byte[] ConvertToMessageBytes(this StompFrame frame)
    {
        var messageText = frame.ToMessageText();
        var serializedJsonArray = JsonSerializer.Serialize(new[] { messageText });
        return Encoding.UTF8.GetBytes(serializedJsonArray);
    }

    private static string ToMessageText(this StompFrame frame)
    {
        var bytes = FrameToBytes(frame);
        return Encoding.UTF8.GetString(bytes);
    }

    private static byte[] FrameToBytes(StompFrame frame)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8);

        frame.ToStream(bw);

        return ms.ToArray();
    }

    public static StompFrame ConvertToStompFrame(this ReceivedMessage message)
    {
        var messageStream = message.GetStream();
        //Remove the first char 'a' to get the json array
        messageStream.Seek(1, SeekOrigin.Begin);

        using var streamReader = new StreamReader(messageStream);
        var stompMessage = JsonSerializer.Deserialize<string[]>(streamReader.ReadToEnd())[0];

        using var memoryStream = new MemoryStream(Encoding.UTF8.GetBytes(stompMessage));
        using var binaryReader = new BinaryReader(memoryStream, Encoding.UTF8);

        var frame = new StompFrame(encodingEnabled: true);
        frame.FromStream(binaryReader);

        return frame;
    }
}