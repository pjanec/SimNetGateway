namespace SimNetGateway.Transport.DisUdp;

/// <summary>
/// A snapshot of the transport's counters. Every PDU slot of every received datagram is counted once in <see cref="PdusReceived"/> and ends in
/// exactly one of the other inbound counters (or is still <see cref="Pending"/>): <see cref="Balanced"/>.
/// </summary>
public sealed record DisUdpCounters
{
    public long DatagramsReceived { get; init; }

    /// <summary>PDU slots found in the datagrams (decoded, unsupported or malformed).</summary>
    public long PdusReceived { get; init; }

    /// <summary>Handed to the engine by a drain.</summary>
    public long Delivered { get; init; }

    /// <summary>Decoded and queued, not yet drained.</summary>
    public long Pending { get; init; }

    /// <summary>Filtered: another exercise.</summary>
    public long FilteredExercise { get; init; }

    /// <summary>Filtered: originated by the own site and application (a looped-back echo).</summary>
    public long FilteredOwn { get; init; }

    /// <summary>Well framed PDUs of a type the codec does not decode.</summary>
    public long Unsupported { get; init; }

    /// <summary>Malformed PDU slots (the codec's error entries, including an empty datagram).</summary>
    public long DecodeErrors { get; init; }

    /// <summary>Dropped because the queue was full.</summary>
    public long QueueOverflow { get; init; }

    /// <summary>Failures of the socket's receive (the loop continues).</summary>
    public long ReceiveErrors { get; init; }

    public long Sent { get; init; }

    /// <summary>PDUs that could not be encoded.</summary>
    public long EncodeErrors { get; init; }

    /// <summary>Socket failures when sending.</summary>
    public long SendErrors { get; init; }

    public long Filtered => FilteredExercise + FilteredOwn;

    public bool Balanced => PdusReceived == Delivered + Pending + FilteredExercise + FilteredOwn + Unsupported + DecodeErrors + QueueOverflow;
}
