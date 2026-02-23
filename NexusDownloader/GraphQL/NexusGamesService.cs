using NexusDownloader.Models;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace NexusDownloader.GraphQL
{
    public class NexusGamesService
    {
        private readonly Dictionary<string, string> _gameNames = new();

        public async Task LoadGameNames(HttpClient http)
        {
            if (_gameNames.Count > 0)
                return;

            var json = await http.GetStringAsync(
                "https://data.nexusmods.com/file/nexus-data/games.json");

            foreach (Match m in Regex.Matches(json,
                @"""id"":\s*(\d+).*?""name"":\s*""([^""]+)""",
                RegexOptions.Singleline))
            {
                _gameNames[m.Groups[1].Value] = m.Groups[2].Value;
            }
        }

        public async Task<List<GameFacet>> LoadGames(HttpClient http, string authorId)
        {
            string body = @"{
""operationName"": ""UserProfileTabData"",
""variables"": {
""mediaFilter"": {
""mediaStatus"": [{ ""op"": ""EQUALS"", ""value"": ""published"" }],
""op"": ""AND"",
""owner"": [{ ""op"": ""EQUALS"", ""value"": """ + authorId + @""" }]
}
},
""query"": ""query UserProfileTabData($mediaFilter: MediaSearchFilter){media(count:0,facets:{gameId:[]},filter:$mediaFilter,viewUserBlockedContent:true){facetsData}}""
}";

            using var req = new HttpRequestMessage(HttpMethod.Post,
                "https://api-router.nexusmods.com/graphql");

            req.Version = HttpVersion.Version20;
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");

            var resp = await http.SendAsync(req);
            var json = await resp.Content.ReadAsStringAsync();

            var map = Regex.Match(json, @"""gameId"":\s*\{([^}]+)\}");

            if (!map.Success)
                return new List<GameFacet>();

            var matches = Regex.Matches(map.Groups[1].Value,
                @"""(\d+)"":\s*(\d+)");

            var list = new List<GameFacet>();

            foreach (Match m in matches)
            {
                var id = m.Groups[1].Value;
                var count = int.Parse(m.Groups[2].Value);

                list.Add(new GameFacet
                {
                    Id = id,
                    Name = _gameNames.TryGetValue(id, out var n) ? n : $"Game {id}",
                    Count = count
                });
            }

            return list
                .OrderByDescending(x => x.Count)
                .ThenBy(x => x.Name)
                .ToList();
        }
    }
}