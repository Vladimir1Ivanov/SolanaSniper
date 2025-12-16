using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace SolanaSniper;

// Снайпер-клиент на logsSubscribe
public class SolanaSniperMonitor : IAsyncDisposable
{
	private readonly Endpoints _endpoints;
	private readonly ClientWebSocket _ws;
	private readonly string _wsUrl;
	private readonly Channel<MessageBuffer> _messageChannel;
	private readonly CancellationTokenSource _cts;
	private readonly Dictionary<string, string> _wallets; 
	private int _requestId;
	private readonly LogChannel _log;

	// Маппинг ID подписки -> Имя кошелька
	private readonly ConcurrentDictionary<int,KeyValuePair<string, string>> _activeSubscriptions = new();
	
	// Для ожидания подтверждения подписки: RequestID -> TaskCompletionSource
	private readonly ConcurrentDictionary<int, TaskCompletionSource<int>> _pendingConfirmations = new();

	// Вспомогательная структура для передачи владения памятью
	private readonly struct MessageBuffer : IDisposable
	{
		public readonly IMemoryOwner<byte> Owner;
		public readonly int Length;
		public readonly DateTime ReceivedAt;

		public MessageBuffer(IMemoryOwner<byte> owner, int length, DateTime receivedAt) 
		{ 
			Owner = owner; 
			Length = length;
			ReceivedAt = receivedAt;
		}
		
		public void Dispose() => Owner?.Dispose();
		public ReadOnlyMemory<byte> Data => Owner.Memory.Slice(0, Length);
	}

	public SolanaSniperMonitor(Endpoints endpoints, Dictionary<string, string> wallets)
	{
		_log = new(endpoints.DbPath);
		_endpoints = endpoints;
		_ws = new ClientWebSocket();
		_wsUrl = endpoints.WssUrl;
		_wallets = wallets;
		_cts = new CancellationTokenSource();
		_messageChannel = Channel.CreateUnbounded<MessageBuffer>(new UnboundedChannelOptions
		{
			SingleReader = true,
			SingleWriter = false
		});
	}

	public async Task StartAsync()
	{
		await _ws.ConnectAsync(new Uri(_wsUrl), _cts.Token);
		Console.WriteLine($"✓ [Sniper] Подключено к {_wsUrl}");

		var receiveTask = ReceiveMessagesAsync(_cts.Token);
		var processTask = ProcessMessagesAsync(_cts.Token);

		// Ждем небольшую паузу, чтобы сокет точно был готов
		await Task.Delay(500);

		await SubscribeToLogsSequentialAsync();

		await Task.WhenAll(receiveTask, processTask);
	}

	private async Task SubscribeToLogsSequentialAsync()
	{
		Console.WriteLine("⏳ Начинаем последовательную подписку...");

		foreach (var wallet in _wallets)
		{
			var requestId = Interlocked.Increment(ref _requestId);
			var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
			_pendingConfirmations.TryAdd(requestId, tcs);

			var json = $$"""
            {
                "jsonrpc": "2.0",
                "id": {{requestId}},
                "method": "logsSubscribe",
                "params": [
                    {
                        "mentions": [ "{{wallet.Key}}" ]
                    },
                    {
                        "commitment": "processed",
                        "encoding": "base64"
                    }
                ]
            }
            """;

			var bytes = Encoding.UTF8.GetBytes(json);
			await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts.Token);
			
			try
			{
				// Ждем подтверждения от сервера (таймаут 5 сек)
				var subscriptionId = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
				
				// Сохраняем маппинг: ID подписки -> Имя кошелька
				_activeSubscriptions.TryAdd(subscriptionId, wallet);
				
				Console.WriteLine($"✓ [Sniper] Подписано: {wallet.Value} (SubID: {subscriptionId})");
			}
			catch (TimeoutException)
			{
				Console.WriteLine($"❌ [Sniper] Ошибка: таймаут подтверждения для {wallet.Value}");
				_pendingConfirmations.TryRemove(requestId, out _);
			}
		}
		
