using System.Text;
using System.Threading.Tasks;
using Ipfs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nerdbank.Streams;
using PeerTalk.Cryptography;

namespace PeerTalk.SecureCommunication
{
    [TestClass]
    public class Secio1Tests
    {
        [TestMethod]
        public async Task Works()
        {
            var localKey = CryptoHelpers.GetKey();
            var localId = localKey.Public.CreateKeyId();
            var localPeer = new Peer
            {
                Id = localId,
                PublicKey = localKey.CreatePublicKey(),
                Addresses = new MultiAddress[]
                    {$"/ip4/0.0.0.0/tcp/4001/p2p/{localId}"}
            };

            var remoteKey = CryptoHelpers.GetKey();
            var remoteId = remoteKey.Public.CreateKeyId();
            var remotePeer = new Peer
            {
                Id = remoteId,
                PublicKey = remoteKey.CreatePublicKey(),
                Addresses = new MultiAddress[]
                    {$"/ip4/0.0.0.0/tcp/4003/p2p/{remoteId}"}
            };

            var localToRemoteConnection = FullDuplexStream.CreatePair();
            var localToRemotePeerConnection = new PeerConnection
            {
                IsIncoming = false,
                Stream = localToRemoteConnection.Item1,
                LocalPeer = localPeer,
                RemotePeer = remotePeer,
                LocalPeerKey = Key.CreatePrivateKey(localKey.Private)
            };

            var remoteFromLocalPeerConnection = new PeerConnection
            {
                IsIncoming = true,
                Stream = localToRemoteConnection.Item2,
                LocalPeer = remotePeer,
                RemotePeer = localPeer,
                LocalPeerKey = Key.CreatePrivateKey(remoteKey.Private)
            };

            var localEncryptTask = Task.Run(() => new Secio1().EncryptAsync(localToRemotePeerConnection));
            var remoteEncryptTask = Task.Run(() =>
                new Secio1().ProcessMessageAsync(remoteFromLocalPeerConnection, remoteFromLocalPeerConnection.Stream));

            await localEncryptTask;
            await remoteEncryptTask;

            Assert.IsInstanceOfType(localToRemotePeerConnection.Stream, typeof(Secio1Stream));
            Assert.IsInstanceOfType(remoteFromLocalPeerConnection.Stream, typeof(Secio1Stream));

            var hello = Encoding.UTF8.GetBytes("Hello");
            await localToRemotePeerConnection.Stream.WriteAsync(hello, 0, hello.Length);
            await localToRemotePeerConnection.Stream.FlushAsync();

            var relayTask = Task.Run(async () =>
            {
                byte[] buffer = new byte[1024];
                var read = await remoteFromLocalPeerConnection.Stream.ReadAsync(buffer, 0, 1024);
                Assert.AreEqual("Hello", Encoding.UTF8.GetString(buffer, 0, read));
                var response = Encoding.UTF8.GetBytes("Hello back");
                await remoteFromLocalPeerConnection.Stream.WriteAsync(response, 0, response.Length);
                await remoteFromLocalPeerConnection.Stream.FlushAsync();
            });

            var requestBytes = new byte[1024];
            var readBack = await localToRemotePeerConnection.Stream.ReadAsync(requestBytes, 0, 1024);
            Assert.AreEqual("Hello back", Encoding.UTF8.GetString(requestBytes, 0, readBack));
        }
    }
}