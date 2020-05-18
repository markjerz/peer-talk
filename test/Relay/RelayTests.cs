using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Ipfs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nerdbank.Streams;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;
using PeerTalk.Cryptography;
using PeerTalk.Protocols;
using PeerTalk.Transports;
using ProtoBuf;

namespace PeerTalk.Relay
{
    [TestClass]
    public class RelayTests
    {
        [TestMethod]
        public async Task JustTestRelay()
        {
            var mockLocalDialer = new MockDialer();
            var localPeer = new Peer
            {
                Id = "QmdpwjdB94eNm2Lcvp9JqoCxswo3AKQqjLuNZyLixmCM1h",
                Addresses = new MultiAddress[]
                    {"/ip4/0.0.0.0/tcp/4001/p2p/QmdpwjdB94eNm2Lcvp9JqoCxswo3AKQqjLuNZyLixmCM1h"}
            };
            var localPeerRelay = new Relay()
            {
                Dialer = mockLocalDialer,
                LocalPeer = localPeer
            };

            var mockRelayDialer = new MockDialer();
            var relayPeer = new Peer
            {
                Id = "QmXK9VBxaXFuuT29AaPUTgW3jBWZ9JgLVZYdMYTHC6LLAH",
                Addresses = new MultiAddress[]
                    {"/ip4/0.0.0.0/tcp/4002/p2p/QmXK9VBxaXFuuT29AaPUTgW3jBWZ9JgLVZYdMYTHC6LLAH"}
            };
            var hopRelay = new Relay()
            {
                LocalPeer = relayPeer,
                Hop = true,
                Dialer = mockRelayDialer
            };

            var remotePeer = new Peer
            {
                Id = "QmbFgm5zan8P6eWWmeyfncR5feYEMPbht5b1FW1C37aQ7y",
                Addresses = new MultiAddress[]
                    {"/ip4/0.0.0.0/tcp/4003/p2p/QmbFgm5zan8P6eWWmeyfncR5feYEMPbht5b1FW1C37aQ7y"}
            };
            var remotePeerRelay = new Relay
            {
                LocalPeer = remotePeer
            };

            var localToRelayConnection = FullDuplexStream.CreatePair();
            var localToRelayPeerConnection = new PeerConnection
            {
                IsIncoming = false,
                Stream = localToRelayConnection.Item1,
                LocalPeer = localPeer,
                RemotePeer = relayPeer
            };

            var relayFromLocalPeerConnection = new PeerConnection
            {
                IsIncoming = true,
                Stream = localToRelayConnection.Item2,
                LocalPeer = relayPeer,
                RemotePeer = localPeer
            };

            var remoteToRelayConnection = FullDuplexStream.CreatePair();
            var remoteToRelayPeerConnection = new PeerConnection
            {
                IsIncoming = false,
                Stream = remoteToRelayConnection.Item1,
                LocalPeer = remotePeer,
                RemotePeer = relayPeer
            };
            var relayFromRemotePeerConnection = new PeerConnection()
            {
                IsIncoming = true,
                Stream = remoteToRelayConnection.Item2,
                LocalPeer = relayPeer,
                RemotePeer = remotePeer
            };

            mockLocalDialer.AddConnection(
                relayPeer.Addresses.First(), 
                localToRelayPeerConnection);
            mockRelayDialer.AddConnection(
                remotePeer.Addresses.First(),
                relayFromRemotePeerConnection);

            remotePeerRelay.Listen(remotePeer.Addresses.First(),
                (stream, localAddress, remoteAddress) =>
                {
                    Task.Run(() =>
                    {
                        while (true)
                        {
                            // echo
                            byte[] buffer = new byte[1024];
                            var read = stream.Read(buffer, 0, 1024);
                            stream.Write(buffer, 0, read);
                            stream.Flush();
                        }
                    });
                    return Task.CompletedTask;
                }, CancellationToken.None);
            
            var remoteRelayAddress = new MultiAddress($"{relayPeer.Addresses.First()}/p2p-circuit/p2p/{remotePeer.Id}");
            var relayTask = Task.Run(() =>
                hopRelay.ProcessMessageAsync(relayFromLocalPeerConnection, relayFromLocalPeerConnection.Stream));
            var connectTask = Task.Run(async () =>
            {
                var connection = await localPeerRelay.ConnectAsync(remoteRelayAddress);
                var hello = Encoding.UTF8.GetBytes("Hello");
                connection.Write(hello, 0, hello.Length);
                connection.Flush();
                var response = new byte[1024];
                var read = connection.Read(response, 0, 1024);
                Assert.AreEqual("Hello", Encoding.UTF8.GetString(response.AsSpan().Slice(0, read).ToArray()));
            });
            var stopTask = Task.Run(() =>
                remotePeerRelay.ProcessMessageAsync(remoteToRelayPeerConnection, remoteToRelayPeerConnection.Stream));
            
            
            await relayTask;
            await connectTask;
            await stopTask;
        }

