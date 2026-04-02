using GAE.Core.Interfaces;
using GAE.Core.Models;
using GraphQL;
using GraphQL.Client.Abstractions;
using Microsoft.Extensions.Logging;

namespace GAE.WikiSync;

public class WikiService : IWikiService
{
    private readonly IGraphQLClient _graphQLClient;
    private readonly ILogger<WikiService> _logger;

    public WikiService(IGraphQLClient graphQLClient, ILogger<WikiService> logger)
    {
        _graphQLClient = graphQLClient;
        _logger = logger;
    }

    public async Task<bool> CreateOrUpdatePageAsync(string path, string title, string content, CancellationToken ct = default)
    {
        try
        {
            var exists = await PageExistsAsync(path, ct);

            if (exists)
            {
                var pageId = await GetPageIdAsync(path, ct);
                if (pageId is null) return false;

                var updateRequest = new GraphQLRequest
                {
                    Query = """
                        mutation UpdatePage($id: Int!, $content: String!, $title: String!) {
                            pages {
                                update(id: $id, content: $content, title: $title) {
                                    responseResult { succeeded, message }
                                }
                            }
                        }
                        """,
                    Variables = new { id = pageId.Value, content, title }
                };

                var updateResponse = await _graphQLClient.SendMutationAsync<UpdatePageResponse>(updateRequest, ct);
                var updateResult = updateResponse.Data?.Pages?.Update?.ResponseResult;
                if (updateResult?.Succeeded != true)
                {
                    _logger.LogWarning("Wiki page update failed at {Path}: {Message}", path, updateResult?.Message);
                    return false;
                }
            }
            else
            {
                var createRequest = new GraphQLRequest
                {
                    Query = """
                        mutation CreatePage($content: String!, $path: String!, $title: String!) {
                            pages {
                                create(content: $content, path: $path, title: $title, editor: "markdown", locale: "en") {
                                    responseResult { succeeded, message }
                                }
                            }
                        }
                        """,
                    Variables = new { content, path, title }
                };

                var createResponse = await _graphQLClient.SendMutationAsync<CreatePageResponse>(createRequest, ct);
                var createResult = createResponse.Data?.Pages?.Create?.ResponseResult;
                if (createResult?.Succeeded != true)
                {
                    _logger.LogWarning("Wiki page creation failed at {Path}: {Message}", path, createResult?.Message);
                    return false;
                }
            }

            _logger.LogDebug("Synced wiki page at {Path}", path);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Wiki sync failed for {Path}", path);
            return false;
        }
    }

    public async Task<string?> GetPageContentAsync(string path, CancellationToken ct = default)
    {
        try
        {
            var request = new GraphQLRequest
            {
                Query = """
                    query GetPage($path: String!) {
                        pages {
                            singleByPath(path: $path, locale: "en") {
                                content
                            }
                        }
                    }
                    """,
                Variables = new { path }
            };

            var response = await _graphQLClient.SendQueryAsync<SinglePageResponse>(request, ct);
            return response.Data?.Pages?.SingleByPath?.Content;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get wiki page at {Path}", path);
            return null;
        }
    }

    public async Task<bool> PageExistsAsync(string path, CancellationToken ct = default)
    {
        var content = await GetPageContentAsync(path, ct);
        return content is not null;
    }

    public Task SyncPlayerPageAsync(PlayerCharacter player, CancellationToken ct = default)
    {
        var content = WikiTemplates.PlayerPage(player);
        return CreateOrUpdatePageAsync($"players/{player.Id}", player.Name, content, ct);
    }

    public Task SyncRoomPageAsync(Room room, CancellationToken ct = default)
    {
        var content = WikiTemplates.RoomPage(room);
        return CreateOrUpdatePageAsync($"rooms/{room.Id}", room.Name, content, ct);
    }

    public Task SyncNpcPageAsync(Npc npc, CancellationToken ct = default)
    {
        var content = WikiTemplates.NpcPage(npc);
        return CreateOrUpdatePageAsync($"npcs/{npc.Id}", npc.Name, content, ct);
    }

