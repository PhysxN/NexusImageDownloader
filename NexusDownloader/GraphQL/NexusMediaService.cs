using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace NexusDownloader.GraphQL
{
    public class NexusMediaService
    {
        private readonly SemaphoreSlim _gqlLimiter;

        public NexusMediaService(SemaphoreSlim limiter)
        {
            _gqlLimiter = limiter;
        }

        public async Task<string?> GetMediaPage(HttpClient http, int offset, int count, string authorId, string? gameId)
        {
            await _gqlLimiter.WaitAsync();

            try
            {
                string facets = string.IsNullOrWhiteSpace(gameId)
                    ? "null"
                    : @"{""gameId"":[""" + gameId + @"""]}";

                string body = @"{
""operationName"":""ProfileMedia"",
""variables"":{
""count"":" + count + @",
""facets"":" + facets + @",
""filter"":{
""mediaStatus"":[{""op"":""EQUALS"",""value"":""published""}],
""owner"":[{""op"":""EQUALS"",""value"":""" + authorId + @"""}]
},
""offset"":" + offset + @",
""sort"":{""createdAt"":{""direction"":""DESC""}}
},
""query"":""query ProfileMedia($count:Int,$facets:MediaFacet,$filter:MediaSearchFilter,$offset:Int,$sort:[MediaSearchSort!]){media(count:$count,facets:$facets,filter:$filter,offset:$offset,sort:$sort,viewUserBlockedContent:true){nodes{... on Image{thumbnailUrl} ... on SupporterImage{thumbnailUrl}}}}""
}";

                using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
                var resp = await http.PostAsync("https://api-router.nexusmods.com/graphql", content);
                return await resp.Content.ReadAsStringAsync();
            }
            catch
            {
                return null;
            }
            finally
            {
                _gqlLimiter.Release();
            }
        }
    }
}