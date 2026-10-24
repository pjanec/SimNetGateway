namespace SimNetGateway.Engine;

/// <summary>Metadata of one attribute slot: who wrote it last, when, and when its value last actually changed.</summary>
public readonly record struct AttrMeta(
    SourceId Source,
    CorrelationId Correlation,
    double WallTime,
    double SimTime,
    double ChangedWallTime,
    double ChangedSimTime,
    uint Version);

/// <summary>An attribute value together with its metadata, as stored in the repository.</summary>
public readonly record struct AttrSlot(AttrValue Value, AttrMeta Meta);

/// <summary>One attribute touched by a transaction. <c>Old</c> is None when the slot was absent before;
/// <c>New</c> is None after a Clear. Every entry was written; <c>Changed</c> says whether the value differs.</summary>
public readonly record struct AttrChange(AttrId Attr, AttrValue Old, AttrValue New, bool Changed);
