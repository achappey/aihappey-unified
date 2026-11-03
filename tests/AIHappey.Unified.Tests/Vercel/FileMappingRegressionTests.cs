using System.Text.Json;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.Vercel;

public sealed class FileMappingRegressionTests
{
    [Theory]
    [InlineData("data:image/png;base64,AQID", "data:image/png;base64,AQID")]
    [InlineData("AQID", "data:image/png;base64,AQID")]
    [InlineData("https://example.test/image.png", "https://example.test/image.png")]
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

    [Fact]
    public void Output_file_bytes_become_a_data_url()
    {
        var message = new AIOutputItem
        {
            Role = "assistant",
            Content = [new AIFileContentPart { MediaType = "application/pdf", Data = new byte[] { 1, 2, 3 } }]
        }.ToUIMessage();

        var file = Assert.IsType<FileUIPart>(Assert.Single(message.Parts));

        Assert.Equal("data:application/pdf;base64,AQID", file.Url);
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
