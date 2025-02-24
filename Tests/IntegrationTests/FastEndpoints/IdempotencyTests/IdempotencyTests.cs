﻿using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using TestCases.Idempotency;

namespace IdempotencyTests;

public class IdempotencyTests(Sut App) : TestBase<Sut>
{
    [Fact]
    public async Task Header_Not_Present()
    {
        var url = $"{Endpoint.BaseRoute}/123";
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        var res = await App.Client.SendAsync(req, Cancellation);
        res.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Multiple_Headers()
    {
        var url = $"{Endpoint.BaseRoute}/123";
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("Idempotency-Key", ["1", "2"]);
        var res = await App.Client.SendAsync(req, Cancellation);
        res.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task MultiPart_Form_Request()
    {
        var idmpKey = Guid.NewGuid().ToString();
        var url = $"{Endpoint.BaseRoute}/321";

        using var fileContent = new ByteArrayContent(
            await new StreamContent(File.OpenRead("test.png"))
                .ReadAsByteArrayAsync(Cancellation));

        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("image/png");

        using var form = new MultipartFormDataContent();
        form.Add(fileContent, "File", "test.png");
        form.Add(new StringContent("500"), "Width");

        var req1 = new HttpRequestMessage(HttpMethod.Get, url);
        req1.Content = form;
        req1.Headers.Add("Idempotency-Key", idmpKey);

        //initial request - uncached response
        var res1 = await App.Client.SendAsync(req1, Cancellation);
        res1.IsSuccessStatusCode.ShouldBeTrue();
        res1.Headers.Any(h => h.Key == "Idempotency-Key" && h.Value.First() == idmpKey).ShouldBeTrue();

        var rsp1 = await res1.Content.ReadFromJsonAsync<Response>(Cancellation);
        rsp1.ShouldNotBeNull();
        rsp1!.Id.ShouldBe("321");

        var ticks = rsp1.Ticks;
        ticks.ShouldBeGreaterThan(0);

        //duplicate request - cached response
        var req2 = new HttpRequestMessage(HttpMethod.Get, url);
        req2.Content = form;
        req2.Headers.Add("Idempotency-Key", idmpKey);

        var res2 = await App.Client.SendAsync(req2, Cancellation);
        res2.IsSuccessStatusCode.ShouldBeTrue();
        var rsp2 = await res2.Content.ReadFromJsonAsync<Response>(Cancellation);
        rsp2.ShouldNotBeNull();
        rsp2!.Id.ShouldBe("321");
        rsp2.Ticks.ShouldBe(ticks);

        //changed request - uncached response
        var req3 = new HttpRequestMessage(HttpMethod.Get, url);
        form.Add(new StringContent("500"), "Height"); // the change
        req3.Content = form;
        req3.Headers.Add("Idempotency-Key", idmpKey);

        var res3 = await App.Client.SendAsync(req3, Cancellation);
        res3.IsSuccessStatusCode.ShouldBeTrue();

        var rsp3 = await res3.Content.ReadFromJsonAsync<Response>(Cancellation);
        rsp3.ShouldNotBeNull();
        rsp3!.Id.ShouldBe("321");
        rsp3.Ticks.ShouldNotBe(ticks);
    }

    [Fact]
    public async Task Json_Body_Request()
    {
        var idmpKey = Guid.NewGuid().ToString();
        var client = App.CreateClient(c => c.DefaultRequestHeaders.Add("Idempotency-Key", idmpKey));
        var req = new Request { Content = "hello" };

        //initial request - uncached response
        var (res1, rsp1) = await client.GETAsync<Endpoint, Request, Response>(req);
        res1.IsSuccessStatusCode.ShouldBeTrue();

        var ticks = rsp1.Ticks;
        ticks.ShouldBeGreaterThan(0);

        //duplicate request - cached response
        var (res2, rsp2) = await client.GETAsync<Endpoint, Request, Response>(req);
        res2.IsSuccessStatusCode.ShouldBeTrue();

        rsp2.Ticks.ShouldBe(ticks);

        //changed request - uncached response
        req.Content = "bye"; //the change
        var (res3, rsp3) = await client.GETAsync<Endpoint, Request, Response>(req);
        res3.IsSuccessStatusCode.ShouldBeTrue();

        rsp3.Ticks.ShouldNotBe(ticks);
    }
}