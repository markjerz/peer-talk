using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ipfs;
using Makaretu.Dns.Resolving;
using PeerTalk.Protocols;
using PeerTalk.Transports;
using ProtoBuf;
using Semver;

namespace PeerTalk.Relay
{

    /// <summary>
    /// Protocol for handling relay requests
    ///
    /// See https://github.com/libp2p/specs/blob/master/relay/README.md
    /// </summary>
    public class Relay : IPeerProtocol, IPeerTransport
    {
        private Func<Stream, MultiAddress, MultiAddress, Task> handler;

        /// <inheritdoc />
        public string Name => "/libp2p/circuit/relay";
        
        /// <inheritdoc />
        public SemVersion Version => new SemVersion(0, 1);
        
        /// <summary>
        /// Indicates that this node will operate as a relay node
        /// </summary>
        public bool Hop { get; set; }

        /// <summary>
        ///   Provides access to other peers.
        /// </summary>
        public Swarm Swarm { get; set; }

        /// <summary>
        /// A list of the known
        /// </summary>
        public RelayCollection KnownRelays { get; set; }
        
        /// <inheritdoc />
        public override string ToString()
        {
            return $"{Name}/{Version}";
        }

        /// <inheritdoc />
        public async Task<Stream> ConnectAsync(MultiAddress address, CancellationToken cancel = default(CancellationToken))
        {
            if (!address.HasPeerId)
            {
                throw new InvalidDataException("The address must contain the destination peer id");
            }

            if (!address.IsP2PCircuitAddress())
            {
                throw new InvalidDataException("The address is not a p2p-circuit address");
            }

            var circuitAddress = address.ToP2PCircuitMultiAddress();
            if (circuitAddress.RelayAddress != null)
            {
                var relayConnection = await this.Swarm.ConnectAsync(circuitAddress.RelayAddress, cancel);
                var stream = await this.HopAsync(relayConnection, circuitAddress.DestinationAddress, cancel);
                if (stream != null)
                {
                    return stream;
                }
            }

            foreach (var relayAddress in this.KnownRelays.RelayAddresses)
            {
                var relayConnection = await this.Swarm.ConnectAsync(relayAddress, cancel);
                var stream = await this.HopAsync(relayConnection, circuitAddress.DestinationAddress, cancel);
                if (stream != null)
                {
                    return stream;
                }
            }

            return null;
        }

        private async Task<Stream> HopAsync(PeerConnection relayConnection, MultiAddress destinationAddress,
            CancellationToken cancel)
        {
            var relayStream = await this.Swarm.DialAsync(relayConnection.RemotePeer, this.ToString(), cancel);
            
            // send the hop
            await SendRelayMessageAsync(new CircuitRelayMessage
            {
                Type = Type.HOP,
                SrcPeer = new RelayPeerMessage
                {
                    Id = this.Swarm.LocalPeer.Id.ToArray(),
                    Addresses = this.Swarm.LocalPeer.Addresses.Select(a => a.ToArray()).ToArray()
                },
                DstPeer = new RelayPeerMessage
                {
                    Id = destinationAddress.PeerId.ToArray(),
                    Addresses = new [] { destinationAddress.ToArray() }
                }
            }, relayStream, cancel);

            var response = await ProtoBufHelper.ReadMessageAsync<CircuitRelayMessage>(relayStream, cancel).ConfigureAwait(false);
            if (response.IsSuccess())
            {
                return relayStream;
            }

            // TODO handle various responses (if they can't hop should be ignore the relay for a while etc)
            // (read the specs and handle all appropriately)
            return null;
        }

        /// <inheritdoc />
        public MultiAddress Listen(MultiAddress address, Func<Stream, MultiAddress, MultiAddress, Task> handler, CancellationToken cancel)
        {
            this.Swarm.AddProtocol(this);
            this.handler = handler;
            return address;
        }

