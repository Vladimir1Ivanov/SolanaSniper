namespace SolanaSniper;

public sealed record Endpoints(
	string RestUrl,
	string WssUrl,
	string JupiterQuote,
	string JupiterSwap,
	string JitoBundle,
	string DbPath);

public sealed record ScalperOptions(
	string PrivateKey,
	string Wallet,
	decimal SpendSol,
	int PriorityFee);

