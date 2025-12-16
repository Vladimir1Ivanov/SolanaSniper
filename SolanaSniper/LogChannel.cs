namespace SolanaSniper;

using Microsoft.Data.Sqlite;
using System;
using System.Data;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;

public sealed class LogChannel : IAsyncDisposable
{
	private readonly Channel<RpcLog> _ch;
	private readonly Task _worker;
	private readonly SqliteConnection _conn;
	private readonly ParameterBatch _batch;

	public LogChannel(string dbPath)
	{
		// 1) SQLite на tmpfs – 0 I/O
		var connStr = @$"Data Source={dbPath};Mode=ReadWriteCreate;Cache=Shared;Pooling=true;";
		_conn = new SqliteConnection(connStr);
		_conn.Open();

		// 2) lock-free канал 50 000 записей
		_ch = Channel.CreateBounded<RpcLog>(
			new BoundedChannelOptions(50_000)
			{ FullMode = BoundedChannelFullMode.DropOldest });

		_batch = new ParameterBatch(_conn, capacity: 500);
		_worker = Task.Run(WriteLoop);
	}

	/* ➜ hot-path: < 1 µс, не ждёт */
	public bool TryLog(in RpcLog entry) => _ch.Writer.TryWrite(entry);

	/* ➜ фон: batch-500 */
	private async Task WriteLoop()
	{
		await foreach (var item in _ch.Reader.ReadAllAsync())
		{
			_batch.Add(item);

			if (_batch.Count == 5)
			{
				await FlushAsync();
			}
		}
		if (_batch.Count > 0)
		{
			await FlushAsync();
		}
	}

	private async ValueTask FlushAsync()
	{
		if (_batch.Count == 0) 
			return;

		var sb = new StringBuilder();
		sb.Append("INSERT INTO RpcLog(UtcTicks,Method,RequestId,RpcEndpoint,StatusCode,RoundTripMs,JsonBody) VALUES ");

		for (int i = 0; i < _batch.Count; i++)
		{
			sb.Append(i == 0 ? "(" : ",(");
			sb.Append($"${i * 7 + 1},${i * 7 + 2},${i * 7 + 3},${i * 7 + 4},${i * 7 + 5},${i * 7 + 6},${i * 7 + 7})");
		}
		sb.Append(';');

		using var cmd = _conn.CreateCommand();
		cmd.CommandText = sb.ToString();

		// добавляем параметры
		for (int i = 0; i < _batch.Count; i++)
		{
			var item = _batch[i];
			cmd.Parameters.AddWithValue($"${i * 7 + 1}", item.UtcTicks);
			cmd.Parameters.AddWithValue($"${i * 7 + 2}", item.Method);
			cmd.Parameters.AddWithValue($"${i * 7 + 3}", item.RequestId);
			cmd.Parameters.AddWithValue($"${i * 7 + 4}", item.RpcEndpoint);
			cmd.Parameters.AddWithValue($"${i * 7 + 5}", item.StatusCode);
			cmd.Parameters.AddWithValue($"${i * 7 + 6}", item.RoundTripMs);
			cmd.Parameters.AddWithValue($"${i * 7 + 7}", item.JsonBody);
		}

		await cmd.ExecuteNonQueryAsync();
		_batch.Clear();
		
	}

	public async ValueTask DisposeAsync()
	{
		_ch.Writer.TryComplete();
		await _worker;
		await FlushAsync(); // сброс остатков
		_conn.Close();
	}
}
