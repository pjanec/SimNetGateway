namespace SimNetGateway.Protocol.Display;

/// <summary>Marker of every message on the display side.</summary>
public interface IDisplayMessage
{
}

public readonly record struct DisplayEntityPosition(
    string Label,
    double LatitudeDeg,
    double LongitudeDeg,
    double Altitude,
    double HeadingDeg) : IDisplayMessage;

public readonly record struct DisplayEntityRemoved(string Label) : IDisplayMessage;
