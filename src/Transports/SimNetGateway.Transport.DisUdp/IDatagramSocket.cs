namespace SimNetGateway.Transport.DisUdp;

/// <summary>
/// The minimal socket surface of <see cref="DisUdpTransport"/>, so that filtering, queueing and threading can be tested without a network.
/// </summary>
public interface IDatagramSocket : IDisposable
{
    /// <summary>
    /// Waits up to <paramref name="timeoutMs"/> for one datagram and copies it to <paramref name="buffer"/>; returns its length, or -1 when none
    /// arrived in time. May throw when the socket fails or is closed.
    /// </summary>
    int Receive(byte[] buffer, int timeoutMs);

    /// <summary>Sends one datagram to the configured destination; throws on failure.</summary>
    void Send(ReadOnlySpan<byte> datagram);
}
