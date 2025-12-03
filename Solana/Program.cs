using System;
using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Channels;
using System.Collections.Concurrent;
using System.Collections.Generic;

// JSON Source Generator Context
[JsonSourceGenerationOptions(
	PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
	DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	WriteIndented = false)]
[JsonSerializable(typeof(SubscribeRequest))]
[JsonSerializable(typeof(SubscribeParams))]
[JsonSerializable(typeof(JsonElement))]
internal partial class SolanaJsonContext : JsonSerializerContext { }

// Модели для сериализации
public record SubscribeRequest(
	string Jsonrpc,
	int Id,
	string Method,
	object[] Params
);

public record SubscribeParams(
	string Commitment,
	string Encoding
);

// Высокопроизводительный WebSocket клиент для мониторинга Solana транзакций
public class SolanaWalletMonitor : IAsyncDisposable
{
	private readonly ClientWebSocket _ws;
	private readonly string _wsUrl;
	private readonly Channel<string> _messageChannel;
	private readonly CancellationTokenSource _cts;
	private readonly Dictionary<string, string> _wallets; // Address -> Name
	private int _requestId;

	// Маппинги для отслеживания подписок
	private readonly ConcurrentDictionary<int, string> _pendingRequests = new();
	private readonly ConcurrentDictionary<int, string> _activeSubscriptions = new();

