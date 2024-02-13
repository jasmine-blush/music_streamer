# Music Streamer Server + WPF Client
This is an implementation of a Music Streamer Server that streams mp3 files from a local music library to a WPF client over network.
Supports multiple clients at once.

Uses [NAudio](https://github.com/naudio/NAudio) and [Audio Tools Library](https://github.com/Zeugma440/atldotnet).

### Server ###
The server first takes a path to the music library as console input and then initializes the socket listening to incoming connections.
When a client connects, it sends the library information to the client and will add it to the client list.

Whenever a song is queued, a "Player" will start going through the bytes of the song (currently at a fixed rate at around ~320kbit/s). The networking thread takes the currently "playing" bytes whenever they change and sends them to all clients.

Songs are started from queue requests received by the clients. Whenever a new song is queued, all clients are notified of the new song in queue and receive its cover image.

### Client ###
Relatively simple WPF-GUI which runs two additional threads: a networking thread and a playback thread.

After connection to the server is established, the networking thread receives any packets sent by the server and distinguishes them by an identifying byte at the start of the packet. Possible received packets are: song bytes to be played, library information, newly queued song and the cover image of a newly queued song.

The playback thread plays already received song data from a buffer. A second buffer is used to temporarily store data received for the next song while the current song is still playing.


### TODO ###
- A newly connected client doesn't receive the currently queued songs yet
- Add shuffle feature
- Add support for different bitrates (currently transfers at a fixed ~320kbit/s)
- Add support for other music formats
