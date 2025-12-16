using Microsoft.Data.Sqlite;

namespace SolanaSniper;

internal sealed class ParameterBatch
{
	private readonly SqliteConnection _conn;
	private readonly List<RpcLog> _items = new(500);
	public int Count => _items.Count;

	public ParameterBatch(SqliteConnection conn, int capacity)
	{
		_conn = conn;
	}

	public void Add(in RpcLog item) => _items.Add(item);

	public RpcLog this[int i] => _items[i];

	public void Clear() => _items.Clear();
}