	public SolanaWalletMonitor(string wsUrl, Dictionary<string, string> wallets)
	{
		_ws = new ClientWebSocket();
		_wsUrl = wsUrl;
		_wallets = wallets;
		_cts = new CancellationTokenSource();
		_messageChannel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
		{
			SingleReader = true,
			SingleWriter = false
		});
	}

	public async Task StartAsync()
	{
		await _ws.ConnectAsync(new Uri(_wsUrl), _cts.Token);
		Console.WriteLine($"✓ Подключено к {_wsUrl}");

		// Запускаем задачи параллельно
		var receiveTask = ReceiveMessagesAsync(_cts.Token);
		var processTask = ProcessMessagesAsync(_cts.Token);

		// Подписываемся на все кошельки
		await SubscribeToWalletsAsync();

		await Task.WhenAll(receiveTask, processTask);
	}

	private async Task SubscribeToWalletsAsync()
	{
		foreach (var wallet in _wallets)
		{
			var id = Interlocked.Increment(ref _requestId);
			_pendingRequests.TryAdd(id, wallet.Key);

			// Формируем JSON вручную для максимальной производительности
			var json = $$"""
            {
                "jsonrpc": "2.0",
                "id": {{id}},
                "method": "accountSubscribe",
                "params": [
                    "{{wallet.Key}}",
                    {
                        "commitment": "processed",
                        "encoding": "jsonParsed"
                    }
                ]
            }
            """;

			var bytes = Encoding.UTF8.GetBytes(json);
			await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts.Token);
			Console.WriteLine($"✓ Подписка на кошелек: {wallet.Value} ({wallet.Key[..8]}...)");
		}
	}

	private async Task ReceiveMessagesAsync(CancellationToken ct)
	{
		var buffer = ArrayPool<byte>.Shared.Rent(1024 * 64); // 64KB буфер

		try
		{
			while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
			{
				var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

				if (result.MessageType == WebSocketMessageType.Close)
				{
					await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed", ct);
					break;
				}

				var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
				await _messageChannel.Writer.WriteAsync(message, ct);
			}
		}
		catch (OperationCanceledException) { }
		finally
		{
			ArrayPool<byte>.Shared.Return(buffer);
			_messageChannel.Writer.Complete();
		}
	}

	private async Task ProcessMessagesAsync(CancellationToken ct)
	{
		await foreach (var message in _messageChannel.Reader.ReadAllAsync(ct))
		{
			try
			{
				using var doc = JsonDocument.Parse(message);
				var root = doc.RootElement;

				// Обработка ответа на подписку
				if (root.TryGetProperty("id", out var idProp) && root.TryGetProperty("result", out var resultProp))
				{
					var id = idProp.GetInt32();
					var subId = resultProp.GetInt32();

					if (_pendingRequests.TryRemove(id, out var walletAddress))
					{
						_activeSubscriptions.TryAdd(subId, walletAddress);
					}
				}
				// Обработка уведомления
				else if (root.TryGetProperty("method", out var method) &&
					method.GetString() == "accountNotification")
				{
					ProcessAccountNotification(root);
				}
				else if (root.TryGetProperty("error", out var error))
				{
					Console.WriteLine($"⚠ Ошибка: {error}");
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine($"⚠ Ошибка обработки: {ex.Message}");
			}
		}
	}

	private void ProcessAccountNotification(JsonElement root)
	{
		if (!root.TryGetProperty("params", out var parameters)) return;

		var subscription = parameters.TryGetProperty("subscription", out var sub)
			? sub.GetInt32() : 0;

		// Определяем кошелек по ID подписки
		string walletDisplay = "Unknown";
		if (_activeSubscriptions.TryGetValue(subscription, out var walletAddress))
		{
			if (_wallets.TryGetValue(walletAddress, out var name))
			{
				walletDisplay = name;
			}
			else
			{
				walletDisplay = walletAddress;
			}
		}

		if (!parameters.TryGetProperty("result", out var resultData)) return;

		var context = resultData.TryGetProperty("context", out var ctx)
			? ctx.GetProperty("slot").GetInt64() : 0;

		var value = resultData.GetProperty("value");
		var lamports = value.GetProperty("lamports").GetInt64();
		var owner = value.TryGetProperty("owner", out var ownerProp)
			? ownerProp.GetString() : "unknown";

		var solBalance = lamports / 1_000_000_000.0;

		Console.WriteLine($"""
            ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
            🔔 Новая транзакция
            ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
            � Wallet: {walletDisplay}
            🕐 Slot: {context}
            💰 Баланс: {solBalance:F9} SOL ({lamports:N0} lamports)
            👤 Owner: {owner}
            ⏰ Время: {DateTime.Now:HH:mm:ss.fff}
            ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
            """);

		if (value.TryGetProperty("data", out var data))
		{
			string dataDisplay = "";

			if (data.ValueKind == JsonValueKind.Array && data.GetArrayLength() == 2)
			{
				var content = data[0].GetString() ?? "";
				var encoding = data[1].GetString();
				
				if (encoding == "base64")
				{
					dataDisplay = content;
				}
				else
				{
					dataDisplay = $"[{encoding}] {content}";
				}
			}
			else
			{
				dataDisplay = data.ToString();
			}

			if (!string.IsNullOrEmpty(dataDisplay))
			{
				Console.WriteLine($"📦 Data: {dataDisplay}");
			}
		}
	}

	public async ValueTask DisposeAsync()
	{
		_cts.Cancel();

		if (_ws.State == WebSocketState.Open)
		{
			await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disposing", CancellationToken.None);
		}

		_ws.Dispose();
		_cts.Dispose();
	}
}

public class Program
{
	public static async Task Main(string[] args)
	{
		Console.OutputEncoding = Encoding.UTF8;
		Console.WriteLine("🚀 Запуск Solana Wallet Monitor...\n");

		var wallets = new Dictionary<string, string>
		{
			{ "4BdKaxN8G6ka4GYtQQWk4G4dZRUTX2vQH9GcXdBREFUk", "Jijo" },
			{ "2fg5QD1eD7rzNNCsvnhmXFm5hqNgwTTG8p7kQ6f3rx6f", "Cupsey" },
			{ "5B79fMkcFeRTiwm7ehsZsFiKsC7m7n1Bgv9yLxPp9q2X", "bandit" }
		};

		var wsUrl = "wss://rpc.ny.shyft.to?api_key=7A9RfMv0JKI6CxZn";

		await using var monitor = new SolanaWalletMonitor(wsUrl, wallets);

		try
		{
			await monitor.StartAsync();
		}
		catch (Exception ex)
		{
			Console.WriteLine($"❌ Критическая ошибка: {ex.Message}");
			Console.WriteLine(ex.StackTrace);
		}
	}
}