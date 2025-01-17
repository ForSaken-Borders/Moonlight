using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moonlight.Api.Events;
using Moonlight.Api.Events.EventArgs;
using Moonlight.Api.Net;
using Moonlight.Protocol.Net;
using Moonlight.Protocol.Net.HandshakeState;

namespace Moonlight.Api
{
    public sealed class Server
    {
        public ServerConfiguration Configuration { get; init; }
        public CancellationToken CancellationToken => _cancellationTokenSource.Token;

        private readonly ILogger<Server> _logger;
        private readonly PacketHandlerFactory _playPacketReaderFactory;
        private readonly PacketHandlerFactory _handshakePacketReaderFactory;
        private readonly CancellationTokenSource _cancellationTokenSource = new();

        private readonly AsyncServerEvent<PacketReceivedAsyncServerEventArgs> _packetReceivedServerEvent;
        private readonly AsyncServerEvent<HandshakeAsyncServerEventArgs> _handshakeServerEvent;

        public Server(ServerConfiguration serverConfiguration, IServiceProvider serviceProvider, AsyncServerEventContainer asyncServerEventContainer, ILogger<Server> logger)
        {
            Configuration = serverConfiguration;
            _handshakePacketReaderFactory = serviceProvider.GetRequiredKeyedService<PacketHandlerFactory>("Moonlight.Handshake");
            _playPacketReaderFactory = serviceProvider.GetRequiredKeyedService<PacketHandlerFactory>("Moonlight.Play");
            _logger = logger;

            _packetReceivedServerEvent = asyncServerEventContainer.GetAsyncServerEvent<PacketReceivedAsyncServerEventArgs>();
            _handshakeServerEvent = asyncServerEventContainer.GetAsyncServerEvent<HandshakeAsyncServerEventArgs>();
        }

        public async Task StartAsync()
        {
            _handshakePacketReaderFactory.Prepare();

            _logger.LogInformation("Starting server...");
            TcpListener listener = new(IPAddress.Parse(Configuration.Host), Configuration.Port);
            listener.Start();

            _logger.LogInformation("Server started on {EndPoint}", listener.LocalEndpoint);
            while (!_cancellationTokenSource.IsCancellationRequested)
            {
                _ = HandshakeClientAsync(await listener.AcceptTcpClientAsync(CancellationToken));
            }
        }

        private async Task HandshakeClientAsync(TcpClient client)
        {
            static bool TryParseLegacyPing(ReadOnlySequence<byte> sequence, out SequencePosition position, [NotNullWhen(true)] out HandshakePacket? packet)
            {
                if (sequence.Length < 2 || sequence.FirstSpan[0] != 0xFE)
                {
                    packet = null;
                    position = sequence.Start;
                    return false;
                }

                SequenceReader<byte> reader = new(sequence);
                bool result = LegacyPingPacket.TryDeserialize(ref reader, out packet);
                position = reader.Position;
                return result;
            }

            _logger.LogInformation("Client connected: {EndPoint}", client.Client.RemoteEndPoint);

            PacketHandler reader = _handshakePacketReaderFactory.Create(client.GetStream());
            if (await reader.TryReadSequenceAsync(CancellationToken) is ReadOnlySequence<byte> sequence)
            {
                if (!TryParseLegacyPing(sequence, out SequencePosition position, out HandshakePacket? handshakePacket))
                {
                    handshakePacket = reader.ReadPacket<HandshakePacket>(sequence, out position);
                    if (handshakePacket is null)
                    {
                        _logger.LogWarning("Failed to parse handshake packet.");
                        return;
                    }
                }

                // Reset the reader since we now know it's not a legacy ping packet
                reader.AdvanceTo(position);
                await _packetReceivedServerEvent.InvokeAsync(new PacketReceivedAsyncServerEventArgs()
                {
                    Packet = handshakePacket,
                    PacketHandler = reader
                });

                await _handshakeServerEvent.InvokeAsync(new HandshakeAsyncServerEventArgs()
                {
                    HandshakePacket = handshakePacket,
                    PacketHandler = reader
                });

                if (handshakePacket.NextState == 1)
                {
                    await HandleServerStatusAsync(client, reader);
                }
                else if (handshakePacket.NextState == 2)
                {
                    _ = HandleLoginAsync(reader);
                }
            }
        }

        private async Task HandleServerStatusAsync(TcpClient client, PacketHandler reader)
        {
            CancellationTokenSource clientTimeoutCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);

            try
            {
                while (!CancellationToken.IsCancellationRequested)
                {
                    if (!clientTimeoutCancellationSource.TryReset())
                    {
                        clientTimeoutCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
                    }

                    clientTimeoutCancellationSource.CancelAfter(Configuration.ClientTimeout);
                    IPacket? packet = await reader.ReadPacketAsync(clientTimeoutCancellationSource.Token);
                    if (packet is null)
                    {
                        return;
                    }

                    await _packetReceivedServerEvent.InvokeAsync(new PacketReceivedAsyncServerEventArgs()
                    {
                        Packet = packet,
                        PacketHandler = reader
                    });
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Client timed out: {EndPoint}", client.Client.RemoteEndPoint);
            }
        }

        // TODO: Disconnect immediately.
        private Task HandleLoginAsync(PacketHandler reader) => Task.CompletedTask;
    }
}
