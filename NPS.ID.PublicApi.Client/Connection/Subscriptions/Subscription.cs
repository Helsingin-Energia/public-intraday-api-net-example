using Stomp.Net.Stomp.Protocol;

namespace NPS.ID.PublicApi.Client.Connection.Subscriptions;

public abstract class Subscription : ISubscription
{
    public string Id { get; }
    public string Type { get; }
    public string Destination { get; }
    public long SequenceNumber { get; set; }

    protected Subscription(string id, string type, string destination)
    {
        Id = id;
        Type = type;
        Destination = destination;
    }

    public abstract void OnMessage(StompFrame frame, DateTimeOffset timestamp);
    public abstract void Close();
}