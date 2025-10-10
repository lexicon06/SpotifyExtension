# Spotify Extension for sb0t

![Version](https://img.shields.io/badge/version-1.0.0-blue)
![Framework](https://img.shields.io/badge/.NET%20Framework-4.7.2-purple)
![Platform](https://img.shields.io/badge/Platform-x86-lightgrey)
![Dependencies](https://img.shields.io/badge/Dependencies-Newtonsoft.Json%2013.0.3%20%7C%20System.Net.Http%204.3.4-yellow)
![Interface](https://img.shields.io/badge/Extension%20Interface-IExtension%20(iconnect)-orange)

An sb0t extension that integrates Spotify OAuth to display users' currently playing tracks in the chatroom. Users can connect their Spotify accounts and automatically broadcast what they're listening to.

## Features

- OAuth 2.0 integration with Spotify API
- Automatic token refresh handling
- Real-time "now playing" broadcasts every 30 seconds
- Manual `/song` command to check current track
- Persistent token storage across server restarts
- State mapping for secure OAuth callbacks
- Easy disconnect with `/spotifyoff` command

## Installation

1. Download the latest release from the source code
2. Edit with your credentials and compile with dotnet build
3. Extract `extension.dll` to your sb0t Extensions folder:
   ```
   %AppData%\sb0t\{YourServerName}\Extensions\SpotifyExtension\
   ```
4. Configure your Spotify API credentials (see Configuration section)
5. Start or restart sb0t
6. Go to the **Extensions** tab
7. Double-click **SpotifyExtension** to load it

## Configuration

Before using the extension, you must configure your Spotify API credentials:

1. Create a Spotify App at [Spotify Developer Dashboard](https://developer.spotify.com/dashboard)
2. Set your redirect URI to match your web server callback endpoint
3. Update the constants in `SpotifyServerEvents.cs`:
   ```csharp
   private const string SPOTIFY_CLIENT_ID = "YOUR_SPOTIFY_CLIENT_ID";
   private const string SPOTIFY_CLIENT_SECRET = "YOUR_SPOTIFY_CLIENT_SECRET";
   private const string REDIRECT_URI = "YOUR_REDIRECT_URI/spotify/callback";
   ```

## Building from Source

### Prerequisites

- .NET Framework 4.7.2 SDK or higher
- Visual Studio 2019+ or .NET CLI
- sb0t source code (for `iconnect` reference)
- Newtonsoft.Json NuGet package (v13.0.3)

### Build Steps

```bash
# Clone the repository
git clone https://github.com/lexicon06/SpotifyExtension.git
cd SpotifyExtension

# Build the project
dotnet build

# The compiled DLL will be in:
# bin\Debug\net472\extension.dll
```

## Usage

### Connecting Your Spotify Account

1. Type `/spotify` in the chatroom <cite />
2. Click the authorization link sent to you <cite />
3. Authorize the application on Spotify's website
4. You'll be automatically connected once the OAuth flow completes <cite />

### Commands

- **`/spotify`** - Connect your Spotify account <cite />
- **`/song`** - Manually check your currently playing track <cite />
- **`/spotifyoff`** - Disconnect your Spotify account <cite />

### Automatic Broadcasting

Once connected, the extension automatically checks your Spotify playback every 30 seconds. <cite /> When you start playing a new track, it broadcasts to the entire chatroom: <cite />

```
🎵 Username is now playing: Song Name by Artist Name
```

## Project Structure

```
SpotifyExtension/
├── SpotifyServerEvents.cs    # Main extension logic with IExtension implementation
├── Extension.csproj           # SDK-style project file
├── spotify-icon.png           # Extension icon
└── README.md                  # This file
```

## Technical Details

- **Framework:** .NET Framework 4.7.2
- **Platform:** x86 <cite />
- **Dependencies:** 
  - Newtonsoft.Json 13.0.3
  - System.Net.Http 4.3.4
- **Extension Interface:** `IExtension` from `iconnect`

## Data Storage

The extension stores data in two locations:

1. **User Tokens:** `%AppData%\sb0t\{ServerName}\Extensions\SpotifyExtension\spotify_tokens.json`
2. **State Mappings:** `C:\web\spotify_state_mapping.json` (for OAuth flow)

## OAuth Flow

The extension implements a secure OAuth 2.0 flow: <cite />

1. User types `/spotify` and receives authorization URL with unique state GUID <cite />
2. State-to-username mapping is saved <cite />
3. User authorizes on Spotify's website
4. Web server writes callback data to `C:\web\spotify_callbacks.json`
5. Extension processes callbacks on next cycle tick <cite />
6. Access and refresh tokens are stored <cite />

## API Integration

The extension uses the following Spotify API endpoints:

- **Token Exchange:** `https://accounts.spotify.com/api/token`
- **Currently Playing:** `https://api.spotify.com/v1/me/player/currently-playing`

Required OAuth scopes:
- `user-read-currently-playing`
- `user-read-playback-state`

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

## License

This project is licensed under the GNU Affero General Public License v3.0 - see the LICENSE file for details. [5](#0-4) 

## Acknowledgments

- Built for [sb0t](https://github.com/bsjaramillo/sb0t) by AresChat
- Uses the sb0t `iconnect` extension API
- Powered by Spotify Web API

## Support

For issues, questions, or suggestions, please open an issue on the [GitHub repository](#).

---

## Notes

This extension requires external web server infrastructure to handle OAuth callbacks, as the callback files are read from `C:\web\`. You'll need to set up a web endpoint that writes OAuth callback data to the expected JSON files. <cite /> The extension implements the full `IExtension` interface with all required event handlers. 
