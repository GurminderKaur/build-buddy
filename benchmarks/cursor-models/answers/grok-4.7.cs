using System.Net.Http;

var client = new HttpClient();
string body = await client.GetStringAsync("https://example.com");
int length = body.Length;
Console.WriteLine($"Length: {length}, half: {Half(length)}");

static int Half(int n)
{
    if (n > 0)
        return n / 2;
    return 0;
}
