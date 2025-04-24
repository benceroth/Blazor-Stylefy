using SpotifyAPI.Web;
using System.Collections.Concurrent;
using System.Text;

namespace Stylefy.Web
{
    public class SpotifyService
    {
        private readonly SpotifyClient api;
        private readonly PrivateUser profile;

        public SpotifyService(SpotifyClient api, PrivateUser profile)
        {
            this.api = api;
            this.profile = profile;
        }

        public StringBuilder Messages { get; private set; } = new();

        public async Task<List<SavedTrack>> GetSavedTracks()
        {
            Messages.AppendLine("Getting saved tracks.");
            var paging = await api.Library.GetTracks().ConfigureAwait(false);
            var tracks = await api.PaginateAll(paging).ConfigureAwait(false);
            return tracks.ToList();
        }

        public async Task<List<FullPlaylist>> GetPlaylists()
        {
            Messages.AppendLine("Getting existing playlists.");
            var paging = await api.Playlists.CurrentUsers().ConfigureAwait(false);
            var playlists = await api.PaginateAll(paging).ConfigureAwait(false);
            return playlists.ToList();
        }

        public async Task<List<FullTrack>> GetPlaylistTracks(string playlistId)
        {
            var playlist = await api.Playlists.Get(playlistId).ConfigureAwait(false);
            var tracks = await api.PaginateAll(playlist.Tracks).ConfigureAwait(false);
            return tracks.Select(x => x.Track as FullTrack).ToList();
        }

        public async Task<List<FullArtist>> GetArtistsByTracks(IEnumerable<SavedTrack> tracks)
        {
            Messages.AppendLine("Getting artists info.");
            const int artistsCount = 50;
            var result = new List<FullArtist>();
            var artistIds = tracks.SelectMany(x => x.Track.Artists).Select(x => x.Id).Distinct().ToList();

            int i = 0;
            while (artistIds.Count > i * artistsCount)
            {
                var currentIds = artistIds.Skip(i * artistsCount).Take(artistsCount).ToList();
                var response = await api.Artists.GetSeveral(new ArtistsRequest(currentIds)).ConfigureAwait(false);
                result.AddRange(response.Artists);
                i++;
            }

            return result;
        }

        public async Task<IDictionary<string, List<SavedTrack>>> GetTrackGroupsBySavedTracks()
        {
            var tracks = await this.GetSavedTracks().ConfigureAwait(false);
            var artists = await this.GetArtistsByTracks(tracks).ConfigureAwait(false);
            return this.GetTrackGroups(tracks, artists);
        }

        public IDictionary<string, List<SavedTrack>> GetTrackGroups(List<SavedTrack> tracks, List<FullArtist> artists)
        {
            Messages.AppendLine("Putting pieces together.");
            var groups = new ConcurrentDictionary<string, List<SavedTrack>>();
            var genres = artists.Where(x => x.Genres != null).SelectMany(x => x.Genres).Distinct().ToList();
            genres.AsParallel().ForAll(genre =>
            {
                var group = new List<SavedTrack>();
                foreach (var track in tracks)
                {
                    if (track.Track.Artists.Any(x => artists.FirstOrDefault(y => y.Id == x.Id)?.Genres.Contains(genre) == true))
                    {
                        group.Add(track);
                    }
                }
                groups.TryAdd(genre, group);
            });
            return groups;
        }

        public async Task CreateGenrePlaylists(int minTrackCount)
        {
            var playlists = await this.GetPlaylists().ConfigureAwait(false);
            var trackGroups = await this.GetTrackGroupsBySavedTracks().ConfigureAwait(false);
            var groups = trackGroups.Where(x => x.Value.Count > minTrackCount);
            foreach (var group in groups)
            {
                var trackUris = group.Value.Select(x => x.Track.Uri).ToList();
                if (playlists.FirstOrDefault(x => x.Name == group.Key) is FullPlaylist playlist)
                {
                    try
                    {
                        Messages.AppendLine($"Extending {group.Key} playlist.");
                        await this.AddTracksWithoutDuplicateToPlaylist(playlist.Id, trackUris).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Messages.Append($"Skipping interrupted {group.Key} playlist: ");
                        Messages.AppendLine(ex.Message);
                    }
                }
                else
                {
                    try
                    {
                        Messages.AppendLine($"Creating {group.Key} playlist.");
                        var fullPlaylist = await this.CreatePlaylist(group.Key);
                        await this.AddTracksToPlaylist(fullPlaylist.Id, trackUris).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Messages.Append($"Skipping interrupted {group.Key} playlist: ");
                        Messages.AppendLine(ex.Message);
                    }
                }
            }
        }

        public async Task<FullPlaylist> CreatePlaylist(string name, string description = "Generated playlist", bool isPublic = true)
        {
            var request = new PlaylistCreateRequest(name)
            {
                Public = isPublic,
                Description = description,
            };
            return await api.Playlists.Create(this.profile.Id, request).ConfigureAwait(false);
        }

        public async Task AddTracksToPlaylist(string playlistId, List<string> trackUris)
        {
            if (trackUris.Count > 0)
            {
                int i = 0;
                const int trackCount = 50;
                while (trackUris.Count > i * trackCount)
                {
                    var request = new PlaylistAddItemsRequest(trackUris.Skip(i * trackCount).Take(trackCount).ToList());
                    await api.Playlists.AddItems(playlistId, request).ConfigureAwait(false);
                    i++;
                }
            }
        }

        public async Task AddTracksWithoutDuplicateToPlaylist(string playlistId, List<string> trackUris)
        {
            var playlistTracks = await this.GetPlaylistTracks(playlistId).ConfigureAwait(false);
            var existingTracks = playlistTracks.Select(x => x.Uri);
            trackUris = trackUris.Except(existingTracks).Distinct().ToList();
            await this.AddTracksToPlaylist(playlistId, trackUris).ConfigureAwait(false);
        }

        public async Task RemoveTracksFromPlaylist(string playlistId, List<string> trackUris)
        {
            if (trackUris.Count > 0)
            {
                var request = new PlaylistRemoveItemsRequest()
                {
                    Tracks = trackUris.Select(x => new PlaylistRemoveItemsRequest.Item() { Uri = x }).ToList()
                };
                var response = await api.Playlists.RemoveItems(playlistId, request).ConfigureAwait(false);
            }
        }

        public async Task RemoveDuplicateTracksFromPlaylists()
        {
            foreach (var playlist in await this.GetPlaylists().ConfigureAwait(false))
            {
                await this.RemoveDuplicateTracksFromPlaylist(playlist.Id).ConfigureAwait(false);
            }
        }

        public async Task RemoveDuplicateTracksFromPlaylist(string playlistId)
        {
            var playlistTracks = await this.GetPlaylistTracks(playlistId).ConfigureAwait(false);
            var duplicateTracks = playlistTracks
                .GroupBy(x => x.Id).Where(x => x.Count() > 1).SelectMany(x => x.Skip(1))
                .ToList();
            if (duplicateTracks.Count > 0)
            {
                var request = new PlaylistRemoveItemsRequest()
                {
                    Tracks = duplicateTracks.Select(x => new PlaylistRemoveItemsRequest.Item() { Uri = x.Uri }).ToList()
                };
                var response = await api.Playlists.RemoveItems(playlistId, request).ConfigureAwait(false);
            }
        }
    }
}