using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Ipfs;

namespace PeerTalk
{
    /// <summary>
    /// 
    /// </summary>
    public interface IDialer
    {
        /// <summary>
        ///   Connect to a peer using the specified <see cref="MultiAddress"/>.
        /// </summary>
        /// <param name="address">
        ///   An ipfs <see cref="MultiAddress"/>, such as
        ///  <c>/ip4/104.131.131.82/tcp/4001/ipfs/QmaCpDMGvV2BGHeYERUEnRQAwe3N8SzbUtfsmvsqQLuvuJ</c>.
        /// </param>
        /// <param name="cancel">
        ///   Is used to stop the task.  When cancelled, the <see cref="TaskCanceledException"/> is raised.
        /// </param>
        /// <returns>
        ///   A task that represents the asynchronous operation. The task's result
        ///   is the <see cref="PeerConnection"/>.
        /// </returns>
        /// <remarks>
        ///   If already connected to the peer and is active on any address, then
        ///   the existing connection is returned.
        /// </remarks>
        Task<PeerConnection> ConnectAsync(MultiAddress address, CancellationToken cancel = default(CancellationToken));

        /// <summary>
        ///   Connect to a peer.
        /// </summary>
        /// <param name="peer">
        ///  A peer to connect to.
        /// </param>
        /// <param name="cancel">
        ///   Is used to stop the task.  When cancelled, the <see cref="TaskCanceledException"/> is raised.
        /// </param>
        /// <returns>
        ///   A task that represents the asynchronous operation. The task's result
        ///   is the <see cref="PeerConnection"/>.
        /// </returns>
        /// <remarks>
        ///   If already connected to the peer and is active on any address, then
        ///   the existing connection is returned.
        /// </remarks>
        Task<PeerConnection> ConnectAsync(Peer peer, CancellationToken cancel = default(CancellationToken));

        /// <summary>
        ///   Create a stream to the peer that talks the specified protocol.
        /// </summary>
        /// <param name="peer">
        ///   The remote peer.
        /// </param>
        /// <param name="protocol">
        ///   The protocol name, such as "/foo/0.42.0".
        /// </param>
        /// <param name="cancel">
        ///   Is used to stop the task.  When cancelled, the <see cref="TaskCanceledException"/> is raised.
        /// </param>
        /// <returns>
        ///   A task that represents the asynchronous operation. The task's result
        ///   is the new <see cref="Stream"/> to the <paramref name="peer"/>.
        /// </returns>
        /// <remarks>
        ///   <para>
        ///   When finished, the caller must <see cref="Stream.Dispose()"/> the
        ///   new stream.
        ///   </para>
        /// </remarks>
        Task<Stream> DialAsync(Peer peer, string protocol, CancellationToken cancel = default(CancellationToken));
    }
}