    public Task SyncStoryEntryAsync(StoryEntry entry, CancellationToken ct = default)
    {
        var content = WikiTemplates.StoryEntryPage(entry);
        return CreateOrUpdatePageAsync($"story/{entry.Id}", $"Story — {entry.Timestamp:yyyy-MM-dd HH:mm}", content, ct);
    }

    public async Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        try
        {
            var request = new GraphQLRequest
            {
                Query = "{ site { config { title } } }"
            };
            var response = await _graphQLClient.SendQueryAsync<object>(request, ct);
            return response.Errors is null || response.Errors.Length == 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<WikiSearchResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        try
        {
            var request = new GraphQLRequest
            {
                Query = """
                    query SearchPages($query: String!) {
                        pages {
                            search(query: $query) {
                                results { id, path, title, description }
                            }
                        }
                    }
                    """,
                Variables = new { query }
            };

            var response = await _graphQLClient.SendQueryAsync<SearchPagesResponse>(request, ct);
            var results = response.Data?.Pages?.Search?.Results;
            if (results is null) return [];

            return results
                .Select(r => new WikiSearchResult(r.Id, r.Path, r.Title, r.Description ?? ""))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Wiki search failed for query '{Query}'", query);
            return [];
        }
    }

    public async Task<IReadOnlyList<WikiPageSummary>> GetPagesAsync(string pathPrefix = "", CancellationToken ct = default)
    {
        try
        {
            var request = new GraphQLRequest
            {
                Query = """
                    {
                        pages {
                            list(orderBy: TITLE) { id, path, title }
                        }
                    }
                    """
            };

            var response = await _graphQLClient.SendQueryAsync<ListPagesResponse>(request, ct);
            var pages = response.Data?.Pages?.List;
            if (pages is null) return [];

            IEnumerable<PageListEntry> filtered = pages;
            if (!string.IsNullOrEmpty(pathPrefix))
                filtered = filtered.Where(p => p.Path.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase));

            return filtered
                .Select(p => new WikiPageSummary(p.Id, p.Path, p.Title))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Wiki page listing failed");
            return [];
        }
    }

    private async Task<int?> GetPageIdAsync(string path, CancellationToken ct)
    {
        var request = new GraphQLRequest
        {
            Query = """
                query GetPageId($path: String!) {
                    pages {
                        singleByPath(path: $path, locale: "en") {
                            id
                        }
                    }
                }
                """,
            Variables = new { path }
        };

        var response = await _graphQLClient.SendQueryAsync<SinglePageIdResponse>(request, ct);
        return response.Data?.Pages?.SingleByPath?.Id;
    }

    // GraphQL response DTOs
    private class ResponseResult { public bool Succeeded { get; set; } public string? Message { get; set; } }
    private class MutationResult { public ResponseResult? ResponseResult { get; set; } }
    private class PagesMutation<T> { public T? Create { get; set; } public T? Update { get; set; } }
    private class CreatePageResponse { public PagesMutation<MutationResult>? Pages { get; set; } }
    private class UpdatePageResponse { public PagesMutation<MutationResult>? Pages { get; set; } }
    private class SinglePageResponse { public PagesQuery<PageContent>? Pages { get; set; } }
    private class SinglePageIdResponse { public PagesQuery<PageId>? Pages { get; set; } }
    private class PagesQuery<T> { public T? SingleByPath { get; set; } }
    private class PageContent { public string? Content { get; set; } }
    private class PageId { public int Id { get; set; } }

    // Search/list response DTOs
    private class SearchPagesResponse { public SearchPagesQuery? Pages { get; set; } }
    private class SearchPagesQuery { public SearchResultSet? Search { get; set; } }
    private class SearchResultSet { public List<SearchResultEntry>? Results { get; set; } }
    private class SearchResultEntry { public int Id { get; set; } public string Path { get; set; } = ""; public string Title { get; set; } = ""; public string? Description { get; set; } }

    private class ListPagesResponse { public ListPagesQuery? Pages { get; set; } }
    private class ListPagesQuery { public List<PageListEntry>? List { get; set; } }
    private class PageListEntry { public int Id { get; set; } public string Path { get; set; } = ""; public string Title { get; set; } = ""; }
}
