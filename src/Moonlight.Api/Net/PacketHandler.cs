using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moonlight.Protocol.Net;
using Moonlight.Protocol.VariableTypes;

namespace Moonlight.Api.Net
{
    public sealed class PacketHandler : IDisposable
    {
        private readonly PacketHandlerFactory _factory;
        private readonly Stream _stream;
        private readonly ILogger<PacketHandler> _logger;
        private readonly PipeReader _pipeReader;
        private object? _disposed;

        public PacketHandler(PacketHandlerFactory factory, Stream stream, ILogger<PacketHandler> logger)
        {
            _factory = factory;
            _stream = stream;
            _pipeReader = PipeReader.Create(_stream);
            _logger = logger;
        }

        public async ValueTask<ReadOnlySequence<byte>?> TryReadSequenceAsync(CancellationToken cancellationToken = default)
        {
            ReadResult readResult = await _pipeReader.ReadAsync(cancellationToken);
            if (readResult.IsCanceled || cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            _logger.LogDebug("Read Buffer ({Bytes:N0} bytes): [{Buffer}]", readResult.Buffer.Length, string.Join(", ", readResult.Buffer.ToArray().Select(b => b.ToString("X2"))));
            return readResult.Buffer;
        }

        public async ValueTask<ReadOnlySequence<byte>> TryReadAtLeastSequenceAsync(int minimumBytes, CancellationToken cancellationToken = default)
        {
            while (true)
            {
                if (await TryReadSequenceAsync(cancellationToken) is ReadOnlySequence<byte> sequence && sequence.Length >= minimumBytes)
                {
                    return sequence;
                }
            }
        }

        public void AdvanceTo(SequencePosition position) => _pipeReader.AdvanceTo(position);

        public async ValueTask<T> ReadPacketAsync<T>(CancellationToken cancellationToken = default) where T : IPacket<T>
        {
            if (await TryReadSequenceAsync(cancellationToken) is not ReadOnlySequence<byte> sequence)
            {
                throw new OperationCanceledException();
            }

            T? packet = ReadPacket<T>(sequence, out SequencePosition position);
            if (packet is null)
            {
                _pipeReader.AdvanceTo(sequence.Start, position);
                return await ReadPacketAsync<T>(cancellationToken);
            }

            _pipeReader.AdvanceTo(position);
            return packet;
        }

        public async ValueTask<IPacket> ReadPacketAsync(CancellationToken cancellationToken = default)
        {
            if (await TryReadSequenceAsync(cancellationToken) is not ReadOnlySequence<byte> sequence)
            {
                throw new OperationCanceledException();
            }

            IPacket? packet = ReadPacket(sequence, out SequencePosition position);
            if (packet is null)
            {
                _pipeReader.AdvanceTo(sequence.Start, position);
                return await ReadPacketAsync(cancellationToken);
            }

            _pipeReader.AdvanceTo(position);
            return packet;
        }

        public T? ReadPacket<T>(ReadOnlySequence<byte> sequence, out SequencePosition position) where T : IPacket<T>
        {
            SequenceReader<byte> reader = new(sequence);
            VarInt length = VarInt.Deserialize(ref reader);
            if (length.Value > reader.Remaining)
            {
                position = sequence.End;
                return default;
            }

            VarInt packetId = VarInt.Deserialize(ref reader);
            if (T.Id != packetId)
            {
                _pipeReader.CancelPendingRead();
                throw new InvalidDataException($"Expected packet ID {T.Id}, but got {packetId}");
            }

            reader = new(reader.Sequence.Slice(reader.Position, length.Value - packetId.Length));
            T packet = T.Deserialize(ref reader);
            position = reader.Position;
            return packet;
        }

        public IPacket? ReadPacket(ReadOnlySequence<byte> sequence, out SequencePosition position)
        {
            SequenceReader<byte> reader = new(sequence);
            VarInt length = VarInt.Deserialize(ref reader);
            if (length.Value > reader.Remaining)
            {
                position = sequence.End;
                return null;
            }

            VarInt packetId = VarInt.Deserialize(ref reader);
            if (!_factory.PreparedPacketDeserializers.TryGetValue(packetId.Value, out DeserializerDelegate? packetDeserializerPointer))
            {
                // Grab the unknown packet deserializer
                packetDeserializerPointer = UnknownPacket.Deserialize;

                // Rewind so the unknown packet can store the received packet ID.
                reader.Rewind(packetId.Length);
            }

            reader = new(reader.Sequence.Slice(reader.Position, length.Value - packetId.Length));
            IPacket packet = packetDeserializerPointer(ref reader);
            position = reader.Position;
            return packet;
        }

        public void Dispose()
        {
            if (_disposed is not null)
            {
                return;
            }

            _disposed = new object();
            _pipeReader.Complete();
            _stream.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}