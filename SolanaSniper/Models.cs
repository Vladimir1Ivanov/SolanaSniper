namespace SolanaSniper;

public enum Side : byte { None, Buy, Sell }

public readonly record struct Delta(decimal Pre, decimal Post, decimal Change, Side Side);

public readonly record struct LogsNotificationKey(
		int Subscription,
		long Slot,
		string Signature,
		bool HasError
	);

public readonly record struct RpcLog(
	long UtcTicks,
	string Method,
	string RequestId,
	string RpcEndpoint,
	int StatusCode,
	double RoundTripMs,
	byte[] JsonBody);
