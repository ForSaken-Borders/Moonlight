using Moonlight.Api.Net;
using Moonlight.Protocol.Net;

namespace Moonlight.Api.Events.EventArgs
{
    public sealed class PacketReceivedAsyncServerEventArgs : AsyncServerEventArgs
    {
        public required PacketHandler PacketHandler { get; init; }
        public required IPacket Packet { get; init; }
    }
}
