using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ImorImageProcessing.Models;
using Microsoft.Azure.EventGrid.Models;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.EventGrid;
using Microsoft.Extensions.Logging;
using Microsoft.WindowsAzure.Storage.Blob;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;

namespace ImorImageProcessing
{
    public static class ImorImageProcessing
    {

        private static readonly string BLOB_STORAGE_CONNECTION_STRING = Environment.GetEnvironmentVariable("imorblobstorage_STORAGE");

        private static string GetBlobNameFromUrl(string bloblUrl)
        {
            var uri = new Uri(bloblUrl);
            var cloudBlob = new CloudBlob(uri);

            return cloudBlob.Name;
        }

        private static IImageEncoder GetEncoder(string extension)
        {
            IImageEncoder encoder = null;

            extension = extension.Replace(".", "");

            var isSupported = Regex.IsMatch(extension, "gif|png|jpe?g", RegexOptions.IgnoreCase);

            if (isSupported)
            {
                switch (extension)
                {
                    case "png":
                        encoder = new PngEncoder();
                        break;

                    case "jpg":
                        encoder = new JpegEncoder();
                        break;

                    case "jpeg":
                        encoder = new JpegEncoder();
                        break;

                    case "gif":
                        encoder = new GifEncoder();
                        break;

                    default:
                        break;
                }
            }

            return encoder;
        }

        [FunctionName("ImorImageProcessing")]
        public static async Task Run(
            [EventGridTrigger]EventGridEvent eventGridEvent,
            [Blob("{data.url}", FileAccess.Read, Connection = "imorblobstorage_STORAGE")] Stream input,
            ILogger log)
        {
            try
            {
                if (input != null)
                {
                    var createdEvent = ((JObject)eventGridEvent.Data).ToObject<StorageBlobCreatedEventData>();
                    var extension = Path.GetExtension(createdEvent.Url);
                    var encoder = GetEncoder(extension);

                    log.LogInformation($"C# Blob Trigger Function Processed Blob:\n Name: '{createdEvent.Url}'\n Size: {input.Length} Bytes");

                    if (encoder != null)
                    {
                        log.LogInformation($"Analyzing uploaded image '{createdEvent.Url}' ..");

                        ImageAnalysisInfo result = await AnalyzeImageAsync(createdEvent.Url, log);

                        log.LogInformation(" RESULT: RequestId: " + result.requestId);

                        log.LogInformation("Analyzing done successfully.");

                        log.LogInformation("Adding data to SPARQL Endpoint ..");

                        string imageName = Path.GetFileNameWithoutExtension(createdEvent.Url);

                        ImorImage imorImage = new ImorImage()
                        {
                            Uri = "http://www.semanticweb.org/ImagesOntology#" + imageName,
                            Description = result.description.captions[0].text,
                            Content = createdEvent.Url,
                            Tags = result.description.tags
                        };

                        await AddImage(imorImage, log);

                        log.LogInformation("Adding data to SPARQL Endpoint done successfully.");
                    }
                    else
                    {
                        log.LogError($"No encoder support for: {createdEvent.Url}");
                    }
                }
                else
                {
                    log.LogError("No input stream.");
                }
            }
            catch (Exception ex)
            {
                log.LogError(ex.Message);
                throw ex;
            }
        }

        private async static Task<ImageAnalysisInfo> AnalyzeImageAsync(string imageUrl, ILogger log)
        {
            HttpClient client = new HttpClient();

            var key = Environment.GetEnvironmentVariable("SubscriptionKey");
            client.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", key);

            var endpoint = Environment.GetEnvironmentVariable("VisionEndpoint");

            var content = new StringContent(
                "{ \"url\": \"" + imageUrl + "\" }", Encoding.UTF8, "application/json");

            var results = await client.PostAsync(endpoint + "/analyze?visualFeatures=Description&language=en", content);

            log.LogInformation(results.ToString());

            try
            {
                results.EnsureSuccessStatusCode();
            }
            catch (Exception ex)
            {
                log.LogError(ex.Message);
                throw ex;
            }

            var result = await results.Content.ReadAsAsync<ImageAnalysisInfo>();

            return result;
        }

        private async static Task AddImage(ImorImage imorImage, ILogger log)
        {
            HttpClient client = new HttpClient();

            var endpoint = Environment.GetEnvironmentVariable("SparqlEndpoint");

            var content = new StringContent(
                JsonConvert.SerializeObject(imorImage),
                Encoding.UTF8,
                "application/json");

            var results = await client.PostAsync(endpoint + "/images/create", content);

            log.LogInformation(results.ToString());

            try
            {
                results.EnsureSuccessStatusCode();
            }
            catch (Exception ex)
            {
                log.LogError(ex.Message);
                throw ex;
            }
        }

        private async static Task<byte[]> ToByteArrayAsync(Stream stream)
        {
            Int32 length = stream.Length > Int32.MaxValue ? Int32.MaxValue : Convert.ToInt32(stream.Length);
            byte[] buffer = new Byte[length];
            await stream.ReadAsync(buffer, 0, length);
            stream.Position = 0;

            return buffer;
        }

    }
}