        class MockDialer : IDialer
        {
            IDictionary<MultiAddress, PeerConnection> connections = new Dictionary<MultiAddress, PeerConnection>();

            public void AddConnection(MultiAddress address, PeerConnection peerConnection)
            {
                if (connections.ContainsKey(address))
                {
                    throw new ArgumentException();
                }

                connections.Add(address, peerConnection);
            }

            public Task<PeerConnection> ConnectAsync(MultiAddress address, CancellationToken cancel = default(CancellationToken))
            {
                if (!connections.TryGetValue(address, out var connection))
                {
                    throw new ArgumentOutOfRangeException();
                }

                return Task.FromResult(connection);
            }

            public Task<PeerConnection> ConnectAsync(Peer peer, CancellationToken cancel = default(CancellationToken))
            {
                throw new NotImplementedException();
            }

            public Task<Stream> DialAsync(Peer peer, string protocol, CancellationToken cancel = default(CancellationToken))
            {
                foreach (var peerConnection in connections.Values)
                {
                    if (peerConnection.RemotePeer == peer)
                    {
                        return Task.FromResult(peerConnection.Stream);
                    }
                }

                throw new ArgumentException();
            }
        }


        [TestMethod]
        public async Task TestEndToEnd()
        {
            var keyA = GetKey();
            var keyB = GetKey();
            var relayKey = GetKey();

            Peer peerA = new Peer
            {
                AgentVersion = "A",
                Id = CreateKeyId(keyA.Public),
                PublicKey = CreatePublicKey(keyA)
            };
            Peer peerB = new Peer
            {
                AgentVersion = "B",
                Id = CreateKeyId(keyB.Public),
                PublicKey = CreatePublicKey(keyB)
            };
            Peer relay = new Peer
            {
                AgentVersion = "R",
                Id = CreateKeyId(relayKey.Public),
                PublicKey = CreatePublicKey(relayKey)
            };

            var swarmA = new Swarm()
            {
                LocalPeer = peerA,
                LocalPeerKey = Key.CreatePrivateKey(keyA.Private)
            };
            var swarmB = new Swarm()
            {
                LocalPeer = peerB,
                LocalPeerKey = Key.CreatePrivateKey(keyB.Private)
            };
            var relaySwarm = new Swarm()
            {
                LocalPeer = relay,
                LocalPeerKey = Key.CreatePrivateKey(relayKey.Private)
            };

            var pingA = new Ping1 { Swarm = swarmA };
            await pingA.StartAsync();
            var pingB = new Ping1() { Swarm = swarmB };
            await pingB.StartAsync();

            // for debugging
            swarmA.TransportConnectionTimeout = TimeSpan.FromHours(1);
            swarmB.TransportConnectionTimeout = TimeSpan.FromHours(1);
            relaySwarm.TransportConnectionTimeout = TimeSpan.FromHours(1);

            // switch to fake tcp
            SwapToFakeTcp(swarmA);
            SwapToFakeTcp(swarmB);
            SwapToFakeTcp(relaySwarm);

            await swarmA.StartAsync();
            await swarmB.StartAsync();
            await relaySwarm.StartAsync();

            await swarmA.StartListeningAsync("/ip4/0.0.0.0/tcp/4002");
            await swarmB.StartListeningAsync("/ip4/0.0.0.0/tcp/4001");
            await relaySwarm.StartListeningAsync("/ip4/0.0.0.0/tcp/4003");

            var relayCollection = new RelayCollection();
            relayCollection.Add(relay.Addresses.First(a => a.Protocols.Any(p => p.Name == "tcp")));
            swarmA.EnableRelay(relayCollection);
            swarmB.EnableRelay(relayCollection);
            relaySwarm.EnableRelay(relayCollection, hop: true);
            //await swarmA.StartListeningAsync("/p2p-circuit");
            await swarmB.StartListeningAsync("/p2p-circuit");
            await relaySwarm.StartListeningAsync("/p2p-circuit");

            var bRelayConn = await swarmB.ConnectAsync(relayCollection.RelayAddresses.First());

            var connectionToBThroughRelay = await swarmA.ConnectAsync(new MultiAddress($"/p2p-circuit/p2p/{peerB.Id}"));
            Assert.IsNotNull(connectionToBThroughRelay);

            var response = await pingA.PingAsync(peerB.Id);
            Assert.IsTrue(response.All(p => p.Success));


            void SwapToFakeTcp(Swarm swarm)
            {
                swarm.DeregisterTransport("tcp");
                swarm.RegisterTransport("tcp", () => new FakeTcpTransport());
            }
        }

