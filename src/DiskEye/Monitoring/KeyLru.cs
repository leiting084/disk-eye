namespace DiskEye.Monitoring;

/// <summary>
/// 带绝对过期时间 + 容量上限的 LRU（最近访问移到队首，超容淘汰队尾）。
/// 非线程安全：AttributionEngine 在自身锁内调用（锁粒度更大，避免双重加锁）。
/// </summary>
internal sealed class KeyLru<TKey, TValue> where TKey : notnull
{
    private sealed record Node(TKey Key, TValue Value, long ExpireMs);

    private readonly LinkedList<Node> _list = new();
    private readonly Dictionary<TKey, LinkedListNode<Node>> _map = new();
    private readonly int _capacity;

    public KeyLru(int capacity) => _capacity = capacity;

    public int Count => _map.Count;

    public void Add(TKey key, TValue value, long expireMsAbs)
    {
        if (_map.TryGetValue(key, out var existing))
        {
            _list.Remove(existing);
            _map.Remove(key);
        }
        var node = _list.AddFirst(new Node(key, value, expireMsAbs));
        _map[key] = node;
        while (_map.Count > _capacity && _list.Last != null)
        {
            var old = _list.Last;
            _list.RemoveLast();
            _map.Remove(old.Value.Key);
        }
    }

    public bool TryGet(TKey key, long nowMs, out TValue value)
    {
        if (_map.TryGetValue(key, out var node))
        {
            if (node.Value.ExpireMs > nowMs)
            {
                _list.Remove(node);
                var fresh = _list.AddFirst(node.Value);
                _map[key] = fresh;
                value = node.Value.Value;
                return true;
            }
            _list.Remove(node);
            _map.Remove(key);
        }
        value = default!;
        return false;
    }

    public void Remove(TKey key)
    {
        if (_map.Remove(key, out var node)) _list.Remove(node);
    }

    public int RemoveExpired(long nowMs)
    {
        int removed = 0;
        var cur = _list.First;
        while (cur != null)
        {
            var next = cur.Next;
            if (cur.Value.ExpireMs <= nowMs)
            {
                _list.Remove(cur);
                _map.Remove(cur.Value.Key);
                removed++;
            }
            cur = next;
        }
        return removed;
    }
}
