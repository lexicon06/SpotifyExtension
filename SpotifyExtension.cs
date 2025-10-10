using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Newtonsoft.Json;
using iconnect;

namespace SpotifyExtension
{
    public class SpotifyServerEvents : IExtension
    {
        private IHostApp host;
        private HttpClient httpClient;
        private Dictionary<string, UserSpotifyData> userTokens;
        private Dictionary<string, string> stateToUsername; // Map state GUID to username  
        private DateTime lastCheck = DateTime.MinValue;
        private string callbackFilePath;
        private string stateMappingFilePath;

        private const string SPOTIFY_CLIENT_ID = "YOUR_SPOTIFY_CLIENT_ID";
        private const string SPOTIFY_CLIENT_SECRET = "YOUR_SPOTIFY_CLIENT_SECRET";
        private const string REDIRECT_URI = "YOUR_REDIRECT_URI/spotify/callback";
        private const int CHECK_INTERVAL_SECONDS = 30;

        private JsonSerializerSettings jsonSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Include
        };

        public SpotifyServerEvents(IHostApp cb)
        {
            host = cb;
            httpClient = new HttpClient();
            userTokens = new Dictionary<string, UserSpotifyData>();
            stateToUsername = new Dictionary<string, string>();
        }

        public void Load()
        {
            host.WriteLog("Spotify Extension loading...");
            callbackFilePath = Path.Combine(@"C:\web", "spotify_callbacks.json");
            stateMappingFilePath = Path.Combine(@"C:\web", "spotify_state_mapping.json");
            LoadUserTokens();
            LoadStateMapping();
            host.WriteLog("Spotify Extension loaded - ready for OAuth callbacks");
        }

        public void ServerStarted()
        {
            host.WriteLog("Server started - Spotify Extension active");
            host.WriteLog("Users can type /spotify to connect their Spotify account");
        }

        #region OAuth Handling  

        private void CheckForOAuthCallbacks()
        {
            try
            {
                if (!File.Exists(callbackFilePath))
                    return;

                string json = File.ReadAllText(callbackFilePath);
                var callbacks = JsonConvert.DeserializeObject<List<OAuthCallback>>(json);
                if (callbacks == null || callbacks.Count == 0)
                    return;

                foreach (var callback in callbacks)
                {
                    // Look up username from state  
                    if (!stateToUsername.TryGetValue(callback.State, out string username))
                    {
                        host.WriteLog($"Unknown state in callback: {callback.State}");
                        continue;
                    }

                    Task.Run(() => ExchangeCodeForToken(callback.Code, username, callback.State));
                }

                File.Delete(callbackFilePath);
                host.WriteLog($"Processed {callbacks.Count} OAuth callback(s)");
            }
            catch (Exception ex)
            {
                host.WriteLog($"Error checking OAuth callbacks: {ex.Message}");
            }
        }

        private async Task ExchangeCodeForToken(string code, string username, string state)
        {
            try
            {
                var content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string,string>("grant_type","authorization_code"),
                    new KeyValuePair<string,string>("code",code),
                    new KeyValuePair<string,string>("redirect_uri",REDIRECT_URI),
                    new KeyValuePair<string,string>("client_id",SPOTIFY_CLIENT_ID),
                    new KeyValuePair<string,string>("client_secret",SPOTIFY_CLIENT_SECRET)
                });

