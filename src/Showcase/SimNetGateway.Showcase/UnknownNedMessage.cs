using SimNetGateway.Protocol.Ned;

namespace SimNetGateway.Showcase;

/// <summary>
/// A NED message of a type that no module of the gateway handles (S-7.2): the engine counts it as unhandled and nothing else
/// happens. Exists only for the harness.
/// </summary>
public readonly record struct UnknownNedMessage(int Code) : INedMessage;
