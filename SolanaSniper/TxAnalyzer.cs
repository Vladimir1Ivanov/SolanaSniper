using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SolanaSniper;

public sealed class TxAnalyzer
{
	/* -------------- public API -------------- */
	public static ValueTask<(Delta delta, string mint)> GetWalletDeltaAsync(
		LogChannel log,
		string restUrl,
		string txSignature,
		string wallet,
		CancellationToken ct = default)
	{
		ArgumentException.ThrowIfNullOrEmpty(txSignature);
		ArgumentException.ThrowIfNullOrEmpty(wallet);
		return Core();

		async ValueTask<(Delta delta, string mint)> Core()
		{
			var sw = Stopwatch.StartNew();
			var _http = new HttpClient() { BaseAddress = new Uri(restUrl) };

			/* 1.  Формируем тело запроса */
			var content = GetContent(txSignature);
			/* 2.  Отправляем */
			using var resp = await http.PostAsync("", content, ct).ConfigureAwait(false);
			resp.EnsureSuccessStatusCode();

			var a = resp.Content.ReadAsStringAsync(ct).GetAwaiter().GetResult();

			/* 3.  Читаем ответ */
			await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
			var rent = ArrayPool<byte>.Shared.Rent(64 * 1024);
			try
			{
				int read, total = 0;
				while ((read = await stream.ReadAsync(rent.AsMemory(total), ct).ConfigureAwait(false)) != 0)
					total += read;

				var (delta, mint) = ParseDelta(rent.AsSpan(0, total), wallet);
				sw.Stop();

				var entry = new RpcLog(
							UtcTicks: DateTime.UtcNow.Ticks,
							Method: "getTransaction",
							RequestId: "2",
							RpcEndpoint: "https://rpc.ny.shyft.to?api_key=7A9RfMv0JKI6CxZn",
							StatusCode: 200,
							RoundTripMs:sw.ElapsedMilliseconds,
							JsonBody: rent);
				log.TryLog(entry);

				return (delta, mint);
			}
			finally
			{
				ArrayPool<byte>.Shared.Return(rent);
			}			
		}
	}

	private static ReadOnlyMemoryContent GetContent(
		string txSignature)
	{
		/* 1.  пишем в ArrayBufferWriter (zero-copy) */
		var writer = new ArrayBufferWriter<byte>(512);
		ReadOnlySpan<byte> prefix = """{"jsonrpc":"2.0","id":1,"method":"getTransaction","params":["""u8;
		ReadOnlySpan<byte> middle =
			""",{"encoding":"base64","commitment":"confirmed","maxSupportedTransactionVersion":0}]}"""u8;

		prefix.CopyTo(writer.GetSpan(prefix.Length));
		writer.Advance(prefix.Length);

		writer.GetSpan(1)[0] = (byte)'"';
		writer.Advance(1);

		int sigBytes = Encoding.UTF8.GetBytes(txSignature, writer.GetSpan(txSignature.Length));
		writer.Advance(sigBytes);

		writer.GetSpan(1)[0] = (byte)'"';
		writer.Advance(1);

		middle.CopyTo(writer.GetSpan(middle.Length));
		writer.Advance(middle.Length);

		/* 2.  без копии – берём Memory<byte> из писателя */
		var content = new ReadOnlyMemoryContent(writer.WrittenMemory);
		content.Headers.ContentType = new("application/json");
		return content;
	}

	/* -------------- парсинг -------------- */
	private static (Delta delta, string mint) ParseDelta(ReadOnlySpan<byte> json, string wallet)
	{
		var reader = new Utf8JsonReader(json);
		ReadOnlySpan<byte> w = Encoding.UTF8.GetBytes(wallet);

		decimal preAmt = 0m, postAmt = 0m;
		string preMint = string.Empty, postMint = string.Empty;
		bool foundPre = false, foundPost = false;

		while (reader.Read())
		{
			if (reader.TokenType != JsonTokenType.PropertyName) 
				continue;

			if (reader.ValueTextEquals("preTokenBalances"u8))
			{
				reader.Read(); // [
				while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
					if (reader.TokenType == JsonTokenType.StartObject &&
						TryBalance(ref reader, w, out var v, out var m))
					{
						preAmt = v; preMint = m; foundPre = true;
					}
				continue;
			}

			if (reader.ValueTextEquals("postTokenBalances"u8))
			{
				reader.Read(); // [
				while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
					if (reader.TokenType == JsonTokenType.StartObject &&
						TryBalance(ref reader, w, out var v, out var m))
					{
						postAmt = v; postMint = m; foundPost = true;
					}
				continue;
			}

			if (foundPre && foundPost) break;
		}

		var change = postAmt - preAmt;
		var side = change == 0 ? Side.None : change > 0 ? Side.Buy : Side.Sell;
		var delta = new Delta(preAmt, postAmt, change, side);

		string mint = change != 0 ? (change > 0 ? postMint : preMint) : string.Empty;
		return (delta, mint);
	}
	private static bool TryBalance(
		ref Utf8JsonReader reader,
		ReadOnlySpan<byte> wallet,
		out decimal amount,
		out string mint)
	{
		amount = 0m;
		mint = string.Empty;
		bool ownerOk = false;

		while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
		{
			if (reader.TokenType != JsonTokenType.PropertyName) continue;

			if (reader.ValueTextEquals("owner"u8))
			{
				reader.Read();
				ownerOk = reader.ValueTextEquals(wallet);
			}
			else if (reader.ValueTextEquals("mint"u8))
			{
				reader.Read();
				mint = reader.GetString() ?? string.Empty;
			}
			else if (reader.ValueTextEquals("uiTokenAmount"u8))
			{
				reader.Read(); // {
				while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
				{
					if (reader.TokenType == JsonTokenType.PropertyName &&
						reader.ValueTextEquals("uiAmountString"u8))
					{
						reader.Read();
						amount = decimal.Parse(reader.ValueSpan, CultureInfo.InvariantCulture);
						goto EXIT;
					}
				}
			}
		}

	EXIT:
		return ownerOk;
	}
}