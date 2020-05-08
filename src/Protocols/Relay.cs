using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ipfs;
using ProtoBuf;
using Semver;

namespace PeerTalk.Protocols
{
    /// <summary>
    /// Protocol for handling relay requests
    ///
    /// See https://github.com/libp2p/specs/blob/master/relay/README.md
    /// </summary>
    public class Relay : IPeerProtocol
    {
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
        
        /// <inheritdoc />
        public override string ToString()
        {
            return $"{Name}/{Version}";
        }

        /// <inheritdoc />
        public async Task ProcessMessageAsync(PeerConnection connection, Stream stream,
            CancellationToken cancel = default(CancellationToken))
        {
            while (true)
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
            ProtoBuf.Serializer.SerializeWithLengthPrefix(dstStream, stopRequest, PrefixStyle.Base128);
            await dstStream.FlushAsync(cancel).ConfigureAwait(false);

            var stopResponse = await ProtoBufHelper.ReadMessageAsync<CircuitRelayMessage>(dstStream, cancel).ConfigureAwait(false);
            if (stopResponse.IsSuccess())
            {
                await SendRelayMessageAsync(CircuitRelayMessage.NewStatusResponse(Status.SUCCESS), srcStream, cancel);
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
            await Swarm.ConnectRemoteAsync(srcStream, dstPeer.Addresses.First(), srcPeer.Addresses.First());
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
                    var x = new MultiAddress($"/ipfs/{id}");
                    peer.Addresses = Addresses
                        .Select(bytes =>
                        {
                            try
                            {
                                var ma = new MultiAddress(bytes);
                                ma.Protocols.AddRange(x.Protocols);
                                return ma;
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