		Console.WriteLine("✅ Все подписки оформлены. Ожидание сигналов...\n");
	}

	private async Task ReceiveMessagesAsync(CancellationToken ct)
	{
		var buffer = ArrayPool<byte>.Shared.Rent(1024 * 64);

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

				if (result.EndOfMessage)
				{
					// Оптимизация: копируем сразу в пул памяти
					var owner = MemoryPool<byte>.Shared.Rent(result.Count);
					buffer.AsSpan(0, result.Count).CopyTo(owner.Memory.Span);
					
					var now = DateTime.UtcNow;
					await _messageChannel.Writer.WriteAsync(new MessageBuffer(owner, result.Count, now), ct);
				}
				else
				{
					// Сборка сообщения из нескольких фреймов
					using var ms = new MemoryStream();
					ms.Write(buffer, 0, result.Count);
					
					do
					{
						result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
						ms.Write(buffer, 0, result.Count);
					} while (!result.EndOfMessage && !ct.IsCancellationRequested);

					var length = (int)ms.Length;
					var owner = MemoryPool<byte>.Shared.Rent(length);
					
					ms.Position = 0;
					ms.Read(owner.Memory.Span.Slice(0, length));
					
					var now = DateTime.UtcNow;
					await _messageChannel.Writer.WriteAsync(new MessageBuffer(owner, length, now), ct);
				}
			}
		}
		catch (OperationCanceledException) { }
		catch (Exception ex)
		{
			Console.WriteLine($"❌ Ошибка в ReceiveMessagesAsync: {ex.Message}");
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(buffer);
			_messageChannel.Writer.Complete();
		}
	}

	private async Task ProcessMessagesAsync(CancellationToken ct)
	{
		await foreach (var msgBuffer in _messageChannel.Reader.ReadAllAsync(ct))
		{
			using (msgBuffer)
			{
				try
				{
					if (TryReadLogsNotificationKey(msgBuffer.Data.Span, out var k))
					{
						var now = DateTime.UtcNow;
						var lag = (now - msgBuffer.ReceivedAt).TotalMilliseconds;
						var entry = new RpcLog(
							UtcTicks: DateTime.UtcNow.Ticks,
							Method: "logSubscribe",
							RequestId: "1",
							RpcEndpoint: "https://rpc.ny.shyft.to?api_key=7A9RfMv0JKI6CxZn",
							StatusCode: 200,
							RoundTripMs: lag,
							JsonBody: msgBuffer.Data.Span.ToArray());
						_log.TryLog(entry);

						// Если есть ошибка транзакции - игнорируем
						if (k.HasError)
							continue;

						if (!_activeSubscriptions.TryGetValue(k.Subscription, out var pair))
							continue;
						var walletName = pair.Value;

						var sw = Stopwatch.StartNew();
						var (delta, mint) = await TxAnalyzer.GetWalletDeltaAsync(_log, _endpoints.RestUrl, k.Signature, pair.Key, ct);
						sw.Stop();

						if (delta.Side == Side.None)
							continue;

						var ms = sw.Elapsed.TotalMilliseconds;
						
						Console.WriteLine($"""
								⚡ [SNIPER ALERT] {walletName}
								Wallet: {pair.Key}
								Slot: {k.Slot}
								Signature: {k.Signature}
								Получено: {msgBuffer.ReceivedAt:HH:mm:ss.fff}
								Обработано: {now:HH:mm:ss.fff} (Lag: {lag:F1} ms)
								Delta: Pre={delta.Pre}, Post={delta.Post}, Change={delta.Change} ({delta.Side})
								Token {mint}
								Время анализа: {ms:F2} ms
								----------------------------------
								""");
					}
					else
					{
						ProcessMessageUtf8(msgBuffer.Data.Span, msgBuffer.ReceivedAt);
					}
				}
				catch (Exception ex)
				{
					Console.WriteLine($"⚠ Ошибка Utf8Reader: {ex.Message}");
				}
			}
		}
	}

	public static bool TryReadLogsNotificationKey(
		ReadOnlySpan<byte> json,
		out LogsNotificationKey key)
	{
		key = default;
		var reader = new Utf8JsonReader(json, isFinalBlock: true, state: default);

		int subscription = 0;
		long slot = 0;
		string signature = null;
		bool hasError = false;

		while (reader.Read())
		{
			if (reader.TokenType != JsonTokenType.PropertyName) continue;

			// 1) самое быстрое — без аллокаций, без длины
			if (reader.ValueTextEquals("method"u8))
			{
				reader.Read();
				if (!reader.ValueTextEquals("logsNotification"u8)) return false;
				continue;
			}

			if (reader.ValueTextEquals("subscription"u8))
			{
				reader.Read();
				if (reader.TokenType == JsonTokenType.Number)
					subscription = reader.GetInt32();
				continue;
			}

			if (reader.ValueTextEquals("slot"u8))
			{
				reader.Read();
				if (reader.TokenType == JsonTokenType.Number)
					slot = reader.GetInt64();
				continue;
			}

			if (reader.ValueTextEquals("signature"u8))
			{
				reader.Read();
				if (reader.TokenType == JsonTokenType.String)
					signature = reader.GetString();
				continue;
			}

			if (reader.ValueTextEquals("err"u8))
			{
				reader.Read();
				hasError = reader.TokenType != JsonTokenType.Null;
				continue;
			}
		}

		if (signature is null) 
			return false;

		key = new LogsNotificationKey(subscription, slot, signature, hasError);
		return true;
	}

	// High-Performance Utf8JsonReader implementation
	private void ProcessMessageUtf8(ReadOnlySpan<byte> jsonSpan, DateTime receivedAt)
	{
		// 2. SLOW PATH: Полный разбор структуры (Fallback)
		var reader = new Utf8JsonReader(jsonSpan);

		// Переменные для хранения состояния парсинга
		int? id = null;
		int? subscriptionId = null;
		string method = null;
		
		// Для logsNotification
		int? notifSubscription = null;
		string signature = null;
		bool hasError = false;

		// Флаги, где мы находимся
		bool inParams = false;
		bool inResult = false;
		bool inValue = false;

		while (reader.Read())
		{
			switch (reader.TokenType)
			{
				case JsonTokenType.PropertyName:
					var propName = reader.ValueSpan;

					// Top-level properties
					if (!inParams && !inResult && !inValue)
					{
						if (propName.SequenceEqual("id"u8))
						{
							reader.Read();
							if (reader.TokenType == JsonTokenType.Number) id = reader.GetInt32();
						}
						else if (propName.SequenceEqual("method"u8))
						{
							reader.Read();
							method = reader.GetString();
						}
						else if (propName.SequenceEqual("result"u8))
						{
							reader.Read();
							if (reader.TokenType == JsonTokenType.Number)
							{
								subscriptionId = reader.GetInt32();
							}
						}
						else if (propName.SequenceEqual("params"u8))
						{
							inParams = true;
						}
					}
					// Inside "params"
					else if (inParams && !inResult && !inValue)
					{
						if (propName.SequenceEqual("subscription"u8))
						{
							reader.Read();
							if (reader.TokenType == JsonTokenType.Number) notifSubscription = reader.GetInt32();
						}
						else if (propName.SequenceEqual("result"u8))
						{
							inResult = true;
						}
					}
					// Inside "params" -> "result"
					else if (inResult && !inValue)
					{
						if (propName.SequenceEqual("value"u8))
						{
							inValue = true;
						}
					}
					// Inside "params" -> "result" -> "value"
					else if (inValue)
					{
						if (propName.SequenceEqual("signature"u8))
						{
							reader.Read();
							signature = reader.GetString();
						}
						else if (propName.SequenceEqual("err"u8))
						{
							reader.Read();
							if (reader.TokenType != JsonTokenType.Null) hasError = true;
						}
					}
					break;

				case JsonTokenType.EndObject:
					if (inValue) inValue = false;
					else if (inResult) inResult = false;
					else if (inParams) inParams = false;
					break;
			}
		}

		// Логика обработки после парсинга
		
		// 1. Подтверждение подписки
		if (id.HasValue && subscriptionId.HasValue)
		{
			if (_pendingConfirmations.TryRemove(id.Value, out var tcs))
			{
				tcs.TrySetResult(subscriptionId.Value);
			}
			return;
		}
	}

	public async ValueTask DisposeAsync()
	{
		_cts.Cancel();
		if (_ws.State == WebSocketState.Open)
			await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disposing", CancellationToken.None);
		_ws.Dispose();
		_cts.Dispose();
	}
}
