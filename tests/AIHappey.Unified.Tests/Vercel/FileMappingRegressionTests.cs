using System.Text.Json;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.Vercel;

public sealed class FileMappingRegressionTests
{
    [Theory]
    [InlineData("data:image/png;base64,AQID", "data:image/png;base64,AQID")]
    [InlineData("https://example.test/image.png", "https://example.test/image.png")]
    [InlineData("https://example.test/a,b.png?size=1,2#preview,3", "https://example.test/a,b.png?size=1,2#preview,3")]
    public void Output_file_url_remains_renderable(string data, string expectedUrl)
    {
        var message = new AIOutputItem
        {
            Role = "assistant",
            Content = [new AIFileContentPart { MediaType = "image/png", Data = data }]
        }.ToUIMessage();

        var file = Assert.IsType<FileUIPart>(Assert.Single(message.Parts));

        Assert.Equal(expectedUrl, file.Url);
        Assert.Equal("image/png", file.MediaType);
    }

    [Theory]
    [InlineData("test")]
    [InlineData("file")]
    [InlineData("AQID")]
    [InlineData("AQI=")]
    [InlineData("images/photo")]
    [InlineData("/images/test")]
    [InlineData("./image.png")]
    [InlineData("../image.png")]
    [InlineData("image.png")]
    [InlineData("not base64!!")]
    [InlineData("AQI!")]
    [InlineData("A===")]
    [InlineData("AQI")]
    [InlineData("")]
    public void Output_file_strings_are_not_inferred_to_be_base64(string data)
    {
        foreach (var payload in new object[] { data, JsonSerializer.SerializeToElement(data) })
        {
            var message = new AIOutputItem
            {
                Role = "assistant",
                Content = [new AIFileContentPart { MediaType = "image/png", Data = payload }]
            }.ToUIMessage();

            var file = Assert.IsType<FileUIPart>(Assert.Single(message.Parts));

            Assert.Equal(data, file.Url);
        }
    }

    [Fact]
    public void Output_file_bytes_become_a_data_url()
    {
        byte[] bytes = [1, 2, 3];
        foreach (var payload in new object[] { bytes, bytes.AsMemory(), new ReadOnlyMemory<byte>(bytes) })
        {
            var message = new AIOutputItem
            {
                Role = "assistant",
                Content = [new AIFileContentPart { MediaType = "application/pdf", Data = payload }]
            }.ToUIMessage();

            var file = Assert.IsType<FileUIPart>(Assert.Single(message.Parts));

            Assert.Equal("data:application/pdf;base64,AQID", file.Url);
        }
    }

    [Fact]
    public void Output_file_json_string_preserves_its_data_url()
    {
        var message = new AIOutputItem
        {
            Role = "assistant",
            Content = [new AIFileContentPart
            {
                MediaType = "image/png",
                Data = JsonSerializer.SerializeToElement("data:image/png;base64,AQID")
            }]
        }.ToUIMessage();

        var file = Assert.IsType<FileUIPart>(Assert.Single(message.Parts));

        Assert.Equal("data:image/png;base64,AQID", file.Url);
    }
}
