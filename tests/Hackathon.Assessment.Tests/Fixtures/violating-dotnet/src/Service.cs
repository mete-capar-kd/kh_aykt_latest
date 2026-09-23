using System.Net.Http;

public sealed class Service
{
    public HttpClient Client { get; } = new HttpClient();
}