        /// <inheritdoc />
        public async Task ProcessMessageAsync(PeerConnection connection, Stream stream,
            CancellationToken cancel = default(CancellationToken))
        {
            var request = await ProtoBufHelper.ReadMessageAsync<CircuitRelayMessage>(stream, cancel).ConfigureAwait(false);
            switch (request.Type)
            {
                case Type.CAN_HOP:
                     await HandleCanHopAsync(request, connection, stream, cancel);
                    break;

                case Type.HOP:
                     await HandleHopAsync(request, connection, stream, cancel);
                    break;

                case Type.STOP:
                     await HandleStopAsync(request, stream, cancel);
                    break;

                case Type.STATUS:
                     await HandleStatusAsync(request);
                    break;

                default:
                    await SendRelayMessageAsync(
                        CircuitRelayMessage.NewStatusResponse(Status.MALFORMED_MESSAGE), stream, cancel);
                    break;
            }
        }

        private static async Task SendRelayMessageAsync(CircuitRelayMessage response, Stream stream, CancellationToken cancel)
        {
            ProtoBuf.Serializer.SerializeWithLengthPrefix(stream, response, PrefixStyle.Base128);
            await stream.FlushAsync(cancel).ConfigureAwait(false);
        }

        private async Task HandleCanHopAsync(CircuitRelayMessage request, PeerConnection connection, Stream stream,
            CancellationToken cancel)
        {
            var response = this.Hop 
                ? CircuitRelayMessage.NewStatusResponse(Status.SUCCESS) 
                : CircuitRelayMessage.NewStatusResponse(Status.HOP_CANT_SPEAK_RELAY);
            await SendRelayMessageAsync(response, stream, cancel);
        }

        private async Task HandleHopAsync(
            CircuitRelayMessage request, 
            PeerConnection connection,
            Stream srcStream,
            CancellationToken cancel)
        {
            if (!this.Hop)
            {
                await SendRelayMessageAsync(CircuitRelayMessage.NewStatusResponse(Status.HOP_CANT_SPEAK_RELAY), srcStream, cancel);
                return;
            }

            // TODO implement hop limits

            if (!request.SrcPeer.TryToPeer(out var srcPeer))
            {
                await SendRelayMessageAsync(CircuitRelayMessage.NewStatusResponse(Status.HOP_SRC_MULTIADDR_INVALID), srcStream, cancel);
                return;
            }

            if (connection.RemotePeer != srcPeer)
            {
                await SendRelayMessageAsync(CircuitRelayMessage.NewStatusResponse(Status.HOP_SRC_MULTIADDR_INVALID), srcStream, cancel);
                return;
            }

            if (!request.DstPeer.TryToPeer(out var dstPeer))
            {
                await SendRelayMessageAsync(CircuitRelayMessage.NewStatusResponse(Status.HOP_DST_MULTIADDR_INVALID), srcStream, cancel);
                return;
            }

            if (dstPeer == connection.LocalPeer)
            {
                await SendRelayMessageAsync(CircuitRelayMessage.NewStatusResponse(Status.HOP_CANT_RELAY_TO_SELF), srcStream, cancel);
                return;
            }

            // TODO prevent active relay through option
            var dstStream = await Swarm.DialAsync(dstPeer, this.ToString(), cancel);
            var stopRequest = new CircuitRelayMessage
            {
                Type = Type.STOP,
                SrcPeer = request.SrcPeer,
                DstPeer = request.DstPeer
            };
            await SendRelayMessageAsync(stopRequest, dstStream, cancel);

            var stopResponse = await ProtoBufHelper.ReadMessageAsync<CircuitRelayMessage>(dstStream, cancel).ConfigureAwait(false);
            if (stopResponse.IsSuccess())
            {
                await SendRelayMessageAsync(CircuitRelayMessage.NewStatusResponse(Status.SUCCESS), srcStream, cancel);
                var srcToDestTask = Pipe(srcStream, dstStream);
                var dstToSrcTask = Pipe(dstStream, srcStream);
                while (true)
                {
                    await Task.WhenAny(srcToDestTask, dstToSrcTask);
                    if (srcToDestTask.IsCompleted)
                    {
                        srcToDestTask = Pipe(srcStream, dstStream);
                    }
                    else
                    {
                        dstToSrcTask = Pipe(dstStream, srcStream);
                    }

                    if (cancel.IsCancellationRequested)
                    {
                        break;
                    }
                }
            }

            async Task Pipe(Stream inStream, Stream outStream)
            {
                var buffer = new byte[1024];
                var bytesRead = await inStream.ReadAsync(buffer, 0, 1024, cancellationToken: cancel);
                await outStream.WriteAsync(buffer, 0, bytesRead, cancel);
            }
        }

