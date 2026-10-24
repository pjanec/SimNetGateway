using System.Runtime.CompilerServices;

namespace SimNetGateway.Engine;

/// <summary>The handlers registered for one message type, in registration order.</summary>
internal sealed class HandlerList<T>
{
    public IngressHandler<T>[] Handlers = new IngressHandler<T>[2];
    public string[] Modules = new string[2];
    public int Count;

    public void Add(string module, IngressHandler<T> handler)
    {
        if (Count == Handlers.Length)
        {
            Array.Resize(ref Handlers, Count * 2);
            Array.Resize(ref Modules, Count * 2);
        }

        Handlers[Count] = handler;
        Modules[Count] = module;
        Count++;
    }
}

/// <summary>Message type -> generic handler list; lookup by typeof(T) without allocation or boxing.</summary>
internal sealed class IngressRouter
{
    private readonly Dictionary<Type, object> _lists = new();

    public void Add<T>(string module, IngressHandler<T> handler)
    {
        if (!_lists.TryGetValue(typeof(T), out var list))
        {
            list = new HandlerList<T>();
            _lists.Add(typeof(T), list);
        }

        Unsafe.As<HandlerList<T>>(list).Add(module, handler);
    }

    public HandlerList<T>? Find<T>() =>
        _lists.TryGetValue(typeof(T), out var list) ? Unsafe.As<HandlerList<T>>(list) : null;
}
