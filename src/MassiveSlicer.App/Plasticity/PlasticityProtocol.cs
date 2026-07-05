namespace MassiveSlicer.App.Plasticity;

/// <summary>
/// Wire message identifiers for the Plasticity WebSocket server (default localhost:8980).
/// Mirrors the enum used by the official Plasticity Blender bridge — a 4-byte little-endian
/// value that prefixes every message.
/// </summary>
internal enum PlasticityMessageType : uint
{
    Transaction    = 0,
    Add            = 1,
    Update         = 2,
    Delete         = 3,
    Move           = 4,
    Attribute      = 5,
    NewVersion     = 10,
    NewFile        = 11,
    ListAll        = 20,
    ListSome       = 21,
    ListVisible    = 22,
    SubscribeAll   = 23,
    SubscribeSome  = 24,
    UnsubscribeAll = 25,
    RefacetSome    = 26,
    PutSome        = 31,
    Handshake      = 100,
}

/// <summary>Plasticity object kinds. Only <see cref="Solid"/> and <see cref="Sheet"/> carry mesh data.</summary>
internal enum PlasticityObjectType : uint
{
    Solid = 0,
    Sheet = 1,
    Wire  = 2,
    Group = 5,
    Empty = 6,
}
