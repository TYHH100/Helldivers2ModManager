using System.Net.Http;

namespace Helldivers2ModManager.Services;

internal sealed class NexusService : IDisposable
{
	private static readonly string s_apiKey = "";
	private readonly HttpClient _client;

	public NexusService()
	{
		_client = new()
		{
			BaseAddress = new Uri("https://api.nexusmods.com/v1")
		};
		_client.DefaultRequestHeaders.Add("accept", "application/json");
		_client.DefaultRequestHeaders.Add("apikey", s_apiKey);
	}

	public void Dispose()
	{
		_client.Dispose();
	}
}
