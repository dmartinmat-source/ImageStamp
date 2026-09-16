using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

using Xunit;

namespace ImageStamp.Tests;

public sealed class BatchCompositionEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public BatchCompositionEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [RequiresPostgresFact]
    public async Task CreateBatch_WithMultipleBaseImages_ReturnsAPngForEachImageInAZip()
    {
        using HttpClient client = _factory.CreateClient();
        using MultipartFormDataContent request = new MultipartFormDataContent();
        int compositionCountBefore = await GetCompositionCountAsync(client);

        request.Add(CreatePngContent(TestImages.Red), "baseImages", "vehicle.png");
        request.Add(CreatePngContent(TestImages.Blue), "baseImages", "vehicle.png");
        request.Add(CreatePngContent(TestImages.Green), "logo", "logo.png");
        request.Add(new StringContent("[{\"type\":\"image\",\"x\":0,\"y\":0,\"opacity\":1,\"zIndex\":1,\"imageKey\":\"logo\"}]"), "layers");

        using HttpResponseMessage response = await client.PostAsync("/api/compositions/batch", request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType!.MediaType);

        byte[] archiveContent = await response.Content.ReadAsByteArrayAsync();
        using MemoryStream archiveStream = new MemoryStream(archiveContent);
        using ZipArchive archive = new ZipArchive(archiveStream, ZipArchiveMode.Read);

        Assert.Equal(2, archive.Entries.Count);
        Assert.Equal("vehicle.png", archive.Entries[0].Name);
        Assert.Equal("vehicle-2.png", archive.Entries[1].Name);

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            await using Stream content = entry.Open();
            using Image<Rgba32> composed = await Image.LoadAsync<Rgba32>(content);
            Assert.Equal(TestImages.Green, composed[0, 0]);
        }

        Assert.Equal(compositionCountBefore + 2, await GetCompositionCountAsync(client));
    }

    [RequiresPostgresFact]
    public async Task Create_WithBlurLayer_MapsTheMultipartLayerAndReturnsBlurredPng()
    {
        using HttpClient client = _factory.CreateClient();
        using MultipartFormDataContent request = new MultipartFormDataContent();

        request.Add(CreatePngContent(TestImages.Red), "baseImage", "base.png");
        request.Add(new StringContent("[{\"type\":\"solid\",\"x\":1,\"y\":0,\"width\":3,\"height\":4,\"color\":\"#0000FF\",\"opacity\":1,\"zIndex\":1},{\"type\":\"blur\",\"x\":0,\"y\":0,\"width\":4,\"height\":4,\"sigma\":1,\"zIndex\":2}]"), "layers");

        using HttpResponseMessage response = await client.PostAsync("/api/compositions", request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType!.MediaType);

        using Image<Rgba32> result = Image.Load<Rgba32>(await response.Content.ReadAsByteArrayAsync());
        Rgba32 blurredBoundary = result[1, 2];

        Assert.InRange(blurredBoundary.R, 1, 254);
        Assert.InRange(blurredBoundary.B, 1, 254);
    }

    [Fact]
    public async Task CreateBatch_WithoutBaseImages_ReturnsBadRequest()
    {
        using HttpClient client = _factory.CreateClient();
        using MultipartFormDataContent request = new MultipartFormDataContent();

        request.Add(new StringContent("[]"), "layers");

        using HttpResponseMessage response = await client.PostAsync("/api/compositions/batch", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("baseImages", await response.Content.ReadAsStringAsync());
    }

    private static ByteArrayContent CreatePngContent(Rgba32 color)
    {
        ByteArrayContent content = new ByteArrayContent(TestImages.SolidPng(4, 4, color));
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        return content;
    }

    private static async Task<int> GetCompositionCountAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.GetAsync("/api/compositions");
        response.EnsureSuccessStatusCode();

        await using Stream content = await response.Content.ReadAsStreamAsync();
        using JsonDocument compositions = await JsonDocument.ParseAsync(content);
        return compositions.RootElement.GetArrayLength();
    }
}