        class FakeTcpTransport : IPeerTransport
        {
            public Task<Stream> ConnectAsync(MultiAddress address, CancellationToken cancel = default(CancellationToken))
            {
                if (!listeners.TryGetValue(GetPort(address), out var handler))
                {
                    return null;
                }

                var streamPair = FullDuplexStream.CreatePair();
                handler(streamPair.Item2, myAddress, address);
                return Task.FromResult(streamPair.Item1);
            }

            /// <summary>
            /// port to handler lookup, as we listen on different ports for different peers
            /// </summary>
            private static Dictionary<int, Func<Stream, MultiAddress, MultiAddress, Task>> listeners = new Dictionary<int, Func<Stream, MultiAddress, MultiAddress, Task>>();
            private MultiAddress myAddress;

            public MultiAddress Listen(MultiAddress address, Func<Stream, MultiAddress, MultiAddress, Task> handler, CancellationToken cancel)
            {
                this.myAddress = address;
                listeners.Add(GetPort(address), handler);
                return address;
            }

            private static int GetPort(MultiAddress address)
            {
                return int.Parse(address.Protocols.First(p => p.Name == "tcp").Value);
            }
        }

        //[TestMethod()]
        public async Task RelayConnectionWorks()
        {
            var keyA = GetKey();
            var keyB = GetKey();
            var relayKey = GetKey();

            Peer peerA = new Peer
            {
                AgentVersion = "A",
                Id = CreateKeyId(keyA.Public),
                PublicKey = CreatePublicKey(keyA)
            };
            Peer peerB = new Peer
            {
                AgentVersion = "B",
                Id = CreateKeyId(keyB.Public),
                PublicKey = CreatePublicKey(keyB)
            };
            Peer relay = new Peer
            {
                AgentVersion = "R",
                Id = CreateKeyId(relayKey.Public),
                PublicKey = CreatePublicKey(relayKey)
            };

            var swarmA = new Swarm()
            {
                LocalPeer = peerA,
                LocalPeerKey = Key.CreatePrivateKey(keyA.Private)
            };
            var swarmB = new Swarm()
            {
                LocalPeer = peerB,
                LocalPeerKey = Key.CreatePrivateKey(keyB.Private)
            };
            var relaySwarm = new Swarm()
            {
                LocalPeer = relay,
                LocalPeerKey = Key.CreatePrivateKey(relayKey.Private)
            };

            var pingA = new Ping1 {Swarm = swarmA};
            await pingA.StartAsync();
            var pingB = new Ping1() {Swarm = swarmB};
            await pingB.StartAsync();

            // for debugging
            swarmA.TransportConnectionTimeout = TimeSpan.FromHours(1);
            swarmB.TransportConnectionTimeout = TimeSpan.FromHours(1);
            relaySwarm.TransportConnectionTimeout = TimeSpan.FromHours(1);

            await swarmA.StartAsync();
            await swarmB.StartAsync();
            await relaySwarm.StartAsync();

            // connect b to relay
            await swarmA.StartListeningAsync("/ip4/0.0.0.0/tcp/4002");
            await swarmB.StartListeningAsync("/ip4/0.0.0.0/tcp/4001");
            await relaySwarm.StartListeningAsync("/ip4/0.0.0.0/tcp/4003");

            var relayCollection = new RelayCollection();
            relayCollection.Add(relay.Addresses.First(a => a.Protocols.Any(p => p.Name == "tcp")));
            swarmA.EnableRelay(relayCollection);
            swarmB.EnableRelay(relayCollection);
            relaySwarm.EnableRelay(relayCollection, hop: true);
            //await swarmA.StartListeningAsync("/p2p-circuit");
            await swarmB.StartListeningAsync("/p2p-circuit");
            await relaySwarm.StartListeningAsync("/p2p-circuit");

            var bRelayConn = await swarmB.ConnectAsync(relayCollection.RelayAddresses.First());

            var connectionToBThroughRelay = await swarmA.ConnectAsync(new MultiAddress($"/p2p-circuit/p2p/{peerB.Id}"));
            Assert.IsNotNull(connectionToBThroughRelay);

            var response = await pingA.PingAsync(peerB.Id);
            Assert.IsTrue(response.All(p => p.Success));
        }

