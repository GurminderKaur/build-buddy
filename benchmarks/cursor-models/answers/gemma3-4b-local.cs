using System;
using System.Net.Http;

var client = new HttpClient();
string body = client.GetStringAsync("https://example.com").Result;
int length = body.Length;
Console.WriteLine($"Length: {length}, half: {Half(length)}");

static int Half(int n)
{
    if (n % 2 == 0)
        return n / 2;
    else
        return (n + 1) / 2;
}