                var response = await httpClient.PostAsync("https://accounts.spotify.com/api/token", content);
                var json = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    host.WriteLog($"Failed to exchange OAuth code: {response.StatusCode} | {json}");
                    return;
                }

                var tokenData = JsonConvert.DeserializeObject<SpotifyTokenResponse>(json);
                if (tokenData == null) return;

                userTokens[username] = new UserSpotifyData
                {
                    Username = username,
                    State = state,
                    AccessToken = tokenData.access_token,
                    RefreshToken = tokenData.refresh_token,
                    ExpiresAt = DateTime.Now.AddSeconds(tokenData.expires_in),
                    LastTrackId = null
                };

                SaveUserTokens();

                // Clean up state mapping  
                if (stateToUsername.ContainsKey(state))
                {
                    stateToUsername.Remove(state);
                    SaveStateMapping();
                }

                host.WriteLog($"Spotify authorized for {username} (access token stored)");
                var user = host.Users.Find(u => u.Name == username);
                if (user != null)
                    host.PublicToTarget(user, "Spotify", "✓ Your Spotify account has been connected successfully!");
            }
            catch (Exception ex)
            {
                host.WriteLog($"ExchangeCodeForToken failed: {ex.Message}");
            }
        }

        private async Task<bool> RefreshAccessToken(UserSpotifyData userData)
        {
            if (string.IsNullOrEmpty(userData.RefreshToken))
            {
                host.WriteLog($"No refresh token for {userData.Username}, removing credentials");
                RemoveUserTokens(userData.Username);
                return false;
            }

            try
            {
                var content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string,string>("grant_type","refresh_token"),
                    new KeyValuePair<string,string>("refresh_token",userData.RefreshToken.Trim()),
                    new KeyValuePair<string,string>("client_id",SPOTIFY_CLIENT_ID),
                    new KeyValuePair<string,string>("client_secret",SPOTIFY_CLIENT_SECRET)
                });

                var response = await httpClient.PostAsync("https://accounts.spotify.com/api/token", content);
                var json = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    host.WriteLog($"Refresh failed: {json}");
                    RemoveUserTokens(userData.Username);
                    return false;
                }

                var tokenData = JsonConvert.DeserializeObject<SpotifyTokenResponse>(json);
                if (tokenData == null || string.IsNullOrEmpty(tokenData.access_token)) return false;

                userData.AccessToken = tokenData.access_token;
                userData.ExpiresAt = DateTime.Now.AddSeconds(tokenData.expires_in);

                if (!string.IsNullOrEmpty(tokenData.refresh_token))
                    userData.RefreshToken = tokenData.refresh_token;

                SaveUserTokens();
                host.WriteLog($"Token refreshed successfully for {userData.Username}");
                return true;
            }
            catch (Exception ex)
            {
                host.WriteLog($"Token refresh exception for {userData.Username}: {ex.Message}");
                return false;
            }
        }

        private void RemoveUserTokens(string username)
        {
            if (userTokens.ContainsKey(username))
                userTokens.Remove(username);
            SaveUserTokens();
        }

        #endregion

        #region Spotify API  

        private async Task CheckCurrentlyPlaying(UserSpotifyData userData)
        {
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, "https://api.spotify.com/v1/me/player/currently-playing");
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", userData.AccessToken);

                var response = await httpClient.SendAsync(request);
                if (response.StatusCode == System.Net.HttpStatusCode.NoContent) return;
                if (!response.IsSuccessStatusCode) return;

                var json = await response.Content.ReadAsStringAsync();
                var playback = JsonConvert.DeserializeObject<SpotifyPlaybackResponse>(json);
                if (playback?.item == null || !playback.is_playing) return;

                if (userData.LastTrackId == playback.item.id) return;

                userData.LastTrackId = playback.item.id;
                SaveUserTokens();

                string message = $"🎵 {userData.Username} is now playing: {playback.item.name} by {playback.item.artists[0].name}";
                host.WriteLog(message);

                host.Users.All(client => host.PublicToTarget(client, "Spotify", message));
            }
            catch (Exception ex)
            {
                host.WriteLog($"Error checking Spotify: {ex.Message}");
            }
        }

        #endregion

        #region Data Persistence  

        private void LoadUserTokens()
        {
            try
            {
                string path = Path.Combine(host.DataPath, "spotify_tokens.json");
                if (!File.Exists(path)) return;

                var json = File.ReadAllText(path);
                userTokens = JsonConvert.DeserializeObject<Dictionary<string, UserSpotifyData>>(json) ?? new Dictionary<string, UserSpotifyData>();
                host.WriteLog($"Loaded {userTokens.Count} Spotify user tokens");
            }
            catch (Exception ex)
            {
                host.WriteLog($"Failed to load Spotify tokens: {ex.Message}");
            }
        }

        private void SaveUserTokens()
        {
            try
            {
                string path = Path.Combine(host.DataPath, "spotify_tokens.json");
                File.WriteAllText(path, JsonConvert.SerializeObject(userTokens, jsonSettings));
            }
            catch (Exception ex)
            {
                host.WriteLog($"Failed to save Spotify tokens: {ex.Message}");
            }
        }

        private void LoadStateMapping()
        {
            try
            {
                if (!File.Exists(stateMappingFilePath)) return;

                var json = File.ReadAllText(stateMappingFilePath);
                stateToUsername = JsonConvert.DeserializeObject<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
                host.WriteLog($"Loaded {stateToUsername.Count} state mappings");
            }
            catch (Exception ex)
            {
                host.WriteLog($"Failed to load state mappings: {ex.Message}");
            }
        }

        private void SaveStateMapping()
        {
            try
            {
                File.WriteAllText(stateMappingFilePath, JsonConvert.SerializeObject(stateToUsername, jsonSettings));
            }
            catch (Exception ex)
            {
                host.WriteLog($"Failed to save state mappings: {ex.Message}");
            }
        }

        #endregion

        #region IExtension  

        public void CycleTick()
        {
            CheckForOAuthCallbacks();

            if ((DateTime.Now - lastCheck).TotalSeconds < CHECK_INTERVAL_SECONDS) return;
            lastCheck = DateTime.Now;

            host.Users.All(client =>
            {
                if (!userTokens.TryGetValue(client.Name, out var userData)) return;
                if (string.IsNullOrEmpty(userData.AccessToken)) return;

                Task.Run(async () =>
                {
                    if (DateTime.Now >= userData.ExpiresAt)
                    {
                        bool refreshed = await RefreshAccessToken(userData);
                        if (!refreshed) return;
                    }

                    await CheckCurrentlyPlaying(userData);
                });
            });
        }

        public void Command(IUser client, string command, IUser target, string args)
        {
            if (command == "spotify")
            {
                if (userTokens.TryGetValue(client.Name, out var existingData) &&
                    !string.IsNullOrEmpty(existingData.AccessToken))
                {
                    host.PublicToTarget(client, "Spotify", "✓ Your Spotify account is already connected!");
                    return;
                }

                string state = Guid.NewGuid().ToString();
                string scopes = "user-read-currently-playing user-read-playback-state";

                // Store state-to-username mapping  
                stateToUsername[state] = client.Name;
                SaveStateMapping();

                string authUrl = $"https://accounts.spotify.com/authorize?" +
                    $"client_id={SPOTIFY_CLIENT_ID}&response_type=code&redirect_uri={Uri.EscapeDataString(REDIRECT_URI)}&" +
                    $"scope={Uri.EscapeDataString(scopes)}&state={state}";

                client.URL(authUrl, "Click here to connect your Spotify account");
                host.PublicToTarget(client, "Spotify", "Connect your Spotify account using the link above.");
                host.WriteLog($"Sent Spotify auth URL to {client.Name} with state {state}");
            }
            else if (command == "song")
            {
                if (!userTokens.TryGetValue(client.Name, out var userData))
                {
                    host.PublicToTarget(client, "Spotify", "You need to connect your Spotify account first. Type /spotify");
                    return;
                }

                if (string.IsNullOrEmpty(userData.AccessToken))
                {
                    host.PublicToTarget(client, "Spotify", "Your Spotify authentication is still processing. Please wait a moment and try again.");
                    return;
                }

                Task.Run(async () =>
                {
                    try
                    {
                        if (DateTime.Now >= userData.ExpiresAt)
                        {
                            bool refreshed = await RefreshAccessToken(userData);
                            if (!refreshed)
                            {
                                host.PublicToTarget(client, "Spotify", "Failed to refresh your Spotify connection. Please reconnect with /spotify");
                                return;
                            }
                        }

                        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.spotify.com/v1/me/player/currently-playing");
                        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", userData.AccessToken);

                        var response = await httpClient.SendAsync(request);
                        if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
                        {
                            host.PublicToTarget(client, "Spotify", "You're not currently playing anything on Spotify.");
                            return;
                        }

                        if (!response.IsSuccessStatusCode)
                        {
                            host.PublicToTarget(client, "Spotify", "Failed to get your current track. Please try again.");
                            return;
                        }

                        var json = await response.Content.ReadAsStringAsync();
                        var playback = JsonConvert.DeserializeObject<SpotifyPlaybackResponse>(json);

                        if (playback?.item == null)
                        {
                            host.PublicToTarget(client, "Spotify", "You're not currently playing anything on Spotify.");
                            return;
                        }

                        string message = playback.is_playing
                            ? $"🎵 Now playing: {playback.item.name} by {playback.item.artists[0].name}"
                            : $"⏸️ Paused: {playback.item.name} by {playback.item.artists[0].name}";

                        host.PublicToTarget(client, "Spotify", message);
                    }
                    catch (Exception ex)
                    {
                        host.WriteLog($"Error in /song command: {ex.Message}");
                        host.PublicToTarget(client, "Spotify", "An error occurred while fetching your current track.");
                    }
                });
            }
            else if (command == "spotifyoff")
            {
                RemoveUserTokens(client.Name);
                host.PublicToTarget(client, "Spotify", "Your Spotify connection has been removed.");
            }
        }

        public void Joined(IUser client)
        {
            if (userTokens.TryGetValue(client.Name, out var userData) &&
                !string.IsNullOrEmpty(userData.AccessToken))
            {
                host.PublicToTarget(client, "Spotify", "✓ Your Spotify is connected! We'll show what you're listening to. Type /song to see your current track.");
            }
        }

        // IExtension interface implementations  
        public void UnhandledProtocol(IUser client, bool custom, byte msg, byte[] packet) { }
        public bool Joining(IUser client) => true;
        public void Rejected(IUser client, RejectedMsg msg) { }
        public void Parting(IUser client) { }
        public void Parted(IUser client) { }
        public bool AvatarReceived(IUser client) => true;
        public bool PersonalMessageReceived(IUser client, string text) => true;
        public void TextReceived(IUser client, string text) { }
        public string TextSending(IUser client, string text) => text;
        public void TextSent(IUser client, string text) { }
        public void EmoteReceived(IUser client, string text) { }
        public string EmoteSending(IUser client, string text) => text;
        public void EmoteSent(IUser client, string text) { }
        public void PrivateSending(IUser client, IUser target, IPrivateMsg msg) { }
        public void PrivateSent(IUser client, IUser target) { }
        public void BotPrivateSent(IUser client, string text) { }
        public bool Nick(IUser client, string name) => true;
        public void Help(IUser client) { }
        public void FileReceived(IUser client, string filename, string title, MimeType type) { }
        public bool Ignoring(IUser client, IUser target) => false;
        public void IgnoredStateChanged(IUser client, IUser target, bool ignored) { }
        public void InvalidLoginAttempt(IUser client) { }
        public void LoginGranted(IUser client) { }
        public void AdminLevelChanged(IUser client) { }
        public void InvalidRegistration(IUser client) { }
        public bool Registering(IUser client) => true;
        public void Registered(IUser client) { }
        public void Unregistered(IUser client) { }
        public void CaptchaSending(IUser client) { }
        public void CaptchaReply(IUser client, string reply) { }
        public bool VroomChanging(IUser client, ushort vroom) => true;
        public void VroomChanged(IUser client) { }
        public bool Flooding(IUser client, byte msg) => false;
        public void Flooded(IUser client) { }
        public bool ProxyDetected(IUser client) => false;
        public void Logout(IUser client) { }
        public void Idled(IUser client) { }
        public void Unidled(IUser client, uint seconds_away) { }
        public void BansAutoCleared() { }
        public void LinkError(ILinkError error) { }
        public void Linked() { }
        public void Unlinked() { }
        public void LeafJoined(ILeaf leaf) { }
        public void LeafParted(ILeaf leaf) { }
        public void LinkedAdminDisabled(ILeaf leaf, IUser client) { }

        #endregion 

        public BitmapSource Icon => new BitmapImage(new Uri("pack://application:,,,/extension;component/icon.png"));
        public UserControl GUI => null;

        public void Dispose()
        {
            httpClient?.Dispose();
            host.WriteLog("Spotify Extension disposed");
        }
    }

    // Model classes  
    public class SpotifyTokenResponse
    {
        public string access_token { get; set; }
        public string token_type { get; set; }
        public int expires_in { get; set; }
        public string refresh_token { get; set; }
        public string scope { get; set; }
    }

    public class SpotifyItem
    {
        public string id { get; set; }
        public string name { get; set; }
        public SpotifyArtist[] artists { get; set; }
        public SpotifyAlbum album { get; set; }
    }

    public class SpotifyArtist { public string name { get; set; } }
    public class SpotifyAlbum { public string name { get; set; } }

    public class UserSpotifyData
    {
        public string Username { get; set; }
        public string AccessToken { get; set; }
        public string RefreshToken { get; set; }
        public DateTime ExpiresAt { get; set; }
        public string State { get; set; }
        public string LastTrackId { get; set; }
    }

    public class OAuthCallback
    {
        public string Code { get; set; }
        public string State { get; set; }
        public string Username { get; set; }
    }

    public class SpotifyPlaybackResponse
    {
        public SpotifyItem item { get; set; }
        public bool is_playing { get; set; }
    }
}