        private AsymmetricCipherKeyPair GetKey()
        {
            var keyAGen = GeneratorUtilities.GetKeyPairGenerator("RSA");
            keyAGen.Init(new RsaKeyGenerationParameters(BigInteger.ValueOf(0x10001), new SecureRandom(), 2048, 25));
            var asymmetricCipherKeyPair = keyAGen.GenerateKeyPair();
            return asymmetricCipherKeyPair;
        }

        string CreatePublicKey(AsymmetricCipherKeyPair key)
        {
            var spki = SubjectPublicKeyInfoFactory
                .CreateSubjectPublicKeyInfo(key.Public)
                .GetDerEncoded();
            // Add protobuf cruft.
            var publicKey = new PublicKeyMessage()
            {
                Data = spki
            };
            if (key.Public is RsaKeyParameters)
                publicKey.Type = KeyType.RSA;
            else if (key.Public is Ed25519PublicKeyParameters)
                publicKey.Type = KeyType.Ed25519;
            else if (key.Public is ECPublicKeyParameters)
                publicKey.Type = KeyType.Secp256k1;
            else
                throw new NotSupportedException($"The key type {key.Public.GetType().Name} is not supported.");

            using (var ms = new MemoryStream())
            {
                ProtoBuf.Serializer.Serialize(ms, publicKey);
                return Convert.ToBase64String(ms.ToArray());
            }
        }

        MultiHash CreateKeyId(AsymmetricKeyParameter key)
        {
            var spki = SubjectPublicKeyInfoFactory
                .CreateSubjectPublicKeyInfo(key)
                .GetDerEncoded();

            // Add protobuf cruft.
            var publicKey = new PublicKeyMessage()
            {
                Data = spki
            };
            if (key is RsaKeyParameters)
                publicKey.Type = KeyType.RSA;
            else if (key is ECPublicKeyParameters)
                publicKey.Type = KeyType.Secp256k1;
            else if (key is Ed25519PublicKeyParameters)
                publicKey.Type = KeyType.Ed25519;
            else
                throw new NotSupportedException($"The key type {key.GetType().Name} is not supported.");

            using (var ms = new MemoryStream())
            {
                ProtoBuf.Serializer.Serialize(ms, publicKey);

                // If the length of the serialized bytes <= 42, then we compute the "identity" multihash of 
                // the serialized bytes. The idea here is that if the serialized byte array 
                // is short enough, we can fit it in a multihash verbatim without having to 
                // condense it using a hash function.
                var alg = (ms.Length <= 48) ? "identity" : "sha2-256";

                ms.Position = 0;
                return MultiHash.ComputeHash(ms, alg);
            }
        }

        enum KeyType
        {
            RSA = 0,
            Ed25519 = 1,
            Secp256k1 = 2,
            ECDH = 4,
        }

        [ProtoContract]
        class PublicKeyMessage
        {
            [ProtoMember(1, IsRequired = true)]
            public KeyType Type { get; set; }
            [ProtoMember(2, IsRequired = true)]
            public byte[] Data { get; set; }
        }
    }
}