        private async Task HandleStopAsync(CircuitRelayMessage request, Stream srcStream, CancellationToken cancel)
        {
            if (!request.SrcPeer.TryToPeer(out var srcPeer))
            {
                await SendRelayMessageAsync(CircuitRelayMessage.NewStatusResponse(Status.HOP_SRC_MULTIADDR_INVALID), srcStream, cancel);
                return;
            }
            
            if (!request.DstPeer.TryToPeer(out var dstPeer))
            {
                await SendRelayMessageAsync(CircuitRelayMessage.NewStatusResponse(Status.HOP_DST_MULTIADDR_INVALID), srcStream, cancel);
                return;
            }

            await SendRelayMessageAsync(CircuitRelayMessage.NewStatusResponse(Status.SUCCESS), srcStream, cancel);
            await this.handler(srcStream, dstPeer.Addresses.First(), srcPeer.Addresses.First());
        }

        private Task HandleStatusAsync(CircuitRelayMessage request)
        {
            return Task.CompletedTask;
        }

        [ProtoContract]
        class CircuitRelayMessage 
        {
            public static CircuitRelayMessage NewStatusResponse(Status code)
            {
                return new CircuitRelayMessage
                {
                    Code = code,
                    Type = Type.STATUS
                };
            }

            [ProtoMember(1)]
            public Type Type { get; set; }

            [ProtoMember(2)]
            public RelayPeerMessage SrcPeer { get; set; }

            [ProtoMember(3)]
            public RelayPeerMessage DstPeer { get; set; }

            [ProtoMember(4)]
            public Status Code { get; set; }

            public bool IsSuccess()
            {
                return this.Code == Status.SUCCESS;
            }
        }

        [ProtoContract]
        class RelayPeerMessage
        {
            [ProtoMember(1, IsRequired = true)]
            public byte[] Id { get; set; }

            [ProtoMember(2, IsRequired = true)]
            public byte[][] Addresses { get; set; }
            
            /// <summary>
            ///   Convert the message into a <see cref="Peer"/>.
            /// </summary>
            /// <param name="peer"></param>
            /// <returns></returns>
            public bool TryToPeer(out Peer peer)
            {
                peer = null;

                // Sanity checks.
                if (Id == null || Id.Length == 0)
                    return false;

                var id = new MultiHash(Id);
                peer = new Peer
                {
                    Id = id
                };
                if (Addresses != null)
                {
                    peer.Addresses = Addresses
                        .Select(bytes =>
                        {
                            try
                            {
                                return new MultiAddress(bytes);
                            }
                            catch
                            {
                                return null;
                            }
                        })
                        .Where(a => a != null)
                        .ToArray();
                }

                return true;
            }
        }

        enum Status
        {
            SUCCESS = 100,
            HOP_SRC_ADDR_TOO_LONG = 220,
            HOP_DST_ADDR_TOO_LONG = 221,
            HOP_SRC_MULTIADDR_INVALID = 250,
            HOP_DST_MULTIADDR_INVALID = 251,
            HOP_NO_CONN_TO_DST = 260,
            HOP_CANT_DIAL_DST = 261,
            HOP_CANT_OPEN_DST_STREAM = 262,
            HOP_CANT_SPEAK_RELAY = 270,
            HOP_CANT_RELAY_TO_SELF = 280,
            HOP_BACKOFF = 290,
            STOP_SRC_ADDR_TOO_LONG = 320,
            STOP_DST_ADDR_TOO_LONG = 321,
            STOP_SRC_MULTIADDR_INVALID = 350,
            STOP_DST_MULTIADDR_INVALID = 351,
            STOP_RELAY_REFUSED = 390,
            MALFORMED_MESSAGE = 400
        }

        enum Type
        { // RPC identifier, either HOP, STOP or STATUS
            HOP = 1,
            STOP = 2,
            STATUS = 3,
            CAN_HOP = 4 // is peer a relay?
        }
    }
}