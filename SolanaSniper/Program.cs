using System.Text;
using Microsoft.Extensions.Configuration;

namespace SolanaSniper;

public class Program
{
	public static async Task Main(string[] args)
	{
		var cfgBuilder = new ConfigurationBuilder()
			.SetBasePath(Directory.GetCurrentDirectory())
			.AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
			.AddEnvironmentVariables();

		IConfigurationRoot cfg = cfgBuilder.Build();

		// 3) биндим в POCO
		var endpoints = cfg.GetSection("Endpoints").Get<Endpoints>()!;
		var scalper = cfg.GetSection("Scalper").Get<ScalperOptions>()!;
		
		Console.OutputEncoding = Encoding.UTF8;
		Console.WriteLine("🔫 Запуск Solana Sniper Monitor (Logs Mode)...\n");

		// Топ 5 трейдеров с Kolscan (Daily)
		var wallets = new Dictionary<string, string>
		{
			{ "6mWEJG9LoRdto8TwTdZxmnJpkXpTsEerizcGiCNZvzXd", "slingoor" },
			{ "G6fUXjMKPJzCY1rveAE6Qm7wy5U3vZgKDJmN1VPAdiZC", "clukz" },
			{ "2fg5QD1eD7rzNNCsvnhmXFm5hqNgwTTG8p7kQ6f3rx6f", "Cupsey" },
			{ "Ez2jp3rwXUbaTx7XwiHGaWVgTPFdzJoSg8TopqbxfaJN", "Keano" },
			{ "4BdKaxN8G6ka4GYtQQWk4G4dZRUTX2vQH9GcXdBREFUk", "Jijo" },
			{ "CyaE1VxvBrahnPWkqm5VsdCvyS2QmNht2UFrKJHga54o", "Cented" },
			{ "BtMBMPkoNbnLF9Xn552guQq528KKXcsNBNNBre3oaQtr", "Letterbomb" },
			{ "J6TDXvarvpBdPXTaTU8eJbtso1PUCYKGkVtMKUUY8iEa", "Pain"},
			{"5B52w1ZW9tuwUduueP5J7HXz5AcGfruGoX6YoAudvyxG","Yenni"},
		};

		await using var sniper = new SolanaSniperMonitor(endpoints, wallets);
		await sniper.StartAsync();
	}
}
